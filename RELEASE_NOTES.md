# Baka Browser 1.4.0 Release

Chrome Web Store extensions, a compact BAKA look that matches BAKA Windows, and a new Settings window.

## What's new

- **Chrome Web Store extensions.** Open an extension's page on the Chrome Web Store and press **Add to Baka** on the toolbar. Baka downloads the package from Google, checks its signature against the extension's ID, shows what it will be able to access, and installs it after you agree. Tested with Dark Reader and uBlock Origin Lite.
- **Extensions button** (puzzle icon): open an extension's popup, its options page, turn it off or on, or remove it. Unpacked extensions can still be loaded from a folder.
- **BAKA theme, compact chrome.** Flat graphite surfaces, a pill-shaped address bar, a slimmer toolbar and tab rail, and icons everywhere: toolbar, menus, bookmarks (with site icons), settings and dialogs. Older installs that never changed the default Dark theme move to BAKA once; Dark, Light and Custom remain in Settings.
- **New menu** (⋯, or Alt+F): new tab, private window, history, downloads, bookmarks bar, extensions, zoom, print, full screen, settings and about.
- **New Settings window** with a section rail, grouped cards, on/off switches, an Extensions page, and an About page with a "Check for updates" button.
- **New-tab page** restyled with mono line icons and quick-link tiles that show each site's icon.
- Page zoom (Ctrl+plus, Ctrl+minus, Ctrl+0), print (Ctrl+P), and a home button.

## Limits

- Store extensions don't update by themselves yet. To update one, remove it and add it again.
- Extensions run inside Microsoft Edge WebView2, which supports most but not all Chrome extension features. Extensions are off in private windows.
- Ad blocking built into the browser is domain-based; install an extension such as uBlock Origin Lite for element hiding.
- The installer is not code-signed, so Windows SmartScreen may warn on first run.
