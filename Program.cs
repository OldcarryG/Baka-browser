using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;
using Velopack;

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
    public const string HomeBackgroundHost = "baka-assets.local";
    public static string HomeBackgroundDirectory => Path_("home-assets");
    /// The same mark as a data: URI for the new-tab page. Built on first use and shared by every tab.
    public static string Logo => _logo ??=
        Resource("baka-logo.png") is { } png ? "data:image/png;base64," + Convert.ToBase64String(png) : "";

    public static string HomeBackgroundUrl
    {
        get
        {
            var name = Config.Current.HomeBackgroundFile;
            if (!IsHomeBackgroundName(name) || !File.Exists(Path.Combine(HomeBackgroundDirectory, name))) return "";
            return $"https://{HomeBackgroundHost}/{Uri.EscapeDataString(name)}";
        }
    }

    public static bool HomeBackgroundIsVideo =>
        Path.GetExtension(Config.Current.HomeBackgroundFile).ToLowerInvariant() is ".mp4" or ".webm" or ".m4v";

    public static bool IsHomeBackgroundName(string? name) => name is
        "home-background.png" or "home-background.jpg" or "home-background.jpeg" or
        "home-background.mp4" or "home-background.webm" or "home-background.m4v";

    public static void MigrateLegacyHomeBackground(string name)
    {
        if (!IsHomeBackgroundName(name)) return;
        try
        {
            var oldPath = Path_(name);
            var newPath = Path.Combine(HomeBackgroundDirectory, name);
            if (!File.Exists(oldPath) || File.Exists(newPath)) return;
            Directory.CreateDirectory(HomeBackgroundDirectory);
            File.Copy(oldPath, newPath);
        }
        catch { /* a missing or locked wallpaper falls back to the built-in dashboard background */ }
    }

    public static string CustomCursorDataUri
    {
        get
        {
            if (!Config.Current.CustomCursorEnabled) return "";
            try
            {
                var bytes = File.ReadAllBytes(Path_("custom-cursor.cur"));
                return bytes.Length is > 0 and <= 65_536 ? "data:image/x-icon;base64," + Convert.ToBase64String(bytes) : "";
            }
            catch { return ""; }
        }
    }

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
        VelopackApp.Build().Run();
        Directory.CreateDirectory(DataDir);
        Args.Set(args);
        if (Args.Has("--self-check")) return SelfCheck.Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Config.Load();
        // The controls we don't draw ourselves — scrollbars, the combo boxes and the spinner in Settings —
        // are themed by the OS. Without this they come up in the light theme: a white stripe down a dark
        // sheet. One line beats owner-drawing three more controls.
        Application.SetColorMode(Theme.DarkMode ? SystemColorMode.Dark : SystemColorMode.System);
        if (!Args.Has("--private"))
        {
            if (!ProfileProtection.Apply(App.DataDir, Config.Current.EncryptProfileAtRest, out var protectionError))
                MessageBox.Show(
                    $"Windows could not {(Config.Current.EncryptProfileAtRest ? "encrypt" : "decrypt")} the browser profile. " +
                    $"The browser will continue using the profile in its current state.\n\n{protectionError}\n\n" +
                    "Check that the profile is closed in other browser processes and that this drive supports Windows file encryption.",
                    App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Application.Run(new BrowserForm(Args.Urls, Args.Has("--private")));
        return 0;
    }
}

sealed class Config
{
    public string TrackingPrevention { get; set; } = "Strict";
    public double IdleMinutes { get; set; } = 2;
    public string SearchUrl { get; set; } = "https://duckduckgo.com/?q={q}";
    public bool ShowBookmarksBar { get; set; } = true;
    public string BookmarksPosition { get; set; } = "Left";
    public bool BookmarksCollapsed { get; set; }
    public string TabsPosition { get; set; } = "Left";
    public bool TabsCollapsed { get; set; }
    public string UserAgent { get; set; } = "";
    public bool JavaScriptEnabled { get; set; } = true;
    public bool ImagesEnabled { get; set; } = true;
    public bool BuiltInBlockingEnabled { get; set; } = true;
    public bool SendPrivacySignals { get; set; } = true;
    public string ThemeMode { get; set; } = "Baka";
    public string AccentColor { get; set; } = "#E4E4E7";
    public string CustomBackgroundColor { get; set; } = "#30243B";
    public bool LiquidGlassEnabled { get; set; } = true;
    public bool RestoreLastSession { get; set; } = true;
    public List<string> LastSessionTabs { get; set; } = [];
    public int LastActiveTabIndex { get; set; }
    public bool AutoUpdateEnabled { get; set; } = true;
    public bool EncryptProfileAtRest { get; set; }
    public List<string> CustomBlockedDomains { get; set; } = [];
    public List<string> AllowedDomains { get; set; } = [];
    public string HomeBackgroundFile { get; set; } = "";
    public bool CustomCursorEnabled { get; set; }
    public string DashboardNotes { get; set; } = "";
    public List<DashboardTask> DashboardTasks { get; set; } = [];
    public List<string> DashboardWidgets { get; set; } = ["clock", "quicklinks", "notes", "tasks", "privacy"];

    [JsonIgnore] public static Config Current { get; private set; } = new();

    static string File_ => App.Path_("settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(File_))
                Current = JsonSerializer.Deserialize<Config>(File.ReadAllText(File_)) ?? new Config();
        }
        catch
        {
            try { if (File.Exists(File_)) File.Copy(File_, File_ + ".corrupt", overwrite: true); } catch { }
            Current = new Config();
        }

        if (Current.ThemeMode is not ("Baka" or "Dark" or "Light" or "Custom")) Current.ThemeMode = "Baka";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Current.AccentColor ?? "", "^#[0-9A-Fa-f]{6}$"))
            Current.AccentColor = "#0A84FF";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Current.CustomBackgroundColor ?? "", "^#[0-9A-Fa-f]{6}$"))
            Current.CustomBackgroundColor = "#30243B";
        if (Current.BookmarksPosition is not ("Left" or "Top")) Current.BookmarksPosition = "Left";
        if (Current.TabsPosition is not ("Left" or "Top")) Current.TabsPosition = "Left";
        Current.LastSessionTabs = (Current.LastSessionTabs ?? [])
            .Where(IsRestorableSessionUrl).TakeLast(50).ToList();
        if (Current.LastActiveTabIndex < 0 || Current.LastActiveTabIndex >= Current.LastSessionTabs.Count)
            Current.LastActiveTabIndex = Math.Max(0, Current.LastSessionTabs.Count - 1);
        if (Current.UserAgent is null || Current.UserAgent.Length > 512 || Current.UserAgent.Any(char.IsControl))
            Current.UserAgent = "";
        if (!Engines.IsValidTemplate(Current.SearchUrl))
            Current.SearchUrl = "https://duckduckgo.com/?q={q}";
        if (Current.TrackingPrevention is not ("Strict" or "Balanced" or "Basic" or "Off"))
            Current.TrackingPrevention = "Strict";
        if (Args.Number("--idle-minutes=") is { } m) Current.IdleMinutes = m;   // test/debug override
        if (!double.IsFinite(Current.IdleMinutes) || Current.IdleMinutes is < 0 or > 600) Current.IdleMinutes = 2;
        Current.CustomBlockedDomains = PrivacyRules.NormalizeDomains(Current.CustomBlockedDomains ?? []);
        Current.AllowedDomains = PrivacyRules.NormalizeDomains(Current.AllowedDomains ?? []);
        if (!App.IsHomeBackgroundName(Current.HomeBackgroundFile)) Current.HomeBackgroundFile = "";
        else App.MigrateLegacyHomeBackground(Current.HomeBackgroundFile);
        if (Current.DashboardNotes is null || Current.DashboardNotes.Length > 4000) Current.DashboardNotes = "";
        Current.DashboardTasks = (Current.DashboardTasks ?? []).Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Text))
            .Take(40).Select(t => new DashboardTask(t.Text.Length > 140 ? t.Text[..140] : t.Text, t.Done)).ToList();
        var validWidgets = new HashSet<string>(["clock", "quicklinks", "notes", "tasks", "privacy"], StringComparer.Ordinal);
        Current.DashboardWidgets = (Current.DashboardWidgets ?? [])
            .Where(w => validWidgets.Contains(w)).Distinct(StringComparer.Ordinal).ToList();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            var temp = File_ + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, File_, overwrite: true);
        }
        catch { }
    }

    static bool IsRestorableSessionUrl(string? url)
    {
        if (url == "") return true; // the browser's home tab
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme is "http" or "https" or "file";
    }
}

sealed record DashboardTask(string Text, bool Done);

/// Host-only rules keep the request matcher small and predictable. Imported lists are capped so each
/// WebView does not accumulate an unbounded number of native request filters.
static class PrivacyRules
{
    public const int CustomLimit = 500;
    public const int RecommendedListLimit = 120_000;
    const int MaxRecommendedListBytes = 12 * 1024 * 1024;
    const string RecommendedHostsUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";
    static readonly string RecommendedHostsPath = App.Path_("recommended-hosts.txt");
    static readonly HttpClient RecommendedListClient = CreateRecommendedListClient();
    static HashSet<string> _downloadedDomains = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> DownloadedDomains => _downloadedDomains;
    public static int DownloadedDomainCount => _downloadedDomains.Count;

    static HttpClient CreateRecommendedListClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BakaBrowser", "1.0"));
        return client;
    }

    public static void LoadDownloadedBlocklist()
    {
        try
        {
            _downloadedDomains = File.Exists(RecommendedHostsPath) &&
                new FileInfo(RecommendedHostsPath).Length <= MaxRecommendedListBytes
                ? new HashSet<string>(ParseHostsText(File.ReadAllText(RecommendedHostsPath), RecommendedListLimit), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { _downloadedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// Downloads only after an explicit user action and keeps a validated local copy for later launches.
    public static async Task<int> UpdateRecommendedBlocklistAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var response = await RecommendedListClient.GetAsync(
            RecommendedHostsUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxRecommendedListBytes)
            throw new InvalidDataException("The recommended list is larger than the 12 MB safety limit.");

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            if (output.Length + read > MaxRecommendedListBytes)
                throw new InvalidDataException("The recommended list is larger than the 12 MB safety limit.");
            output.Write(buffer, 0, read);
        }

        var parsed = ParseHostsText(Encoding.UTF8.GetString(output.ToArray()), RecommendedListLimit);
        if (parsed.Count < 1_000)
            throw new InvalidDataException("The downloaded list did not contain enough valid domains. Your current list was kept.");

        Directory.CreateDirectory(App.DataDir);
        var tempPath = RecommendedHostsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllLinesAsync(tempPath, parsed.Select(domain => "0.0.0.0 " + domain));
            File.Move(tempPath, RecommendedHostsPath, overwrite: true);
        }
        finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
        _downloadedDomains = new HashSet<string>(parsed, StringComparer.OrdinalIgnoreCase);
        return _downloadedDomains.Count;
    }

    public static void ClearDownloadedBlocklist()
    {
        if (File.Exists(RecommendedHostsPath)) File.Delete(RecommendedHostsPath);
        _downloadedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static List<string> NormalizeDomains(IEnumerable<string> domains, int limit = CustomLimit) => domains
        .Select(NormalizeDomain).Where(d => d is not null).Select(d => d!)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(limit, 1, RecommendedListLimit)).ToList();

    public static List<string> ParseHostsText(string content, int limit = CustomLimit)
    {
        var result = new List<string>();
        var safeLimit = Math.Clamp(limit, 1, RecommendedListLimit);
        foreach (var raw in content.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
            if (line.StartsWith("||", StringComparison.Ordinal))
            {
                var rule = line[2..].Split(['^', '$', '|', '/'], 2)[0];
                if (NormalizeDomain(rule) is { } domain) result.Add(domain);
            }
            else
            {
                var comment = line.IndexOf('#');
                if (comment >= 0) line = line[..comment].TrimEnd();
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 1 && (tokens[0] is "0.0.0.0" or "127.0.0.1" or "::" or "::1"))
                {
                    foreach (var token in tokens.Skip(1))
                        if (NormalizeDomain(token) is { } domain) result.Add(domain);
                }
                else if (tokens.Length == 1 && NormalizeDomain(tokens[0]) is { } domain) result.Add(domain);
            }
            if (result.Count >= safeLimit) break;
        }
        return NormalizeDomains(result, safeLimit);
    }

    static string? NormalizeDomain(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var value = input.Trim().TrimEnd('.');
        if (value.StartsWith("||", StringComparison.Ordinal)) value = value[2..].TrimEnd('^', '*', '|', '/');
        if (value.StartsWith("*.", StringComparison.Ordinal)) value = value[2..];
        if (value.StartsWith('.')) value = value[1..];
        try { value = new System.Globalization.IdnMapping().GetAscii(value).ToLowerInvariant(); }
        catch { return null; }
        if (value.Length is < 4 or > 253 || !value.Contains('.') ||
            System.Net.IPAddress.TryParse(value, out _) || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
            return null;
        var labels = value.Split('.');
        return labels.All(l => l.Length is > 0 and <= 63 && char.IsAsciiLetterOrDigit(l[0]) &&
            char.IsAsciiLetterOrDigit(l[^1]) && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) ? value : null;
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

    public static bool IsValidTemplate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || !value.Contains("{q}", StringComparison.Ordinal)) return false;
        var sample = value.Replace("{q}", "query", StringComparison.Ordinal);
        return Uri.TryCreate(sample, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") && uri.Host.Length > 0;
    }

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
                All = [.. (JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(File_)) ?? [])
                    .Where(b => b is not null && !string.IsNullOrWhiteSpace(b.Url))
                    .Select(b => b with { Title = b.Title ?? b.Url })];
        }
        catch { All = []; }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            var temp = File_ + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, File_, overwrite: true);
        }
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

    /// Internal pages are plumbing, not places you went.
    public static bool Worth(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static void Load()
    {
        All.Clear();
        var latest = new Queue<Visit>(Cap);
        var trimmed = false;
        try
        {
            foreach (var line in File.ReadLines(File_))
            {
                if (line.Length <= 2) continue;
                try
                {
                    if (JsonSerializer.Deserialize<Visit>(line) is not { } v ||
                        string.IsNullOrWhiteSpace(v.Url) || !Worth(v.Url)) continue;
                    if (latest.Count == Cap) { latest.Dequeue(); trimmed = true; }
                    latest.Enqueue(v with { Title = v.Title ?? v.Url });
                }
                catch { /* skip one damaged record and continue reading the rest of the history */ }
            }
        }
        catch { }
        All.AddRange(latest);
        if (trimmed) Rewrite();
    }

    public static void Add(string url, string title)
    {
        if (!Worth(url)) return;
        if (All.Count > 0 && All[^1].Url == url) return;      // a reload is not a second visit
        if (All.Count >= Cap)
        {
            All.RemoveRange(0, All.Count - Cap + 1);
            All.Add(new Visit(url, title, DateTime.Now));
            Rewrite();
            return;
        }
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
        try
        {
            var temp = File_ + ".tmp";
            File.WriteAllLines(temp, All.Select(v => JsonSerializer.Serialize(v)));
            File.Move(temp, File_, overwrite: true);
        }
        catch { }
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

        Check(History.Worth("https://example.com/page"), "http(s) pages are remembered");
        Check(!History.Worth("about:blank"), "about:blank is not history");
        Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
        return fails;
    }
}
