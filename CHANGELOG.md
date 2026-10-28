# Changelog

Notable changes to Baka Browser are recorded here. Versions are published as GitHub Releases by pushing a matching `vMAJOR.MINOR.PATCH` tag.

## [1.5.0] - 2026-10-28

### Added

- uBlock Origin Lite is added from the Chrome Web Store on first start (once per profile; removal is respected). Setting: Settings > Extensions > Ad blocker.
- `--ui-test` uploads a 3 MB photo and a 40 MB video through a page file input by form, `fetch`/`FormData` and chunked `XMLHttpRequest`, and reports 4K VP9/AV1/H.264/HEVC decoding support.

### Changed

- Background tabs run with WebView2 `MemoryUsageTargetLevel = Low`; the selected tab is set back to Normal.

## [1.4.0] - 2026-10-28

### Added

- Chrome Web Store install: an **Add to Baka** button on store extension pages. The `.crx` package is downloaded from Google's update service over HTTPS, its CRX3 signature is verified against the extension ID, it is unpacked with size and path checks, the store key is kept so the extension keeps its store ID, and the requested permissions are shown before install.
- Extensions toolbar button and menu: popups, options pages, on/off, remove, Chrome Web Store link, unpacked install.
- Main menu (Alt+F) with icons; page zoom, print, home button; `--ui-test` scripted UI run with screenshots.
- Settings > Extensions (installed list with switches) and Settings > About (version, WebView2 version, update check).
- Site icons for bookmarks and new-tab quick links, cached locally from pages already visited.
- Self-checks for store URLs, CRX parsing, signature and ID checks, tampered packages and zip-slip.

### Changed

- BAKA theme is flat and opaque (no Mica) to match BAKA Windows; compact toolbar (40 px), tab rail and bookmarks rail.
- Settings redesigned: section rail with icons, cards, switches, pill buttons, themed dialogs instead of Windows message boxes.
- Settings from versions before 1.3 that still use the old default (Dark with the blue accent) move to BAKA once.
- Version 1.3.0 was never published; its changes ship in this release.

## [1.3.0] - 2026-10-28 (not published)

### Added

- Baka theme: the BAKA Windows Carbon Mono palette (graphite surfaces, white text and accent). Default for new installs; listed in Settings > Appearance.

### Changed

- Default accent for new installs is #E4E4E7 (soft white) to match BAKA Windows.

## [1.2.0] - 2026-10-10

### Added

- Optional manual download and local caching for the recommended StevenBlack adware/malware hosts list, with size and content validation and a remove action.
- A Windows x64 Velopack one-click installer and GitHub release workflow, including WebView2 Runtime bootstrapping and background update packages.
- A pull-request and main-branch Windows build workflow.
- Documentation for setup, updates, privacy limits, and updating the existing GitHub repository.

### Changed

- Pinned the WebView2 SDK package version so CI builds are reproducible. The evergreen WebView2 Runtime still updates separately.
- Updated the product version to 1.2.0 and aligned release notes, package version validation, and tag conventions.
- Updated the README to reflect the actual browser engine and current capabilities.

### Fixed

- Save the regular window's last open tabs and selected tab during shutdown, even if the dashboard's debounced save timer has not fired.
- Prevent idle suspension while a background tab is loading or playing audio, and handle selection changes during suspension before leaving the tab asleep.
- Pass the GitHub Actions token to Velopack's GitHub publishing step so automated releases can be created with the workflow's `contents: write` permission.

### Known limitations

- The browser still uses WebView2/Chromium. The engine migration is not included in this release.
- The installer is unsigned; Windows SmartScreen may display an unknown-publisher warning until a code-signing certificate is configured.
- Ad blocking is host-based and cannot hide page elements or reliably block first-party ads.
