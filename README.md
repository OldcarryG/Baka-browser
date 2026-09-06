# baka

A small Windows browser that ships with real ad and tracker blocking already installed, and the
phone-home switches off.

It does **not** bundle a browser engine. It drives the WebView2 (Chromium) runtime that Windows 11
already has, so the whole app is a 1.2 MB exe instead of a 200 MB second copy of Chromium.

## Build & run

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Output is `bin/Release/net10.0-windows/win-x64/publish/baka.exe` (1.25 MB) plus a 164 KB
`WebView2Loader.dll`. Needs .NET 10 to build; the WebView2 runtime ships with Windows 11.

```bash
dotnet run -c Release
```

## Installer

An MSI, built from `installer/baka.wxs` with [WiX 6](https://wixtoolset.org) (`dotnet tool install
--global wix`). Publish first, then build the package from inside `installer/`:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```
```bash
wix build baka.wxs -arch x64 -d Publish=../bin/Release/net10.0-windows/win-x64/publish -o baka-1.0.0-x64.msi
```

The version is read out of the exe (`!(bind.fileVersion.baka.exe)`), so `<Version>` in `baka.csproj`
is the only place it is written down.

It installs **per user, without admin**: `baka.exe` and `WebView2Loader.dll` into
`%LOCALAPPDATA%\Programs\baka\` — where Windows puts per-user apps, next to VS Code and Teams — plus a
Start-menu shortcut and an Apps & features entry. There is nothing to gain from Program Files here
except a UAC prompt on a 1.4 MB copy.

There is no wizard. Two files into a folder you already own needs a progress bar, not four Next
buttons, and every dialog set WiX ships opens with a licence page baka does not have; the install is
over in well under a second, so baka opening is the "finished" signal, the same thing VS Code and
Discord do. That only happens on a real interactive install — `/qn` and `/qb` stay silent.

**Prerequisites are deliberately not chained.** A framework-dependent .NET app that cannot find its
runtime gets Windows' own dialog naming the exact version, architecture and download URL, which is
better than anything the installer could say; WebView2 ships in-box on Windows 11, and baka offers
Microsoft's download page itself if the runtime fails to start. Chaining both would mean a Burn bundle
and a ~60 MB download to re-check what is almost always already there.

Uninstalling removes both files, the shortcut and the registry marker, and leaves
`%LOCALAPPDATA%\baka-browser\` — your bookmarks, history, extensions and profile — alone. Reinstalling
replaces the installed copy rather than stacking a second entry, even at the same version, and
Restart Manager closes a running baka to free the exe instead of asking for a reboot.

## Extensions

Two real upstream extensions are downloaded on first run and enabled by default:

| | what it does | source |
|---|---|---|
| **uBlock Origin Lite** | blocks ads and ad domains | latest `.edge.zip` release from `uBlockOrigin/uBOL-home` |
| **Privacy Badger** | blocks trackers that follow you, sends `DNT` and `Sec-GPC` | EFF's Chrome Web Store build (CRX3) |

**Substitution you should know about:** uBlock Origin *proper* is a Manifest V2 extension, and
Chromium 152 — the version WebView2 is on — removed MV2 support entirely. uBlock Origin Lite is the
same project's MV3 build and is the only one that will load. Its filtering is declarative, so it is
lighter but slightly less capable than the classic one on hand-written cosmetic rules.

Scores **99/100** on adblock-tester.com out of the box (11 ad services, 22 checks), with no
configuration.

Settings (gear icon, or `Ctrl+,`) lists everything installed with a checkbox to turn each on or off —
the toggle takes effect immediately, no restart — plus **Options** to open that extension's own
settings page in a tab, and **Remove** for the ones you added yourself. "Update extensions"
re-downloads the two bundled ones; new versions load on the next launch, because a directory the
engine has open cannot be replaced in place.

### Installing your own

Paste a Chrome Web Store link (or just the 32-letter id out of one) and press **Install**, or point
**Load folder…** at an unpacked extension that has a `manifest.json` in it. Either way baka lists
what the extension asks for and waits for a yes before the engine ever sees it, and refuses Manifest
V2 outright — Chromium 152 cannot run it.

Store downloads go through the same unauthenticated `clients2.google.com` endpoint Chrome uses:
no account, nothing sent about you. The store's own `update_url` is then stripped from the unpacked
copy, so Chromium stops re-checking it against Google every few hours behind your back. (Left in, it
also fails visibly: background networking is off, so each failed check lands in the download list as
a broken `.crx`.)

**Pin** puts an extension's icon in the toolbar; clicking it opens that extension's own popup in a
floating panel, or its options page in a tab if it has no popup. See Known limits for what a pinned
popup cannot do.

An extension that declares `chrome_url_overrides.newtab` — a new-tab dashboard — becomes the page
`Ctrl+T` opens. WebView2 has no new-tab concept of its own and ignores the override, so baka does
that substitution itself; the first enabled one wins, as in Chrome.

## What else is on

**Tracking off.** Strict tracking prevention, no autofill, no password autosave, and no
sync/telemetry/crash-upload/SmartScreen URL reporting (see `flags.txt`). `DNT` and `Sec-GPC` come
from Privacy Badger rather than a per-request hook in the app, which keeps every subresource off the
app's IPC path.

**Resource use.** One renderer per site rather than per tab, background tabs stop compositing, and
tabs idle for 5 minutes are suspended. Measured with 3 tabs on 3 distinct sites, 2 gone idle:

| | processes | RAM |
|---|---|---|
| 3 tabs active | 8 | 731 MB |
| 2 tabs suspended | 8 | **499 MB** |

Honest caveat: the ~380 MB floor for a single tab is Chromium's, not ours — this is the same engine
Edge and Chrome use, so a like-for-like page will not use less RAM than Edge. What you save is disk
(~1 MB vs ~200 MB), background services, and the RAM of tabs you are not looking at.

## Looks

**No title bar.** The tab strip *is* the caption. `WM_NCCALCSIZE` gives the frame's top row back to
the client area and `WM_NCHITTEST` answers `HTCAPTION` for the empty space beside the tabs, so
dragging, double-click-to-maximize and the system menu all still work on a window that has no bar of
its own. Maximizing needs the borrowed row back or the client area hangs above the screen, which is
what `IsZoomed` is checked for.

**Snap layouts still work.** Hovering the maximize button opens Windows 11's snap-layouts flyout. There
is no API that asks for it: a window earns it by answering `HTMAXBUTTON` from `WM_NCHITTEST` over that
one rect while `WS_MAXIMIZEBOX` is set. The price is that Windows then owns the rect and the control
stops seeing the mouse there, so the hover arrives as `WM_NCMOUSEMOVE` and the click as
`WM_NCLBUTTONDOWN`/`UP` — both routed into the same `CapClick` an ordinary click uses, so there is one
implementation of "maximize" and not two.

**Scaling.** The app is `PerMonitorV2`, so Windows does not bitmap-stretch it: point-sized fonts grow
with the display and a hardcoded pixel does not. Every layout number is written for a 96-dpi screen and
passed through `Theme.S()`, one scale read from `GetDpiForSystem()` on first touch of `Theme` — before
any control exists to ask, which is why it is not read from a form. Sizes that were *measured* rather
than declared are left alone, because `MeasureText` already answers in real pixels.

**Glass, for free.** The dark frame, rounded corners and the frosted tint behind the toolbar are
Windows' own Mica backdrop, switched on with four `DwmSetWindowAttribute` calls at window creation.
The compositor draws it; there is no per-frame work, no blur bitmap and no timer in the app. Two
rules it imposes:

- Paint the chrome with GDI, not a GDI+ `SolidBrush`. A GDI+ fill writes alpha 255 over the whole
  client area and the backdrop disappears behind it. The reverse also bites: a native `TextBox` erases
  with GDI and leaves alpha 0, so the address field needs an opaque parent behind it or the wallpaper
  shows through the text.
- Dark mode alone does not stop the accent colour appearing on captions that still exist (the settings
  dialog). Attribute 35 pins that to the sheet colour — the fourth call.

The tab strip is the one band painted opaque, close enough to Mica-over-black (`#101012` against a
measured `#0D0A0E`) that the seam is invisible. That is what lets inactive tabs go unfilled instead of
carrying nine identical grey pills.

**Icon and favicons.** `baka icon.jpg` is masked into a squircle and written out as a six-frame
uncompressed-DIB `.ico` (16 → 64) by `mkicon.ps1`, which is both the exe's Win32 icon and an embedded
resource for the window. Every frame has to be a plain DIB: `System.Drawing.Icon`, which is what
`Form.Icon` parses, renders PNG-compressed frames as noise. Tabs show the site's real favicon, taken
from the engine's own cache via `FaviconChanged` and rasterised once to the box the strip draws (16 px
at 100%, larger on a scaled display), with a globe when a site has none.

**Address bar.** A padlock on `https`, a red warning mark on plain `http`, a magnifier when there is
no transport to speak of. The field is capped at 760 px and centred rather than stretched edge to
edge, because a URL never needs 1100 px and a full-width field reads as a horizon line.

## History and downloads

`Ctrl+H` opens history in a tab: newest first, grouped by day, with a filter box that runs inside the
page. `Ctrl+J` opens the engine's own download panel, which is also where a download in progress
shows itself.

WebView2 has no history API and no `chrome://history`, so this is baka's own: one JSON line per visit
in `history.jsonl`, newest 3000 kept, `http`/`https` only — `about:`, `data:` and extension pages are
plumbing, not places you went. It never leaves the machine. Settings → Advanced → **Clear history**
deletes the file.

The history page is rendered HTML with no message channel back into the app, deliberately: filtering
happens in the page, and clearing happens in the settings dialog, so no web content has a way to ask
the app for anything.

## Bookmarks

`Ctrl+D` bookmarks the current page (the star in the toolbar fills in). The bar under the toolbar
appears once you have one; `Ctrl+Shift+B` hides it. On a bookmark: left-click opens it in the current
tab, middle-click in a new tab, right-click removes it. Stored in `bookmarks.json`.

No folders — a flat bar is the whole feature.

## Search engine

Settings → Search picks between DuckDuckGo, Startpage, Brave, Ecosia, Google and Bing, or takes any
custom URL with `{q}` where the query goes. Anything you type in the address bar that isn't a URL
goes there.

## Keys

`Ctrl+T` new tab · `Ctrl+W` close tab · `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle tabs · middle-click a tab
to close · `Ctrl+L` address bar · `F5` reload · `Alt+←/→` back/forward · `Ctrl+D` bookmark ·
`Ctrl+Shift+B` bookmarks bar · `Ctrl+H` history · `Ctrl+J` downloads · `Ctrl+,` settings

All of them work while the page has keyboard focus. That needs saying because it is the one thing
WinForms will not do for you: a focused WebView owns the keyboard, and neither `ProcessCmdKey` nor
`ProcessDialogKey` on the form ever sees the keystroke. The engine hands accelerators back as a plain
`KeyDown` on the WebView2 control, so that is where the shortcut table is hooked.

## Config

Everything lives in `%LOCALAPPDATA%\baka-browser\` (Settings → Advanced → Open data folder):

- `settings.json` — what the settings dialog writes: the engine picker, tracking level, idle timeout,
  bookmarks bar, and per-extension `ExtensionOn` / `ExtensionPinned` / `Extra` (the ones you added).
- `flags.txt` — Chromium switches, one per line, `#` to comment out. Editing beats rebuilding.
  Re-enable Microsoft's SmartScreen malware blocklist by removing `msSmartScreenProtection` from the
  `--disable-features` line, at the cost of your visited URLs being sent to Microsoft.
- `bookmarks.json` — the bookmarks bar.
- `history.jsonl` — one visit per line, oldest first. Delete it and history is gone.
- `extensions/` — the unpacked extensions. `extensions/<name>.new` is a staged update.
- `profile/` — the WebView2 profile (cookies, storage, extension state).

Command line: `--idle-minutes=N` (0 disables suspension), `--self-check`, plus any number of URLs to
open as tabs.

## Tests

```bash
bin/Release/net10.0-windows/baka.exe --self-check
```

41 assertions over address-bar input handling, the security mark, palette contrast, CRX3 unwrapping
(including a hostile header length), manifest reading, `update_url` removal, what counts as history, and
the DPI arithmetic (identity at 96 dpi, rounded rather than truncated so a 1 px hairline survives).
Exit code is the failure count. Run the exe rather than `dotnet run`: it is a `WinExe`, so it attaches
to the console it was launched from and `dotnet run` gives it none.

## Known limits

- Blocking is whatever the extensions do. Same-origin ads (YouTube pre-rolls) still need a real filter
  engine that uBOL's declarative rules cannot express.
- **Proxy and VPN extensions cannot work, and baka cannot stand in for them.** WebView2 does not host
  `chrome.proxy`, so an extension whose whole job is routing your traffic (Urban VPN and friends)
  installs, enables, and then does nothing. The obvious workaround does not work either: Microsoft
  documents `--proxy-server` as a supported WebView2 flag, and it does reach the engine's command line
  from `flags.txt`, but WebView2 152 ignores it — with a listener sitting on the proxy address, not one
  connection arrives, and pages load direct through a proxy that isn't there. `host:port`,
  `http://host:port` and `socks5://host:port` all behave the same. So there is no proxy setting in the
  app, because there is nothing to hook it to. A VPN client that routes at the network adapter works
  fine, because that is below the browser and needs nothing from it.
- A pinned popup is the extension's popup HTML in a plain WebView — WebView2 exposes no extension
  action API, so per-site controls inside it may not know which page you are looking at, and the icon
  cannot show a badge or a count.
- Manifest V2 extensions do not load, here or in any Chromium 152 browser.
- Extension updates need a restart to take effect.
- No bookmark folders.
- One scale for the session. Dragging the window to a second monitor at a different scale keeps the
  metrics it started with; that needs `WM_DPICHANGED` and a full relayout.
