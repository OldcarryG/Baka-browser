using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Baka;

/// Extensions (Chrome Web Store and unpacked folders), the toolbar menus, zoom and the home button.
sealed partial class BrowserForm
{
    readonly HashSet<string> _installedExtensionIds = new(StringComparer.Ordinal);
    ExtensionPopup? _popup;

    /// Component extensions WebView2 ships itself (clipboard helper, PDF viewer). They are not the user's
    /// and cannot be removed, so they stay out of the menu and Settings.
    static readonly HashSet<string> BuiltInExtensionIds = new(StringComparer.Ordinal)
    {
        "dgiklkfkllikcanfonkcabmbdfmgleag", "mhjfbmdgcfjbbpaeojofohoefgiehjai",
    };

    CoreWebView2Profile? Profile =>
        Current?.View.CoreWebView2?.Profile ?? _open.Select(t => t.View.CoreWebView2).FirstOrDefault(c => c is not null)?.Profile;

    // ---------- IExtensionHost ----------

    public bool ExtensionsSupported => _extensionsAvailable;

    public async Task<IReadOnlyList<ExtensionInfo>> ListExtensionsAsync()
    {
        if (!_extensionsAvailable || Profile is not { } profile) return [];
        var index = WebStore.LoadIndex();
        var list = new List<ExtensionInfo>();
        foreach (var ext in await profile.GetBrowserExtensionsAsync())
        {
            if (BuiltInExtensionIds.Contains(ext.Id)) continue;
            index.TryGetValue(ext.Id, out var folder);
            var (popup, options, icon) = folder is not null ? WebStore.Pages(folder) : (null, null, null);
            var version = folder is not null && WebStore.ReadManifest(folder) is { } manifest
                ? manifest["version"]?.GetValue<string>() ?? "" : "";
            list.Add(new ExtensionInfo(ext.Id, ext.Name, ext.IsEnabled, version, popup, options,
                icon is not null && folder is not null ? Path.Combine(folder, icon) : null, folder,
                WebStore.IsManagedFolder(folder)));
        }
        _installedExtensionIds.Clear();
        foreach (var e in list) _installedExtensionIds.Add(e.Id);
        UpdateStoreButton();
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    async Task<CoreWebView2BrowserExtension?> FindExtensionAsync(string id) =>
        Profile is { } profile ? (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id) : null;

    public async Task SetExtensionEnabledAsync(string id, bool enabled)
    {
        if (await FindExtensionAsync(id) is { } ext) await ext.EnableAsync(enabled);
    }

    public async Task RemoveExtensionAsync(string id)
    {
        if (await FindExtensionAsync(id) is not { } ext) return;
        await ext.RemoveAsync();
        var index = WebStore.LoadIndex();
        if (index.Remove(id, out var folder)) WebStore.TryDelete(folder);   // only folders this browser unpacked
        WebStore.SaveIndex(index);
        _installedExtensionIds.Remove(id);
        UpdateStoreButton();
    }

    public async Task<string> InstallExtensionFolderAsync(string folder)
    {
        if (_privateBrowsing) throw new InvalidOperationException("Extensions are not installed from private windows.");
        if (Profile is not { } profile) throw new InvalidOperationException("Open a browser tab before installing an extension.");
        var fullPath = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(fullPath, "manifest.json")))
            throw new InvalidDataException("Choose the folder that contains the extension's manifest.json file.");
        var ext = await profile.AddBrowserExtensionAsync(fullPath);
        var index = WebStore.LoadIndex();
        index[ext.Id] = fullPath;
        WebStore.SaveIndex(index);
        _installedExtensionIds.Add(ext.Id);
        return ext.Name;
    }

    public void OpenWebStore() => OpenTab(WebStore.Home);

    public void OpenExtensionOptions(string id)
    {
        var index = WebStore.LoadIndex();
        if (index.TryGetValue(id, out var folder) && WebStore.Pages(folder).Options is { } options)
            OpenTab($"chrome-extension://{id}/{options}");
        else
            BakaDialog.Info(this, "No options page", "This extension has no settings page of its own.");
    }

    // ---------- Chrome Web Store ----------

    void UpdateStoreButton()
    {
        if (IsDisposed) return;
        var id = _extensionsAvailable ? WebStore.IdFromUrl(Current?.View.CoreWebView2?.Source) : null;
        _storeId = id;
        var show = id is not null;
        var installed = id is not null && _installedExtensionIds.Contains(id);
        if (!_storeInstalling)
        {
            _storeButton.Text = installed ? "Added to Baka" : "Add to Baka";
            _storeButton.Glyph = installed ? Theme.Check : Theme.Add;
            _storeButton.Enabled = !installed;
        }
        if (_storeButton.Visible != show) { _storeButton.Visible = show; LayoutBar(); }
        else if (show) LayoutBar();
    }

    /// Set by the UI self-test so it can install without a person clicking the confirmation.
    bool _autoConfirmInstall;

    async Task<string?> InstallFromStoreAsync(string? requestedId = null)
    {
        var id = requestedId ?? _storeId;
        if (id is null || _storeInstalling || _privateBrowsing || !_extensionsAvailable) return null;
        if (Profile is not { } profile) return null;
        _storeInstalling = true;
        _storeButton.Text = "Adding…";
        _storeButton.Glyph = Theme.Download;
        _storeButton.Enabled = false;
        LayoutBar();
        ExtensionPackage? package = null;
        try
        {
            if ((await profile.GetBrowserExtensionsAsync()).Any(e => e.Id == id))
            {
                _installedExtensionIds.Add(id);
                return id;
            }
            var engine = WebStore.EngineVersion(CoreWebView2Environment.GetAvailableBrowserVersionString());
            package = await WebStore.DownloadAsync(id, engine);
            if (!_autoConfirmInstall && !BakaDialog.Confirm(this, $"Add “{package.Name}”?",
                    "It will be able to:", package.Permissions, "Add extension", Theme.Puzzle))
            {
                WebStore.Discard(package);
                return null;
            }
            var folder = WebStore.Commit(package);
            package = null;
            var ext = await profile.AddBrowserExtensionAsync(folder);
            var index = WebStore.LoadIndex();
            index[ext.Id] = folder;
            WebStore.SaveIndex(index);
            _installedExtensionIds.Add(ext.Id);
            if (!_autoConfirmInstall)
                BakaDialog.Info(this, $"{ext.Name} was added",
                    "Open it from the puzzle button on the toolbar. Manage it in Settings > Extensions.", Theme.Puzzle);
            return ext.Id;
        }
        catch (Exception ex)
        {
            if (package is not null) WebStore.Discard(package);
            if (!_autoConfirmInstall)
                BakaDialog.Info(this, "The extension could not be added", ex.Message, Theme.Info);
            else throw;
            return null;
        }
        finally
        {
            _storeInstalling = false;
            UpdateStoreButton();
        }
    }

    /// uBlock Origin Lite (free, open source, Manifest V3) from the Chrome Web Store.
    internal const string DefaultAdBlockerId = "ddkjiahejlhfcafbddmgiahcphecmpfh";

    /// Adds uBlock Origin Lite once, on the first start with extensions available. It is tried once per
    /// profile: if the user later removes it, it stays removed. Turned off with Settings > Privacy.
    async Task EnsureDefaultAdBlockerAsync()
    {
        // the main window does this; a second window on the same profile would race it for the same folder
        if (!_extensionsAvailable || _privateBrowsing || _detached || !Config.Current.InstallDefaultAdBlocker
            || Config.Current.DefaultAdBlockerAdded || Profile is not { } profile) return;
        ExtensionPackage? package = null;
        try
        {
            if (!(await profile.GetBrowserExtensionsAsync()).Any(e => e.Id == DefaultAdBlockerId))
            {
                var engine = WebStore.EngineVersion(CoreWebView2Environment.GetAvailableBrowserVersionString());
                package = await WebStore.DownloadAsync(DefaultAdBlockerId, engine);
                if (_closing || IsDisposed) { WebStore.Discard(package); return; }
                var folder = WebStore.Commit(package);
                package = null;
                var ext = await profile.AddBrowserExtensionAsync(folder);
                var index = WebStore.LoadIndex();
                index[ext.Id] = folder;
                WebStore.SaveIndex(index);
                _installedExtensionIds.Add(ext.Id);
            }
            Config.Current.DefaultAdBlockerAdded = true;
            Config.Current.Save();
            UpdateStoreButton();
        }
        catch
        {
            // Offline or the store is unreachable: try again on the next start.
            if (package is not null) WebStore.Discard(package);
        }
    }

    // ---------- extensions menu and popups ----------

    async Task ShowExtensionsMenuAsync()
    {
        if (_closing || !_extensionsAvailable) return;
        IReadOnlyList<ExtensionInfo> list;
        try { list = await ListExtensionsAsync(); }
        catch (Exception ex) { BakaDialog.Info(this, "Extensions are unavailable", ex.Message); return; }
        _extensionsMenu.Items.Clear();
        var header = new ToolStripMenuItem("Extensions") { Enabled = false, Font = Theme.UiBold };
        _extensionsMenu.Items.Add(header);
        if (list.Count == 0)
            _extensionsMenu.Items.Add(new ToolStripMenuItem("No extensions yet") { Enabled = false });
        foreach (var ext in list)
        {
            var item = new ToolStripMenuItem(ext.Enabled ? ext.Name : ext.Name + "  (off)", ExtensionIcon(ext));
            if (ext.PopupPage is not null && ext.Enabled)
            {
                item.Click += (_, _) => OpenExtensionPopup(ext);
                item.DropDownItems.Add(MenuItem("Open", Theme.OpenNew, () => OpenExtensionPopup(ext)));
            }
            if (ext.OptionsPage is not null)
                item.DropDownItems.Add(MenuItem("Options", Theme.Settings, () => OpenExtensionOptions(ext.Id)));
            item.DropDownItems.Add(MenuItem(ext.Enabled ? "Turn off" : "Turn on", ext.Enabled ? Theme.NoShield : Theme.Check,
                async () => { try { await SetExtensionEnabledAsync(ext.Id, !ext.Enabled); } catch (Exception ex) { BakaDialog.Info(this, ext.Name, ex.Message); } }));
            item.DropDownItems.Add(MenuItem("Remove…", Theme.Delete, async () =>
            {
                if (!BakaDialog.Confirm(this, $"Remove “{ext.Name}”?", "Its settings and data in this browser are deleted.", [], "Remove", Theme.Delete)) return;
                try { await RemoveExtensionAsync(ext.Id); } catch (Exception ex) { BakaDialog.Info(this, ext.Name, ex.Message); }
            }));
            _extensionsMenu.Items.Add(item);
        }
        _extensionsMenu.Items.Add(new ToolStripSeparator());
        _extensionsMenu.Items.Add(MenuItem("Get extensions from Chrome Web Store", Theme.Store, OpenWebStore));
        _extensionsMenu.Items.Add(MenuItem("Install unpacked extension…", Theme.Folder, ChooseUnpackedExtension));
        _extensionsMenu.Items.Add(MenuItem("Manage extensions", Theme.Settings, () => OpenSettings("Extensions")));
        Theme.Style(_extensionsMenu);
        _extensionsMenu.Show(_extensionsButton, new Point(_extensionsButton.Width, _extensionsButton.Height), ToolStripDropDownDirection.BelowLeft);
    }

    readonly Dictionary<string, Image> _extensionIcons = new(StringComparer.OrdinalIgnoreCase);

    Image ExtensionIcon(ExtensionInfo ext)
    {
        if (ext.IconFile is { } file && File.Exists(file))
        {
            if (_extensionIcons.TryGetValue(file, out var cached)) return cached;
            try
            {
                using var src = Image.FromFile(file);
                var n = Theme.S(16);
                var bmp = new Bitmap(n, n);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, n, n);
                }
                _extensionIcons[file] = bmp;
                return bmp;
            }
            catch { }
        }
        return Theme.GlyphImage(Theme.Puzzle, Theme.Muted);
    }

    void ChooseUnpackedExtension()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the unpacked extension folder that contains manifest.json.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _ = InstallUnpackedAsync(picker.SelectedPath);
    }

    async Task InstallUnpackedAsync(string folder)
    {
        try
        {
            var name = await InstallExtensionFolderAsync(folder);
            BakaDialog.Info(this, $"{name} was added", "Open it from the puzzle button on the toolbar.", Theme.Puzzle);
        }
        catch (Exception ex) { BakaDialog.Info(this, "The extension could not be added", ex.Message, Theme.Info); }
    }

    void OpenExtensionPopup(ExtensionInfo ext)
    {
        if (_env is null || ext.PopupPage is null) return;
        _popup?.Close();
        var anchor = _extensionsButton.PointToScreen(new Point(_extensionsButton.Width, _extensionsButton.Height + Theme.S(4)));
        _popup = new ExtensionPopup(_env, $"chrome-extension://{ext.Id}/{ext.PopupPage}", anchor, ext.Name);
        _popup.FormClosed += (_, _) => _popup = null;
        _popup.Show(this);
    }

    /// The quick-link tile on the new-tab page: the site's saved icon as a data URI (nothing is fetched),
    /// or the first letter of its title.
    static string FaviconTile(Bookmark mark)
    {
        if (SiteIcons.For(mark.Url) is { } icon)
        {
            try
            {
                using var ms = new MemoryStream();
                icon.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return $"<img src=\"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}\" alt=\"\">";
            }
            catch { }
        }
        var letter = mark.Title.Trim().FirstOrDefault(char.IsLetterOrDigit);
        return Esc(letter == default ? "•" : char.ToUpperInvariant(letter).ToString());
    }

    // ---------- main menu ----------

    ToolStripMenuItem? _bookmarksBarItem, _zoomResetItem;

    static ToolStripMenuItem MenuItem(string text, string glyph, Action act, string shortcut = "")
    {
        var item = new ToolStripMenuItem(text, Theme.GlyphImage(glyph)) { ShortcutKeyDisplayString = shortcut.Length > 0 ? shortcut : null };
        item.Click += (_, _) => act();
        return item;
    }

    void BuildMainMenu()
    {
        _mainMenu.Items.Clear();
        _mainMenu.Items.Add(MenuItem("New tab", Theme.Add, () => _ = NewTabAsync(null), "Ctrl+T"));
        if (!_privateBrowsing)
            _mainMenu.Items.Add(MenuItem("New private window", Theme.Private, OpenPrivateWindow, "Ctrl+Shift+N"));
        _mainMenu.Items.Add(new ToolStripSeparator());
        _mainMenu.Items.Add(MenuItem("History", Theme.HistoryIcon,
            () => _ = NewTabAsync(null, _privateBrowsing ? PrivateHistoryPage() : HistoryPage()), "Ctrl+H"));
        _mainMenu.Items.Add(MenuItem("Downloads", Theme.Download,
            () => { try { Current?.View.CoreWebView2?.OpenDefaultDownloadDialog(); } catch { } }, "Ctrl+J"));
        _bookmarksBarItem = MenuItem("Hide bookmarks bar", Theme.BookmarkList, ToggleBookmarksBar, "Ctrl+Shift+B");
        _mainMenu.Items.Add(_bookmarksBarItem);
        if (_extensionsAvailable)
            _mainMenu.Items.Add(MenuItem("Extensions", Theme.Puzzle, () => _ = ShowExtensionsMenuAsync()));
        _mainMenu.Items.Add(new ToolStripSeparator());
        _mainMenu.Items.Add(MenuItem("Zoom out", Theme.ZoomOut, () => SetZoom(Config.Current.ZoomFactor - 0.1), "Ctrl+−"));
        _zoomResetItem = MenuItem("Zoom 100%", Theme.Search, () => SetZoom(1.0), "Ctrl+0");
        _mainMenu.Items.Add(_zoomResetItem);
        _mainMenu.Items.Add(MenuItem("Zoom in", Theme.ZoomIn, () => SetZoom(Config.Current.ZoomFactor + 0.1), "Ctrl++"));
        _mainMenu.Items.Add(MenuItem("Print…", Theme.Print, PrintPage, "Ctrl+P"));
        _mainMenu.Items.Add(MenuItem("Full screen", Theme.Fullscreen, EnterTrueFullscreen, "F11"));
        _mainMenu.Items.Add(MenuItem("Maximize window", Theme.Window, KeepBrowserControlsVisible));
        _mainMenu.Items.Add(new ToolStripSeparator());
        _mainMenu.Items.Add(MenuItem("Settings", Theme.Settings, () => OpenSettings(), "Ctrl+,"));
        _mainMenu.Items.Add(MenuItem("About Baka Browser", Theme.Info, () => OpenSettings("About")));
        Theme.Style(_mainMenu);
    }

    void ShowMainMenu()
    {
        if (_closing || _menuButton.IsDisposed) return;
        if (_bookmarksBarItem is not null) _bookmarksBarItem.Text = Config.Current.ShowBookmarksBar ? "Hide bookmarks bar" : "Show bookmarks bar";
        if (_zoomResetItem is not null) _zoomResetItem.Text = $"Zoom {Math.Round(Config.Current.ZoomFactor * 100)}%  (reset)";
        _mainMenu.Show(_menuButton, new Point(_menuButton.Width, _menuButton.Height), ToolStripDropDownDirection.BelowLeft);
    }

    void ToggleBookmarksBar()
    {
        Config.Current.ShowBookmarksBar = !Config.Current.ShowBookmarksBar;
        if (!_privateBrowsing) Config.Current.Save();
        RefreshBookmarks();
    }

    void PrintPage()
    {
        try { Current?.View.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch { }
    }

    void SetZoom(double factor)
    {
        factor = Math.Clamp(Math.Round(factor, 2), 0.3, 3.0);
        Config.Current.ZoomFactor = factor;
        foreach (var t in _open) try { t.View.ZoomFactor = factor; } catch { }
        if (!_privateBrowsing) Config.Current.Save();
    }

    void GoHome()
    {
        if (Current is not { } tab || tab.View.CoreWebView2 is not { } core) { _ = NewTabAsync(null); return; }
        tab.HasHomePage = true;
        tab.IsHomePage = true;
        core.Settings.IsScriptEnabled = true;
        MapHomeAssets(tab);
        core.NavigateToString(StartPage(tab.HomeNonce));
    }
}

/// An extension's toolbar popup: a small borderless window under the puzzle button, sized to the
/// popup page, closed when it loses focus or the page calls window.close().
sealed class ExtensionPopup : Form
{
    readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    readonly CoreWebView2Environment _env;
    readonly string _url;
    readonly Point _anchor;
    /// True once the popup page has loaded (read by the UI self-test).
    public bool Loaded { get; private set; }

    public ExtensionPopup(CoreWebView2Environment env, string url, Point anchorTopRight, string title)
    {
        _env = env;
        _url = url;
        _anchor = anchorTopRight;
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Line;
        Padding = new Padding(1);
        _view.DefaultBackgroundColor = Theme.Content;
        Controls.Add(_view);
        Place(Theme.S(360), Theme.S(460));
        Deactivate += (_, _) => { if (!IsDisposed && !_view.Focused && !ContainsFocus) Close(); };
    }

    void Place(int width, int height)
    {
        var area = Screen.FromPoint(_anchor).WorkingArea;
        var x = Math.Clamp(_anchor.X - width, area.Left, Math.Max(area.Left, area.Right - width));
        var y = Math.Clamp(_anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - height));
        Bounds = new Rectangle(x, y, width, height);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.Glass(this, backdrop: false);
        try
        {
            await _view.EnsureCoreWebView2Async(_env);
            if (IsDisposed) return;
            var core = _view.CoreWebView2;
            core.WindowCloseRequested += (_, _) => { if (!IsDisposed) Close(); };
            core.NewWindowRequested += (_, args) =>
            {
                // links in a popup open as tabs in the browser window, as in Chrome
                args.Handled = true;
                if (Owner is BrowserForm browser) browser.OpenTab(args.Uri);
            };
            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess || IsDisposed) return;
                Loaded = true;
                try
                {
                    await Task.Delay(120);
                    var json = await core.ExecuteScriptAsync(
                        "JSON.stringify([Math.ceil(document.documentElement.scrollWidth),Math.ceil(document.documentElement.scrollHeight)])");
                    var size = System.Text.Json.JsonSerializer.Deserialize<string>(json);
                    var dims = System.Text.Json.JsonSerializer.Deserialize<int[]>(size ?? "[]") ?? [];
                    if (dims.Length == 2 && !IsDisposed)
                    {
                        var zoom = _view.ZoomFactor;
                        var w = Math.Clamp((int)(dims[0] * zoom * Theme.Scale) + 2, Theme.S(180), Theme.S(800));
                        var h = Math.Clamp((int)(dims[1] * zoom * Theme.Scale) + 2, Theme.S(80), Theme.S(600));
                        Place(w, h);
                    }
                }
                catch { }
            };
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Close();
                BakaDialog.Info(Owner, "The extension popup could not open", ex.Message);
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
