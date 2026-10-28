# Baka Browser 1.6.0 Release

Tabs that behave like a full browser: a right-click menu, drag to reorder, drag out to a new window, and a mute button.

## What's new

- **Tab menu.** Right-click any tab: New tab, Reload, Duplicate, Mute or unmute the site, Copy address, Move to new window, Close tab, Close other tabs, Close tabs below (or to the right), Reopen closed tab.
- **Drag tabs.** Drag a tab along the strip to reorder it. Drag it off the strip and it opens in its own window where you drop it.
- **Sound indicator.** Tabs playing audio show a speaker; click it to mute that site, click again to unmute.
- **Keyboard.** `Ctrl+Shift+T` reopens the last closed tabs (up to 25), `Ctrl+1` to `Ctrl+8` jump to a tab, `Ctrl+9` to the last one. `Ctrl+W` from inside a page closes the tab reliably.
- **Fixes.** Closing a background tab no longer switches the selected tab. The zoom level is applied to new tabs straight after a settings change. Tabs playing audio are not put into low-memory mode. Windows opened from a dragged-out tab do not write over the main window's session, settings or history.
- **Smaller and cleaner.** Unused code paths and settings were removed.

## Limits

- A tab dragged into a new window reloads its page there (it is a separate browser process).
- Store extensions do not update by themselves yet. To update one, remove it and add it again.
- The installer is not code-signed, so Windows SmartScreen may warn on first run.
