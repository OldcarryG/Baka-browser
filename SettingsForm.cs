using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Baka;

/// Settings: a navigation rail on the left (one icon and name per section) and one page per section on
/// the right, with related rows grouped into rounded cards. Every control writes straight to
/// Config.Current; the browser applies the result when the window closes.
sealed class SettingsForm : Form
{
    static readonly int NavWidth = Theme.S(200), PagePad = Theme.S(26);

    readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Theme.Content };
    readonly NavRail _nav = new() { Dock = DockStyle.Left, Width = NavWidth };
    readonly List<(string Name, Panel Page)> _pages = [];
    Panel _page = null!;
    CardPanel? _card;
    int _y, _pageY;

    TextBox? _blockedDomainsBox, _allowedDomainsBox;
    CardPanel? _extensionsList;
    readonly bool _privateBrowsing;
    readonly Func<Task>? _clearSiteData;
    readonly IExtensionHost? _extensions;
    readonly Func<Task<int>>? _updateRecommendedList;
    readonly Action? _removeRecommendedList;
    readonly Func<Task<string>>? _checkUpdates;

    public SettingsForm(bool privateBrowsing = false, Func<Task>? clearSiteData = null,
        IExtensionHost? extensions = null, Func<Task<int>>? updateRecommendedList = null,
        Action? removeRecommendedList = null, Func<Task<string>>? checkUpdates = null)
    {
        _privateBrowsing = privateBrowsing;
        _clearSiteData = clearSiteData;
        _extensions = extensions;
        _updateRecommendedList = updateRecommendedList;
        _removeRecommendedList = removeRecommendedList;
        _checkUpdates = checkUpdates;
        Text = "Settings — Baka Browser";
        BackColor = Theme.Content;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ClientSize = new Size(Theme.S(880), Theme.S(620));
        MinimizeBox = MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        KeyPreview = true;
        Controls.Add(_host);
        Controls.Add(_nav);
        _nav.Selected += ShowPage;

        BuildPrivacy();
        BuildAppearance();
        BuildSearch();
        if (!privateBrowsing) BuildExtensions();
        BuildTabsAndBookmarks();
        BuildPerformance();
        BuildCompatibility();
        BuildAdvanced();
        BuildAbout();
        EndPage();
        ShowPage(_pages[0].Name);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        FormClosing += (_, _) => { if (!privateBrowsing) Config.Current.Save(); };
        Shown += (_, _) => Theme.Glass(this, backdrop: false);
    }

    public void ShowPage(string name)
    {
        var index = _pages.FindIndex(p => p.Name == name);
        if (index < 0) return;
        for (var i = 0; i < _pages.Count; i++) _pages[i].Page.Visible = i == index;
        _pages[index].Page.AutoScrollPosition = Point.Empty;
        _nav.Select(index, raise: false);
        if (name == "Extensions") _ = RefreshExtensionsAsync();
    }

    // ---------- pages ----------

    void BuildPrivacy()
    {
        Page("Privacy", Theme.ShieldIcon, "Tracking protection, blocking, and what websites can run.");
        Card("Protection");
        Dropdown("Tracking prevention", ["Strict", "Balanced", "Basic", "Off"],
            Config.Current.TrackingPrevention, v => Config.Current.TrackingPrevention = v);
        Toggle("Block known ad and tracker domains", Config.Current.BuiltInBlockingEnabled,
            v => Config.Current.BuiltInBlockingEnabled = v, "Built-in host rules for common ad and tracking networks");
        Toggle("Send Do Not Track and Global Privacy Control", Config.Current.SendPrivacySignals,
            v => Config.Current.SendPrivacySignals = v);
        Note("Host blocking cannot remove page elements or ads served from a site's own domain. Autofill, password saving, sync, crash reporting and SmartScreen URL reporting are always off.");

        Card("Website content");
        Toggle("JavaScript", Config.Current.JavaScriptEnabled, v => Config.Current.JavaScriptEnabled = v,
            "Off breaks most modern sites; the new-tab page keeps working");
        Toggle("Images", Config.Current.ImagesEnabled, v => Config.Current.ImagesEnabled = v,
            "Off blocks new image requests; images already shown stay until reload");

        Card("Recommended block list");
        Buttons(("Update ad & tracker list", Theme.Sync, () => _ = UpdateRecommendedListAsync()),
            ("Remove downloaded list", Theme.Delete, RemoveRecommendedList));
        Note($"The optional StevenBlack hosts list adds about 70,000 known adware and malware domains. It downloads only when you press Update and is kept on this PC. {(PrivacyRules.DownloadedDomainCount > 0 ? $"Active now: {PrivacyRules.DownloadedDomainCount:N0} domains." : "Not downloaded yet.")}");

        if (!_privateBrowsing)
        {
            Card("Your data");
            Buttons(("Clear site data…", Theme.Delete, () => _ = ClearSiteDataAsync()));
            Note("Removes cookies, local storage and service workers from the regular profile. History and bookmarks stay.");
            Toggle("Encrypt browser data with this Windows account", Config.Current.EncryptProfileAtRest,
                v => Config.Current.EncryptProfileAtRest = v, "Windows EFS, applied at the next launch");
        }

        Card("Custom rules");
        DomainList("Extra blocked domains", Config.Current.CustomBlockedDomains, v => Config.Current.CustomBlockedDomains = v);
        DomainList("Always allowed domains (and their subdomains)", Config.Current.AllowedDomains, v => Config.Current.AllowedDomains = v);
        Buttons(("Import hosts list…", Theme.Folder, ImportHostsList), ("Clear custom rules", Theme.Delete, () =>
        {
            Config.Current.CustomBlockedDomains = [];
            Config.Current.AllowedDomains = [];
            if (_blockedDomainsBox is not null) _blockedDomainsBox.Text = "";
            if (_allowedDomainsBox is not null) _allowedDomainsBox.Text = "";
        }));
        Note($"One domain per line, or a hosts file. Up to {PrivacyRules.CustomLimit:N0} domains.");
    }

    void BuildAppearance()
    {
        Page("Appearance", Theme.Paint, "The browser's look. BAKA matches the BAKA Windows desktop.");
        Card("Theme");
        Dropdown("Theme", ["Baka", "Dark", "Light", "Custom"], Config.Current.ThemeMode, v => Config.Current.ThemeMode = v);
        Toggle("Glass effects", Config.Current.LiquidGlassEnabled, v => Config.Current.LiquidGlassEnabled = v,
            "Windows backdrop and highlights for the Dark and Light themes");
        Buttons(("Accent color…", Theme.Paint, () =>
        {
            using var picker = new ColorDialog { Color = Theme.Accent, FullOpen = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            Config.Current.AccentColor = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        }), ("Custom surface color…", Theme.Picture, ChooseCustomSurface));
        Note("Changes apply when this window closes. A custom surface color switches the theme to Custom.");

        Card("New tab page");
        Buttons(("Choose image or video…", Theme.Picture, ChooseBackground), ("Remove background", Theme.Delete, RemoveBackground));
        Note("PNG or JPEG images and muted MP4 or WebM videos, stored on this PC. Videos pause in background tabs.");

        Card("Cursor");
        Buttons(("Choose cursor…", Theme.Mouse, ChooseCursor), ("Reset cursor", Theme.Sync, ResetCustomCursor));
        Note("Windows .cur files. Page cursors need JavaScript on the site.");
    }

    void BuildSearch()
    {
        Page("Search", Theme.Search, "Where text typed in the address bar goes.");
        Card(null);
        EnginePicker();
        Note("Anything typed in the address bar that isn't a web address is searched here. Edit the address to use any engine; {q} marks the search words.");
    }

    void BuildExtensions()
    {
        Page("Extensions", Theme.Puzzle, "Add extensions from the Chrome Web Store, or load your own.");
        if (_extensions is null || !_extensions.ExtensionsSupported)
        {
            Card(null);
            Note("Extensions need WebView2 Runtime 120 or newer. Update the Microsoft Edge WebView2 Runtime to turn them on.");
            return;
        }
        Card("Get extensions");
        Buttons(("Open Chrome Web Store", Theme.Store, () => { _extensions.OpenWebStore(); Close(); }),
            ("Install unpacked extension…", Theme.Folder, ChooseExtensionFolder));
        Note("On an extension's Web Store page, press Add to Baka on the toolbar. Baka downloads the package from Google, checks its signature, shows what it can access, and installs it after you agree. Extensions can read or change site data within their permissions, so add only ones you trust. Extensions don't update by themselves yet: to update one, remove it and add it again.");
        Card("Installed");
        _extensionsList = _card;
        Note("Loading…");
    }

    void BuildTabsAndBookmarks()
    {
        Page("Tabs & bookmarks", Theme.Tabs, "Where tabs and bookmarks sit, and what reopens at startup.");
        Card("Tabs");
        Dropdown("Tab placement", ["Left", "Top"], Config.Current.TabsPosition, v => Config.Current.TabsPosition = v);
        Toggle("Reopen tabs from last time", Config.Current.RestoreLastSession, v =>
        {
            Config.Current.RestoreLastSession = v;
            if (!v) Config.Current.LastSessionTabs = [];
        }, "Up to 50 tabs; private tabs are never saved");
        Toggle("Start with the tab rail collapsed", Config.Current.TabsCollapsed, v => Config.Current.TabsCollapsed = v);

        Card("Bookmarks");
        Toggle("Show bookmarks", Config.Current.ShowBookmarksBar, v => Config.Current.ShowBookmarksBar = v, "Ctrl+Shift+B");
        Dropdown("Bookmarks position", ["Left", "Top"], Config.Current.BookmarksPosition, v => Config.Current.BookmarksPosition = v);
        Note("Ctrl+D bookmarks the page. On the bookmarks bar, middle-click opens in a new tab and right-click removes.");
    }

    void BuildPerformance()
    {
        Page("Performance", Theme.Lightning, "Memory use and page zoom.");
        Card("Memory");
        Spinner("Sleep hidden tabs after (minutes, 0 = never)", Config.Current.IdleMinutes, v => Config.Current.IdleMinutes = v);
        Note("Hidden tabs that are not loading or playing audio are put to sleep and wake when you select them.");
        Card("Zoom");
        var zooms = new[] { "80%", "90%", "100%", "110%", "125%", "150%" };
        var current = $"{Math.Round(Config.Current.ZoomFactor * 100)}%";
        Dropdown("Page zoom", zooms.Contains(current) ? zooms : [.. zooms, current], current,
            v => Config.Current.ZoomFactor = double.Parse(v.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100);
        Note("Ctrl+plus and Ctrl+minus change it from any page; Ctrl+0 resets.");
    }

    void BuildCompatibility()
    {
        Page("Compatibility", Theme.Globe, "For sites that only work with a specific browser.");
        Card(null);
        TextField("Custom user agent (leave blank for the default)", Config.Current.UserAgent, v => Config.Current.UserAgent = v);
        Note("Applies to every open tab after Settings closes. Some sites serve different content or reject unfamiliar values.");
    }

    void BuildAdvanced()
    {
        Page("Advanced", Theme.Code, "Files and engine options.");
        Card(null);
        Buttons(("Open data folder", Theme.Folder, () => Process.Start(new ProcessStartInfo("explorer.exe", App.DataDir))),
            ("Edit engine flags", Theme.Code, () =>
            {
                Flags.Load();     // recreates flags.txt from defaults if it went missing
                Process.Start(new ProcessStartInfo(App.Path_("flags.txt")) { UseShellExecute = true });
            }));
        if (!_privateBrowsing)
            Buttons(("Clear history", Theme.HistoryIcon, () =>
            {
                if (BakaDialog.Confirm(this, $"Forget all {History.All.Count} pages?", "Bookmarks and site data stay.", [], "Clear history", Theme.HistoryIcon))
                    History.Clear();
            }));
        Note($"Settings, flags and history live in {App.DataDir}. Flag changes need a restart. Ctrl+H opens history, Ctrl+J downloads.");
    }

    void BuildAbout()
    {
        Page("About", Theme.Info, "");
        Card(null);
        var version = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "";
        var head = new Panel { BackColor = Theme.Fill1 };
        var logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(0, Theme.S(4), Theme.S(48), Theme.S(48)) };
        try { logo.Image = App.Icon.ToBitmap(); } catch { }
        head.Controls.Add(logo);
        head.Controls.Add(new Label { Text = "Baka Browser", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = false, Bounds = new Rectangle(Theme.S(62), Theme.S(6), Theme.S(300), Theme.S(24)) });
        string engine;
        try { engine = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { engine = "not installed"; }
        head.Controls.Add(new Label { Text = $"Version {version}  ·  WebView2 {engine}", ForeColor = Theme.Muted, AutoSize = false, Bounds = new Rectangle(Theme.S(62), Theme.S(31), Theme.S(420), Theme.S(20)) });
        Add(head, Theme.S(58), Theme.S(10));
        var status = new Label { Text = "", ForeColor = Theme.Muted, AutoSize = false };
        Buttons(("Check for updates", Theme.Sync, async () =>
        {
            if (_checkUpdates is null) return;
            status.Text = "Checking…";
            status.Text = await _checkUpdates();
        }), ("Release notes", Theme.Info, () => OpenLink("https://github.com/OldcarryG/Baka-browser/releases")),
            ("Source code", Theme.Code, () => OpenLink("https://github.com/OldcarryG/Baka-browser")));
        Add(status, Theme.S(36));
        if (!_privateBrowsing)
            Toggle("Download updates automatically", Config.Current.AutoUpdateEnabled, v => Config.Current.AutoUpdateEnabled = v,
                "Updates install the next time the browser starts");
        Note("Baka Browser is built on Microsoft Edge WebView2. It is part of BAKA Windows and also runs on any Windows 10 or 11 PC.");
    }

    static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ---------- extensions list ----------

    async Task RefreshExtensionsAsync()
    {
        if (_extensions is null || _extensionsList is not { } card || card.IsDisposed) return;
        IReadOnlyList<ExtensionInfo> list;
        try { list = await _extensions.ListExtensionsAsync(); }
        catch (Exception ex) { list = []; SetListMessage(card, $"Could not read installed extensions: {ex.Message}"); return; }
        if (card.IsDisposed) return;
        card.SuspendLayout();
        foreach (Control c in card.Controls.Cast<Control>().ToList()) { card.Controls.Remove(c); c.Dispose(); }
        var width = card.Width - card.Padding.Horizontal;
        var y = card.Padding.Top;
        if (list.Count == 0)
        {
            card.Controls.Add(new Label { Text = "No extensions yet. Open the Chrome Web Store to add one.", ForeColor = Theme.Muted, BackColor = Theme.Fill1, AutoSize = false, Bounds = new Rectangle(card.Padding.Left, y, width, Theme.S(24)) });
            y += Theme.S(24);
        }
        foreach (var ext in list)
        {
            var row = new Panel { BackColor = Theme.Fill1, Bounds = new Rectangle(card.Padding.Left, y, width, Theme.S(48)) };
            var icon = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(0, Theme.S(10), Theme.S(28), Theme.S(28)) };
            try
            {
                if (ext.IconFile is { } file && File.Exists(file)) { using var img = Image.FromFile(file); icon.Image = new Bitmap(img); }
                else icon.Image = Theme.GlyphImage(Theme.Puzzle, Theme.Muted, 14f);
            }
            catch { }
            row.Controls.Add(icon);
            var source = ext.FromWebStore ? "Chrome Web Store" : "Unpacked";
            row.Controls.Add(new Label { Text = ext.Name, ForeColor = Theme.Text, Font = Theme.UiBold, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Theme.S(40), Theme.S(6), width - Theme.S(300), Theme.S(20)) });
            row.Controls.Add(new Label { Text = $"{(ext.Version.Length > 0 ? "Version " + ext.Version + "  ·  " : "")}{source}", ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Theme.S(40), Theme.S(25), width - Theme.S(300), Theme.S(18)) });
            var remove = new PillButton("Remove", Theme.Delete);
            remove.SetBounds(width - remove.PreferredWidth, Theme.S(9), remove.PreferredWidth, Theme.S(30));
            remove.Click += async (_, _) =>
            {
                if (!BakaDialog.Confirm(this, $"Remove “{ext.Name}”?", "Its settings and data in this browser are deleted.", [], "Remove", Theme.Delete)) return;
                try { await _extensions.RemoveExtensionAsync(ext.Id); } catch (Exception ex) { BakaDialog.Info(this, ext.Name, ex.Message); }
                await RefreshExtensionsAsync();
            };
            row.Controls.Add(remove);
            var right = remove.Left - Theme.S(8);
            if (ext.OptionsPage is not null)
            {
                var options = new PillButton("Options", Theme.Settings);
                options.SetBounds(right - options.PreferredWidth, Theme.S(9), options.PreferredWidth, Theme.S(30));
                options.Click += (_, _) => { _extensions.OpenExtensionOptions(ext.Id); Close(); };
                row.Controls.Add(options);
                right = options.Left - Theme.S(8);
            }
            var toggle = new ToggleSwitch("", ext.Enabled) { AccessibleName = $"{ext.Name} enabled" };
            toggle.SetBounds(right - Theme.S(48), Theme.S(7), Theme.S(46), Theme.S(34));
            toggle.CheckedChanged += async (_, _) =>
            {
                try { await _extensions.SetExtensionEnabledAsync(ext.Id, toggle.Checked); }
                catch (Exception ex) { BakaDialog.Info(this, ext.Name, ex.Message); }
            };
            row.Controls.Add(toggle);
            card.Controls.Add(row);
            y += Theme.S(52);
        }
        card.Height = y + card.Padding.Bottom;
        card.ResumeLayout();
    }

    static void SetListMessage(CardPanel card, string text)
    {
        foreach (Control c in card.Controls.Cast<Control>().ToList()) { card.Controls.Remove(c); c.Dispose(); }
        card.Controls.Add(new Label { Text = text, ForeColor = Theme.Muted, BackColor = Theme.Fill1, AutoSize = false, Bounds = new Rectangle(card.Padding.Left, card.Padding.Top, card.Width - card.Padding.Horizontal, Theme.S(40)) });
    }

    void ChooseExtensionFolder()
    {
        if (_extensions is null) return;
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the unpacked extension folder that contains manifest.json.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _ = InstallFolderAsync(picker.SelectedPath);
    }

    async Task InstallFolderAsync(string folder)
    {
        try
        {
            var name = await _extensions!.InstallExtensionFolderAsync(folder);
            BakaDialog.Info(this, $"{name} was added", "Open it from the puzzle button on the toolbar.", Theme.Puzzle);
        }
        catch (Exception ex) { BakaDialog.Info(this, "The extension could not be added", ex.Message); }
        await RefreshExtensionsAsync();
    }

    // ---------- page and card builders ----------

    // From the fixed window size rather than _host, which has no final bounds until the first layout.
    int PageInner => ClientSize.Width - NavWidth - PagePad * 2 - SystemInformation.VerticalScrollBarWidth;
    static readonly Font PageTitleFont = new(Theme.Title.FontFamily, 16f, FontStyle.Bold);
    static readonly Font CaptionFont = new(Theme.UiBold.FontFamily, 7.5f, FontStyle.Bold);
    int Inner => _card is { } c ? c.Width - c.Padding.Horizontal : PageInner;
    Color Surface => _card is null ? Theme.Content : Theme.Fill1;

    void Page(string name, string glyph, string subtitle)
    {
        EndPage();
        _page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Content, Visible = false };
        _host.Controls.Add(_page);
        _pages.Add((name, _page));
        _nav.Add(name, glyph);
        _pageY = Theme.S(22);
        var title = new Label { Text = name, UseMnemonic = false, Font = PageTitleFont, ForeColor = Theme.Text, AutoSize = false, BackColor = Theme.Content };
        title.SetBounds(PagePad, _pageY, PageInner, Theme.S(32));
        _page.Controls.Add(title);
        _pageY += Theme.S(34);
        if (subtitle.Length > 0)
        {
            _page.Controls.Add(new Label { Text = subtitle, ForeColor = Theme.Muted, AutoSize = false, BackColor = Theme.Content, Bounds = new Rectangle(PagePad, _pageY, PageInner, Theme.S(20)) });
            _pageY += Theme.S(24);
        }
        _pageY += Theme.S(8);
        _y = _pageY;
    }

    void EndPage()
    {
        EndCard();
        if (_page is null) return;
        // bottom breathing room inside the scroll area
        _page.Controls.Add(new Panel { BackColor = Theme.Content, Bounds = new Rectangle(PagePad, _pageY, 1, Theme.S(20)) });
    }

    void Card(string? caption)
    {
        EndCard();
        if (caption is not null)
        {
            _page.Controls.Add(new Label { Text = caption.ToUpperInvariant(), Font = CaptionFont, ForeColor = Theme.Muted, BackColor = Theme.Content, AutoSize = false, Bounds = new Rectangle(PagePad + Theme.S(4), _pageY, PageInner, Theme.S(18)) });
            _pageY += Theme.S(20);
        }
        _card = new CardPanel();
        _card.SetBounds(PagePad, _pageY, PageInner, Theme.S(40));
        _page.Controls.Add(_card);
        _y = _card.Padding.Top;
    }

    void EndCard()
    {
        if (_card is null) return;
        _card.Height = Math.Max(Theme.S(36), _y - Theme.S(4) + _card.Padding.Bottom);
        _pageY += _card.Height + Theme.S(16);
        _card = null;
        _y = _pageY;
    }

    /// `height` and `gapAfter` are real pixels — callers scale, because Note() measures its own.
    void Add(Control c, int height, int? gapAfter = null)
    {
        var left = _card is { } card ? card.Padding.Left : PagePad;
        c.SetBounds(left, _y, Inner, height);
        if (c is Label or Panel && c.BackColor == Theme.Content) c.BackColor = Surface;
        (_card as Control ?? _page).Controls.Add(c);
        _y += height + (gapAfter ?? Theme.S(6));
        if (_card is null) _pageY = _y;
    }

    void Note(string text) =>
        Add(new Label { Text = text, ForeColor = Theme.Muted, AutoSize = false, BackColor = Surface },
            TextRenderer.MeasureText(text, Theme.Ui, new Size(Inner, 0), TextFormatFlags.WordBreak).Height + Theme.S(4));

    static ComboBox Combo(int right, IEnumerable<string> values)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat,
            ItemHeight = Theme.S(22),
            BackColor = Theme.Fill2,
            ForeColor = Theme.Text,
            Bounds = new Rectangle(right - Theme.S(180), Theme.S(3), Theme.S(180), Theme.S(28)),
        };
        combo.Items.AddRange(values.ToArray());
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var hot = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(hot ? Theme.Fill3 : Theme.Fill2)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, (string)combo.Items[e.Index]!, Theme.Ui,
                new Rectangle(e.Bounds.X + Theme.S(8), e.Bounds.Y, e.Bounds.Width - Theme.S(10), e.Bounds.Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        return combo;
    }

    void Dropdown(string label, string[] values, string selected, Action<string> set)
    {
        var row = Row(label, out var right);
        var combo = Combo(right, values);
        combo.AccessibleName = label;
        combo.SelectedIndex = Math.Max(0, Array.IndexOf(values, selected));
        combo.SelectedIndexChanged += (_, _) => set((string)combo.SelectedItem!);
        row.Controls.Add(combo);
    }

    /// Preset engines plus a live custom URL box: picking a preset fills the box, editing the box
    /// switches the dropdown to Custom. One source of truth (Config.SearchUrl), two ways in.
    void EnginePicker()
    {
        var names = Engines.All.Select(e => e.Name).Append(Engines.Custom).ToArray();
        var row = Row("Search engine", out var right);
        var combo = Combo(right, names);
        combo.AccessibleName = "Search engine";
        combo.SelectedIndex = Array.IndexOf(names, Engines.NameOf(Config.Current.SearchUrl));
        row.Controls.Add(combo);

        var box = Box(Config.Current.SearchUrl);
        box.AccessibleName = "Search address";
        Add(box, Theme.S(28));

        var syncing = false;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (syncing || Engines.UrlOf((string)combo.SelectedItem!) is not { } url) return;
            syncing = true;
            box.Text = url;
            Config.Current.SearchUrl = url;
            syncing = false;
        };
        box.TextChanged += (_, _) =>
        {
            if (syncing) return;
            var v = box.Text.Trim();
            if (!Engines.IsValidTemplate(v)) return;       // ignore half-typed URLs and non-web schemes
            Config.Current.SearchUrl = v;
            syncing = true;
            combo.SelectedIndex = Array.IndexOf(names, Engines.NameOf(v));
            syncing = false;
        };
    }

    static TextBox Box(string text, bool multiline = false) => new()
    {
        Text = text,
        Multiline = multiline,
        AcceptsReturn = multiline,
        ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.Content,
        ForeColor = Theme.Text,
    };

    void DomainList(string label, List<string> domains, Action<List<string>> set)
    {
        Add(new Label { Text = label, ForeColor = Theme.Text, AutoSize = false }, Theme.S(20), Theme.S(3));
        var box = Box(string.Join(Environment.NewLine, domains), multiline: true);
        box.MaxLength = 20_000;
        box.AccessibleName = label;
        if (label.StartsWith("Extra blocked", StringComparison.Ordinal)) _blockedDomainsBox = box;
        else _allowedDomainsBox = box;
        box.TextChanged += (_, _) => set(PrivacyRules.ParseHostsText(box.Text));
        Add(box, Theme.S(84), Theme.S(10));
    }

    void ImportHostsList()
    {
        using var picker = new OpenFileDialog { Title = "Import a local hosts or domain list", Filter = "Text lists (*.txt;*.hosts)|*.txt;*.hosts|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var info = new FileInfo(picker.FileName);
        if (info.Length > 2_000_000) throw new InvalidDataException("Choose a text list smaller than 2 MB.");
        var parsed = PrivacyRules.ParseHostsText(File.ReadAllText(picker.FileName));
        Config.Current.CustomBlockedDomains = PrivacyRules.NormalizeDomains(Config.Current.CustomBlockedDomains.Concat(parsed));
        if (_blockedDomainsBox is not null)
            _blockedDomainsBox.Text = string.Join(Environment.NewLine, Config.Current.CustomBlockedDomains);
        BakaDialog.Info(this, $"Imported {parsed.Count:N0} domains", $"The saved list is capped at {PrivacyRules.CustomLimit:N0}.");
    }

    void ChooseCustomSurface()
    {
        using var picker = new ColorDialog
        {
            Color = ColorTranslator.FromHtml(Config.Current.CustomBackgroundColor),
            FullOpen = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        Config.Current.CustomBackgroundColor = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        Config.Current.ThemeMode = "Custom";
    }

    void ChooseBackground()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose a new-tab background image or video",
            Filter = "Images and video (*.png;*.jpg;*.jpeg;*.mp4;*.webm;*.m4v)|*.png;*.jpg;*.jpeg;*.mp4;*.webm;*.m4v|All files (*.*)|*.*",
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var ext = Path.GetExtension(picker.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".mp4" or ".webm" or ".m4v"))
            throw new InvalidDataException("Choose a PNG/JPEG image or MP4/WebM video.");
        if (ext is ".png" or ".jpg" or ".jpeg")
            using (var image = Image.FromFile(picker.FileName))
                if (image.Width > 8192 || image.Height > 8192 || (long)image.Width * image.Height > 25_000_000)
                    throw new InvalidDataException("Choose an image no larger than 8192 × 8192 pixels or 25 megapixels.");
        var old = Config.Current.HomeBackgroundFile;
        var name = "home-background" + ext;
        ReplaceManagedFile(picker.FileName, Path.Combine(App.HomeBackgroundDirectory, name));
        Config.Current.HomeBackgroundFile = name;
        if (App.IsHomeBackgroundName(old) && old != name)
            try { File.Delete(Path.Combine(App.HomeBackgroundDirectory, old)); } catch { }
    }

    void RemoveBackground()
    {
        var old = Config.Current.HomeBackgroundFile;
        Config.Current.HomeBackgroundFile = "";
        if (App.IsHomeBackgroundName(old))
            try { File.Delete(Path.Combine(App.HomeBackgroundDirectory, old)); } catch { }
    }

    void ChooseCursor()
    {
        using var picker = new OpenFileDialog { Title = "Choose a Windows cursor", Filter = "Windows cursor (*.cur)|*.cur" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length is 0 or > 65_536) throw new InvalidDataException("Choose a cursor file no larger than 64 KB.");
        using (var validate = new Cursor(picker.FileName)) { }
        ReplaceManagedFile(picker.FileName, App.Path_("custom-cursor.cur"));
        Config.Current.CustomCursorEnabled = true;
    }

    void ResetCustomCursor()
    {
        Config.Current.CustomCursorEnabled = false;
        try { File.Delete(App.Path_("custom-cursor.cur")); } catch { }
    }

    async Task ClearSiteDataAsync()
    {
        if (_clearSiteData is null) return;
        if (!BakaDialog.Confirm(this, "Clear site data?",
            "Cookies and site storage for every website in the regular profile are removed. This may sign you out of sites.",
            [], "Clear", Theme.Delete)) return;
        try
        {
            await _clearSiteData();
            BakaDialog.Info(this, "Site data cleared", "Website cookies and site data were removed.");
        }
        catch (Exception ex) { BakaDialog.Info(this, "Could not clear website data", ex.Message); }
    }

    async Task UpdateRecommendedListAsync()
    {
        if (_updateRecommendedList is null) return;
        try
        {
            var count = await _updateRecommendedList();
            BakaDialog.Info(this, "Block list updated", $"The recommended list is now active with {count:N0} domains.", Theme.ShieldIcon);
        }
        catch (Exception ex)
        {
            BakaDialog.Info(this, "The list could not be updated", $"Any previously downloaded list remains active.\n\n{ex.Message}");
        }
    }

    void RemoveRecommendedList()
    {
        if (PrivacyRules.DownloadedDomainCount == 0 || _removeRecommendedList is null)
        {
            BakaDialog.Info(this, "Nothing to remove", "There is no downloaded list on this PC.");
            return;
        }
        if (!BakaDialog.Confirm(this, "Remove the downloaded list?", "Built-in and custom rules stay active.", [], "Remove", Theme.Delete)) return;
        _removeRecommendedList();
        BakaDialog.Info(this, "List removed", "The downloaded ad and tracker domains were removed.");
    }

    static void ReplaceManagedFile(string source, string destination)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(App.DataDir);
        var temp = destination + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    void Toggle(string label, bool value, Action<bool> set, string description = "")
    {
        var toggle = new ToggleSwitch(label, value) { Description = description, BackColor = Surface };
        toggle.CheckedChanged += (_, _) => set(toggle.Checked);
        Add(toggle, Theme.S(description.Length > 0 ? 44 : 34));
    }

    void TextField(string label, string value, Action<string> set)
    {
        Add(new Label { Text = label, ForeColor = Theme.Text, AutoSize = false }, Theme.S(20), Theme.S(3));
        var box = Box(value);
        box.MaxLength = 512;
        box.AccessibleName = label;
        box.TextChanged += (_, _) => set(box.Text);
        Add(box, Theme.S(28));
    }

    void Spinner(string label, double value, Action<double> set)
    {
        var row = Row(label, out var right);
        var num = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 600,
            Value = (decimal)Math.Clamp(value, 0, 600),
            BackColor = Theme.Fill2,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = label,
            Bounds = new Rectangle(right - Theme.S(90), Theme.S(4), Theme.S(90), Theme.S(26)),
        };
        num.ValueChanged += (_, _) => set((double)num.Value);
        row.Controls.Add(num);
    }

    /// Pill buttons in a row that wraps when it runs out of width.
    void Buttons(params (string Text, string Glyph, Action Do)[] items)
    {
        var row = new Panel { BackColor = Surface };
        int x = 0, y = 0, h = Theme.S(32), gap = Theme.S(8);
        foreach (var (text, glyph, act) in items)
        {
            var b = new PillButton(text, glyph);
            var w = b.PreferredWidth;
            if (x > 0 && x + w > Inner) { x = 0; y += h + gap; }
            b.SetBounds(x, y, w, h);
            b.Click += (_, _) => { try { act(); } catch (Exception ex) { BakaDialog.Info(this, text, ex.Message); } };
            row.Controls.Add(b);
            x += w + gap;
        }
        Add(row, y + h + Theme.S(2), Theme.S(8));
    }

    /// Label on the left, control anchored right. `right` is the usable inner width.
    Panel Row(string label, out int right)
    {
        var width = Inner;
        var row = new Panel { BackColor = Surface };
        row.Controls.Add(new Label
        {
            Text = label,
            ForeColor = Theme.Text,
            AutoSize = false,
            BackColor = Surface,
            Bounds = new Rectangle(0, Theme.S(8), width - Theme.S(190), Theme.S(20)),
        });
        right = width;
        Add(row, Theme.S(36));
        return row;
    }

    /// The section list on the left: icon + name rows, the selected one filled with an accent bar.
    sealed class NavRail : Control
    {
        readonly List<(string Name, string Glyph)> _items = [];
        readonly Font _glyphFont = new(Theme.IconFamily, 10f);
        int _selected = -1, _hot = -1;
        public event Action<string>? Selected;

        public NavRail()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Strip;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTabList;
            AccessibleName = "Settings sections";
        }

        public void Add(string name, string glyph) { _items.Add((name, glyph)); Invalidate(); }

        public void Select(int index, bool raise = true)
        {
            if (index < 0 || index >= _items.Count) return;
            _selected = index;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Selection, index);
            if (raise) Selected?.Invoke(_items[index].Name);
        }

        static readonly int Top0 = Theme.S(54), RowH = Theme.S(36);
        Rectangle RowRect(int i) => new(Theme.S(10), Top0 + i * (RowH + Theme.S(2)), Width - Theme.S(20), RowH);
        int Hit(Point p) => Enumerable.Range(0, _items.Count).FirstOrDefault(i => RowRect(i).Contains(p), -1);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var hot = Hit(e.Location);
            if (hot != _hot) { _hot = hot; Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (Hit(e.Location) is var i and >= 0) Select(i); base.OnMouseDown(e); }
        protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) { Select(Math.Min(_items.Count - 1, _selected + 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { Select(Math.Max(0, _selected - 1)); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Theme.Strip)) g.FillRectangle(bg, ClientRectangle);
            Draw.Label(g, "Settings", Theme.Title, Theme.Text, new RectangleF(Theme.S(20), Theme.S(14), Width - Theme.S(30), Theme.S(28)));
            for (var i = 0; i < _items.Count; i++)
            {
                var r = RowRect(i);
                var active = i == _selected;
                if (active || i == _hot) Draw.Fill(g, r, active ? Theme.Fill2 : Theme.Fill1, Theme.Sf(8));
                if (active)
                {
                    using var mark = new SolidBrush(Theme.Accent);
                    g.FillRectangle(mark, r.X, r.Y + Theme.S(9), Theme.S(2), r.Height - Theme.S(18));
                    if (Focused && ShowFocusCues) Draw.Stroke(g, r, Theme.Accent, Theme.Sf(8), Theme.Sf(1.4f));
                }
                Draw.Glyph(g, _items[i].Glyph, _glyphFont, active ? Theme.Text : Theme.Muted,
                    new RectangleF(r.X + Theme.S(10), r.Y, Theme.S(20), r.Height));
                Draw.Label(g, _items[i].Name, active ? Theme.UiBold : Theme.Ui, active ? Theme.Text : Theme.Muted,
                    new RectangleF(r.X + Theme.S(40), r.Y, r.Width - Theme.S(46), r.Height));
            }
            using var line = new Pen(Theme.Line);
            g.DrawLine(line, Width - 1, 0, Width - 1, Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _glyphFont.Dispose();
            base.Dispose(disposing);
        }
    }
}
