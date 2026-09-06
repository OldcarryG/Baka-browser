using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Baka;

static class Args
{
    static string[] _all = [];
    public static void Set(string[] args) => _all = args;
    public static bool Has(string flag) => _all.Contains(flag);
    public static string[] Urls => [.. _all.Where(a => !a.StartsWith('-'))];

    public static double? Number(string prefix) =>
        _all.FirstOrDefault(a => a.StartsWith(prefix)) is { } a &&
        double.TryParse(a[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : null;
}

static class App
{
    public const string Name = "baka";

    // Not just "baka": another app already owns %LOCALAPPDATA%\baka on some machines.
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "baka-browser");

    public static string Path_(params string[] parts) => Path.Combine([DataDir, .. parts]);

    /// The window, Alt+Tab and taskbar icon, built from `baka icon.jpg` and embedded at build time.
    /// Multi-size on purpose: WinForms picks 16 for the taskbar and 32 for Alt+Tab out of the same Icon.
    public static readonly Icon Icon = Resource("baka.ico") is { } ico
        ? new Icon(new MemoryStream(ico)) : SystemIcons.Application;

    static string? _logo;
    /// The same mark as a data: URI for the new-tab page. Built on first use and shared by every tab.
    public static string Logo => _logo ??=
        Resource("baka-logo.png") is { } png ? "data:image/png;base64," + Convert.ToBase64String(png) : "";

    static byte[]? Resource(string name)
    {
        using var s = typeof(App).Assembly.GetManifestResourceStream(name);
        if (s is null) return null;
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        Args.Set(args);
        if (Args.Has("--self-check")) return SelfCheck.Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // The controls we don't draw ourselves — scrollbars, the combo boxes and the spinner in Settings —
        // are themed by the OS. Without this they come up in the light theme: a white stripe down a dark
        // sheet. One line beats owner-drawing three more controls.
        Application.SetColorMode(SystemColorMode.Dark);
        Application.Run(new BrowserForm(Args.Urls));
        return 0;
    }
}

sealed class Config
{
    public string TrackingPrevention { get; set; } = "Strict";
    public double IdleMinutes { get; set; } = 5;
    public string SearchUrl { get; set; } = "https://duckduckgo.com/?q={q}";
    public bool ShowBookmarksBar { get; set; } = true;

    /// key -> the id WebView2 assigned the extension, so Settings can deep-link its options page.
    public Dictionary<string, string> ExtensionIds { get; set; } = [];
    /// key -> whether the user left it on. Re-applied at every launch.
    public Dictionary<string, bool> ExtensionOn { get; set; } = [];
    /// key -> show a toolbar button for it.
    public Dictionary<string, bool> ExtensionPinned { get; set; } = [];
    /// Extensions the user added themselves. The bundled two are in code, not here.
    public List<ExtraExtension> Extra { get; set; } = [];

    [JsonIgnore] public static Config Current { get; private set; } = new();

    static string File_ => App.Path_("settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(File_))
                Current = JsonSerializer.Deserialize<Config>(File.ReadAllText(File_)) ?? new Config();
        }
        catch { Current = new Config(); }
        if (Args.Number("--idle-minutes=") is { } m) Current.IdleMinutes = m;   // test/debug override
    }

    public void Save()
    {
        try { File.WriteAllText(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

/// Search engines offered in Settings. `{q}` is the query placeholder Url.From substitutes.
static class Engines
{
    public static readonly (string Name, string Url)[] All =
    [
        ("DuckDuckGo", "https://duckduckgo.com/?q={q}"),
        ("Startpage", "https://www.startpage.com/sp/search?query={q}"),
        ("Brave Search", "https://search.brave.com/search?q={q}"),
        ("Ecosia", "https://www.ecosia.org/search?q={q}"),
        ("Google", "https://www.google.com/search?q={q}"),
        ("Bing", "https://www.bing.com/search?q={q}"),
    ];

    public const string Custom = "Custom";

    public static string NameOf(string url) =>
        All.FirstOrDefault(e => e.Url == url).Name ?? Custom;

    public static string? UrlOf(string name) =>
        All.FirstOrDefault(e => e.Name == name) is { Url: not null } e ? e.Url : null;
}

sealed record Bookmark(string Title, string Url);

/// Flat ordered list in bookmarks.json. No folders: a bar of buttons is the whole feature.
/// ponytail: linear scans, fine to a few hundred entries; add a dictionary if that ever changes.
static class Bookmarks
{
    static string File_ => App.Path_("bookmarks.json");

    public static List<Bookmark> All { get; private set; } = [];

    public static void Load()
    {
        try
        {
            if (File.Exists(File_))
                All = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(File_)) ?? [];
        }
        catch { All = []; }
    }

    static void Save()
    {
        try { File.WriteAllText(File_, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public static bool Has(string url) => All.Any(b => b.Url == url);

    /// Adds the page, or removes it if it is already bookmarked. Returns true when it ended up added.
    public static bool Toggle(string title, string url)
    {
        var existing = All.FirstOrDefault(b => b.Url == url);
        if (existing is not null) All.Remove(existing);
        else All.Add(new Bookmark(title.Length > 0 ? title : url, url));
        Save();
        return existing is null;
    }

    public static void Remove(Bookmark b) { All.Remove(b); Save(); }
}

sealed record Visit(string Url, string Title, DateTime At);

/// Where you have been. WebView2 has no history API and no chrome://history of its own, so this is
/// ours: one JSON object per line in history.jsonl, oldest first, appended as you browse.
/// ponytail: read and rendered whole at the 3000-visit cap — a few hundred KB and under a
/// millisecond. Upgrade path: SQLite if it ever has to search more than that.
static class History
{
    const int Cap = 3000;
    static string File_ => App.Path_("history.jsonl");

    public static List<Visit> All { get; } = [];

    /// about:, data: and chrome-extension: pages are plumbing, not places you went.
    public static bool Worth(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static void Load()
    {
        All.Clear();
        try
        {
            foreach (var line in File.ReadLines(File_))
                if (line.Length > 2 && JsonSerializer.Deserialize<Visit>(line) is { } v) All.Add(v);
        }
        catch { }
        if (All.Count > Cap) { All.RemoveRange(0, All.Count - Cap); Rewrite(); }
    }

    public static void Add(string url, string title)
    {
        if (!Worth(url)) return;
        if (All.Count > 0 && All[^1].Url == url) return;      // a reload is not a second visit
        All.Add(new Visit(url, title, DateTime.Now));
        try { File.AppendAllText(File_, JsonSerializer.Serialize(All[^1]) + "\n"); } catch { }
    }

    /// A page's title usually lands after its navigation finished; keep the entry it belongs to.
    public static void Touch(string url, string title)
    {
        if (All.Count > 0 && All[^1].Url == url) All[^1] = All[^1] with { Title = title };
    }

    public static void Clear() { All.Clear(); try { File.Delete(File_); } catch { } }

    /// Titles fixed up by Touch only reach the file here, on the way out.
    public static void Rewrite()
    {
        try { File.WriteAllLines(File_, All.Select(v => JsonSerializer.Serialize(v))); } catch { }
    }
}

/// Chromium switches, editable at %LOCALAPPDATA%\baka-browser\flags.txt without a rebuild.
static class Flags
{
    const string Defaults = """
        # baka engine flags - one per line, '#' comments a line out.
        # Delete a line to re-enable that Edge/Chromium feature.

        # --- resource use ---
        --process-per-site                          # one renderer per site, not per tab
        --disable-background-networking             # no idle phone-home
        --disable-component-update                  # no background component downloads

        # --- telemetry / tracking ---
        --disable-breakpad                          # no crash upload
        --disable-domain-reliability                # no network-error reporting to Google
        --disable-sync
        --no-pings                                  # ignore <a ping> beacons
        --no-first-run
        --no-default-browser-check
        --disable-client-side-phishing-detection    # local model that reports hits
        # SmartScreen sends visited URLs to Microsoft. Comment the next line out if you
        # want Microsoft's malware/phishing blocklist back at the cost of URL reporting.
        --disable-features=msSmartScreenProtection,OptimizationHints,OptimizationGuideModelDownloading,Translate,TranslateUI,MediaRouter,AutofillServerCommunication,InterestFeedContentSuggestions,EdgeShoppingAssistant,EdgeCollections,EdgeDiscoverPage,ImplicitSignin,msWebOOUI,msPdfOOUI
        """;

    public static string Load()
    {
        var path = App.Path_("flags.txt");
        if (!File.Exists(path)) File.WriteAllText(path, Defaults);
        return string.Join(' ', File.ReadAllLines(path)
            .Select(l => (l.IndexOf('#') is var c && c >= 0 ? l[..c] : l).Trim())
            .Where(l => l.Length > 0));
    }
}

/// One bundled extension: where to get it and what to call it in the UI.
sealed record ExtensionSource(string Key, string Display, string Blurb, string Url, bool IsCrx);

/// An extension the user added: from the Web Store (Dir is ours) or a folder of their own (Dir is theirs).
sealed record ExtraExtension(string Key, string Name, string Dir);

/// Anything installed, bundled or not, as the rest of the app wants to see it.
sealed record Installed(string Key, string Display, string Blurb, string Dir, bool Bundled);

/// What the app needs out of a manifest.json.
sealed record ManifestInfo(string Name, int Version, string? Popup, string? Options, string? Icon,
    string[] Permissions, string? NewTab);

/// Downloads and unpacks the extensions that ship enabled by default.
static class Extensions
{
    // Pinned fallback. uBOL's real URL is resolved from GitHub's latest release at startup so its
    // MV3 rule sets don't go stale; the Web Store CRX endpoint always serves the current build.
    const string UbolFallback =
        "https://github.com/uBlockOrigin/uBOL-home/releases/download/2026.901.1442/uBOLite_2026.901.1442.edge.zip";

    /// The unauthenticated CRX endpoint the Web Store itself uses. prodversion picks the build the
    /// store serves for; it has to be at least our Chromium's or we get an older package.
    public static string CrxUrl(string id) =>
        "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=152.0" +
        $"&acceptformat=crx2,crx3&x=id%3D{id}%26uc";

    public static readonly ExtensionSource[] Bundled =
    [
        new("ubol", "uBlock Origin Lite", "Blocks ads and ad domains", UbolFallback, IsCrx: false),
        new("privacybadger", "Privacy Badger", "Blocks trackers that follow you",
            CrxUrl("pkehgijcmpdhfbdbbnkijodmdjhbjlgp"), IsCrx: true),
    ];

    public static string DirFor(string key) => App.Path_("extensions", key);
    public static bool IsUnpacked(string key) => File.Exists(Path.Combine(DirFor(key), "manifest.json"));

    public static IEnumerable<Installed> All() =>
        Bundled.Select(b => new Installed(b.Key, b.Display, b.Blurb, DirFor(b.Key), true))
            .Concat(Config.Current.Extra.Select(e =>
                new Installed(e.Key, e.Name, e.Dir.StartsWith(App.DataDir) ? "Chrome Web Store" : e.Dir, e.Dir, false)));

    /// A Web Store link or a bare id -> the 32-letter id. Store ids are always a-p.
    public static string? StoreId(string input) =>
        Regex.Match(input.Trim(), "[a-p]{32}") is { Success: true } m ? m.Value : null;


    /// Applies any update staged as "<key>.new" by a previous run. Must run before the engine loads
    /// the folders, because a live extension directory cannot be replaced.
    public static void ApplyStagedUpdates()
    {
        foreach (var src in Bundled)
        {
            var staged = DirFor(src.Key) + ".new";
            if (!File.Exists(Path.Combine(staged, "manifest.json"))) continue;
            try
            {
                if (Directory.Exists(DirFor(src.Key))) Directory.Delete(DirFor(src.Key), true);
                Directory.Move(staged, DirFor(src.Key));
            }
            catch { /* still in use; try again next launch */ }
        }
    }

    public static async Task<string?> FetchAsync(ExtensionSource src, bool staged)
    {
        var target = DirFor(src.Key) + (staged ? ".new" : "");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var url = src.Key == "ubol" ? await ResolveUbolAsync(http) ?? src.Url : src.Url;
            var bytes = await http.GetByteArrayAsync(url);

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            using var zip = new MemoryStream(src.IsCrx ? StripCrxHeader(bytes) : bytes);
            new ZipArchive(zip, ZipArchiveMode.Read).ExtractToDirectory(target, overwriteFiles: true);

            if (!File.Exists(Path.Combine(target, "manifest.json")))
                return "downloaded package had no manifest.json";
            CutUpdateUrl(target);
            return null;
        }
        catch (Exception ex)
        {
            Scrub(target);
            return ex.Message;
        }
    }

    static void Scrub(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// WebView2 hosts no chrome.proxy at all, so a VPN or proxy extension installs, enables, and then
    /// sits there failing inside its own popup ("try again") with nothing the app can do about it.
    /// Nor can the app route around it: --proxy-server is documented as a WebView2 flag and does reach
    /// the engine's command line, but WebView2 152 ignores it — with a listener on the proxy address,
    /// not one connection arrives and pages load direct. Tested, not assumed. What does work is a VPN
    /// client that routes at the network adapter, which is below the browser and needs nothing from it.
    /// Short enough for one row: the row is ~280 px once the buttons take their share. The long version
    /// is in the install prompt, where there is room for it.
    public const string NoProxy = "No proxy support in WebView2. Use a VPN app.";

    public static bool Proxies(ManifestInfo? m) => m?.Permissions.Contains("proxy") == true;

    /// A Web Store package carries the store's own update_url, so Chromium re-checks it against Google
    /// every few hours. --disable-background-networking blocks that, so the check fails and lands in the
    /// download list as a broken .crx — a phone-home we don't want and a failure we don't need. Ours are
    /// updated by "Update extensions" instead, so the key comes out of the unpacked copy.
    internal static void CutUpdateUrl(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o || !o.Remove("update_url")) return;
            File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* unparseable or read-only: leave it exactly as it came */ }
    }

    /// Downloads a Web Store extension into extensions/&lt;id&gt;/ and vets it. The caller still has to
    /// hand the folder to the profile — this only puts a trustworthy unpacked copy on disk.
    public static async Task<(string? Key, string? Error)> InstallStoreAsync(string idOrUrl)
    {
        if (StoreId(idOrUrl) is not { } id)
            return (null, "That is not a Chrome Web Store link or a 32-letter extension id.");
        var dir = DirFor(id);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var bytes = await http.GetByteArrayAsync(CrxUrl(id));
            Scrub(dir);
            Directory.CreateDirectory(dir);
            using var zip = new MemoryStream(StripCrxHeader(bytes));
            // ExtractToDirectory refuses entries that resolve outside the destination, so a zip-slip
            // package fails here rather than writing over the profile.
            new ZipArchive(zip, ZipArchiveMode.Read).ExtractToDirectory(dir, overwriteFiles: true);
        }
        catch (Exception ex) { Scrub(dir); return (null, ex.Message); }

        if (ReadManifest(dir) is not { } m) { Scrub(dir); return (null, "the package has no readable manifest.json"); }
        if (m.Version < 3)
        {
            Scrub(dir);
            return (null, $"\"{m.Name}\" is a Manifest V2 extension. This engine only loads V3.");
        }
        CutUpdateUrl(dir);
        return (id, null);
    }

    static async Task<string?> ResolveUbolAsync(HttpClient http)
    {
        try
        {
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(
                await http.GetStringAsync("https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest"));
            // the .edge build is the one matched to the Chromium flavour WebView2 runs
            return doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(u => u?.EndsWith(".edge.zip") == true);
        }
        catch { return null; }
    }

    /// CRX3 = "Cr24" | uint32 version | uint32 headerLength | header | zip
    internal static byte[] StripCrxHeader(byte[] crx)
    {
        if (crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            return crx;                                        // already a plain zip
        var headerLen = BitConverter.ToUInt32(crx, 8);
        // compared as long: a hostile 4 GB length must not overflow into a valid-looking offset
        if (headerLen == 0 || headerLen > crx.Length - 12L)
            throw new InvalidDataException("CRX header length out of range");
        return crx[(12 + (int)headerLen)..];
    }

    /// Everything the UI needs from an unpacked extension: display name, whether the engine will load
    /// it at all, its popup and options pages, an icon to pin, and what it asked for.
    public static ManifestInfo? ReadManifest(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
            var r = doc.RootElement;
            var name = Str(r, "name") ?? "Extension";
            if (name.StartsWith("__MSG_") && name.EndsWith("__"))
                name = Message(dir, Str(r, "default_locale") ?? "en", name[6..^2]) ?? name;
            var action = r.TryGetProperty("action", out var a) ? a
                       : r.TryGetProperty("browser_action", out var b) ? b : default;
            return new ManifestInfo(
                name,
                r.TryGetProperty("manifest_version", out var mv) && mv.TryGetInt32(out var v) ? v : 2,
                Str(action, "default_popup")?.TrimStart('/'),
                (Str(r, "options_page") ?? (r.TryGetProperty("options_ui", out var ui) ? Str(ui, "page") : null))
                    ?.TrimStart('/'),
                Icon(r, action),
                Perms(r),
                r.TryGetProperty("chrome_url_overrides", out var ov) ? Str(ov, "newtab")?.TrimStart('/') : null);
        }
        catch { return null; }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// "__MSG_extName__" is an i18n placeholder; the real name is in _locales/<locale>/messages.json.
    static string? Message(string dir, string locale, string key)
    {
        foreach (var loc in new[] { locale, "en", "en_US" })
            try
            {
                using var doc = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(dir, "_locales", loc, "messages.json")));
                if (doc.RootElement.TryGetProperty(key, out var entry) && Str(entry, "message") is { } m) return m;
            }
            catch { }
        return null;
    }

    /// Whichever declared icon is closest to the 32 px we draw at.
    static string? Icon(JsonElement root, JsonElement action)
    {
        foreach (var owner in new[] { action, root })
        {
            if (owner.ValueKind != JsonValueKind.Object) continue;
            var set = owner.TryGetProperty("default_icon", out var d) ? d
                    : owner.TryGetProperty("icons", out var i) ? i : default;
            if (set.ValueKind == JsonValueKind.String) return set.GetString()!.TrimStart('/');
            if (set.ValueKind != JsonValueKind.Object) continue;
            if (set.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                   .OrderBy(p => int.TryParse(p.Name, out var n) ? Math.Abs(n - 32) : 999)
                   .FirstOrDefault() is { Value.ValueKind: JsonValueKind.String } best)
                return best.Value.GetString()!.TrimStart('/');
        }
        return null;
    }

    /// What the extension will be able to do, for the install prompt.
    static string[] Perms(JsonElement r) =>
        [.. new[] { "permissions", "host_permissions" }
            .SelectMany(k => r.TryGetProperty(k, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(x => x.GetString() ?? "")
                : Enumerable.Empty<string>())
            .Where(s => s.Length > 0).Distinct()];
}

static class SelfCheck
{
    // WinExe has no console; borrow the launching terminal's so `--self-check` is usable from a shell.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    public static int Run()
    {
        AttachConsole(-1);
        var fails = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok) fails++;
        }

        const string search = "https://duckduckgo.com/?q={q}";
        Check(Url.From("example.com", search) == "https://example.com", "bare host gets https");
        Check(Url.From("http://example.com/x", search) == "http://example.com/x", "explicit scheme kept");
        Check(Url.From("localhost:3000", search) == "https://localhost:3000", "localhost with port is a host");
        Check(Url.From("how do i tie a tie", search).StartsWith("https://duckduckgo.com/?q="), "words become a search");
        Check(Url.From("2+2", search) == "https://duckduckgo.com/?q=2%2B2", "no dot, no space -> escaped search");
        Check(Url.From("  ", search) == "about:blank", "blank input");
        Check(Url.From("mailto:a@b.com", search) == "mailto:a@b.com", "non-web scheme passed through");
        Check(Url.From("chrome-extension://abc/x.html", search) == "chrome-extension://abc/x.html",
            "extension pages are navigable");

        // The address bar's security mark. A padlock on an http page would be a lie, so it gets a test.
        Check(OmniBox.Mark("https://example.com") == Theme.Lock, "https shows the padlock");
        Check(OmniBox.Mark("http://example.com") == Theme.Insecure, "http shows the warning mark");
        Check(OmniBox.Mark("about:blank") == Theme.Search, "no transport shows the magnifier");

        // Every colour pair that carries text, against the 4.5:1 floor. A palette tweak that drops one
        // of these below it is a real accessibility regression and should not need eyes to catch.
        Check(Theme.Contrast(Theme.Text, Theme.Content) >= 4.5, "body text clears 4.5:1 on the sheet");
        Check(Theme.Contrast(Theme.Text, Theme.Fill2) >= 4.5, "the active tab's title clears 4.5:1");
        Check(Theme.Contrast(Theme.Muted, Theme.Strip) >= 4.5, "an inactive tab's title clears 4.5:1");
        Check(Theme.Contrast(Theme.Muted, Theme.Fill1) >= 4.5, "secondary text clears 4.5:1 on a card");
        Check(Theme.Contrast(Theme.Bad, Theme.Fill1) >= 4.5, "an error line clears 4.5:1 on a card");
        Check(Theme.Contrast(Theme.Accent, Theme.Content) >= 3.0, "the focus ring clears 3:1");

        // Every layout number goes through S(), so the invariant worth pinning is S() itself: identity at
        // 96 dpi (so nothing moved for a 100% user) and monotonic above it. Round-half-up, not truncate:
        // truncation collapses a 1 px hairline to 0 and the toolbar loses its border.
        Check(Theme.S(0) == 0 && Theme.Sf(0) == 0, "zero pixels stay zero at any scale");
        Check(Theme.Scale >= 1.0, "the display is never under 96 dpi");
        Check((int)Math.Round(42 * Theme.Scale) == Theme.S(42), "S rounds rather than truncates");
        Check(Theme.S(1) >= 1, "a hairline survives scaling");
        Check(Theme.S(220) > Theme.S(90), "S keeps the tab-width clamp ordered");
        Check(Theme.R == Theme.S(9), "the corner radius scales with everything else");

        // The new-tab "+": OnMouseDown gives the window buttons the first look at a click, so a "+" that
        // has drifted under them is not a small target, it is a minimise button. Walk the tab counts that
        // used to push it there.
        using (var strip = new TabStrip { Width = Theme.S(1280) })
        {
            var capLeft = strip.MaxRect.Left - strip.MaxRect.Width;   // where the three window buttons start
            var clear = true;
            for (var n = 0; n <= 30; n++)
            {
                strip.Tabs.Add(new StripTab());
                clear &= strip.PlusRect().Right <= capLeft && strip.PlusRect().Left >= 0;
            }
            Check(clear, "the new-tab + stays clear of the window buttons at 1 to 31 tabs");
            Check(strip.PlusRect().Width >= Theme.S(24) && strip.PlusRect().Height >= Theme.S(24),
                "the new-tab + is at least a 24 px target");
        }

        // CRX3 unwrapping: 12-byte prefix + declared header, then the zip
        var crx = new byte[] { (byte)'C', (byte)'r', (byte)'2', (byte)'4', 3, 0, 0, 0, 2, 0, 0, 0, 0xAA, 0xBB, 1, 2, 3 };
        Check(Extensions.StripCrxHeader(crx).SequenceEqual<byte>([1, 2, 3]), "CRX3 header stripped");
        var plain = new byte[] { (byte)'P', (byte)'K', 3, 4, 9 };
        Check(Extensions.StripCrxHeader(plain).SequenceEqual(plain), "plain zip passed through untouched");
        // header length claims 2 GB inside a 20-byte file: must throw, not wrap around into a valid offset
        var bad = new byte[20];
        "Cr24"u8.CopyTo(bad);
        BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(bad, 8);
        try { Extensions.StripCrxHeader(bad); Check(false, "absurd CRX header length rejected"); }
        catch (InvalidDataException) { Check(true, "absurd CRX header length rejected"); }

        Check(Extensions.StoreId("https://chromewebstore.google.com/detail/x/ddkjiahejlhfcafbddmgiahcphecmpfh")
              == "ddkjiahejlhfcafbddmgiahcphecmpfh", "store id pulled out of a Web Store link");
        Check(Extensions.StoreId(" pkehgijcmpdhfbdbbnkijodmdjhbjlgp ") == "pkehgijcmpdhfbdbbnkijodmdjhbjlgp",
            "bare id accepted");
        Check(Extensions.StoreId("https://example.com/downloads/thing.zip") is null, "junk is not an id");

        Check(History.Worth("https://example.com/page"), "http(s) pages are remembered");
        Check(!History.Worth("about:blank"), "about:blank is not history");
        Check(!History.Worth("chrome-extension://abc/newtab.html"), "extension pages are not history");

        var tmp = Directory.CreateTempSubdirectory("baka-check");
        try
        {
            void Manifest(string json) => File.WriteAllText(Path.Combine(tmp.FullName, "manifest.json"), json);
            Manifest("""
                {"manifest_version":3,"name":"__MSG_extName__","default_locale":"en",
                 "action":{"default_popup":"/popup.html","default_icon":{"16":"a.png","32":"b.png","128":"c.png"}},
                 "options_ui":{"page":"opts.html"},"chrome_url_overrides":{"newtab":"/tab.html"},
                 "permissions":["storage"],"host_permissions":["<all_urls>"]}
                """);
            Directory.CreateDirectory(Path.Combine(tmp.FullName, "_locales", "en"));
            File.WriteAllText(Path.Combine(tmp.FullName, "_locales", "en", "messages.json"),
                """{"extName":{"message":"Test Blocker"}}""");
            var m = Extensions.ReadManifest(tmp.FullName);
            Check(m?.Name == "Test Blocker", "__MSG__ name resolved from _locales");
            Check(m?.Popup == "popup.html", "popup path read without its leading slash");
            Check(m?.Options == "opts.html", "options_ui.page read");
            Check(m?.Icon == "b.png", "icon closest to 32px chosen");
            Check(m?.Permissions.Length == 2, "permissions and host_permissions merged for the prompt");
            Check(m?.NewTab == "tab.html", "chrome_url_overrides.newtab read");

            // update_url has to come out without taking the rest of the manifest with it
            Manifest("""{"manifest_version":3,"name":"Thing","update_url":"https://clients2.google.com/x"}""");
            Extensions.CutUpdateUrl(tmp.FullName);
            Check(Extensions.ReadManifest(tmp.FullName) is { Name: "Thing", Version: 3 }, "manifest survives update_url removal");
            Check(!File.ReadAllText(Path.Combine(tmp.FullName, "manifest.json")).Contains("update_url"),
                "update_url stripped so Chromium stops phoning the store");

            Manifest("""{"manifest_version":2,"name":"Old Thing"}""");
            Check(Extensions.ReadManifest(tmp.FullName) is { Version: 2, Name: "Old Thing" },
                "manifest v2 spotted so the install can be refused");
        }
        finally { tmp.Delete(true); }

        Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
        return fails;
    }
}
