# Changelog

Notable changes to Baka Browser are recorded here. Versions are published as GitHub Releases by pushing a matching `vMAJOR.MINOR.PATCH` tag.

## [1.3.0] - 2026-10-28

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
