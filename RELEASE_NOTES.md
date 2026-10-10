# Baka Browser 1.2.0 Release

This release improves local privacy controls and background-tab reliability, restores regular tabs on restart, and adds a one-click Windows installer with automatic update packages.

## What's new

- Install the Windows x64 build with `BakaBrowser-Setup.exe`; setup installs per-user and can bootstrap WebView2 when it is missing.
- Installed copies quietly download updates and apply them the next time the browser starts.
- Update or remove an optional locally cached ad/tracker hosts list from Settings.
- Regular windows now save their open tabs and selected tab at close.
- Idle suspension skips tabs that are still loading or playing audio and recovers from a tab becoming active during suspension.
- CI builds the Windows app for pushes and pull requests.

## Privacy and compatibility

Ad blocking remains domain-based and may miss ads served by the page's own domain. The browser still uses WebView2/Chromium; this release does not remove the Chromium engine. See the README for details and current limitations.
