using System.Diagnostics;

namespace Baka;

/// The tab right-click menu, reopening closed tabs, and moving a tab into its own window.
sealed partial class BrowserForm
{
    /// The address worth reopening or moving, or null for the new-tab page and internal pages.
    static string? TabUrl(Tab tab)
    {
        var source = tab.View.IsDisposed ? "" : tab.View.CoreWebView2?.Source ?? "";
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" && source.Length <= 4096
            ? source : null;
    }

    void ShowTabMenu(int index, Point screen)
    {
        if (_closing || index < 0 || index >= _open.Count) return;
        var tab = _open[index];
        var core = tab.View.CoreWebView2;
        var url = TabUrl(tab);
        var below = Config.Current.TabsPosition == "Left" ? "below" : "to the right";
        // the old items hold the previous tab in their click handlers: dispose them, do not just drop them
        foreach (var old in _tabMenu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        _tabMenu.Items.Clear();
        _tabMenu.Items.Add(MenuItem("New tab", Theme.Add, () => _ = NewTabAsync(null), "Ctrl+T"));
        _tabMenu.Items.Add(new ToolStripSeparator());
        _tabMenu.Items.Add(MenuItem("Reload", Theme.Reload, () => { try { tab.View.CoreWebView2?.Reload(); } catch { } }, "Ctrl+R"));
        var duplicate = MenuItem("Duplicate", Theme.Copy, () => { if (TabUrl(tab) is { } u) _ = NewTabAsync(u); });
        duplicate.Enabled = url is not null;
        _tabMenu.Items.Add(duplicate);
        bool? muted = null;
        try { muted = core?.IsMuted; } catch { }   // older runtimes have no mute API
        if (muted is { } isMuted)
            _tabMenu.Items.Add(MenuItem(isMuted ? "Unmute site" : "Mute site", isMuted ? Theme.Volume : Theme.MuteIcon,
                () => { try { tab.View.CoreWebView2!.IsMuted = !isMuted; } catch { } }));
        var copy = MenuItem("Copy address", Theme.Link, () => { if (TabUrl(tab) is { } u) try { Clipboard.SetText(u); } catch { } });
        copy.Enabled = url is not null;
        _tabMenu.Items.Add(copy);
        var move = MenuItem("Move to new window", Theme.OpenNew, () => MoveTabToNewWindow(_open.IndexOf(tab), null));
        // a private window's sign-ins live in its own temporary profile, which a new window cannot share
        move.Enabled = (url is not null || tab.IsHomePage) && _open.Count > 1 && !_privateBrowsing;
        _tabMenu.Items.Add(move);
        _tabMenu.Items.Add(new ToolStripSeparator());
        _tabMenu.Items.Add(MenuItem("Close tab", Theme.Close, () => CloseTab(_open.IndexOf(tab)), "Ctrl+W"));
        var others = MenuItem("Close other tabs", Theme.Tabs, () => CloseTabsWhere(t => t != tab));
        others.Enabled = _open.Count > 1;
        _tabMenu.Items.Add(others);
        var after = MenuItem($"Close tabs {below}", Theme.Tabs, () =>
        {
            var at = _open.IndexOf(tab);
            CloseTabsWhere(t => _open.IndexOf(t) > at);
        });
        after.Enabled = index < _open.Count - 1;
        _tabMenu.Items.Add(after);
        var reopen = MenuItem("Reopen closed tab", Theme.Undo, ReopenClosedTab, "Ctrl+Shift+T");
        reopen.Enabled = _closedTabs.Count > 0;
        _tabMenu.Items.Add(reopen);
        Theme.Style(_tabMenu);
        _tabMenu.Show(screen);
    }

    void CloseTabsWhere(Func<Tab, bool> close)
    {
        foreach (var t in _open.Where(close).ToList())
            if (_open.Count > 1) CloseTab(_open.IndexOf(t));
    }

    void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        var url = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        _ = NewTabAsync(url);
    }

    /// Opens the tab's page in a new browser window (same profile, or a new private window from a private
    /// one) and closes it here. `at` is where a dragged tab was dropped; the new window opens there.
    /// The most recent window started by MoveTabToNewWindow (the UI self-test checks and closes it).
    Process? LastDetachedWindow;

    void MoveTabToNewWindow(int index, Point? at)
    {
        if (_closing || index < 0 || index >= _open.Count) return;
        var tab = _open[index];
        var url = TabUrl(tab);
        if (url is null && !tab.IsHomePage) return;
        if (_open.Count == 1)
        {
            // Dragging the only tab moves this window instead of opening a copy.
            if (at is { } drop && WindowState == FormWindowState.Normal)
                Location = new Point(drop.X - Theme.S(80), drop.Y - Theme.S(16));
            return;
        }
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            if (_privateBrowsing) return;   // see ShowTabMenu: private sign-ins cannot move to another process
            var args = new List<string> { "--detached" };
            if (Args.Value("--profile-dir") is { } profile) { args.Add("--profile-dir"); args.Add(profile); }
            if (at is { } p)
            {
                args.Add(FormattableString.Invariant($"--x={p.X - Theme.S(80)}"));
                args.Add(FormattableString.Invariant($"--y={p.Y - Theme.S(16)}"));
            }
            if (url is not null) args.Add(url);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            LastDetachedWindow?.Dispose();
            LastDetachedWindow = Process.Start(start);
        }
        catch (Exception ex) { BakaDialog.Info(this, "Could not open a new window", ex.Message); return; }
        CloseTab(_open.IndexOf(tab), remember: false);
    }
}
