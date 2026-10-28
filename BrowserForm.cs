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
        "file:", "edge://", "chrome://", "view-source:", "chrome-extension://",
    ];

    public static string From(string input, string searchTemplate)
    {
        var s = input.Trim();
        if (s.Length == 0) return "about:blank";
        if (PassThrough.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return s;

        var looksLikeHost = !s.Contains(' ') &&
            (s.Contains('.') || s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
        var search = Engines.IsValidTemplate(searchTemplate) ? searchTemplate : Engines.All[0].Url;
        return looksLikeHost && Uri.TryCreate("https://" + s, UriKind.Absolute, out _)
            ? "https://" + s
            : search.Replace("{q}", Uri.EscapeDataString(s), StringComparison.Ordinal);
    }
}

sealed class Tab
{
    public required WebView2 View;
    public required StripTab Strip;
    public string DefaultUserAgent = "";
    public bool ResourceFilterInstalled;
    public bool IsHomePage;
    public bool HasHomePage;
    public bool IsLoading;
    public string HomeNonce = "";
    public string CursorScriptId = "";
    public string CursorScriptKey = "";
    public DateTime LastActive = DateTime.UtcNow;
    public DateTime SuspendRetryAfterUtc;
    public bool HomeAssetsMapped;
}

sealed partial class BrowserForm : Form, IExtensionHost
{
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Content };
    readonly Panel _bookmarkPanel = new() { Dock = DockStyle.Left, BackColor = Theme.ChromeBg };
    readonly PillBar _marks = new() { Dock = DockStyle.Fill };
    readonly IconButton _marksToggle = new(Theme.ChevronLeft, "Collapse bookmarks sidebar", 8f);
    readonly Panel _bar = new() { Dock = DockStyle.Top, Height = Theme.BarHeight, BackColor = Theme.ChromeBg };
    readonly Panel _tabsPanel = new() { Dock = DockStyle.Left, BackColor = Theme.Strip };
    readonly TabStrip _tabs = new() { Dock = DockStyle.Fill };
    readonly IconButton _tabsToggle = new(Theme.ChevronLeft, "Collapse tabs sidebar", 8f);
    readonly IconButton _back = new(Theme.Back, "Back  (Alt+Left)");
    readonly IconButton _fwd = new(Theme.Fwd, "Forward  (Alt+Right)");
    readonly IconButton _reload = new(Theme.Reload, "Reload  (F5)");
    readonly IconButton _home = new(Theme.Home, "New tab page");
    readonly IconButton _star = new(Theme.Star, "Bookmark this page  (Ctrl+D)");
    readonly IconButton _shield = new(Theme.ShieldIcon, "Built-in privacy settings");
    readonly IconButton _extensionsButton = new(Theme.Puzzle, "Extensions");
    readonly IconButton _menuButton = new(Theme.More, "Menu  (Alt+F)");
    /// Shown only on a Chrome Web Store extension page: installs that extension into this browser.
    readonly PillButton _storeButton = new("Add to Baka", Theme.Add, accent: true) { Visible = false };
    readonly ContextMenuStrip _mainMenu = new();
    readonly ContextMenuStrip _extensionsMenu = new();
    string? _storeId;
    bool _storeInstalling;
    readonly Label _privateBadge = new()
    {
        Text = "PRIVATE", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.FromArgb(72, 35, 65), ForeColor = Color.FromArgb(255, 204, 235),
        Font = Theme.UiBold, Visible = false,
    };
    readonly OmniBox _omni = new();
    readonly List<Tab> _open = [];
    readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer _dashboardSaveTimer = new() { Interval = 900 };
    readonly ContextMenuStrip _tabMenu = new();
    /// Addresses of recently closed tabs, newest last, for Ctrl+Shift+T and the tab menu.
    readonly List<string> _closedTabs = [];
    readonly string[] _startUrls;
    readonly bool _privateBrowsing;
    readonly string? _privateProfilePath;
    readonly List<DashboardTask> _dashboardTasks;
    readonly Dictionary<Control, Cursor> _defaultCursors = [];
    string _dashboardNotes;
    Cursor? _customCursor;
    bool _closing, _trueFullscreen, _suspensionSweep, _nativeCaptionLayout;
    bool _extensionsAvailable;
    /// A window opened by moving a tab out: it shows only that page, does not restore the session,
    /// and leaves the main window's saved session alone when it closes.
    readonly bool _detached;
    /// True while the previous session's tabs are being reopened at startup.
    bool _restoring;
    Rectangle _savedBounds;
    FormBorderStyle _savedBorderStyle;
    FormWindowState _savedWindowState;

    CoreWebView2Environment? _env;
    Tab? Current => _tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _open.Count ? _open[_tabs.SelectedIndex] : null;

    public BrowserForm(string[] startUrls, bool privateBrowsing = false)
    {
        _startUrls = startUrls;
        _privateBrowsing = privateBrowsing;
        _detached = Args.Has("--detached");
        _privateProfilePath = privateBrowsing
            ? Path.Combine(Path.GetTempPath(), "baka-private-" + Guid.NewGuid().ToString("N")) : null;
        if (_privateProfilePath is { } temporaryProfile)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DeletePrivateProfileOnExit(temporaryProfile);
        _dashboardNotes = privateBrowsing ? "" : Config.Current.DashboardNotes;
        _dashboardTasks = privateBrowsing ? [] : [.. Config.Current.DashboardTasks];
        Text = privateBrowsing ? $"Private — {App.Name}" : App.Name;
        BackColor = Theme.ChromeBg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        MinimumSize = new Size(Theme.S(560), Theme.S(400));
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(1180), Theme.S(760));
        if (Args.Number("--x=") is { } x && Args.Number("--y=") is { } y)
        {
            // Where a dragged tab was dropped, kept on the screen that point is on.
            var area = Screen.FromPoint(new Point((int)x, (int)y)).WorkingArea;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Math.Clamp((int)x, area.Left, Math.Max(area.Left, area.Right - Width)),
                                 Math.Clamp((int)y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
        Icon = App.Icon;

        _privateBadge.Visible = privateBrowsing;
        _star.Enabled = !privateBrowsing;
        _extensionsButton.Visible = false;   // shown once StartAsync knows the engine supports extensions
        _bar.Controls.AddRange([_back, _fwd, _reload, _home, _omni, _storeButton, _extensionsButton, _star, _shield, _menuButton, _privateBadge]);
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
            if (button == MouseButtons.Right)
            {
                if (!_privateBrowsing) { Bookmarks.Remove(mark); RefreshBookmarks(); }
                return;
            }
            else if (button == MouseButtons.Middle) OpenTab(mark.Url);
            else if (Current is { } t) Navigate(t, mark.Url);
        };
        _marksToggle.Click += (_, _) =>
        {
            Config.Current.BookmarksCollapsed = !Config.Current.BookmarksCollapsed;
            if (!_privateBrowsing) Config.Current.Save();
            LayoutBookmarks();
        };
        _bookmarkPanel.Controls.Add(_marks);
        _bookmarkPanel.Controls.Add(_marksToggle);
        _tabsToggle.Click += (_, _) =>
        {
            Config.Current.TabsCollapsed = !Config.Current.TabsCollapsed;
            if (!_privateBrowsing) Config.Current.Save();
            LayoutTabs();
        };
        _tabsPanel.Controls.Add(_tabs);
        _tabsPanel.Controls.Add(_tabsToggle);

        // Docking resolves last-child-first: tabs claim the left edge (or top row), then the toolbar,
        // then bookmarks. The page gets the remaining area; both side rails can be independently collapsed.
        Controls.Add(_content);
        Controls.Add(_bookmarkPanel);
        Controls.Add(_bar);
        Controls.Add(_tabsPanel);
        LayoutTabs();
        LayoutBookmarks();

        _back.Click += (_, _) => { if (Current?.View.CoreWebView2?.CanGoBack == true) Current.View.CoreWebView2.GoBack(); };
        _fwd.Click += (_, _) => { if (Current?.View.CoreWebView2?.CanGoForward == true) Current.View.CoreWebView2.GoForward(); };
        _reload.Click += (_, _) =>
        {
            if (Current?.View.CoreWebView2 is not { } core) return;
            if (_reload.Glyph == Theme.Stop) core.Stop(); else core.Reload();
        };
        _home.Click += (_, _) => GoHome();
        _shield.Click += (_, _) => OpenSettings("Privacy");
        _star.Click += (_, _) => ToggleBookmark();
        _menuButton.Click += (_, _) => ShowMainMenu();
        _extensionsButton.Click += (_, _) => _ = ShowExtensionsMenuAsync();
        _storeButton.Click += (_, _) => _ = InstallFromStoreAsync();
        BuildMainMenu();
        Theme.Style(_extensionsMenu);

        _omni.Input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Go(_omni.Input.Text);
        };

        _tabs.SelectionChanged += _ => ShowActiveTab();
        _tabs.NewTabClicked += () => _ = NewTabAsync(null);
        _tabs.CloseClicked += CloseTab;
        _tabs.TabMenuRequested += ShowTabMenu;
        _tabs.AudioClicked += i =>
        {
            if (i < 0 || i >= _open.Count || _open[i].View.CoreWebView2 is not { } core) return;
            try { core.IsMuted = !core.IsMuted; } catch { }
        };
        _tabs.TabMoved += (from, to) =>
        {
            if (from < 0 || to < 0 || from >= _open.Count || to >= _open.Count) return;
            var moved = _open[from];
            _open.RemoveAt(from);
            _open.Insert(to, moved);
        };
        _tabs.TabDraggedOut += (index, at) => MoveTabToNewWindow(index, at);
        Theme.Style(_tabMenu);

        _idleTimer.Tick += async (_, _) => await SuspendIdleTabsAsync();
        _dashboardSaveTimer.Tick += (_, _) =>
        {
            _dashboardSaveTimer.Stop();
            if (_privateBrowsing || _detached) return;
            SaveSession();
            Config.Current.Save();
        };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Stop();
            if (!_privateBrowsing && Config.Current.AutoUpdateEnabled)
                await AutoUpdater.CheckAndDownloadAsync();
            if (!_closing && !_privateBrowsing && Config.Current.AutoUpdateEnabled)
            {
                _updateTimer.Interval = 6 * 60 * 60 * 1000;
                _updateTimer.Start();
            }
        };
        // titles that arrived after their visit was appended only reach history.jsonl here
        FormClosing += (_, _) =>
        {
            _closing = true;
            _idleTimer.Stop();
            _updateTimer.Stop();
            _dashboardSaveTimer.Stop();
            // A window opened by moving a tab out shares the profile with the main window. It must not write
            // its start-up copy of settings or history over what the main window saved since then.
            if (!_privateBrowsing && !_detached)
            {
                SaveSession();
                Config.Current.Save();
                History.Rewrite();
            }
        };
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.Glass(this, backdrop: Theme.GlassStyle, updateGlobalBackdrop: true);
        // the backdrop only shows where the chrome paints black, so the panels have to agree with it
        // (_tabs is deliberately absent: it paints itself opaque, over DWM's leftover caption strip).
        // It has to happen here, not in a field initialiser: Glass() above is what decides ChromeBg.
        foreach (Control c in new Control[] { this, _bar, _bookmarkPanel, _marks, _omni })
            c.BackColor = Theme.ChromeBg;
        _tabsToggle.BackColor = Theme.Strip;
        _omni.ApplyTheme();
        Invalidate(true);
        LayoutBar();
        LayoutTabs();
        LayoutBookmarks();
        try { await StartAsync(); }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                MessageBox.Show($"{App.Name} could not finish starting:\n\n{ex.Message}",
                    App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }
    }

    void LayoutBar()
    {
        int pad = Theme.S(6), btn = Theme.ButtonSize, gap = Theme.S(2);
        var y = (_bar.Height - btn) / 2;
        var x = pad;
        foreach (var b in new[] { _back, _fwd, _reload, _home }) { b.SetBounds(x, y, btn, btn); x += btn + gap; }

        var right = _bar.Width - pad;
        foreach (var b in new Control[] { _menuButton, _shield, _star, _extensionsButton }.Where(b => b.Visible))
        {
            right -= btn;
            b.SetBounds(right, y, btn, btn);
            right -= gap;
        }
        if (_storeButton.Visible)
        {
            var storeWidth = _storeButton.PreferredWidth;
            right -= storeWidth + Theme.S(4);
            _storeButton.SetBounds(right, y + 1, storeWidth, btn - 2);
            right -= Theme.S(4);
        }
        if (_privateBadge.Visible)
        {
            _privateBadge.SetBounds(x + Theme.S(4), y + Theme.S(3), Theme.S(58), btn - Theme.S(6));
            x += Theme.S(68);
        }
        // Capped and centred in the space that is left, the way every desktop browser does it: stretched
        // edge to edge on a wide window the field reads as a horizon line, and a URL never needs 1100 px.
        var span = right - x - Theme.S(14);
        var w = Math.Clamp(span, Theme.S(60), Theme.S(760));
        _omni.SetBounds(x + Theme.S(6) + Math.Max(0, (span - w) / 2), y, w, btn);
    }

    void LayoutBookmarks()
    {
        var top = Config.Current.BookmarksPosition == "Top";
        var collapsed = !top && Config.Current.BookmarksCollapsed;
        _bookmarkPanel.SuspendLayout();
        _bookmarkPanel.Dock = top ? DockStyle.Top : DockStyle.Left;
        _bookmarkPanel.Height = top ? Theme.S(34) : 0;
        _bookmarkPanel.Width = top ? 0 : Theme.S(collapsed ? 44 : 196);
        _bookmarkPanel.Visible = Config.Current.ShowBookmarksBar;
        _bookmarkPanel.BackColor = Theme.ChromeBg;
        _tabsPanel.BackColor = Theme.Strip;
        _marks.BackColor = Theme.ChromeBg;
        _marks.Dock = DockStyle.Fill;
        _marksToggle.Dock = top ? DockStyle.Right : DockStyle.Top;
        _marksToggle.Visible = !top;
        _marksToggle.Glyph = collapsed ? Theme.ChevronRight : Theme.ChevronLeft;
        _marksToggle.Height = Theme.RailRowHeight;
        _marksToggle.BackColor = Theme.ChromeBg;
        _marksToggle.AccessibleName = collapsed ? "Expand bookmarks sidebar" : "Collapse bookmarks sidebar";
        _marks.Set(Bookmarks.All.Select(m => (m.Title.Length > 26 ? m.Title[..25] + "…" : m.Title, SiteIcons.For(m.Url))),
            vertical: !top, compact: collapsed);
        _bookmarkPanel.ResumeLayout(performLayout: true);
    }

    void LayoutTabs()
    {
        var left = Config.Current.TabsPosition == "Left";
        var collapsed = left && Config.Current.TabsCollapsed;
        var captionChanged = _nativeCaptionLayout != left;
        _nativeCaptionLayout = left;
        _tabsPanel.SuspendLayout();
        _tabsPanel.Dock = left ? DockStyle.Left : DockStyle.Top;
        _tabsPanel.Width = left ? Theme.S(collapsed ? 50 : 204) : 0;
        _tabsPanel.Height = left ? 0 : Theme.S(38);
        _tabsPanel.BackColor = Theme.Strip;
        _tabs.Vertical = left;
        _tabs.Compact = collapsed;
        _tabsToggle.Visible = left;
        _tabsToggle.Dock = DockStyle.Top;
        _tabsToggle.Height = Theme.RailRowHeight;
        _tabsToggle.BackColor = Theme.Strip;
        _tabsToggle.Glyph = collapsed ? Theme.ChevronRight : Theme.ChevronLeft;
        _tabsToggle.AccessibleName = collapsed ? "Expand tabs sidebar" : "Collapse tabs sidebar";
        _tabs.BackColor = Theme.Strip;
        _tabsPanel.ResumeLayout(performLayout: true);
        _tabs.RefreshLayout();
        if (captionChanged && IsHandleCreated)
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // frame changed, keep size and position
    }

    // ---------- bookmarks ----------

    void ToggleBookmark()
    {
        if (_privateBrowsing) return;
        if (Current?.View.CoreWebView2 is not { } core) return;
        if (core.Source is "about:blank" or "" or null) return;
        Bookmarks.Toggle(core.DocumentTitle, core.Source);
        RefreshBookmarks();
    }

    void RefreshBookmarks()
    {
        LayoutBookmarks();
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
        Bookmarks.Load();
        if (!_privateBrowsing) History.Load();
        PrivacyRules.LoadDownloadedBlocklist();
        RefreshPrivacyRuleCache();
        RefreshBookmarks();
        _extensionsAvailable = !_privateBrowsing && RuntimeSupportsExtensions();
        _extensionsButton.Visible = _extensionsAvailable;
        if (_extensionsAvailable) WebStore.CleanStaging();
        BuildMainMenu();   // again, now that the Extensions entry can be included
        LayoutBar();
        try
        {
            _env = await CoreWebView2Environment.CreateAsync(null, _privateProfilePath ?? App.Path_("profile"),
                new CoreWebView2EnvironmentOptions(Flags.Load())
                {
                    EnableTrackingPrevention = true,
                    AreBrowserExtensionsEnabled = _extensionsAvailable,
                });
        }
        catch (Exception ex)
        {
            // The installer bootstraps the Evergreen runtime, but an offline machine or a managed
            // Windows policy can still prevent setup. Offer Microsoft's official repair/download page.
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

        if (_closing || IsDisposed) return;
        ApplyNativeCursor();
        if (_detached && _startUrls.Length > 0)
        {
            foreach (var u in _startUrls) await NewTabAsync(Url.From(u, Config.Current.SearchUrl));
            if (_open.Count == 0) await NewTabAsync(null, home: true);
        }
        else if (!_privateBrowsing && !_detached && _startUrls.Length == 0 && Config.Current.RestoreLastSession && Config.Current.LastSessionTabs.Count > 0)
        {
            // A copy: while tabs open, nothing may save a half-restored session over the saved one.
            var saved = Config.Current.LastSessionTabs.ToList();
            var savedActiveIndex = Config.Current.LastActiveTabIndex;
            Tab? savedActive = null;
            _restoring = true;
            try
            {
                for (var i = 0; i < saved.Count && !_closing; i++)
                {
                    var tab = saved[i].Length == 0 ? await NewTabAsync(null, home: true) : await NewTabAsync(saved[i]);
                    if (tab is not null && i == savedActiveIndex) savedActive = tab;
                }
            }
            finally { _restoring = false; }
            if (_closing) return;
            if (_open.Count == 0) await NewTabAsync(null, home: true);
            else if (savedActive is not null) _tabs.Select(_open.IndexOf(savedActive));
            else _tabs.Select(0);
        }
        else
        {
            var first = await NewTabAsync(null, home: true);
            if (first is null) return;
            foreach (var u in _startUrls) await NewTabAsync(Url.From(u, Config.Current.SearchUrl));
            if (_startUrls.Length > 0) _tabs.Select(1);
        }
        UpdateShield();
        if (_extensionsAvailable)
            try { await ListExtensionsAsync(); } catch { }   // fills the "already added" state of the store button

        _idleTimer.Start();
        if (Args.Value("--ui-test") is { } testDir)
        {
            await EnsureDefaultAdBlockerAsync();   // the test checks the first-run result
            _ = RunUiTestAsync(testDir);
        }
        else _ = EnsureDefaultAdBlockerAsync();
        if (!_privateBrowsing && Config.Current.AutoUpdateEnabled) _updateTimer.Start();
    }

    void UpdateShield()
    {
        var on = Config.Current.BuiltInBlockingEnabled;
        _shield.Glyph = on ? Theme.ShieldIcon : Theme.NoShield;
        _shield.AccessibleName = on ? "Built-in ad and tracker blocking is on" : "Built-in ad and tracker blocking is off";
        _shield.Invalidate();
    }

    void ApplyNativeCursor()
    {
        _customCursor?.Dispose();
        _customCursor = null;
        try
        {
            if (Config.Current.CustomCursorEnabled && File.Exists(App.Path_("custom-cursor.cur")))
                _customCursor = new Cursor(App.Path_("custom-cursor.cur"));
        }
        catch { _customCursor = null; }
        Cursor = _customCursor ?? Cursors.Default;
        ApplyCursorTree(this);
    }

    void ApplyCursorTree(Control parent)
    {
        if (!_defaultCursors.ContainsKey(parent)) _defaultCursors[parent] = parent.Cursor;
        parent.Cursor = _customCursor ?? _defaultCursors[parent];
        foreach (Control child in parent.Controls)
        {
            ApplyCursorTree(child);
        }
    }

    void ForgetCursorTree(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            ForgetCursorTree(child);
        }
        _defaultCursors.Remove(parent);
    }

    SettingsForm? _settings;

    async void OpenSettings(string? page = null)
    {
        if (_settings is { IsDisposed: false })
        {
            if (page is not null) _settings.ShowPage(page);
            _settings.Activate();
            return;
        }
        using var dlg = new SettingsForm(_privateBrowsing, ClearSiteDataAsync, this,
            UpdateRecommendedBlocklistAsync, RemoveRecommendedBlocklist, CheckForUpdatesNowAsync);
        _settings = dlg;
        if (page is not null) dlg.ShowPage(page);
        dlg.ShowDialog(this);
        _settings = null;
        try { await ApplySettingsAsync(); }
        catch (Exception ex) { if (!_closing && !IsDisposed) MessageBox.Show(ex.Message, App.Name); }
    }

    async Task<string> CheckForUpdatesNowAsync()
    {
        if (_privateBrowsing) return "Updates are not checked from private windows.";
        return await AutoUpdater.CheckNowAsync();
    }

    async Task<int> UpdateRecommendedBlocklistAsync()
    {
        var count = await PrivacyRules.UpdateRecommendedBlocklistAsync();
        RefreshPrivacyRuleCache();
        return count;
    }

    void RemoveRecommendedBlocklist()
    {
        PrivacyRules.ClearDownloadedBlocklist();
        RefreshPrivacyRuleCache();
    }

    static bool RuntimeSupportsExtensions()
    {
        // WebView2 browser extensions became available in the stable runtime at 120.0.2210.55.
        // Older installed runtimes keep extension support off rather than failing browser startup.
        try
        {
            var versionText = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var versionToken = versionText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return Version.TryParse(versionToken, out var version) && version >= new Version(120, 0, 2210, 55);
        }
        catch { return false; }
    }



    void SaveSession()
    {
        if (_privateBrowsing || _detached || _restoring) return;
        if (!Config.Current.RestoreLastSession)
        {
            Config.Current.LastSessionTabs = [];
            Config.Current.LastActiveTabIndex = 0;
            return;
        }

        // "" restores as the new-tab page
        var tabs = _open.Select(t => t.IsHomePage ? "" : TabUrl(t) ?? "").ToList();

        const int maxSessionTabs = 50;
        var firstSaved = Math.Max(0, tabs.Count - maxSessionTabs);
        Config.Current.LastSessionTabs = tabs.Skip(firstSaved).ToList();
        Config.Current.LastActiveTabIndex = Math.Clamp(_tabs.SelectedIndex - firstSaved, 0,
            Math.Max(0, Config.Current.LastSessionTabs.Count - 1));
    }

    Task ClearSiteDataAsync() => Current?.View.CoreWebView2 is { } core
        ? core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite)
        : Task.CompletedTask;

    async Task ApplySettingsAsync()
    {
        ApplyTheme();
        ApplyNativeCursor();
        RefreshPrivacyRuleCache();
        LayoutTabs();
        // a copy: tabs can open or close while this awaits
        foreach (var t in _open.ToArray())
            if (!t.View.IsDisposed && _open.Contains(t) && t.View.CoreWebView2 is { } core)
            {
                try { t.View.ZoomFactor = Config.Current.ZoomFactor; } catch { }
                core.Profile.PreferredTrackingPreventionLevel = TrackingLevel();
                ApplyPagePreferences(core, t.DefaultUserAgent);
                if (t.IsHomePage) core.Settings.IsScriptEnabled = true;
                ConfigureResourceFiltering(t);
                _ = ApplyPrivacyHeadersAsync(core);
                await ApplyCustomCursorAsync(t);
                if (t.IsHomePage)
                {
                    MapHomeAssets(t);
                    core.NavigateToString(StartPage(t.HomeNonce));
                }
            }
        UpdateShield();
        _idleTimer.Enabled = Config.Current.IdleMinutes > 0;
        if (_privateBrowsing || !Config.Current.AutoUpdateEnabled)
            _updateTimer.Stop();
        else if (!_updateTimer.Enabled)
        {
            _updateTimer.Interval = 30_000;
            _updateTimer.Start();
        }
        RefreshBookmarks();
    }

    void ApplyTheme()
    {
        Theme.Glass(this, backdrop: Theme.GlassStyle, updateGlobalBackdrop: true);
        BackColor = Theme.ChromeBg;
        ForeColor = Theme.Text;
        _content.BackColor = Theme.Content;
        _bar.BackColor = Theme.ChromeBg;
        _bookmarkPanel.BackColor = Theme.ChromeBg;
        _tabsPanel.BackColor = Theme.Strip;
        _tabsToggle.BackColor = Theme.Strip;
        _marks.BackColor = Theme.ChromeBg;
        _omni.ApplyTheme();
        Theme.ResetGlyphCache();
        BuildMainMenu();
        Theme.Style(_extensionsMenu);
        foreach (var t in _open) t.View.DefaultBackgroundColor = Theme.Content;
        Application.SetColorMode(Theme.DarkMode ? SystemColorMode.Dark : SystemColorMode.System);
        LayoutBookmarks();
        Invalidate(true);
        _tabs.Invalidate();
    }

    public static void ApplyPagePreferences(CoreWebView2 core, string defaultUserAgent)
    {
        core.Settings.IsScriptEnabled = Config.Current.JavaScriptEnabled;
        var userAgent = Config.Current.UserAgent.Trim();
        try { core.Settings.UserAgent = userAgent.Length == 0 ? defaultUserAgent : userAgent; }
        catch { try { core.Settings.UserAgent = defaultUserAgent; } catch { } }
    }

    static void ConfigureResourceFiltering(Tab tab)
    {
        if (tab.View.CoreWebView2 is not { } core) return;
        var shouldFilter = !Config.Current.ImagesEnabled || Config.Current.BuiltInBlockingEnabled;
        if (tab.ResourceFilterInstalled == shouldFilter) return;
        const string allHttp = "*://*/*";
        if (shouldFilter)
            core.AddWebResourceRequestedFilter(allHttp, CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
        else
            core.RemoveWebResourceRequestedFilter(allHttp, CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
        tab.ResourceFilterInstalled = shouldFilter;
    }

    static readonly string[] BuiltInTrackerDomains =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "google-analytics.com",
        "analytics.google.com", "googletagmanager.com", "adservice.google.com", "facebook.net",
        "analytics.tiktok.com", "hotjar.com", "clarity.ms", "segment.io", "segment.com",
        "fullstory.com", "mixpanel.com", "amplitude.com", "inspectlet.com", "mouseflow.com",
        "logrocket.io", "newrelic.com", "nr-data.net", "quantserve.com", "scorecardresearch.com",
        "outbrain.com", "taboola.com", "adnxs.com", "criteo.com", "criteo.net", "pubmatic.com",
        "openx.net", "rubiconproject.com", "indexexchange.com", "advertising.com", "media.net",
        "adform.net", "smartadserver.com", "lijit.com", "bidswitch.net", "casalemedia.com",
        "serving-sys.com", "sharethrough.com", "triplelift.com", "yieldmo.com", "tapad.com",
        "bluekai.com", "demdex.net", "everesttech.net", "omtrdc.net", "2o7.net", "mookie1.com",
        "agkn.com", "adjust.com", "appsflyer.com", "appsflyer.tk", "adroll.com",
        "adzerk.net", "adsrvr.org", "adsymptotic.com", "bounceexchange.com",
        "chartbeat.com", "evergage.com", "extole.com", "flashtalking.com", "heap.io", "heap.com",
        "krxd.net", "lotame.com", "marketo.net", "microsoftadvertising.com", "moatads.com", "optimove.net",
        "pardot.com", "parsely.com", "permutive.com", "quantummetric.com",
        "rfihub.com", "rlcdn.com", "siftscience.com", "siteimproveanalytics.io", "tealiumiq.com",
        "userzoom.com", "veinteractive.com", "webengage.com", "zemanta.com",
    ];

    static HashSet<string> _requestBlockDomains = new(BuiltInTrackerDomains, StringComparer.OrdinalIgnoreCase);
    static HashSet<string> _allowedDomainSet = new(StringComparer.OrdinalIgnoreCase);

    static void RefreshPrivacyRuleCache()
    {
        _requestBlockDomains = [.. BuiltInTrackerDomains.Concat(PrivacyRules.DownloadedDomains)
            .Concat(Config.Current.CustomBlockedDomains).Distinct(StringComparer.OrdinalIgnoreCase)];
        _allowedDomainSet = new HashSet<string>(Config.Current.AllowedDomains, StringComparer.OrdinalIgnoreCase);
    }

    static bool DomainMatches(string host, HashSet<string> domains)
    {
        if (domains.Contains(host)) return true;
        for (var dot = host.IndexOf('.'); dot >= 0; dot = host.IndexOf('.', dot + 1))
            if (domains.Contains(host[(dot + 1)..])) return true;
        return false;
    }

    // The new-tab background (image or video) is answered here, not through WebView2's folder mapping:
    // with the mapping, a background video never started (readyState 0, stuck loading) because video
    // players fetch byte ranges. This answers Range requests with 206 and the right Content-Type.
    static bool TryServeHomeAsset(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals(App.HomeBackgroundHost, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var path = Path.Combine(App.HomeBackgroundDirectory, name);
            if (!App.IsHomeBackgroundName(name) || !File.Exists(path))
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return true;
            }
            var type = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm", ".png" => "image/png", _ => "image/jpeg",
            };
            long length = new FileInfo(path).Length, start = 0, end = length - 1;
            var partial = false;
            var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : "";
            var m = System.Text.RegularExpressions.Regex.Match(range, @"bytes=(\d*)-(\d*)");
            if (m.Success && length > 0)
            {
                partial = true;
                if (m.Groups[1].Value.Length > 0) start = Math.Min(long.Parse(m.Groups[1].Value), length - 1);
                if (m.Groups[2].Value.Length > 0) end = Math.Min(long.Parse(m.Groups[2].Value), length - 1);
                else if (m.Groups[1].Value.Length == 0 && m.Groups[2].Value.Length > 0) { start = Math.Max(0, length - long.Parse(m.Groups[2].Value)); end = length - 1; }
                end = Math.Min(end, start + 8 * 1024 * 1024 - 1); // at most 8 MB per answer; the player asks for the rest
            }
            var count = (int)Math.Max(0, end - start + 1);
            var data = new byte[count];
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                f.Seek(start, SeekOrigin.Begin);
                var read = 0; while (read < count) { var n = f.Read(data, read, count - read); if (n <= 0) break; read += n; }
            }
            var headers = $"Content-Type: {type}\r\nAccept-Ranges: bytes\r\nContent-Length: {count}\r\nCache-Control: no-store";
            if (partial) headers += $"\r\nContent-Range: bytes {start}-{start + count - 1}/{length}";
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(data, false), partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
        }
        catch { e.Response = core.Environment.CreateWebResourceResponse(null, 500, "Error", ""); }
        return true;
    }

    static void HandleWebResourceRequest(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (TryServeHomeAsset(core, e)) return;
        var blockedImage = e.ResourceContext == CoreWebView2WebResourceContext.Image && !Config.Current.ImagesEnabled;
        var blockedTracker = false;
        if (Config.Current.BuiltInBlockingEnabled && TryGetHttpHost(e.Request.Uri, out var host))
            blockedTracker = !DomainMatches(host, _allowedDomainSet) && DomainMatches(host, _requestBlockDomains);
        if (!blockedImage && !blockedTracker) return;
        try
        {
            e.Response = core.Environment.CreateWebResourceResponse(
                null, 204, blockedImage ? "Images disabled by browser settings" : "Blocked by browser privacy settings",
                "Content-Length: 0");
        }
        catch { }
    }

    static bool TryGetHttpHost(string url, out string host)
    {
        host = "";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        host = uri.IdnHost;
        return true;
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

    async Task<Tab?> NewTabAsync(string? url, string? html = null, bool home = false)
    {
        if (_closing || IsDisposed || _env is null) return null;

        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Content, Visible = false };
        var tab = new Tab
        {
            View = view, Strip = new StripTab(), IsHomePage = home || (url is null && html is null),
            HasHomePage = home || (url is null && html is null),
            HomeNonce = Guid.NewGuid().ToString("N"),
        };
        _content.Controls.Add(view);
        if (_customCursor is not null) ApplyCursorTree(view);
        _open.Add(tab);
        _tabs.Tabs.Add(tab.Strip);
        _tabs.RefreshLayout();

        try
        {
            await view.EnsureCoreWebView2Async(_env);
            view.ZoomFactor = Config.Current.ZoomFactor;
        }
        catch (Exception ex)
        {
            if (_open.Remove(tab) && !IsDisposed) _tabs.Tabs.Remove(tab.Strip);
            ForgetCursorTree(view);
            if (!view.IsDisposed) view.Dispose();
            if (!_closing && !IsDisposed)
                MessageBox.Show(ex.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }

        if (_closing || IsDisposed || view.IsDisposed || !_open.Contains(tab))
        {
            if (!view.IsDisposed) ForgetCursorTree(view);
            if (!view.IsDisposed) view.Dispose();
            if (_open.Remove(tab) && !IsDisposed) _tabs.Tabs.Remove(tab.Strip);
            return null;
        }

        try { await WireAsync(tab); }
        catch (Exception ex)
        {
            if (_open.Remove(tab) && !IsDisposed) _tabs.Tabs.Remove(tab.Strip);
            ForgetCursorTree(view);
            if (!view.IsDisposed) view.Dispose();
            if (!_closing && !IsDisposed)
                MessageBox.Show($"Could not initialize a tab.\n\n{ex.Message}", App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        if (_closing || IsDisposed || view.IsDisposed || !_open.Contains(tab))
        {
            if (!view.IsDisposed) view.Dispose();
            return null;
        }
        _tabs.Select(_open.IndexOf(tab));
        ShowActiveTab();

        // html == "" means "leave it alone": NewWindowRequested navigates the view itself.
        if (tab.IsHomePage)
        {
            view.CoreWebView2!.Settings.IsScriptEnabled = true;
            MapHomeAssets(tab);
        }
        if (html is { Length: > 0 }) view.CoreWebView2!.NavigateToString(html);
        else if (url is not null) Navigate(tab, url);
        else if (html is null) view.CoreWebView2!.NavigateToString(StartPage(tab.HomeNonce));
        return tab;
    }

    static void Navigate(Tab tab, string url)
    {
        try { tab.View.CoreWebView2!.Navigate(url); }
        catch { try { tab.View.CoreWebView2?.Navigate("about:blank"); } catch { } }
    }

    static void MapHomeAssets(Tab tab)
    {
        // The background is served by TryServeHomeAsset now; only an old folder mapping is removed here.
        if (tab.View.CoreWebView2 is not { } core) return;
        if (tab.HomeAssetsMapped)
        {
            try { core.ClearVirtualHostNameToFolderMapping(App.HomeBackgroundHost); } catch { }
            tab.HomeAssetsMapped = false;
        }
    }

    async Task WireAsync(Tab tab)
    {
        var core = tab.View.CoreWebView2!;

        // A focused page owns the keyboard: ProcessCmdKey never sees Ctrl+H. The wrapper re-raises the
        // accelerators the engine saw as plain KeyDown on the control, and Handled suppresses whatever
        // the engine would have done with them (Ctrl+P's print dialog, and so on).
        // The shortcut runs after this event returns: Ctrl+W would otherwise dispose this WebView2 while its
        // own key event is still on the stack.
        tab.View.KeyDown += (_, e) =>
        {
            if (e.KeyData == (Keys.Control | Keys.W))
            {
                e.Handled = e.SuppressKeyPress = true;
                BeginInvoke(() => CloseTab(_open.IndexOf(tab)));
                return;
            }
            if (Shortcut(e.KeyData)) e.Handled = e.SuppressKeyPress = true;
        };

        core.Profile.PreferredTrackingPreventionLevel = TrackingLevel();
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        tab.DefaultUserAgent = core.Settings.UserAgent;
        ApplyPagePreferences(core, tab.DefaultUserAgent);
        core.WebResourceRequested += (_, e) => HandleWebResourceRequest(core, e);
        // the new-tab background is always answered by TryServeHomeAsset, whatever the blocking settings
        core.AddWebResourceRequestedFilter($"https://{App.HomeBackgroundHost}/*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        ConfigureResourceFiltering(tab);
        core.WebMessageReceived += (_, e) => SaveDashboardMessage(tab, e);

        await ApplyPrivacyHeadersAsync(core);
        await ApplyCustomCursorAsync(tab);

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Strip.Title = core.DocumentTitle.Length > 0 ? core.DocumentTitle : "New tab";
            _tabs.Invalidate();
            if (!_privateBrowsing) History.Touch(core.Source, tab.Strip.Title);
            if (tab == Current) SetWindowTitle(tab.Strip.Title);
        };

        core.SourceChanged += (_, _) => { if (tab == Current) { ShowUrl(core.Source); UpdateStar(); UpdateStoreButton(); } };
        core.HistoryChanged += (_, _) => { if (tab == Current) SyncNavButtons(core); };
        core.FaviconChanged += async (_, _) => await LoadFaviconAsync(tab);
        // speaker on tabs that play sound; clicking it mutes the site
        void SyncAudio()
        {
            try { (tab.Strip.Audio, tab.Strip.Muted) = (core.IsDocumentPlayingAudio, core.IsMuted); } catch { return; }
            _tabs.Invalidate();
        }
        try { core.IsDocumentPlayingAudioChanged += (_, _) => SyncAudio(); core.IsMutedChanged += (_, _) => SyncAudio(); } catch { }

        core.NavigationStarting += (_, e) =>
        {
            tab.IsLoading = true;
            if (!e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                if (tab.HomeAssetsMapped)
                {
                    try { core.ClearVirtualHostNameToFolderMapping(App.HomeBackgroundHost); } catch { }
                    tab.HomeAssetsMapped = false;
                }
                core.Settings.IsScriptEnabled = Config.Current.JavaScriptEnabled;
            }
            else if (tab.HasHomePage)
            {
                tab.IsHomePage = true;
                core.Settings.IsScriptEnabled = true;
                MapHomeAssets(tab);
            }
            tab.LastActive = DateTime.UtcNow;
            tab.SuspendRetryAfterUtc = DateTime.MinValue;
            if (core.IsSuspended)
                try { core.Resume(); } catch { }
            if (tab == Current) _reload.Glyph = Theme.Stop;
        };
        core.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            tab.IsHomePage = tab.HasHomePage && core.Source.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
            tab.LastActive = DateTime.UtcNow;
            if (tab == Current) _reload.Glyph = Theme.Reload;
            if (e.IsSuccess && !_privateBrowsing) History.Add(core.Source, core.DocumentTitle);
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
            catch { /* the opener went away or the runtime refused the window: the page just gets no popup */ }
            finally { deferral.Complete(); }
        };
    }

    static async Task ApplyPrivacyHeadersAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            var headers = Config.Current.SendPrivacySignals
                ? new Dictionary<string, string> { ["DNT"] = "1", ["Sec-GPC"] = "1" }
                : new Dictionary<string, string>();
            await core.CallDevToolsProtocolMethodAsync("Network.setExtraHTTPHeaders",
                JsonSerializer.Serialize(new { headers }));
        }
        catch { /* some runtime versions restrict privacy headers; blocking remains independent */ }
    }

    void SaveDashboardMessage(Tab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!tab.IsHomePage || e.Source is not ("about:blank" or "about:srcdoc")) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "dashboard" ||
                !root.TryGetProperty("nonce", out var nonce) || nonce.GetString() != tab.HomeNonce ||
                !root.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return;

            var before = JsonSerializer.Serialize(new { _dashboardNotes, _dashboardTasks, Config.Current.DashboardWidgets });
            _dashboardNotes = notes.GetString() is { Length: <= 4000 } note ? note : "";
            _dashboardTasks.Clear();
            foreach (var item in tasks.EnumerateArray().Take(40))
            {
                if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;
                var value = text.GetString()?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                var done = item.TryGetProperty("done", out var completed) && completed.ValueKind == JsonValueKind.True;
                _dashboardTasks.Add(new DashboardTask(value.Length > 140 ? value[..140] : value, done));
            }
            if (root.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
            {
                var allowed = new HashSet<string>(["clock", "quicklinks", "notes", "tasks", "privacy"], StringComparer.Ordinal);
                Config.Current.DashboardWidgets = widgets.EnumerateArray()
                    .Where(w => w.ValueKind == JsonValueKind.String)
                    .Select(w => w.GetString()!)
                    .Where(allowed.Contains).Distinct(StringComparer.Ordinal).ToList();
            }
            if (!_privateBrowsing)
            {
                Config.Current.DashboardNotes = _dashboardNotes;
                Config.Current.DashboardTasks = [.. _dashboardTasks];
                if (before != JsonSerializer.Serialize(new { _dashboardNotes, _dashboardTasks, Config.Current.DashboardWidgets }))
                {
                    _dashboardSaveTimer.Stop();
                    _dashboardSaveTimer.Start();
                    BroadcastDashboardState(tab);
                }
            }
        }
        catch { /* malformed page messages cannot interrupt navigation */ }
    }

    void BroadcastDashboardState(Tab source)
    {
        var json = JsonSerializer.Serialize(new
        {
            kind = "dashboard-state", notes = _dashboardNotes, tasks = _dashboardTasks,
            widgets = Config.Current.DashboardWidgets,
        });
        foreach (var tab in _open)
        {
            if (tab == source || !tab.IsHomePage || tab.View.CoreWebView2 is not { } core) continue;
            try { core.PostWebMessageAsJson(json); } catch { }
        }
    }

    async Task ApplyCustomCursorAsync(Tab tab)
    {
        if (tab.View.CoreWebView2 is not { } core) return;
        var dataUri = App.CustomCursorDataUri;
        var shouldRun = Config.Current.JavaScriptEnabled || tab.IsHomePage;
        var key = dataUri + (shouldRun ? "|script" : "|off");
        var css = dataUri.Length == 0 ? "" : $"*,*:before,*:after{{cursor:url('{dataUri}') 0 0,auto!important}}";
        var script = "(()=>{const apply=()=>{if(!document.documentElement)return false;let s=document.getElementById('baka-custom-cursor');if(!s){s=document.createElement('style');s.id='baka-custom-cursor';document.documentElement.appendChild(s)}s.textContent=" +
            JsonSerializer.Serialize(css) + ";return true};if(!apply()){const o=new MutationObserver(()=>{if(apply())o.disconnect()});o.observe(document,{childList:true,subtree:true})}})();";
        try
        {
            if (tab.CursorScriptKey != key)
            {
                if (tab.CursorScriptId.Length > 0) core.RemoveScriptToExecuteOnDocumentCreated(tab.CursorScriptId);
                tab.CursorScriptId = shouldRun ? await core.AddScriptToExecuteOnDocumentCreatedAsync(script) : "";
                tab.CursorScriptKey = key;
            }
            if (shouldRun) await core.ExecuteScriptAsync(script);
        }
        catch { }
    }

    void SetWindowTitle(string title) => Text = _privateBrowsing
        ? $"{title} — Private — {App.Name}" : $"{title} — {App.Name}";

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
        if (_closing || IsDisposed || !_open.Contains(tab)) { next?.Dispose(); return; }
        if (tab.Strip.Icon == next) return;
        tab.Strip.Icon?.Dispose();
        tab.Strip.Icon = next;
        _tabs.Invalidate();
        // Remembered by host so the bookmarks rail can show the site's icon (regular windows only).
        if (next is not null && !_privateBrowsing && tab.View.CoreWebView2?.Source is { } source)
        {
            SiteIcons.Save(source, next);
            if (Bookmarks.All.Any(b => Uri.TryCreate(b.Url, UriKind.Absolute, out var bu) &&
                    Uri.TryCreate(source, UriKind.Absolute, out var su) && bu.Host.Equals(su.Host, StringComparison.OrdinalIgnoreCase)))
                LayoutBookmarks();
        }
    }

    void ShowUrl(string url)
    {
        if (_omni.Input.Focused) return;                                   // don't fight the user's typing
        // a new tab page is a new tab page, whoever renders it: no plumbing URL in the address bar
        var blank = url is "about:blank" or "";
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
            t.View.Visible = active;
            // Background tabs ask the engine to trim memory (caches, decoded images); the visible one runs normally.
            if (t.View.CoreWebView2 is { } levelCore)
            {
                try
                {
                    // a background tab that is playing music or video keeps normal memory, like it is never put to sleep
                    var level = active || levelCore.IsDocumentPlayingAudio
                        ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
                    if (levelCore.MemoryUsageTargetLevel != level) levelCore.MemoryUsageTargetLevel = level;
                }
                catch { }
            }
            if (!active) continue;
            t.SuspendRetryAfterUtc = DateTime.MinValue;
            if (t.View.CoreWebView2 is { } activeCore && activeCore.IsSuspended)
                try { activeCore.Resume(); } catch { }
            t.View.BringToFront();
            t.LastActive = DateTime.UtcNow;
            if (t.View.CoreWebView2 is { } core)
            {
                ShowUrl(core.Source);
                SyncNavButtons(core);
                UpdateStar();
                UpdateStoreButton();
                SetWindowTitle(t.Strip.Title);
            }
        }
    }

    /// `remember` puts the page on the reopen-closed-tab list (not for a tab that was moved to another window).
    void CloseTab(int index) => CloseTab(index, remember: true);

    void CloseTab(int index, bool remember)
    {
        if (index < 0 || index >= _open.Count) return;
        var tab = _open[index];
        if (remember && !tab.IsHomePage && TabUrl(tab) is { } closedUrl)
        {
            _closedTabs.Add(closedUrl);
            if (_closedTabs.Count > 25) _closedTabs.RemoveAt(0);
        }
        // Closing a background tab keeps the tab you are looking at; closing the active one selects its neighbour.
        var selected = _tabs.SelectedIndex;
        _open.RemoveAt(index);
        _tabs.Tabs.RemoveAt(index);
        _tabs.RefreshLayout();
        tab.Strip.Icon?.Dispose();
        ForgetCursorTree(tab.View);
        tab.View.Dispose();
        if (_open.Count == 0) { Close(); return; }
        _tabs.Removed(index < selected ? selected - 1 : index == selected ? Math.Min(index, _open.Count - 1) : selected);
    }

    async Task SuspendIdleTabsAsync()
    {
        if (_closing || IsDisposed || _suspensionSweep) return;
        _suspensionSweep = true;
        try
        {
            var minutes = Config.Current.IdleMinutes;
            if (minutes <= 0) return;
            foreach (var t in _open.ToArray())
            {
                if (_closing || IsDisposed) return;
                if (t == Current || t.View.IsDisposed || t.View.Visible) continue;
                if ((DateTime.UtcNow - t.LastActive).TotalMinutes < minutes) continue;
                if (t.SuspendRetryAfterUtc > DateTime.UtcNow) continue;
                try
                {
                    var core = t.View.CoreWebView2;
                    if (core is null) continue;
                    if (t.IsLoading || core.IsSuspended || core.IsDocumentPlayingAudio) continue;
                    var suspended = await core.TrySuspendAsync();
                    if (_closing || IsDisposed || t.View.IsDisposed || !_open.Contains(t)) continue;
                    // A user can select the tab while the suspend request is in flight. Visibility is
                    // authoritative here: WebView2 resumes automatically when shown, and an explicit
                    // Resume handles the short race where suspension completed just after selection.
                    if (t == Current || t.View.Visible)
                    {
                        if (core.IsSuspended) try { core.Resume(); } catch { }
                        t.SuspendRetryAfterUtc = DateTime.MinValue;
                    }
                    else if (!suspended || !core.IsSuspended)
                        t.SuspendRetryAfterUtc = DateTime.UtcNow.AddMinutes(5);
                    else
                        t.SuspendRetryAfterUtc = DateTime.MinValue;
                }
                catch
                {
                    if (!t.View.IsDisposed && _open.Contains(t))
                        t.SuspendRetryAfterUtc = DateTime.UtcNow.AddMinutes(5);
                }
            }
        }
        finally { _suspensionSweep = false; }
    }

    // ---------- keyboard ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        Shortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

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
        if (_trueFullscreen)
        {
            base.WndProc(ref m);
            return;
        }

        // With tabs on the left, use the normal Windows caption. The compact top-tab mode retains the
        // custom caption controls and snap-layout hit testing below.
        if (_tabs.Vertical)
        {
            base.WndProc(ref m);
            return;
        }

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
            case Keys.F11:
                if (_trueFullscreen) ExitTrueFullscreen(); else EnterTrueFullscreen();
                return true;
            case Keys.Alt | Keys.F: ShowMainMenu(); return true;
            case Keys.Control | Keys.P: PrintPage(); return true;
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Shift | Keys.Oemplus:
            case Keys.Control | Keys.Add: SetZoom(Config.Current.ZoomFactor + 0.1); return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract: SetZoom(Config.Current.ZoomFactor - 0.1); return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0: SetZoom(1.0); return true;
            case Keys.Control | Keys.Shift | Keys.E when _extensionsAvailable: _ = ShowExtensionsMenuAsync(); return true;
            case Keys.Escape when _trueFullscreen:
                ExitTrueFullscreen();
                return true;
            case Keys.Control | Keys.T: _ = NewTabAsync(null); return true;
            case Keys.Control | Keys.Shift | Keys.T: ReopenClosedTab(); return true;
            case Keys.Control | Keys.D1: case Keys.Control | Keys.D2: case Keys.Control | Keys.D3:
            case Keys.Control | Keys.D4: case Keys.Control | Keys.D5: case Keys.Control | Keys.D6:
            case Keys.Control | Keys.D7: case Keys.Control | Keys.D8:
                if (_open.Count > 0) _tabs.Select(Math.Min(_open.Count - 1, (int)(keyData & Keys.KeyCode) - (int)Keys.D1));
                return true;
            case Keys.Control | Keys.D9: if (_open.Count > 0) _tabs.Select(_open.Count - 1); return true;
            case Keys.Control | Keys.Shift | Keys.N: OpenPrivateWindow(); return true;
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
            case Keys.Control | Keys.H:
                _ = NewTabAsync(null, _privateBrowsing ? PrivateHistoryPage() : HistoryPage());
                return true;
            case Keys.Control | Keys.Shift | Keys.B:
                Config.Current.ShowBookmarksBar = !Config.Current.ShowBookmarksBar;
                if (!_privateBrowsing) Config.Current.Save();
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

    void EnterTrueFullscreen()
    {
        if (_trueFullscreen || _closing) return;
        _savedBounds = Bounds;
        _savedWindowState = WindowState;
        _savedBorderStyle = FormBorderStyle;
        var monitor = Screen.FromControl(this).Bounds;

        _trueFullscreen = true;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        _tabsPanel.Visible = _bar.Visible = _bookmarkPanel.Visible = false;
        Bounds = monitor; // Includes the taskbar area; the borderless window covers the monitor.
        _content.BackColor = Theme.Content;
    }

    void KeepBrowserControlsVisible()
    {
        ExitTrueFullscreen();
        _tabsPanel.Visible = _bar.Visible = true;
        LayoutTabs();
        LayoutBookmarks();
        WindowState = FormWindowState.Maximized;
        _content.BackColor = Theme.Content; // Dock.Fill gives the active tab the remaining client area.
    }

    void ExitTrueFullscreen()
    {
        if (!_trueFullscreen) return;
        _trueFullscreen = false;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = _savedBorderStyle;
        var restoreBounds = _savedBounds;
        if (!Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(restoreBounds)))
            restoreBounds = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(100, 100, Theme.S(1180), Theme.S(760));
        Bounds = restoreBounds;
        WindowState = _savedWindowState;
        _tabsPanel.Visible = _bar.Visible = true;
        LayoutTabs();
        LayoutBookmarks();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _idleTimer.Dispose();
            _updateTimer.Dispose();
            _dashboardSaveTimer.Dispose();
            _mainMenu.Dispose();
            _extensionsMenu.Dispose();
            _tabMenu.Dispose();
            LastDetachedWindow?.Dispose();
            _popup?.Dispose();
            foreach (var icon in _extensionIcons.Values) icon.Dispose();
        }
        base.Dispose(disposing);
        // the private profile folder is deleted at process exit (DeletePrivateProfileOnExit), once WebView2 has let go of it
        if (disposing) _customCursor?.Dispose();
    }

    void OpenPrivateWindow()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            Process.Start(new ProcessStartInfo(exe, "--private") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show($"Could not open a private window.\n\n{ex.Message}", App.Name); }
    }

    static void DeletePrivateProfileOnExit(string path)
    {
        if (!IsPrivateProfilePath(path)) return;
        var fullPath = Path.GetFullPath(path);
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try { if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true); return; }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }

    static bool IsPrivateProfilePath(string path)
    {
        // Only delete a GUID-named directory made directly under the OS temp folder by this feature.
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);
        return fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("baka-private-", StringComparison.Ordinal) &&
            Guid.TryParseExact(name["baka-private-".Length..], "N", out _);
    }

    // ---------- pages ----------

    string StartPage(string nonce)
    {
        static string Css(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var mode = Theme.DarkMode ? "dark" : "light";
        var logo = App.Logo;
        var background = App.HomeBackgroundUrl;
        var video = background.Length > 0 && App.HomeBackgroundIsVideo;
        var liquidGlass = Theme.GlassStyle;
        var backdrop = background.Length == 0 || video
            ? $"background:radial-gradient(ellipse at 50% 10%,{Css(Theme.Fill2)},{Css(Theme.Content)} 68%);"
            : $"background-image:linear-gradient(#0003,#0005),url('{background}');background-size:cover;background-position:center;";
        var videoLayer = video
            ? $"<div class=wallpaper aria-hidden=true><video src=\"{Esc(background)}\" autoplay muted loop playsinline preload=none></video></div>"
            : "";
        var cardSurface = video || !liquidGlass ? Css(Theme.Fill1) : $"{Css(Theme.Fill1)}c8";
        var cardFilter = video || !liquidGlass ? "" : "backdrop-filter:blur(18px) saturate(145%);";
        var cardGlass = liquidGlass
            ? "background-image:linear-gradient(145deg,#ffffff12,transparent 58%);box-shadow:0 18px 48px #0003,inset 0 1px 0 #ffffff24;"
            : "box-shadow:0 8px 22px #0002;";
        var searchGlass = liquidGlass && !video ? "backdrop-filter:blur(14px) saturate(135%);" : "";
        var menuSurface = video || !liquidGlass ? Css(Theme.Content) : $"{Css(Theme.Fill1)}f2";
        var menuGlass = liquidGlass && !video ? "backdrop-filter:blur(16px) saturate(140%);" : "";
        var cursor = App.CustomCursorDataUri;
        var cursorCss = cursor.Length == 0 ? "" : $"*,*:before,*:after{{cursor:url('{cursor}') 0 0,auto!important}}";
        var privateBadge = _privateBrowsing ? "<span class=private>PRIVATE</span>" : "";
        var links = new System.Text.StringBuilder();
        foreach (var mark in Bookmarks.All.Take(20))
            if (Uri.TryCreate(mark.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                links.Append($"<a class=link href=\"{Esc(mark.Url)}\" title=\"{Esc(mark.Title)}\"><span class=fav>{FaviconTile(mark)}</span><span class=t>{Esc(mark.Title.Length > 22 ? mark.Title[..21] + "…" : mark.Title)}</span></a>");
        if (links.Length == 0) links.Append("<span class=empty>Bookmark a page to add a quick link.</span>");
        var enabledWidgets = Config.Current.DashboardWidgets;
        string WidgetVisibility(string id) => enabledWidgets.Contains(id, StringComparer.Ordinal) ? "" : "hidden";
        var widgetOptions = string.Join("", new[]
        {
            ("clock", "Clock and date"), ("quicklinks", "Bookmark shortcuts"), ("notes", "Notes"),
            ("tasks", "Task list"), ("privacy", "Privacy status"),
        }.Select(w => $"<label><input type=checkbox data-widget=\"{w.Item1}\" {(enabledWidgets.Contains(w.Item1, StringComparer.Ordinal) ? "checked" : "")}>{w.Item2}</label>"));
        var state = JsonSerializer.Serialize(new { notes = _dashboardNotes, tasks = _dashboardTasks, widgets = enabledWidgets });
        var signals = Config.Current.SendPrivacySignals ? "privacy signals enabled when supported" : "privacy signals off";
        var privateText = _privateBrowsing ? "Private window · this session is not added to history" :
            $"Host blocking {(Config.Current.BuiltInBlockingEnabled ? "on" : "off")} · {signals}";
        var script = """
            <script>
            const T=__SEARCH__,nonce=__NONCE__,state=__STATE__;
            const noteBox=document.getElementById('notes'),list=document.getElementById('tasks'),taskInput=document.getElementById('newtask');
            noteBox.value=state.notes||'';
            function snapshotTasks(){return Array.from(list.children).map(row=>({text:row.querySelector('span').textContent,done:row.querySelector('input').checked}))}
            function save(){try{chrome.webview.postMessage({kind:'dashboard',nonce,notes:noteBox.value,tasks:snapshotTasks(),widgets:state.widgets||[]})}catch{}}
            function applyWidgets(){for(const id of ['clock','quicklinks','notes','tasks','privacy']){const card=document.getElementById('widget-'+id);if(card)card.hidden=!(state.widgets||[]).includes(id);const clock=document.getElementById('widget-clock');if(clock)clock.hidden=!(state.widgets||[]).includes('clock');const box=document.querySelector('[data-widget="'+id+'"]');if(box)box.checked=(state.widgets||[]).includes(id)}}
            function setWidget(id,on){state.widgets=state.widgets||[];state.widgets=on?[...new Set([...state.widgets,id])]:state.widgets.filter(x=>x!==id);applyWidgets();save()}
            function drawTasks(){list.replaceChildren();for(const item of (state.tasks||[])){const text=item.text??item.Text,done=item.done??item.Done;const row=document.createElement('li');row.className='task'+(done?' done':'');
              const check=document.createElement('input');check.type='checkbox';check.checked=!!done;const label=document.createElement('span');label.textContent=text;
              const remove=document.createElement('button');remove.type='button';remove.className='remove';remove.textContent='×';check.onchange=()=>{state.tasks=Array.from(list.children).map(r=>({text:r.querySelector('span').textContent,done:r.querySelector('input').checked}));drawTasks();save()};
              remove.onclick=()=>{row.remove();state.tasks=Array.from(list.children).map(r=>({text:r.querySelector('span').textContent,done:r.querySelector('input').checked}));save()};row.append(check,label,remove);list.append(row)}}
            function tick(){const d=new Date();document.getElementById('time').textContent=d.toLocaleTimeString([], {hour:'numeric',minute:'2-digit'});document.getElementById('date').textContent=d.toLocaleDateString([], {weekday:'long',month:'long',day:'numeric'})}
            chrome.webview.addEventListener('message',e=>{const m=e.data;if(m?.kind!=='dashboard-state')return;state.notes=m.notes||'';state.tasks=m.tasks||[];state.widgets=m.widgets||state.widgets;noteBox.value=state.notes;drawTasks();applyWidgets()});
            document.querySelectorAll('[data-widget]').forEach(box=>box.onchange=()=>setWidget(box.dataset.widget,box.checked));
            drawTasks();applyWidgets();tick();setInterval(()=>{if(!document.hidden&&!document.getElementById('widget-clock').hidden)tick()},30000);noteBox.oninput=()=>{clearTimeout(window.noteTimer);window.noteTimer=setTimeout(save,750)};noteBox.onchange=save;
            function addTask(){const text=taskInput.value.trim();if(!text||list.children.length>=40)return;state.tasks=[...Array.from(list.children).map(r=>({text:r.querySelector('span').textContent,done:r.querySelector('input').checked})),{text,done:false}];taskInput.value='';drawTasks();save()}
            document.getElementById('addtask').onclick=addTask;taskInput.onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();addTask()}};
            document.getElementById('search').onsubmit=e=>{e.preventDefault();location=T.replace('{q}',encodeURIComponent(document.getElementById('q').value))};
            document.head.append(Object.assign(document.createElement('link'),{rel:'icon',href:document.getElementById('mark').src}));
            const bgVideo=document.querySelector('.wallpaper video');
            document.addEventListener('visibilitychange',()=>{if(!bgVideo)return;if(document.hidden)bgVideo.pause();else bgVideo.play().catch(()=>{})});
            window.addEventListener('pagehide',save);
            </script>
            """;
        script = script.Replace("__SEARCH__", Json(Config.Current.SearchUrl), StringComparison.Ordinal)
            .Replace("__NONCE__", Json(nonce), StringComparison.Ordinal).Replace("__STATE__", state, StringComparison.Ordinal);
        const string IconLink = "<svg viewBox='0 0 24 24' aria-hidden=true><path d='M10 13a5 5 0 0 0 7.07 0l3-3a5 5 0 0 0-7.07-7.07l-1.5 1.5'/><path d='M14 11a5 5 0 0 0-7.07 0l-3 3a5 5 0 0 0 7.07 7.07l1.5-1.5'/></svg>";
        const string IconNote = "<svg viewBox='0 0 24 24' aria-hidden=true><path d='M5 4h10l4 4v12H5z'/><path d='M9 12h6M9 16h4'/></svg>";
        const string IconTasks = "<svg viewBox='0 0 24 24' aria-hidden=true><path d='M9 11l3 3 8-8'/><path d='M20 12v7a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h11'/></svg>";
        const string IconShield = "<svg viewBox='0 0 24 24' aria-hidden=true><path d='M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z'/></svg>";
        const string IconSearch = "<svg viewBox='0 0 24 24' aria-hidden=true><circle cx='11' cy='11' r='7'/><path d='M20 20l-3.5-3.5'/></svg>";
        const string IconSliders = "<svg viewBox='0 0 24 24' aria-hidden=true><path d='M4 6h9M19 6h1M4 12h3M13 12h7M4 18h11M21 18h-1'/><circle cx='16' cy='6' r='2.2'/><circle cx='10' cy='12' r='2.2'/><circle cx='18' cy='18' r='2.2'/></svg>";
        return $$"""
        <!doctype html><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1"><title>New tab</title>
        <style>
        html{color-scheme:{{mode}}}*{box-sizing:border-box}
        body{position:relative;isolation:isolate;margin:0;min-height:100vh;{{backdrop}}background-color:{{Css(Theme.Content)}};color:{{Css(Theme.Text)}};
             font:13px 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif;background-attachment:fixed}
        svg{width:15px;height:15px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round;flex:none}
        .wallpaper{position:fixed;inset:0;z-index:0;overflow:hidden;pointer-events:none}.wallpaper:after{content:"";position:absolute;inset:0;background:linear-gradient(#0005,#0008)}
        .wallpaper video{width:100%;height:100%;object-fit:cover}
        .wrap{position:relative;z-index:1;width:min(920px,92vw);margin:0 auto;padding:7vh 0 6vh}
        header{display:flex;align-items:center;justify-content:space-between;margin-bottom:34px;color:{{Css(Theme.Muted)}}}
        .brand{display:flex;align-items:center;gap:10px;font-weight:600;letter-spacing:.14em;text-transform:uppercase;font-size:12px;color:{{Css(Theme.Text)}}}
        .brand img{width:26px;height:26px;border-radius:8px}
        .tools{display:flex;align-items:center;gap:10px}
        .clock{text-align:right;font-variant-numeric:tabular-nums;line-height:1.25}.clock b{display:block;font-size:20px;color:{{Css(Theme.Text)}};font-weight:600}
        .private{color:#ffc9e8;border:1px solid #a44787;border-radius:99px;padding:4px 10px;font-size:11px;letter-spacing:.08em}
        .search{position:relative;margin:0 auto 28px;max-width:640px}
        .search svg{position:absolute;left:18px;top:50%;transform:translateY(-50%);width:17px;height:17px;color:{{Css(Theme.Muted)}}}
        .search input{width:100%;height:48px;padding:0 20px 0 48px;border-radius:999px;border:1px solid {{Css(Theme.Line)}};background:{{Css(Theme.Fill1)}}{{(liquidGlass && !video ? "e6" : "")}};
          color:{{Css(Theme.Text)}};font-size:15px;font-family:inherit;outline:none;{{searchGlass}}transition:border-color .14s ease,background-color .14s ease}
        .search input:hover{background:{{Css(Theme.Fill2)}}}
        .search input:focus{border-color:{{Css(Theme.Accent)}};background:{{Css(Theme.Fill2)}}}
        input:focus,textarea:focus{border-color:{{Css(Theme.Accent)}}!important}
        .grid{display:grid;grid-template-columns:1.25fr 1fr;gap:12px}.card[hidden]{display:none}
        .card{background:{{cardSurface}};border:1px solid {{Css(Theme.Line)}};border-radius:14px;padding:16px;{{cardFilter}}{{cardGlass}}min-height:150px;transition:border-color .14s ease}
        .card:hover{border-color:{{Css(Theme.Muted)}}55}
        .card h2{display:flex;align-items:center;gap:8px;font-size:11px;font-weight:600;text-transform:uppercase;letter-spacing:.12em;color:{{Css(Theme.Muted)}};margin:0 0 12px}
        .links{display:grid;grid-template-columns:repeat(auto-fill,minmax(88px,1fr));gap:8px}
        .link{display:flex;flex-direction:column;align-items:center;gap:7px;padding:12px 6px 10px;border-radius:12px;color:{{Css(Theme.Text)}};text-decoration:none;min-width:0}
        .link:hover{background:{{Css(Theme.Fill2)}}}
        .fav{width:34px;height:34px;border-radius:10px;background:{{Css(Theme.Fill2)}};border:1px solid {{Css(Theme.Line)}};display:grid;place-items:center;font-weight:600;font-size:14px;color:{{Css(Theme.Text)}};overflow:hidden}
        .fav img{width:18px;height:18px}
        .link .t{max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:12px;color:{{Css(Theme.Muted)}}}.link:hover .t{color:{{Css(Theme.Text)}}}
        .empty{color:{{Css(Theme.Muted)}};grid-column:1/-1}
        .notes{width:100%;height:104px;resize:vertical;border:1px solid {{Css(Theme.Line)}};border-radius:10px;padding:10px;background:{{Css(Theme.Content)}};color:{{Css(Theme.Text)}};font:inherit;outline:none}
        .taskrow{display:flex;gap:8px;margin-top:10px}
        button,.widget-menu summary{display:inline-flex;align-items:center;gap:7px;border:1px solid {{Css(Theme.Line)}};background:{{Css(Theme.Fill1)}};color:{{Css(Theme.Text)}};border-radius:999px;padding:7px 13px;font:inherit;cursor:pointer}
        button:hover,.widget-menu summary:hover{background:{{Css(Theme.Fill2)}}}
        .widget-menu{position:relative}.widget-menu summary{list-style:none}.widget-menu summary::-webkit-details-marker{display:none}
        .widget-options{position:absolute;right:0;top:40px;z-index:5;min-width:200px;padding:8px;background:{{menuSurface}};{{menuGlass}}border:1px solid {{Css(Theme.Line)}};border-radius:12px;box-shadow:0 12px 30px #0006;display:grid;gap:2px}
        .widget-options label{display:flex;align-items:center;gap:9px;white-space:nowrap;color:{{Css(Theme.Text)}};padding:7px 8px;border-radius:8px}.widget-options label:hover{background:{{Css(Theme.Fill2)}}}
        .widget-options input,.task input{accent-color:{{Css(Theme.Accent)}}}
        .taskrow input{flex:1;min-width:0;border:1px solid {{Css(Theme.Line)}};border-radius:999px;padding:8px 13px;background:{{Css(Theme.Content)}};color:{{Css(Theme.Text)}};font:inherit;outline:none}
        ul{list-style:none;padding:0;margin:0;display:grid;gap:6px}.task{display:flex;align-items:center;gap:9px}
        .task span{flex:1;overflow-wrap:anywhere}.task.done span{text-decoration:line-through;color:{{Css(Theme.Muted)}}}.remove{border:0;background:none;color:{{Css(Theme.Muted)}};padding:2px 6px}
        .privacy p{margin:0 0 8px;line-height:1.45}
        footer{text-align:center;color:{{Css(Theme.Muted)}};font-size:11px;margin-top:18px;opacity:.8}
        {{cursorCss}}
        @media(max-width:700px){.grid{grid-template-columns:1fr}.wrap{padding-top:3vh}.card{min-height:unset} }
        @media(prefers-reduced-motion:reduce){*,*:before,*:after{transition:none!important;scroll-behavior:auto!important} }
        </style>
        {{videoLayer}}
        <main class=wrap>
          <header><div class=brand><img id=mark src="{{logo}}" alt=""><span>Baka</span></div>
            <div class=tools>{{privateBadge}}<details class=widget-menu><summary>{{IconSliders}}Widgets</summary><div class=widget-options>{{widgetOptions}}</div></details><div id=widget-clock class=clock {{WidgetVisibility("clock")}}><b id=time></b><span id=date></span></div></div>
          </header>
          <form class=search id=search>{{IconSearch}}<input id=q autofocus autocomplete=off placeholder="Search or enter address" aria-label="Search or enter address"></form>
          <section class=grid>
            <article id=widget-quicklinks class=card {{WidgetVisibility("quicklinks")}}><h2>{{IconLink}}Quick links</h2><div class=links>{{links}}</div></article>
            <article id=widget-notes class=card {{WidgetVisibility("notes")}}><h2>{{IconNote}}Notes</h2><textarea class=notes id=notes maxlength=4000 placeholder="A private scratchpad saved on this device"></textarea></article>
            <article id=widget-tasks class=card {{WidgetVisibility("tasks")}}><h2>{{IconTasks}}Tasks</h2><ul id=tasks></ul><div class=taskrow><input id=newtask maxlength=140 placeholder="Add a task"><button id=addtask type=button>Add</button></div></article>
            <article id=widget-privacy class="card privacy" {{WidgetVisibility("privacy")}}><h2>{{IconShield}}Privacy</h2><p>{{privateText}}</p><p style="color:{{Css(Theme.Muted)}}">No weather calls or dashboard data are sent to a service.</p></article>
          </section>
          <footer>Dashboard data stays in this browser profile. Private windows keep it only for this session.</footer>
        </main>
        {{script}}
        """;
    }

    static string Json(string s) => JsonSerializer.Serialize(s);

    static string PrivateHistoryPage() => """
        <!doctype html><meta charset=utf-8><title>Private browsing</title>
        <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#1c1c1e;color:#f2f2f7;font:16px 'Segoe UI',sans-serif}
        main{max-width:520px;padding:34px;border:1px solid #49404a;border-radius:18px;background:#28242b}h1{font-size:26px}p{color:#c5bdc7;line-height:1.6}</style>
        <main><h1>Private browsing</h1><p>Pages in this window are not written to baka's history. Close the private window to discard its cookies, site storage, and temporary profile.</p></main>
        """;

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

