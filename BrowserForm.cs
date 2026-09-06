using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Baka;

static class Url
{
    // Schemes handed to the engine as-is. javascript: and data: are deliberately absent: pasting one
    // into an address bar is a known self-XSS vector, so they fall through to search, like Chrome.
    static readonly string[] PassThrough =
    [
        "http://", "https://", "mailto:", "tel:", "about:",
        "file:", "chrome-extension://", "edge://", "chrome://", "view-source:",
    ];

    public static string From(string input, string searchTemplate)
    {
        var s = input.Trim();
        if (s.Length == 0) return "about:blank";
        if (PassThrough.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return s;

        var looksLikeHost = !s.Contains(' ') &&
            (s.Contains('.') || s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
        return looksLikeHost && Uri.TryCreate("https://" + s, UriKind.Absolute, out _)
            ? "https://" + s
            : searchTemplate.Replace("{q}", Uri.EscapeDataString(s));
    }
}

sealed class Tab
{
    public required WebView2 View;
    public required StripTab Strip;
    public DateTime LastActive = DateTime.UtcNow;
    public bool Suspended;
}

sealed class BrowserForm : Form
{
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Content };
    readonly PillBar _marks = new() { Dock = DockStyle.Top, Visible = false };
    readonly Panel _bar = new() { Dock = DockStyle.Top, Height = Theme.S(44), BackColor = Theme.ChromeBg };
    readonly TabStrip _tabs = new() { Dock = DockStyle.Top };
    readonly IconButton _back = new(Theme.Back, "Back  (Alt+Left)");
    readonly IconButton _fwd = new(Theme.Fwd, "Forward  (Alt+Right)");
    readonly IconButton _reload = new(Theme.Reload, "Reload  (F5)");
    readonly IconButton _star = new(Theme.Star, "Bookmark this page  (Ctrl+D)");
    readonly IconButton _shield = new(Theme.Shield, "Blocking is on", 11f);
    readonly IconButton _gear = new(Theme.Settings, "Settings  (Ctrl+,)");
    readonly OmniBox _omni = new();
    /// One toolbar button per pinned extension, rebuilt whenever the pin set changes.
    readonly List<IconButton> _pins = [];
    readonly List<Tab> _open = [];
    readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };
    readonly string[] _startUrls;

    CoreWebView2Environment? _env;
    /// key -> why its install failed, surfaced in Settings instead of a startup popup.
    public readonly Dictionary<string, string> ExtensionErrors = [];
    /// The extension page Ctrl+T lands on, if one claimed the new tab. Kept so the address bar can
    /// stay empty on it, the way it does on a built-in new tab page.
    string? _newTab;

    Tab? Current => _tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _open.Count ? _open[_tabs.SelectedIndex] : null;

    public BrowserForm(string[] startUrls)
    {
        _startUrls = startUrls;
        Text = App.Name;
        BackColor = Theme.ChromeBg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        MinimumSize = new Size(Theme.S(560), Theme.S(400));
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(1180), Theme.S(760));
        Icon = App.Icon;

        _bar.Controls.AddRange([_back, _fwd, _reload, _omni, _star, _shield, _gear]);
        _bar.Resize += (_, _) => LayoutBar();
        // hairline under the toolbar, Safari's one concession to a border
        _bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, 0, _bar.Height - 1, _bar.Width, _bar.Height - 1);
        };

        _marks.ItemClicked += (i, button) =>
        {
            if (i >= Bookmarks.All.Count) return;
            var mark = Bookmarks.All[i];
            if (button == MouseButtons.Right) { Bookmarks.Remove(mark); RefreshBookmarks(); }
            else if (button == MouseButtons.Middle) OpenTab(mark.Url);
            else if (Current is { } t) Navigate(t, mark.Url);
        };

        // WinForms resolves docking last-child-first, so _tabs (added last) takes the top edge,
        // then _bar, then the bookmarks bar, and _content gets the rest.
        Controls.Add(_content);
        Controls.Add(_marks);
        Controls.Add(_bar);
        Controls.Add(_tabs);

        _back.Click += (_, _) => { if (Current?.View.CoreWebView2?.CanGoBack == true) Current.View.CoreWebView2.GoBack(); };
        _fwd.Click += (_, _) => { if (Current?.View.CoreWebView2?.CanGoForward == true) Current.View.CoreWebView2.GoForward(); };
        _reload.Click += (_, _) =>
        {
            if (Current?.View.CoreWebView2 is not { } core) return;
            if (_reload.Glyph == Theme.Stop) core.Stop(); else core.Reload();
        };
        _gear.Click += (_, _) => OpenSettings();
        _shield.Click += (_, _) => OpenSettings();
        _star.Click += (_, _) => ToggleBookmark();

        _omni.Input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Go(_omni.Input.Text);
        };

        _tabs.SelectionChanged += _ => ShowActiveTab();
        _tabs.NewTabClicked += () => _ = NewTabAsync(null);
        _tabs.CloseClicked += CloseTab;

        _idleTimer.Tick += async (_, _) => await SuspendIdleTabsAsync();
        // titles that arrived after their visit was appended only reach history.jsonl here
        FormClosing += (_, _) => History.Rewrite();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.Glass(this);
        // the backdrop only shows where the chrome paints black, so the panels have to agree with it
        // (_tabs is deliberately absent: it paints itself opaque, over DWM's leftover caption strip).
        // It has to happen here, not in a field initialiser: Glass() above is what decides ChromeBg.
        foreach (Control c in new Control[] { this, _bar, _marks, _omni, _omni.Input })
            c.BackColor = Theme.ChromeBg;
        Invalidate(true);
        LayoutBar();
        await StartAsync();
    }

    void LayoutBar()
    {
        int pad = Theme.S(8), btn = Theme.S(32), gap = Theme.S(2);
        var y = (_bar.Height - btn) / 2;
        var x = pad;
        foreach (var b in new[] { _back, _fwd, _reload }) { b.SetBounds(x, y, btn, btn); x += btn + gap; }

        var right = _bar.Width - pad;
        foreach (var b in new[] { _gear, _shield, _star }.Concat(_pins))
        {
            right -= btn;
            b.SetBounds(right, y, btn, btn);
            right -= gap;
        }
        // Capped and centred in the space that is left, the way every desktop browser does it: stretched
        // edge to edge on a wide window the field reads as a horizon line, and a URL never needs 1100 px.
        var span = right - x - Theme.S(14);
        var w = Math.Clamp(span, Theme.S(60), Theme.S(760));
        _omni.SetBounds(x + Theme.S(6) + Math.Max(0, (span - w) / 2), y, w, btn);
    }

    // ---------- bookmarks ----------

    void ToggleBookmark()
    {
        if (Current?.View.CoreWebView2 is not { } core) return;
        if (core.Source is "about:blank" or "" or null) return;
        Bookmarks.Toggle(core.DocumentTitle, core.Source);
        RefreshBookmarks();
    }

    void RefreshBookmarks()
    {
        _marks.Set(Bookmarks.All.Select(m => m.Title.Length > 26 ? m.Title[..25] + "…" : m.Title));
        _marks.Visible = Config.Current.ShowBookmarksBar && Bookmarks.All.Count > 0;
        UpdateStar();
    }

    void UpdateStar()
    {
        var url = Current?.View.CoreWebView2?.Source ?? "";
        _star.Glyph = Bookmarks.Has(url) ? Theme.StarFill : Theme.Star;
        _star.Invalidate();
    }

    // ---------- startup ----------

    async Task StartAsync()
    {
        Config.Load();
        Bookmarks.Load();
        History.Load();
        RefreshBookmarks();
        Extensions.ApplyStagedUpdates();   // must precede env creation: a loaded folder can't be replaced

        try
        {
            _env = await CoreWebView2Environment.CreateAsync(null, App.Path_("profile"),
                new CoreWebView2EnvironmentOptions(Flags.Load())
                {
                    EnableTrackingPrevention = true,
                    AreBrowserExtensionsEnabled = true,
                });
        }
        catch (Exception ex)
        {
            // The one prerequisite the app cannot install or work around. Windows 11 ships the runtime,
            // Windows 10 does not always, so say where to get it rather than only that it is missing —
            // the installer deliberately does not chain it, so this dialog is the whole safety net.
            var go = MessageBox.Show(
                $"The WebView2 runtime is missing or failed to start, and {App.Name} draws every page with it." +
                $"\n\n{ex.Message}\n\nOpen Microsoft's download page for it?",
                App.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Error);
            if (go == DialogResult.Yes)
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
                }
                catch { }
            Close();
            return;
        }

        var firstRun = Extensions.Bundled.Any(s => !Extensions.IsUnpacked(s.Key));
        var first = await NewTabAsync(null, firstRun ? SetupPage : StartPage());
        if (first is null) return;

        await SetupExtensionsAsync(first.View.CoreWebView2!.Profile);
        if (firstRun) first.View.CoreWebView2!.NavigateToString(StartPage());

        foreach (var u in _startUrls) await NewTabAsync(Url.From(u, Config.Current.SearchUrl));
        if (_startUrls.Length > 0) _tabs.Select(1);

        _idleTimer.Start();
    }

    /// Downloads anything missing, registers each folder with the profile, and re-applies the
    /// on/off state the user chose in Settings.
    async Task SetupExtensionsAsync(CoreWebView2Profile profile)
    {
        IReadOnlyList<CoreWebView2BrowserExtension> installed;
        try { installed = await profile.GetBrowserExtensionsAsync(); }
        catch { installed = []; }

        foreach (var ext in Extensions.All())
        {
            // retroactive: anything installed before the strip existed still has the store's update_url
            if (ext.Dir.StartsWith(App.DataDir, StringComparison.OrdinalIgnoreCase))
                Extensions.CutUpdateUrl(ext.Dir);

            var wanted = Config.Current.ExtensionOn.GetValueOrDefault(ext.Key, true);
            var known = Config.Current.ExtensionIds.GetValueOrDefault(ext.Key);
            var live = installed.FirstOrDefault(x => x.Id == known);

            if (live is null)
            {
                if (!File.Exists(Path.Combine(ext.Dir, "manifest.json")))
                {
                    // only the bundled two know how to fetch themselves; a user's folder that moved is gone
                    if (!ext.Bundled) { ExtensionErrors[ext.Key] = "its folder is no longer there"; continue; }
                    if (await Extensions.FetchAsync(Extensions.Bundled.First(b => b.Key == ext.Key), staged: false)
                        is { } err)
                    {
                        ExtensionErrors[ext.Key] = err;
                        continue;
                    }
                }
                try { live = await profile.AddBrowserExtensionAsync(ext.Dir); }
                catch (Exception ex) { ExtensionErrors[ext.Key] = ex.Message; continue; }
                Config.Current.ExtensionIds[ext.Key] = live.Id;
            }

            try { if (live.IsEnabled != wanted) await live.EnableAsync(wanted); } catch { }
            Config.Current.ExtensionOn[ext.Key] = wanted;
        }
        Config.Current.Save();
        UpdateShield();
        RefreshPins();
    }

    void UpdateShield()
    {
        var on = Extensions.Bundled.Count(s => Config.Current.ExtensionOn.GetValueOrDefault(s.Key, true)
                                               && !ExtensionErrors.ContainsKey(s.Key));
        _shield.Glyph = on > 0 ? Theme.Shield : Theme.NoShield;
        _shield.Invalidate();
    }

    /// Called by Settings so the toggle takes effect without a restart.
    public async Task SetExtensionEnabledAsync(string key, bool on)
    {
        Config.Current.ExtensionOn[key] = on;
        Config.Current.Save();
        UpdateShield();
        RefreshPins();
        var id = Config.Current.ExtensionIds.GetValueOrDefault(key);
        if (id is null || Current?.View.CoreWebView2 is not { } core) return;
        try
        {
            if ((await core.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id) is { } ext)
                await ext.EnableAsync(on);
        }
        catch { }
    }

    public string? ExtensionId(string key) => Config.Current.ExtensionIds.GetValueOrDefault(key);

    public void SetExtensionPinned(string key, bool pinned)
    {
        Config.Current.ExtensionPinned[key] = pinned;
        Config.Current.Save();
        RefreshPins();
    }

    /// Hands an already-unpacked folder to the profile and remembers it. Returns an error, or null.
    public async Task<string?> AddExtensionAsync(string key, string dir, string name)
    {
        if (Current?.View.CoreWebView2 is not { } core) return "no window is ready yet";
        CoreWebView2BrowserExtension live;
        try { live = await core.Profile.AddBrowserExtensionAsync(dir); }
        catch (Exception ex) { return ex.Message; }

        Config.Current.Extra.RemoveAll(e => e.Key == key);
        Config.Current.Extra.Add(new ExtraExtension(key, name, dir));
        Config.Current.ExtensionIds[key] = live.Id;
        Config.Current.ExtensionOn[key] = true;
        ExtensionErrors.Remove(key);
        Config.Current.Save();
        RefreshPins();
        return null;
    }

    /// Unregisters a user-added extension and deletes its folder if we were the ones who downloaded it.
    public async Task RemoveExtensionAsync(string key)
    {
        var id = Config.Current.ExtensionIds.GetValueOrDefault(key);
        if (id is not null && Current?.View.CoreWebView2 is { } core)
            try
            {
                if ((await core.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id) is { } ext)
                    await ext.RemoveAsync();
            }
            catch { }

        var extra = Config.Current.Extra.FirstOrDefault(e => e.Key == key);
        Config.Current.Extra.RemoveAll(e => e.Key == key);
        Config.Current.ExtensionIds.Remove(key);
        Config.Current.ExtensionOn.Remove(key);
        Config.Current.ExtensionPinned.Remove(key);
        ExtensionErrors.Remove(key);
        Config.Current.Save();
        // a folder of the user's own is theirs; only our own download gets deleted
        if (extra is not null && extra.Dir.StartsWith(App.DataDir, StringComparison.OrdinalIgnoreCase))
            try { Directory.Delete(extra.Dir, true); } catch { }
        RefreshPins();
    }

    // ---------- pinned extensions ----------

    void RefreshPins()
    {
        foreach (var p in _pins) { _bar.Controls.Remove(p); p.Dispose(); }
        _pins.Clear();

        foreach (var ext in Extensions.All().Reverse())
        {
            if (!Config.Current.ExtensionPinned.GetValueOrDefault(ext.Key)) continue;
            if (!Config.Current.ExtensionOn.GetValueOrDefault(ext.Key, true)) continue;
            if (ExtensionErrors.ContainsKey(ext.Key)) continue;

            var info = Extensions.ReadManifest(ext.Dir);
            var b = new IconButton(ext.Display[..1].ToUpperInvariant(), ext.Display, 9f)
            {
                Picture = info?.Icon is { } rel ? LoadIcon(Path.Combine(ext.Dir, rel.Replace('/', '\\'))) : null,
            };
            b.Click += (_, _) => OpenExtensionUi(ext, b);
            _pins.Add(b);
            _bar.Controls.Add(b);
        }
        LayoutBar();
    }

    /// Copied off the stream so GDI+ does not keep the icon file locked for the life of the button.
    static Image? LoadIcon(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var src = Image.FromStream(fs);
            return new Bitmap(src);
        }
        catch { return null; }
    }

    /// A pinned button opens the extension's own popup in a floating panel, falling back to its options
    /// page in a tab when it has no popup.
    /// ponytail: WebView2 exposes no browser-action host, so the popup is just its HTML in a WebView.
    /// Per-site controls inside it may not see the page you are looking at. Upgrade path: none until
    /// WebView2 ships an extension-action API.
    void OpenExtensionUi(Installed ext, Control anchor)
    {
        if (Config.Current.ExtensionIds.GetValueOrDefault(ext.Key) is not { } id || _env is null) return;
        var info = Extensions.ReadManifest(ext.Dir);
        if (info?.Popup is { } popup)
        {
            var at = anchor.PointToScreen(new Point(anchor.Width, anchor.Height + 6));
            new ExtensionPopup(_env, $"chrome-extension://{id}/{popup}", at).Show(this);
        }
        else if (info?.Options is { } options) OpenTab($"chrome-extension://{id}/{options}");
    }

    void OpenSettings()
    {
        // the extension rows are built in the dialog's ctor, so adding or removing one asks for a reopen
        while (true)
        {
            using var dlg = new SettingsForm(this);
            var again = dlg.ShowDialog(this) == DialogResult.Retry;
            ApplySettings();
            if (!again) return;
        }
    }

    void ApplySettings()
    {
        foreach (var t in _open)
            if (t.View.CoreWebView2 is { } core)
                core.Profile.PreferredTrackingPreventionLevel = TrackingLevel();
        _idleTimer.Enabled = Config.Current.IdleMinutes > 0;
        RefreshBookmarks();
    }

    static CoreWebView2TrackingPreventionLevel TrackingLevel() => Config.Current.TrackingPrevention switch
    {
        "Off" => CoreWebView2TrackingPreventionLevel.None,
        "Basic" => CoreWebView2TrackingPreventionLevel.Basic,
        "Balanced" => CoreWebView2TrackingPreventionLevel.Balanced,
        _ => CoreWebView2TrackingPreventionLevel.Strict,
    };

    // ---------- tabs ----------

    public void OpenTab(string url) => _ = NewTabAsync(url);

    async Task<Tab?> NewTabAsync(string? url, string? html = null)
    {
        if (_env is null) return null;

        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Content, Visible = false };
        var tab = new Tab { View = view, Strip = new StripTab() };
        tab.Strip.Tag = tab;
        _content.Controls.Add(view);
        _open.Add(tab);
        _tabs.Tabs.Add(tab.Strip);

        try { await view.EnsureCoreWebView2Async(_env); }
        catch (Exception ex)
        {
            _open.Remove(tab);
            _tabs.Tabs.Remove(tab.Strip);
            view.Dispose();
            MessageBox.Show(ex.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }

        Wire(tab);
        _tabs.Select(_open.Count - 1);
        ShowActiveTab();

        // html == "" means "leave it alone": NewWindowRequested navigates the view itself.
        if (html is { Length: > 0 }) view.CoreWebView2!.NavigateToString(html);
        else if (url is not null) Navigate(tab, url);
        else if (html is null)
        {
            if (NewTabOverride() is { } page) Navigate(tab, page);
            else view.CoreWebView2!.NavigateToString(StartPage());
        }
        return tab;
    }

    /// An extension that declares chrome_url_overrides.newtab expects to *be* the new tab page.
    /// WebView2 has no new-tab concept, so it ignores the override outright — this is the substitution
    /// it would otherwise do. First enabled one wins, like Chrome.
    string? NewTabOverride()
    {
        foreach (var ext in Extensions.All())
        {
            if (!Config.Current.ExtensionOn.GetValueOrDefault(ext.Key, true)) continue;
            if (ExtensionErrors.ContainsKey(ext.Key)) continue;
            if (Config.Current.ExtensionIds.GetValueOrDefault(ext.Key) is not { } id) continue;
            if (Extensions.ReadManifest(ext.Dir)?.NewTab is { } page)
                return _newTab = $"chrome-extension://{id}/{page}";
        }
        return null;
    }

    static void Navigate(Tab tab, string url)
    {
        try { tab.View.CoreWebView2!.Navigate(url); }
        catch { tab.View.CoreWebView2!.Navigate("about:blank"); }
    }

    void Wire(Tab tab)
    {
        var core = tab.View.CoreWebView2!;

        // A focused page owns the keyboard: ProcessCmdKey never sees Ctrl+H. The wrapper re-raises the
        // accelerators the engine saw as plain KeyDown on the control, and Handled suppresses whatever
        // the engine would have done with them (Ctrl+P's print dialog, and so on).
        tab.View.KeyDown += (_, e) => { if (Shortcut(e.KeyData)) e.Handled = e.SuppressKeyPress = true; };

        core.Profile.PreferredTrackingPreventionLevel = TrackingLevel();
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Strip.Title = core.DocumentTitle.Length > 0 ? core.DocumentTitle : "New tab";
            _tabs.Invalidate();
            History.Touch(core.Source, tab.Strip.Title);
            if (tab == Current) Text = $"{tab.Strip.Title} — {App.Name}";
        };

        core.SourceChanged += (_, _) => { if (tab == Current) { ShowUrl(core.Source); UpdateStar(); } };
        core.HistoryChanged += (_, _) => { if (tab == Current) SyncNavButtons(core); };
        core.FaviconChanged += async (_, _) => await LoadFaviconAsync(tab);

        core.NavigationStarting += (_, _) =>
        {
            tab.LastActive = DateTime.UtcNow;
            tab.Suspended = false;
            if (tab == Current) _reload.Glyph = Theme.Stop;
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (tab == Current) _reload.Glyph = Theme.Reload;
            if (e.IsSuccess) History.Add(core.Source, core.DocumentTitle);
        };

        // keep window.opener intact instead of dropping target=_blank links on the floor
        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var t = await NewTabAsync(null, html: "");
                if (t is not null) { e.NewWindow = t.View.CoreWebView2; e.Handled = true; }
            }
            finally { deferral.Complete(); }
        };
    }

    /// The tab's own favicon, taken from the engine's cache rather than fetched again, scaled once to
    /// the box the strip draws (16 px at 100%, more on a scaled display) and cached on the tab. Failures
    /// leave the globe fallback in place.
    async Task LoadFaviconAsync(Tab tab)
    {
        Image? next = null;
        try
        {
            if (tab.View.CoreWebView2 is { FaviconUri.Length: > 0 } core)
            {
                // copied out of the engine's stream first: GDI+ needs a seekable one
                await using var raw = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                using var mem = new MemoryStream();
                await raw.CopyToAsync(mem);
                mem.Position = 0;
                using var src = Image.FromStream(mem);
                var n = TabStrip.IconBox;
                var box = new Bitmap(n, n);
                using (var g = Graphics.FromImage(box))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, 0, 0, n, n);
                }
                next = box;
            }
        }
        catch { }
        if (tab.Strip.Icon == next) return;
        tab.Strip.Icon?.Dispose();
        tab.Strip.Icon = next;
        _tabs.Invalidate();
    }

    void ShowUrl(string url)
    {
        if (_omni.Input.Focused) return;                                   // don't fight the user's typing
        // a new tab page is a new tab page, whoever renders it: no plumbing URL in the address bar
        var blank = url is "about:blank" or "" || url == _newTab;
        _omni.Input.Text = blank ? "" : url;
        // The glyph is the whole security indicator: plain http gets the warning mark, in red, because a
        // browser that ships with tracking protection on shouldn't be quiet about an unencrypted page.
        _omni.Leading = OmniBox.Mark(url);
    }

    void SyncNavButtons(CoreWebView2 core)
    {
        _back.Enabled = core.CanGoBack;
        _fwd.Enabled = core.CanGoForward;
    }

    void Go(string text)
    {
        if (Current is not { } tab) { _ = NewTabAsync(Url.From(text, Config.Current.SearchUrl)); return; }
        Navigate(tab, Url.From(text, Config.Current.SearchUrl));
        tab.View.Focus();
    }

    /// Only the active WebView stays Visible — WebView2 will not suspend a view it believes is on
    /// screen, and hidden views stop compositing.
    void ShowActiveTab()
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var t = _open[i];
            var active = i == _tabs.SelectedIndex;
            if (active && t.Suspended)
            {
                try { t.View.CoreWebView2?.Resume(); t.Suspended = false; } catch { }
            }
            t.View.Visible = active;
            if (!active) continue;
            t.View.BringToFront();
            t.LastActive = DateTime.UtcNow;
            if (t.View.CoreWebView2 is { } core)
            {
                ShowUrl(core.Source);
                SyncNavButtons(core);
                UpdateStar();
                Text = $"{t.Strip.Title} — {App.Name}";
            }
        }
    }

    void CloseTab(int index)
    {
        if (index < 0 || index >= _open.Count) return;
        var tab = _open[index];
        _open.RemoveAt(index);
        _tabs.Tabs.RemoveAt(index);
        tab.Strip.Icon?.Dispose();
        tab.View.Dispose();
        if (_open.Count == 0) { Close(); return; }
        _tabs.Removed(index);
        ShowActiveTab();
    }

    async Task SuspendIdleTabsAsync()
    {
        var minutes = Config.Current.IdleMinutes;
        if (minutes <= 0) return;
        foreach (var t in _open.ToArray())
        {
            if (t == Current || t.Suspended || t.View.IsDisposed) continue;
            if ((DateTime.UtcNow - t.LastActive).TotalMinutes < minutes) continue;
            try { t.Suspended = await t.View.CoreWebView2!.TrySuspendAsync(); } catch { }
        }
    }

    // ---------- keyboard ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        Shortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

    const int WmNcCalcSize = 0x83, WmNcHitTest = 0x84, WmNcMouseMove = 0xA0, WmNcLButtonDown = 0xA1,
              WmNcLButtonUp = 0xA2, WmNcMouseLeave = 0x02A2, HtClient = 1, HtCaption = 2, HtMaxButton = 9;

    /// No title bar. WM_NCCALCSIZE keeps every part of the frame Windows gives us — resize borders,
    /// snap, rounded corners, the Mica backdrop — and hands back only the caption strip, which the tab
    /// row then fills. WM_NCHITTEST tells Windows that the empty part of that row *is* the caption, so
    /// dragging, double-click to maximise, Aero Snap and the Alt+Space menu keep working with no mouse
    /// loop of our own. The top few pixels stay a resize gutter, exactly as in Chrome.
    /// The maximise button is reported as HTMAXBUTTON, which is the only way to get Win11's snap-layouts
    /// flyout on hover — Windows watches for that hit-test result, there is no API to ask for it. The
    /// cost is that Windows then owns the rect: our TabStrip stops getting mouse events over it, so the
    /// four WM_NC* cases below feed its hover and its click back in by hand.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero)
        {
            var win = Marshal.PtrToStructure<Rect>(m.LParam);        // window rect, before Windows insets it
            base.WndProc(ref m);
            var r = Marshal.PtrToStructure<Rect>(m.LParam);
            // Maximised, Windows hangs the window over every screen edge by the width of its invisible
            // resize border. Keep that one inset at the top, or the first rows of tab pixels sit off-screen.
            r.Top = win.Top + (IsZoomed(Handle) ? r.Left - win.Left : 0);
            Marshal.StructureToPtr(r, m.LParam, false);
            return;
        }

        switch (m.Msg)
        {
            // Non-client now, so no OnMouseMove/OnMouseLeave to light the button up or put it out.
            case WmNcMouseMove when (int)m.WParam == HtMaxButton:
                _tabs.HotCap = 1;
                break;
            case WmNcMouseLeave:
                _tabs.HotCap = -1;
                break;
            // Swallow the press (DefWindowProc would try to draw a caption button that does not exist)
            // and act on the release, so a press-and-drag-away cancels like a real button. The flyout is
            // driven by the hit test alone and is unaffected either way.
            case WmNcLButtonDown when (int)m.WParam == HtMaxButton:
                m.Result = IntPtr.Zero;
                return;
            case WmNcLButtonUp when (int)m.WParam == HtMaxButton:
                _tabs.HotCap = -1;
                _tabs.CapClick(1);
                m.Result = IntPtr.Zero;
                return;
        }

        base.WndProc(ref m);

        if (m.Msg != WmNcHitTest || (int)m.Result != HtClient) return;   // borders and corners keep theirs
        var lp = (long)m.LParam;
        var p = _tabs.PointToClient(new Point(unchecked((short)lp), unchecked((short)(lp >> 16))));
        if (!_tabs.ClientRectangle.Contains(p)) return;
        if (_tabs.MaxRect.Contains(p)) m.Result = HtMaxButton;
        else if (_tabs.Caption(p)) m.Result = HtCaption;
    }

    /// Every shortcut lives here because there are two doors into it: ProcessCmdKey while the chrome has
    /// focus, and the WebView's own KeyDown while the page does.
    bool Shortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.T: _ = NewTabAsync(null); return true;
            case Keys.Control | Keys.W: CloseTab(_tabs.SelectedIndex); return true;
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
                _omni.Input.Focus(); _omni.Input.SelectAll(); return true;
            case Keys.Control | Keys.R:
            case Keys.F5: Current?.View.CoreWebView2?.Reload(); return true;
            case Keys.Control | Keys.Oemcomma: OpenSettings(); return true;
            case Keys.Control | Keys.D: ToggleBookmark(); return true;
            // the engine's own download flyout, which is also where an in-progress download shows up
            case Keys.Control | Keys.J:
                try { Current?.View.CoreWebView2?.OpenDefaultDownloadDialog(); } catch { }
                return true;
            case Keys.Control | Keys.H: _ = NewTabAsync(null, HistoryPage()); return true;
            case Keys.Control | Keys.Shift | Keys.B:
                Config.Current.ShowBookmarksBar = !Config.Current.ShowBookmarksBar;
                Config.Current.Save();
                RefreshBookmarks();
                return true;
            case Keys.Alt | Keys.Left: _back.PerformClick(); return true;
            case Keys.Alt | Keys.Right: _fwd.PerformClick(); return true;
            case Keys.Control | Keys.Tab:
                if (_open.Count > 1) _tabs.Select((_tabs.SelectedIndex + 1) % _open.Count);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                if (_open.Count > 1) _tabs.Select((_tabs.SelectedIndex - 1 + _open.Count) % _open.Count);
                return true;
        }
        return false;
    }

    // ---------- pages ----------

    const string SetupPage = $$"""
        <!doctype html><meta charset=utf-8><title>Setting up</title>
        <style>html{color-scheme:dark}body{margin:0;height:100vh;display:grid;place-items:center;
        background:#1c1c1e;color:#f2f2f7;font:15px 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif}
        h1{font-size:46px;margin:0 0 10px;letter-spacing:-1.5px;font-weight:600}p{color:#9e9ea3}</style>
        <div style="text-align:center">
        <h1>{{App.Name}}</h1>
        <p>Installing uBlock Origin Lite and Privacy Badger…</p></div>
        """;

    static string StartPage() => $$"""
        <!doctype html><meta charset=utf-8><title>New tab</title>
        <style>
        html{color-scheme:dark}
        body{margin:0;height:100vh;display:grid;place-items:center;background:#1c1c1e;color:#f2f2f7;
             font:15px 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif}
        img{width:104px;height:104px;margin-bottom:26px;border-radius:23px}
        input{width:min(72vw,540px);padding:13px 20px;border-radius:12px;border:1px solid #38383a;
              background:#2c2c2e;color:#f2f2f7;font-size:15px;font-family:inherit;outline:none;
              transition:border-color .15s}
        input:focus{border-color:#0a84ff}
        p{margin-top:24px;color:#9e9ea3;font-size:13px}
        </style>
        <div style="text-align:center">
          <img id=mark src="{{App.Logo}}" alt="{{App.Name}}">
          <form onsubmit="location=T.replace('{q}',encodeURIComponent(q.value));return false">
            <input id=q name=q autofocus autocomplete=off placeholder="Search or enter address"
                   aria-label="Search or enter address">
          </form>
          <p>Ads and trackers blocked · no telemetry · idle tabs suspended</p>
        </div>
        <script>const T={{Json(Config.Current.SearchUrl)}};
        // reuse the one copy of the mark as this page's favicon, so the tab strip shows it too
        document.head.append(Object.assign(document.createElement('link'),{rel:'icon',href:mark.src}))</script>
        """;

    static string Json(string s) => JsonSerializer.Serialize(s);

    static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";

    /// History as a page, newest first and grouped by day. The filter runs inside the page, so nothing
    /// here needs a message channel from web content back into the app.
    static string HistoryPage()
    {
        var rows = new System.Text.StringBuilder();
        var day = "";
        foreach (var v in Enumerable.Reverse(History.All))
        {
            if (v.At.ToString("dddd d MMMM yyyy") is var d && d != day) rows.Append($"<h2>{Esc(day = d)}</h2>");
            rows.Append($"""
                <div class=r><i>{v.At:HH:mm}</i><a href="{Esc(v.Url)}">{Esc(v.Title.Length > 0 ? v.Title : v.Url)}</a><em>{Esc(Host(v.Url))}</em></div>
                """);
        }
        if (History.All.Count == 0)
            rows.Append("<p class=none>Nothing here yet. Pages you visit show up here.</p>");

        return $$"""
            <!doctype html><meta charset=utf-8><title>History</title>
            <style>
            html{color-scheme:dark}
            body{margin:0;padding:32px 8vw 60px;background:#1c1c1e;color:#f2f2f7;
                 font:14px 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif}
            header{display:flex;align-items:center;gap:16px;margin-bottom:24px}
            h1{font-size:30px;margin:0;letter-spacing:-.8px;font-weight:600;flex:1}
            input{width:240px;padding:9px 14px;border-radius:9px;border:1px solid #38383a;background:#2c2c2e;
                  color:#f2f2f7;font:inherit;outline:none}
            input:focus{border-color:#0a84ff}
            h2{font-size:12px;text-transform:uppercase;letter-spacing:.6px;color:#9e9ea3;font-weight:600;
               margin:26px 0 8px;padding-bottom:8px;border-bottom:1px solid #38383a}
            .r{display:flex;align-items:baseline;gap:14px;padding:7px 10px;border-radius:8px}
            .r:hover{background:#2c2c2e}
            .r i{color:#9e9ea3;font-style:normal;font-variant-numeric:tabular-nums;width:44px;flex:none}
            .r a{color:#f2f2f7;text-decoration:none;overflow:hidden;white-space:nowrap;text-overflow:ellipsis}
            .r a:hover{color:#0a84ff}
            .r em{color:#9e9ea3;font-style:normal;margin-left:auto;padding-left:16px;flex:none}
            .none,.foot{color:#9e9ea3}
            .foot{margin-top:40px;font-size:13px}
            </style>
            <header><h1>History</h1><input id=q autocomplete=off placeholder="Filter {{History.All.Count}} pages"></header>
            {{rows}}
            <p class=foot>Kept on this machine only, newest 3000 pages. Clear it in Settings → Advanced.</p>
            <script>
            const q=document.getElementById('q'),els=document.querySelectorAll('h2,.r');
            q.oninput=()=>{const s=q.value.toLowerCase();let head=null,shown=0;
              for(const el of els){
                if(el.tagName==='H2'){if(head)head.hidden=!shown;head=el;shown=0;continue}
                el.hidden=!!s&&!el.textContent.toLowerCase().includes(s);if(!el.hidden)shown++}
              if(head)head.hidden=!shown}
            </script>
            """;
    }
}

/// An extension's own popup HTML in a rounded panel under its toolbar button. The WebView is created
/// on open and disposed on close, so its renderer only exists while the panel is up.
sealed class ExtensionPopup : Form
{
    readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Content };
    readonly Point _want;
    bool _ready;

    public ExtensionPopup(CoreWebView2Environment env, string url, Point bottomRight)
    {
        _want = bottomRight;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Content;
        ClientSize = new Size(380, 460);
        Location = Fit(ClientSize);
        Controls.Add(_view);
        Deactivate += (_, _) => { if (_ready) Close(); };
        FormClosed += (_, _) => _view.Dispose();
        Shown += (_, _) => Theme.Glass(this);      // rounded corners and a dark frame, same as the window
        _ = OpenAsync(env, url);
    }

    Point Fit(Size size)
    {
        var work = Screen.FromPoint(_want).WorkingArea;
        return new Point(
            Math.Clamp(_want.X - size.Width, work.Left, Math.Max(work.Left, work.Right - size.Width)),
            Math.Clamp(_want.Y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
    }

    async Task OpenAsync(CoreWebView2Environment env, string url)
    {
        try { await _view.EnsureCoreWebView2Async(env); }
        catch { Close(); return; }
        _ready = true;
        var core = _view.CoreWebView2!;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationCompleted += async (_, _) => await FitToContentAsync(core);
        try { core.Navigate(url); } catch { Close(); }
    }

    /// Extension popups size themselves to their content, so ask the document how big it wants to be.
    async Task FitToContentAsync(CoreWebView2 core)
    {
        try
        {
            using var doc = JsonDocument.Parse(await core.ExecuteScriptAsync(
                "[document.documentElement.scrollWidth,document.documentElement.scrollHeight]"));
            var scale = DeviceDpi / 96f;
            ClientSize = new Size(
                Math.Clamp((int)(doc.RootElement[0].GetDouble() * scale) + 2, 260, 800),
                Math.Clamp((int)(doc.RootElement[1].GetDouble() * scale) + 2, 140, 820));
            Location = Fit(ClientSize);
        }
        catch { }
    }
}
