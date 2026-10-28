using System.Diagnostics;

namespace Baka;

/// One flat settings dialog. No tabs, no tree, no MVVM — just the browser controls users need.
sealed class SettingsForm : Form
{
    readonly Panel _page = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        BackColor = Theme.Content,
        Padding = new Padding(Theme.S(28), Theme.S(20), Theme.S(28), Theme.S(24)),
    };
    int _y;
    TextBox? _blockedDomainsBox, _allowedDomainsBox;
    readonly Func<Task>? _clearSiteData;
    readonly Func<string, Task<string>>? _installExtension;
    readonly Func<Task<int>>? _updateRecommendedList;
    readonly Action? _removeRecommendedList;

    public SettingsForm(bool privateBrowsing = false, Func<Task>? clearSiteData = null,
        Func<string, Task<string>>? installExtension = null, bool extensionsSupported = false,
        Func<Task<int>>? updateRecommendedList = null, Action? removeRecommendedList = null)
    {
        _clearSiteData = clearSiteData;
        _installExtension = installExtension;
        _updateRecommendedList = updateRecommendedList;
        _removeRecommendedList = removeRecommendedList;
        Text = $"{App.Name} settings";
        BackColor = Theme.Content;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        ClientSize = new Size(Theme.S(600), Theme.S(640));
        MinimizeBox = MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_page);

        Header("Privacy");
        Dropdown("Tracking prevention", ["Strict", "Balanced", "Basic", "Off"],
            Config.Current.TrackingPrevention, v => Config.Current.TrackingPrevention = v);
        Toggle("Block known ad and tracker domains", Config.Current.BuiltInBlockingEnabled,
            v => Config.Current.BuiltInBlockingEnabled = v);
        Toggle("Send Do Not Track and Global Privacy Control signals", Config.Current.SendPrivacySignals,
            v => Config.Current.SendPrivacySignals = v);
        Toggle("Enable JavaScript on websites", Config.Current.JavaScriptEnabled,
            v => Config.Current.JavaScriptEnabled = v);
        Toggle("Load images globally", Config.Current.ImagesEnabled,
            v => Config.Current.ImagesEnabled = v);
        Note("The built-in host rules block common ad and tracking networks. Do Not Track and Global Privacy Control " +
             "signals can be toggled above. Add local host rules or site exceptions below. " +
             "This is host blocking, so it cannot remove page elements or catch ads served from a site's own domain. " +
             "Tracking prevention adds the engine's own protections. Some sites may rely on blocked " +
             "services. Autofill, password saving, sync, crash reporting and SmartScreen URL reporting " +
             "are off in all modes. Disabling JavaScript can " +
             "disable site scripts; the built-in dashboard stays interactive. Disabling images blocks " +
             "new image requests; images already shown remain until the page is reloaded.");
        Buttons(("Update ad & tracker list…", () => _ = UpdateRecommendedListAsync()),
            ("Remove downloaded list", RemoveRecommendedList));
        Note("The optional StevenBlack hosts list adds roughly 70,000+ known adware and malware domains. It downloads only when you press Update, is kept on this PC, and is shared by regular and private windows. Updates are provided by the list's GitHub project. Domain lists cannot remove ads served directly by a site or hide ad page elements.");
        if (!privateBrowsing)
        {
            Buttons(("Clear site data…", () => _ = ClearSiteDataAsync()));
            Note("This removes cookies, local storage, service workers and other site data from the regular profile. It does not clear history or bookmarks.");
            Toggle("Encrypt browser data with this Windows account (EFS)", Config.Current.EncryptProfileAtRest,
                v => Config.Current.EncryptProfileAtRest = v);
            Note("Applies at the next launch to the local profile, history, bookmarks and settings in Local AppData. Windows Encrypting File System protects it at rest for this Windows account. It cannot protect data while your account is unlocked or from a device administrator. Keep your Windows recovery credentials; unsupported drives may reject encryption.");
        }
        DomainList("Extra blocked domains", Config.Current.CustomBlockedDomains,
            v => Config.Current.CustomBlockedDomains = v);
        DomainList("Allowed domains (include subdomains)", Config.Current.AllowedDomains,
            v => Config.Current.AllowedDomains = v);
        Buttons(("Import hosts list…", ImportHostsList), ("Clear custom rules", () =>
        {
            Config.Current.CustomBlockedDomains = [];
            Config.Current.AllowedDomains = [];
            if (_blockedDomainsBox is not null) _blockedDomainsBox.Text = "";
            if (_allowedDomainsBox is not null) _allowedDomainsBox.Text = "";
        }));
        Note($"Rules are stored locally. Custom block lists accept up to {PrivacyRules.CustomLimit} domains; imported text supports hosts files and one-domain-per-line lists.");

        Header("Appearance");
        Dropdown("Theme", ["Baka", "Dark", "Light", "Custom"], Config.Current.ThemeMode,
            v => Config.Current.ThemeMode = v);
        Toggle("Liquid-glass browser chrome and dashboard", Config.Current.LiquidGlassEnabled,
            v => Config.Current.LiquidGlassEnabled = v);
        Note("Uses the Windows backdrop where available in dark and light themes, with crisp glass-style surfaces and soft edge highlights. Dashboard blur is limited to the new-tab page; video wallpapers stay sharp and avoid extra blur work.");
        Buttons(("Custom surface color…", ChooseCustomSurface), ("Accent color…", () =>
        {
            using var picker = new ColorDialog { Color = Theme.Accent, FullOpen = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            Config.Current.AccentColor = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        }));
        Note("Custom keeps the surface and accent colors you choose. The browser applies them after this settings window closes.");
        Buttons(("Choose image or video…", ChooseBackground), ("Remove background", RemoveBackground));
        Buttons(("Choose cursor…", ChooseCursor), ("Reset cursor", ResetCustomCursor));
        Note("New-tab PNG/JPEG images and MP4/WebM videos have no file-size cap and are stored locally. Videos start muted and pause in background tabs. Custom cursors accept Windows .cur files; page cursors require JavaScript on the site.");

        Header("Search");
        EnginePicker();
        Note("Anything typed in the address bar that isn't a URL goes to this engine.");

        if (!privateBrowsing)
        {
            Header("Extensions");
            if (extensionsSupported)
            {
                Buttons(("Install extension from folder…", ChooseExtensionFolder));
                Note("Choose an unpacked extension folder containing manifest.json. Chrome Web Store pages and .crx files cannot be installed directly. Extensions can read or change site data according to their permissions, so install only extensions you trust.");
            }
            else
                Note("Extension installation is unavailable because the installed WebView2 runtime does not expose the required extension support. Update the WebView2 Runtime to version 120 or newer to enable it.");
        }

        Header("Bookmarks");
        Toggle("Show the bookmarks bar", Config.Current.ShowBookmarksBar,
            v => Config.Current.ShowBookmarksBar = v);
        Dropdown("Bookmarks position", ["Left", "Top"], Config.Current.BookmarksPosition,
            v => Config.Current.BookmarksPosition = v);
        Note("Use the chevron at the top of the left sidebar to collapse or expand it. Ctrl+D bookmarks the page; Ctrl+Shift+B hides or shows the bar. On the bar: " +
             "middle-click opens in a new tab, right-click removes.");

        Header("Tabs");
        Dropdown("Tab placement", ["Left", "Top"], Config.Current.TabsPosition,
            v => Config.Current.TabsPosition = v);
        Toggle("Restore tabs from the previous session", Config.Current.RestoreLastSession, v =>
        {
            Config.Current.RestoreLastSession = v;
            if (!v) Config.Current.LastSessionTabs = [];
        });
        Toggle("Start with the left tab rail collapsed", Config.Current.TabsCollapsed,
            v => Config.Current.TabsCollapsed = v);
        Note("When enabled, normal windows reopen up to 50 tabs from the last session and select the tab that was active. Private-window tabs are never saved. Use the chevron above the tab list to collapse or expand the left rail. The top layout keeps the native Windows title bar.");

        Header("Performance");
        Spinner("Suspend tabs idle for (minutes, 0 = never)", Config.Current.IdleMinutes,
            v => Config.Current.IdleMinutes = v);
        Toggle("Check for updates automatically", Config.Current.AutoUpdateEnabled,
            v => Config.Current.AutoUpdateEnabled = v);
        Note("After the delay, the browser asks WebView2 to suspend only hidden tabs that are not loading or playing audio. This is best-effort; some pages cannot be suspended. Suspended tabs resume when selected. Updates download quietly and apply at the next launch.");

        Header("Website compatibility");
        TextField("Custom user agent (blank uses the engine default)", Config.Current.UserAgent,
            v => Config.Current.UserAgent = v);
        Note("The user agent takes effect on every open tab after these settings are saved. Some sites may " +
             "serve different content or reject an unfamiliar value.");

        Header("Advanced");
        Buttons(
            ("Open data folder", () => Process.Start(new ProcessStartInfo("explorer.exe", App.DataDir))),
            ("Edit engine flags", () =>
            {
                Flags.Load();     // recreates flags.txt from defaults if it went missing
                Process.Start(new ProcessStartInfo(App.Path_("flags.txt")) { UseShellExecute = true });
            }));
        if (!privateBrowsing)
            Buttons(("Clear history", () =>
            {
                if (MessageBox.Show($"Forget all {History.All.Count} pages?", App.Name, MessageBoxButtons.YesNo)
                    == DialogResult.Yes) History.Clear();
            }));
        else Note("This private window has no saved history. It cannot clear history from the regular browser profile.");
        Note($"Settings, flags and history live in {App.DataDir}. Flag changes need a restart. " +
             "Ctrl+H opens history, Ctrl+J the downloads list.");

        FormClosing += (_, _) => { if (!privateBrowsing) Config.Current.Save(); };
        // Rounded native frame, but an opaque sheet so muted notes retain contrast in either theme.
        Shown += (_, _) => Theme.Glass(this, backdrop: false);
    }

    // ---------- rows ----------

    /// `height` and `gapAfter` are real pixels — callers scale, because Note() measures its own.
    void Add(Control c, int height, int? gapAfter = null)
    {
        c.SetBounds(_page.Padding.Left, _y, _page.ClientSize.Width - _page.Padding.Horizontal, height);
        c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _page.Controls.Add(c);
        _y += height + (gapAfter ?? Theme.S(8));
    }

    void Header(string text)
    {
        if (_y > 0) _y += Theme.S(14);
        Add(new Label { Text = text, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = false },
            Theme.S(26), Theme.S(6));
        Add(new Panel { BackColor = Theme.Line }, 1, Theme.S(12));
    }

    void Note(string text) =>
        Add(new Label
        {
            Text = text,
            ForeColor = Theme.Muted,
            AutoSize = false,
            // measured, not scaled: the font is in points, so MeasureText already answers in real pixels
        }, TextRenderer.MeasureText(text, Theme.Ui,
              new Size(_page.ClientSize.Width - _page.Padding.Horizontal, 0), TextFormatFlags.WordBreak).Height + 4);

    static ComboBox Combo(int right, IEnumerable<string> values)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Theme.S(20),
            Bounds = new Rectangle(right - Theme.S(170), Theme.S(3), Theme.S(170), Theme.S(26)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        combo.Items.AddRange(values.ToArray());
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var hot = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(hot ? Theme.Fill3 : Theme.Fill1)) e.Graphics.FillRectangle(bg, e.Bounds);
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
        combo.SelectedIndex = Array.IndexOf(names, Engines.NameOf(Config.Current.SearchUrl));
        row.Controls.Add(combo);

        var box = new TextBox
        {
            Text = Config.Current.SearchUrl,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Fill1,
            ForeColor = Theme.Text,
        };
        Add(box, Theme.S(26));

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

    void DomainList(string label, List<string> domains, Action<List<string>> set)
    {
        Add(new Label { Text = label, ForeColor = Theme.Text, AutoSize = false }, Theme.S(20), Theme.S(3));
        var box = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, domains), MaxLength = 20_000,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Fill1, ForeColor = Theme.Text,
        };
        if (label.StartsWith("Extra blocked", StringComparison.Ordinal)) _blockedDomainsBox = box;
        else _allowedDomainsBox = box;
        box.TextChanged += (_, _) => set(PrivacyRules.ParseHostsText(box.Text));
        Add(box, Theme.S(96));
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
        MessageBox.Show(this, $"Imported {parsed.Count} unique domains. The saved list is capped at {PrivacyRules.CustomLimit}.", App.Name,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ChooseExtensionFolder()
    {
        if (_installExtension is null) return;
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the unpacked browser extension folder containing manifest.json.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _ = InstallExtensionAsync(picker.SelectedPath);
    }

    async Task InstallExtensionAsync(string folder)
    {
        try
        {
            var name = await _installExtension!(folder);
            MessageBox.Show(this, $"{name} was installed for this browser profile.", App.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not install the extension.\n\n{ex.Message}", App.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        if (MessageBox.Show(this,
            "Clear cookies and site storage for every website in the regular profile? This may sign you out of sites.",
            App.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            await _clearSiteData();
            MessageBox.Show(this, "Website cookies and site data were cleared.", App.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not clear website data.\n\n{ex.Message}", App.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task UpdateRecommendedListAsync()
    {
        if (_updateRecommendedList is null) return;
        try
        {
            var count = await _updateRecommendedList();
            MessageBox.Show(this, $"The recommended list is now active with {count:N0} domains.", App.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The list could not be updated. Any previously downloaded list remains active.\n\n{ex.Message}",
                App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void RemoveRecommendedList()
    {
        if (PrivacyRules.DownloadedDomainCount == 0 || _removeRecommendedList is null)
        {
            MessageBox.Show(this, "There is no downloaded list to remove.", App.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, "Remove the downloaded ad and tracker domains from this PC? Built-in and custom rules will remain active.",
                App.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _removeRecommendedList();
        MessageBox.Show(this, "The downloaded list was removed.", App.Name,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void ReplaceManagedFile(string source, string destination)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(App.DataDir);
        var temp = destination + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    void Toggle(string label, bool value, Action<bool> set)
    {
        var check = new CheckBox
        {
            Text = label,
            Checked = value,
            ForeColor = Theme.Text,
            BackColor = Theme.Content,
            AutoSize = false,
        };
        check.CheckedChanged += (_, _) => set(check.Checked);
        Add(check, Theme.S(24));
    }

    void TextField(string label, string value, Action<string> set)
    {
        Add(new Label { Text = label, ForeColor = Theme.Text, AutoSize = false }, Theme.S(20), Theme.S(3));
        var box = new TextBox
        {
            Text = value,
            MaxLength = 512,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Fill1,
            ForeColor = Theme.Text,
        };
        box.TextChanged += (_, _) => set(box.Text);
        Add(box, Theme.S(26));
    }

    void Spinner(string label, double value, Action<double> set)
    {
        var row = Row(label, out var right);
        var num = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 600,
            Value = (decimal)Math.Clamp(value, 0, 600),
            // dark-themed by the OS, spinner arrows included
            Bounds = new Rectangle(right - Theme.S(90), Theme.S(3), Theme.S(90), Theme.S(26)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        num.ValueChanged += (_, _) => set((double)num.Value);
        row.Controls.Add(num);
    }

    void Buttons(params (string Text, Action Do)[] items)
    {
        var row = new Panel { BackColor = Theme.Content };
        var x = 0;
        foreach (var (text, act) in items)
        {
            var b = Flat(text);
            b.Bounds = new Rectangle(x, 0, Theme.S(150), Theme.S(32));
            b.Click += (_, _) => { try { act(); } catch (Exception ex) { MessageBox.Show(ex.Message, App.Name); } };
            row.Controls.Add(b);
            x += Theme.S(158);
        }
        Add(row, Theme.S(34));
    }

    /// Label on the left, control anchored right. `right` is the usable inner width.
    Panel Row(string label, out int right)
    {
        var width = _page.ClientSize.Width - _page.Padding.Horizontal;
        var row = new Panel { BackColor = Theme.Content };
        row.Controls.Add(new Label
        {
            Text = label,
            ForeColor = Theme.Text,
            AutoSize = false,
            Bounds = new Rectangle(0, Theme.S(7), width - Theme.S(180), Theme.S(20)),
        });
        right = width;
        Add(row, Theme.S(32));
        return row;
    }

    static Button Flat(string text) => Theme.Flat(text);

    // ---------- installing ----------
}
