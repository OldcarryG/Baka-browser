using System.Diagnostics;

namespace Baka;

/// One flat dark settings dialog. No tabs, no tree, no MVVM — it is four short sections.
sealed class SettingsForm : Form
{
    readonly BrowserForm _owner;
    readonly Panel _page = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        BackColor = Theme.Content,
        Padding = new Padding(Theme.S(28), Theme.S(20), Theme.S(28), Theme.S(24)),
    };
    int _y;

    public SettingsForm(BrowserForm owner)
    {
        _owner = owner;
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

        Header("Extensions");
        foreach (var ext in Extensions.All().ToArray()) ExtensionRow(ext);
        InstallRow();
        Note("Paste a Chrome Web Store link (or its 32-letter id) to add an extension, or point baka at " +
             "an unpacked folder. Only Manifest V3 extensions load — Chromium 152 removed V2, which is " +
             "why the bundled blocker is uBlock Origin Lite, uBlock Origin's own MV3 build. Privacy " +
             "Badger also sends the Do Not Track and Global Privacy Control signals for you. Pin puts an " +
             "extension in the toolbar; its button opens the extension's own popup.");

        Header("Privacy");
        Dropdown("Tracking prevention", ["Strict", "Balanced", "Basic", "Off"],
            Config.Current.TrackingPrevention, v => Config.Current.TrackingPrevention = v);
        Note("Strict blocks known trackers in every context. Autofill, password saving, sync, crash " +
             "reporting and SmartScreen URL reporting are off in all modes.");

        Header("Search");
        EnginePicker();
        Note("Anything typed in the address bar that isn't a URL goes to this engine.");

        Header("Bookmarks");
        Toggle("Show the bookmarks bar", Config.Current.ShowBookmarksBar,
            v => Config.Current.ShowBookmarksBar = v);
        Note("Ctrl+D bookmarks the page you are on, Ctrl+Shift+B hides the bar. On the bar: " +
             "middle-click opens in a new tab, right-click removes.");

        Header("Performance");
        Spinner("Suspend tabs idle for (minutes, 0 = never)", Config.Current.IdleMinutes,
            v => Config.Current.IdleMinutes = v);

        Header("Advanced");
        Buttons(
            ("Open data folder", () => Process.Start(new ProcessStartInfo("explorer.exe", App.DataDir))),
            ("Edit engine flags", () =>
            {
                Flags.Load();     // recreates flags.txt from defaults if it went missing
                Process.Start(new ProcessStartInfo(App.Path_("flags.txt")) { UseShellExecute = true });
            }),
            ("Update extensions", UpdateExtensions));
        Buttons(("Clear history", () =>
        {
            if (MessageBox.Show($"Forget all {History.All.Count} pages?", App.Name, MessageBoxButtons.YesNo)
                == DialogResult.Yes) History.Clear();
        }));
        Note($"Settings, flags, history and extensions live in {App.DataDir}. Flag changes need a restart. " +
             "Ctrl+H opens history, Ctrl+J the downloads list.");

        FormClosing += (_, _) => Config.Current.Save();
        // dark frame and rounded corners, but an opaque sheet: notes in Muted grey need a fixed backdrop
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

    void ExtensionRow(Installed ext)
    {
        var failed = _owner.ExtensionErrors.GetValueOrDefault(ext.Key);
        // an extension can also be perfectly installed and still unable to do its job here
        var warn = failed is not null ? "Install failed: " + failed
                 : Extensions.Proxies(Extensions.ReadManifest(ext.Dir)) ? Extensions.NoProxy
                 : null;
        var card = new Panel { BackColor = Theme.Fill1 };
        var check = new CheckBox
        {
            Text = ext.Display,
            Checked = failed is null && Config.Current.ExtensionOn.GetValueOrDefault(ext.Key, true),
            Enabled = failed is null,
            ForeColor = Theme.Text,
            BackColor = Theme.Fill1,
            Font = Theme.UiBold,
            AutoSize = false,
            // 24 px, the desktop minimum for a click target. A CheckBox with AutoSize off is clickable
            // across the whole bounds, label included, so the width is doing most of the work.
            Bounds = new Rectangle(Theme.S(12), Theme.S(8), Theme.S(300), Theme.S(24)),
        };
        var blurb = new Label
        {
            Text = warn ?? ext.Blurb,
            ForeColor = warn is null ? Theme.Muted : Theme.Bad,
            BackColor = Theme.Fill1,
            AutoSize = false,
            AutoEllipsis = true,   // one line, so a long blurb ends in "…" instead of a cut-off word
            Bounds = new Rectangle(Theme.S(32), Theme.S(30), Theme.S(300), Theme.S(20)),
        };
        var pin = new CheckBox
        {
            Text = "Pin",
            Checked = Config.Current.ExtensionPinned.GetValueOrDefault(ext.Key),
            Enabled = failed is null,
            ForeColor = Theme.Text,
            BackColor = Theme.Fill1,
            AutoSize = false,
            Width = Theme.S(48),
            Height = Theme.S(24),
        };
        var options = Flat("Options");
        options.Size = new Size(Theme.S(74), Theme.S(28));
        options.Enabled = failed is null && _owner.ExtensionId(ext.Key) is not null;
        var remove = Flat("Remove");
        remove.Size = new Size(Theme.S(74), Theme.S(28));
        remove.Visible = !ext.Bundled;

        check.CheckedChanged += async (_, _) =>
        {
            options.Enabled = check.Checked && _owner.ExtensionId(ext.Key) is not null;
            await _owner.SetExtensionEnabledAsync(ext.Key, check.Checked);
        };
        pin.CheckedChanged += (_, _) => _owner.SetExtensionPinned(ext.Key, pin.Checked);
        options.Click += (_, _) =>
        {
            if (_owner.ExtensionId(ext.Key) is not { } id) return;
            var page = Extensions.ReadManifest(ext.Dir)?.Options ?? "";
            _owner.OpenTab($"chrome-extension://{id}/{page}");
            Close();
        };
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show($"Remove {ext.Display}?", App.Name, MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;
            await _owner.RemoveExtensionAsync(ext.Key);
            Reopen();
        };

        card.Controls.AddRange([check, blurb, pin, options, remove]);
        void Place()
        {
            var right = card.ClientSize.Width - Theme.S(12);
            foreach (var c in new Control[] { remove, options, pin })
            {
                if (!c.Visible) continue;
                right -= c.Width;
                c.Location = new Point(right, (card.ClientSize.Height - c.Height) / 2);
                right -= Theme.S(6);
            }
            check.Width = Math.Max(Theme.S(80), right - Theme.S(20));
            blurb.Width = Math.Max(Theme.S(80), right - Theme.S(40));
        }
        card.Resize += (_, _) => Place();
        Add(card, Theme.S(58));
        Place();
    }

    /// Web Store link or id, plus a folder picker for anything not in the store.
    void InstallRow()
    {
        var row = new Panel { BackColor = Theme.Content };
        var box = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Fill1,
            ForeColor = Theme.Text,
            PlaceholderText = "Chrome Web Store link or extension id",
            Bounds = new Rectangle(0, Theme.S(3), Theme.S(260), Theme.S(26)),
        };
        var install = Flat("Install");
        install.Bounds = new Rectangle(Theme.S(266), Theme.S(1), Theme.S(92), Theme.S(30));
        var unpacked = Flat("Load folder…");
        unpacked.Bounds = new Rectangle(Theme.S(364), Theme.S(1), Theme.S(110), Theme.S(30));

        install.Click += async (_, _) => await InstallStoreAsync(box.Text);
        unpacked.Click += async (_, _) => await InstallFolderAsync();
        box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await InstallStoreAsync(box.Text);
        };

        row.Controls.AddRange([box, install, unpacked]);
        Add(row, Theme.S(32));
    }

    /// A dark dropdown that draws its own items. The stock one clipped the first letter of the closed
    /// value ("Strict" -> "trict") under Application.SetColorMode's still-experimental dark renderer,
    /// and owner-drawing is both the fix and one place to keep the colours.
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
            if (!v.Contains("{q}")) return;               // ignore half-typed URLs, don't fight the caret
            Config.Current.SearchUrl = v;
            syncing = true;
            combo.SelectedIndex = Array.IndexOf(names, Engines.NameOf(v));
            syncing = false;
        };
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

    /// Store link or id -> unpacked folder on disk -> prompt -> handed to the engine.
    async Task InstallStoreAsync(string input)
    {
        if (Extensions.StoreId(input) is null)
        {
            MessageBox.Show("Paste a Chrome Web Store link, or the 32-letter id out of one.", App.Name);
            return;
        }
        Enabled = false;
        var (key, error) = await Extensions.InstallStoreAsync(input);
        Enabled = true;
        if (key is null) { MessageBox.Show("Could not install it: " + error, App.Name); return; }
        await ConfirmAndAddAsync(key, Extensions.DirFor(key), ours: true);
    }

    /// A folder of their own. Nothing to download, but the same V3 gate and the same prompt.
    async Task InstallFolderAsync()
    {
        using var pick = new FolderBrowserDialog
        {
            Description = "Pick the folder that has manifest.json in it",
            UseDescriptionForTitle = true,
        };
        if (pick.ShowDialog(this) != DialogResult.OK) return;

        if (Extensions.ReadManifest(pick.SelectedPath) is not { } m)
        {
            MessageBox.Show("That folder has no readable manifest.json.", App.Name);
            return;
        }
        if (m.Version < 3)
        {
            MessageBox.Show($"\"{m.Name}\" is a Manifest V2 extension. This engine only loads V3.", App.Name);
            return;
        }
        // keyed by path, so re-adding the same folder updates the entry instead of doubling it
        await ConfirmAndAddAsync("dir:" + pick.SelectedPath.ToLowerInvariant(), pick.SelectedPath, ours: false);
    }

    /// Says what the extension will be able to do before it gets to do any of it.
    async Task ConfirmAndAddAsync(string key, string dir, bool ours)
    {
        var m = Extensions.ReadManifest(dir);
        var perms = m?.Permissions is { Length: > 0 } p ? string.Join(", ", p) : "nothing in particular";
        var proxy = Extensions.Proxies(m)
            ? "\n\nHeads up: it wants the proxy API, which WebView2 does not have. It will install and " +
              "enable, and then route nothing — not because of a setting, but because there is no proxy " +
              "support in this engine to reach. A VPN app that routes at the network adapter works; an " +
              "extension here cannot."
            : "";
        if (MessageBox.Show($"Install \"{m?.Name ?? "this extension"}\"?\n\nIt asks for: {perms}{proxy}",
                App.Name, MessageBoxButtons.OKCancel) != DialogResult.OK)
        {
            if (ours) try { Directory.Delete(dir, true); } catch { }
            return;
        }
        if (await _owner.AddExtensionAsync(key, dir, m?.Name ?? "Extension") is { } err)
        {
            MessageBox.Show("The engine refused it: " + err, App.Name);
            if (ours) try { Directory.Delete(dir, true); } catch { }
            return;
        }
        Reopen();
    }

    /// The rows are built in the constructor, so a list that changed means a fresh dialog.
    void Reopen() => DialogResult = DialogResult.Retry;

    async void UpdateExtensions()
    {
        Enabled = false;
        var problems = new List<string>();
        foreach (var src in Extensions.Bundled)
            if (await Extensions.FetchAsync(src, staged: true) is { } err)
                problems.Add($"{src.Display}: {err}");
        Enabled = true;
        MessageBox.Show(problems.Count == 0
            ? $"Downloaded. Restart {App.Name} to load the new versions."
            : "Some updates failed:\n\n" + string.Join('\n', problems), App.Name);
    }
}
