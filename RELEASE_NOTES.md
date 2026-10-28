# Baka Browser 1.6.2 Release

Background videos on the new-tab page now play.

## Fixed in 1.6.2

- **New-tab background video plays.** A .webm or .mp4 picked under Settings > Appearance > "Choose image or video…" showed a still or blank page. The browser now serves the file itself, with byte-range support, and the UI test checks that both formats play.

## Fixed in 1.6.1

- **"Choose image or video…" works on a new profile.** Settings > Appearance > New tab page stopped with "Could not find a part of the path …\baka-browser\home-assets\…" because the folder for the background did not exist yet. The folder is now created first, and the self-check tests it.

## New in 1.6.0

- **Tab menu.** Right-click any tab: New tab, Reload, Duplicate, Mute or unmute the site, Copy address, Move to new window, Close tab, Close other tabs, Close tabs below (or to the right), Reopen closed tab.
- **Drag tabs.** Drag a tab along the strip to reorder it. Drag it off the strip and it opens in its own window where you drop it.
- **Sound indicator.** Tabs playing audio show a speaker; click it to mute that site, click again to unmute.
- **Keyboard.** `Ctrl+Shift+T` reopens the last closed tabs (up to 25), `Ctrl+1` to `Ctrl+8` jump to a tab, `Ctrl+9` to the last one. `Ctrl+W` from inside a page closes the tab reliably.
- **Fixes.** Closing a background tab no longer switches the selected tab. The zoom level is applied to new tabs straight after a settings change. Tabs playing audio are not put into low-memory mode. Windows opened from a dragged-out tab do not write over the main window's session, settings or history.

## Limits

- A tab dragged into a new window reloads its page there (it is a separate browser process).
- Store extensions do not update by themselves yet. To update one, remove it and add it again.
- The installer is not code-signed, so Windows SmartScreen may warn on first run.
