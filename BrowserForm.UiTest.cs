using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Baka;

/// `baka.exe --profile-dir <test folder> --ui-test <report folder>` drives the real browser through the
/// things a person does: the start page, menus, a Chrome Web Store install, an extension popup, every
/// settings page, bookmarks, zoom, tabs, and the themes. It saves a screenshot per step and a report,
/// then exits with the number of failed steps. Use a throwaway --profile-dir: it installs extensions.
sealed partial class BrowserForm
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// PW_RENDERFULLCONTENT (2) includes the WebView2 surface, which a plain PrintWindow leaves black.
    static Bitmap CaptureWindow(IntPtr hwnd, Size size)
    {
        var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try { PrintWindow(hwnd, hdc, 2); } finally { g.ReleaseHdc(hdc); }
        return bmp;
    }

    const string DarkReaderId = "eimadpbcbfnmbkopoojfekhnkhdbieeh";
    const string UBlockLiteId = "ddkjiahejlhfcafbddmgiahcphecmpfh";

    async Task RunUiTestAsync(string reportDir)
    {
        var log = new List<string>();
        var fails = 0;
        var shots = 0;
        Directory.CreateDirectory(reportDir);
        void Step(bool ok, string what)
        {
            log.Add((ok ? "ok   " : "FAIL ") + what);
            if (!ok) fails++;
        }
        void Note(string what) => log.Add("     " + what);
        async Task Wait(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Application.DoEvents(); await Task.Delay(50); } }
        async Task<bool> WaitFor(Func<bool> condition, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (condition()) return true; await Task.Delay(150); }
            return condition();
        }
        // Screenshots come from the browser's own windows (PrintWindow), never from the screen, so a
        // test run can't record anything else the person at the PC has open. An open menu is drawn on
        // top of the window image at its own position.
        void Shot(string name, Form? form = null, ToolStripDropDown? menu = null)
        {
            try
            {
                var target = form ?? this;
                using var bmp = CaptureWindow(target.Handle, target.Size);
                if (menu is { Visible: true } && menu.IsHandleCreated)
                {
                    using var menuImage = CaptureWindow(menu.Handle, menu.Size);
                    using var g = Graphics.FromImage(bmp);
                    g.DrawImage(menuImage, menu.Left - target.Left, menu.Top - target.Top);
                }
                bmp.Save(Path.Combine(reportDir, $"{++shots:00}-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { Note($"screenshot {name} failed: {ex.Message}"); }
        }
        async Task<string> Eval(string script)
        {
            if (Current?.View.CoreWebView2 is not { } core) return "";
            try { return await core.ExecuteScriptAsync(script); } catch (Exception ex) { return "error: " + ex.Message; }
        }
        // Every step works in one tab that is re-selected first: extensions open their own welcome tabs
        // on install (Dark Reader opens its help page), which would otherwise move "the current tab".
        Tab? testTab = null;
        async Task Go(string url, int settle = 2500)
        {
            testTab ??= Current;
            if (testTab is null || !_open.Contains(testTab)) testTab = Current;
            if (testTab is null) return;
            _tabs.Select(_open.IndexOf(testTab));
            Navigate(testTab, url);
            await WaitFor(() => testTab.IsLoading == false && testTab.View.CoreWebView2?.Source.StartsWith(url[..Math.Min(url.Length, 20)], StringComparison.OrdinalIgnoreCase) == true, 25_000);
            await Wait(settle);
            _tabs.Select(_open.IndexOf(testTab));
            await Wait(300);
        }

        try
        {
            WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(40, 40, Theme.S(1280), Theme.S(800));
            await Wait(2500);
            var menuOpened = false;
            _mainMenu.Opened += (_, _) => menuOpened = true;

            // 1. start page
            Step(Current?.IsHomePage == true, "the new-tab page opens at startup");
            Step(_extensionsButton.Visible == _extensionsAvailable, $"extensions button visible ({_extensionsAvailable})");
            Shot("new-tab");

            // 2. main menu
            ShowMainMenu();
            await Wait(700);
            Step(!_extensionsAvailable || _mainMenu.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == "Extensions"), "the main menu has an Extensions entry");
            Step(menuOpened && _mainMenu.Items.OfType<ToolStripMenuItem>().All(i => i.Image is not null),
                $"main menu opens and every item has an icon (opened={menuOpened}, items={_mainMenu.Items.Count})");
            Shot("main-menu", menu: _mainMenu);
            _mainMenu.Close();

            // 3. Chrome Web Store: Add to Baka, real download, signature check, install
            if (_extensionsAvailable)
            {
                await Go($"https://chromewebstore.google.com/detail/dark-reader/{DarkReaderId}", 4000);
                Step(_storeButton.Visible, "Add to Baka appears on a Chrome Web Store extension page");
                Shot("web-store");
                _autoConfirmInstall = true;
                foreach (var id in new[] { DarkReaderId, UBlockLiteId })
                {
                    try
                    {
                        var installed = await InstallFromStoreAsync(id);
                        Step(installed == id, $"Chrome Web Store install of {id} keeps the store ID (got {installed})");
                    }
                    catch (Exception ex) { Step(false, $"Chrome Web Store install of {id}: {ex.Message}"); }
                }
                var list = await ListExtensionsAsync();
                foreach (var e in list) Note($"installed: {e.Name} {e.Version} id={e.Id} enabled={e.Enabled} popup={e.PopupPage} options={e.OptionsPage}");
                Step(list.Any(e => e.Id == DarkReaderId) && list.Any(e => e.Id == UBlockLiteId), "both extensions are listed");
                Step(list.All(e => !BuiltInExtensionIds.Contains(e.Id)), "WebView2's own built-in extensions are not listed");
                Note($"tabs after install: {string.Join(" | ", _open.Select(t => t.View.CoreWebView2?.Source))}");
                if (testTab is not null && _open.Contains(testTab)) _tabs.Select(_open.IndexOf(testTab));
                await Wait(400);
                UpdateStoreButton();
                Step(_storeButton.Text == "Added to Baka" && !_storeButton.Enabled, "the store button changes to Added to Baka");
                Shot("web-store-added");

                // 4. the extension actually runs: Dark Reader injects its style sheet into pages
                // A plain light page: example.com now has its own dark mode, which Dark Reader leaves alone.
                await Go("https://info.cern.ch/hypertext/WWW/TheProject.html", 4000);
                // Dark Reader marks the page it themes (attribute and/or style elements) and repaints it dark.
                var dark = await Eval("JSON.stringify({attr:document.documentElement.getAttribute('data-darkreader-mode')||document.documentElement.getAttribute('data-darkreader-scheme'),styles:document.querySelectorAll('.darkreader').length,bg:getComputedStyle(document.documentElement).backgroundColor+'/'+getComputedStyle(document.body).backgroundColor})");
                Note("info.cern.ch with Dark Reader: " + dark);
                var themed = false;
                try
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(dark) ?? "{}");
                    themed = doc.RootElement.GetProperty("attr").ValueKind == JsonValueKind.String ||
                             doc.RootElement.GetProperty("styles").GetInt32() > 0;
                }
                catch { }
                Step(themed, "Dark Reader's content script themes a light page");
                Shot("extension-running");

                // 5. extensions menu and a popup
                var extMenuOpened = false;
                _extensionsMenu.Opened += (_, _) => extMenuOpened = true;
                await ShowExtensionsMenuAsync();
                await Wait(700);
                Step(extMenuOpened && _extensionsMenu.Items.Count >= 7, $"the extensions menu lists extensions and store actions ({_extensionsMenu.Items.Count} items)");
                Shot("extensions-menu", menu: _extensionsMenu);
                _extensionsMenu.Close();
                if (list.FirstOrDefault(e => e.Id == DarkReaderId) is { PopupPage: not null } dr)
                {
                    OpenExtensionPopup(dr);
                    var opened = await WaitFor(() => _popup is { Loaded: true }, 15_000);
                    await Wait(1500);
                    Step(opened, $"the Dark Reader popup loads (chrome-extension://{dr.Id}/{dr.PopupPage})");
                    if (_popup is { } p) Shot("extension-popup", p);
                    _popup?.Close();
                    await Wait(500);
                }
                else Step(false, "Dark Reader has a popup page");

                // 6. options page in a tab
                if (list.FirstOrDefault(e => e.OptionsPage is not null) is { } withOptions)
                {
                    OpenExtensionOptions(withOptions.Id);
                    var ok = await WaitFor(() => Current?.View.CoreWebView2?.Source.StartsWith("chrome-extension://") == true && Current.IsLoading == false, 15_000);
                    await Wait(1500);
                    Step(ok, $"{withOptions.Name} options page opens in a tab");
                    Shot("extension-options");
                    CloseTab(_tabs.SelectedIndex);
                    await Wait(500);
                }

                // 7. turn off / on
                await SetExtensionEnabledAsync(UBlockLiteId, false);
                Step((await ListExtensionsAsync()).First(e => e.Id == UBlockLiteId).Enabled == false, "an extension can be turned off");
                await SetExtensionEnabledAsync(UBlockLiteId, true);
                Step((await ListExtensionsAsync()).First(e => e.Id == UBlockLiteId).Enabled, "and turned back on");
            }

            // 8. every settings page
            var settings = new SettingsForm(_privateBrowsing, ClearSiteDataAsync, this, UpdateRecommendedBlocklistAsync,
                RemoveRecommendedBlocklist, CheckForUpdatesNowAsync);
            settings.StartPosition = FormStartPosition.Manual;
            settings.Location = new Point(Left + Theme.S(60), Top + Theme.S(60));
            settings.Show(this);
            await Wait(1200);
            foreach (var page in new[] { "Privacy", "Appearance", "Search", "Extensions", "Tabs & bookmarks", "Performance", "Compatibility", "Advanced", "About" })
            {
                settings.ShowPage(page);
                await Wait(page == "Extensions" ? 2500 : 600);
                settings.Refresh();
                await Wait(300);
                Shot("settings-" + page.Replace(" & ", "-").ToLowerInvariant(), settings);
            }
            Step(true, "all nine settings pages open");
            settings.Close();
            settings.Dispose();
            await Wait(600);

            // 9. bookmarks with icons, zoom, tabs
            await Go("https://github.com/OldcarryG/Baka-browser", 3000);
            var before = Bookmarks.All.Count;
            if (!Bookmarks.Has(Current?.View.CoreWebView2?.Source ?? "")) ToggleBookmark();
            Step(Bookmarks.All.Count >= before && Bookmarks.Has(Current?.View.CoreWebView2?.Source ?? ""), "Ctrl+D bookmarks the page");
            Step(_star.Glyph == Theme.StarFill, "the star fills for a bookmarked page");
            SetZoom(1.25);
            Step(Math.Abs((Current?.View.ZoomFactor ?? 0) - 1.25) < 0.01, "zoom in applies to the page");
            Shot("bookmarked-zoomed");
            SetZoom(1.0);
            var count = _open.Count;
            await NewTabAsync(null);
            await Wait(1200);
            Step(_open.Count == count + 1 && Current?.IsHomePage == true, "Ctrl+T opens a new-tab page");
            Shot("new-tab-with-quick-link");
            CloseTab(_tabs.SelectedIndex);
            Step(_open.Count == count, "Ctrl+W closes it");

            // 10. top tabs layout and collapsed rail
            Config.Current.TabsPosition = "Top";
            await ApplySettingsAsync();
            await Wait(1200);
            Shot("tabs-top");
            Config.Current.TabsPosition = "Left";
            Config.Current.TabsCollapsed = true;
            Config.Current.BookmarksCollapsed = true;
            await ApplySettingsAsync();
            await Wait(1000);
            Shot("rails-collapsed");
            Config.Current.TabsCollapsed = false;
            Config.Current.BookmarksCollapsed = false;

            // 11. themes
            foreach (var theme in new[] { "Light", "Dark", "Baka" })
            {
                Config.Current.ThemeMode = theme;
                await ApplySettingsAsync();
                await Wait(1200);
                Step(Theme.Contrast(Theme.Text, Theme.Content) >= 4.5, $"{theme}: text contrast {Theme.Contrast(Theme.Text, Theme.Content):0.0}:1");
                Shot("theme-" + theme.ToLowerInvariant());
            }
            GoHome();
            await Wait(1500);
            Shot("baka-home");

            // 12. removing an extension deletes its unpacked copy
            if (_extensionsAvailable)
            {
                var folder = WebStore.LoadIndex().GetValueOrDefault(UBlockLiteId);
                await RemoveExtensionAsync(UBlockLiteId);
                Step(!(await ListExtensionsAsync()).Any(e => e.Id == UBlockLiteId) && (folder is null || !Directory.Exists(folder)),
                    "removing an extension uninstalls it and deletes its files");
            }
        }
        catch (Exception ex) { Step(false, "UI test stopped: " + ex); }

        log.Add(fails == 0 ? "all passed" : $"{fails} failed");
        File.WriteAllLines(Path.Combine(reportDir, "ui-test.txt"), log);
        File.WriteAllText(Path.Combine(reportDir, "ui-test.json"), JsonSerializer.Serialize(new { fails, steps = log }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = fails;
        Close();
    }
}
