# Baka Browser 1.5.0 Release

uBlock Origin Lite out of the box, lower memory for background tabs, and tested photo/video uploads and 4K video.

## What's new

- **uBlock Origin Lite on first start.** New profiles get the free, open-source uBlock Origin Lite ad and tracker blocker from the Chrome Web Store (signature checked like any store install). It is added once: if you remove it, it stays removed. Turn this off in Settings > Extensions > Ad blocker. The built-in domain blocker keeps running alongside it.
- **Less memory for background tabs.** Tabs you are not looking at ask the engine to trim caches and decoded images straight away (WebView2 `MemoryUsageTargetLevel = Low`); the tab you select goes back to normal. Idle tabs are still put to sleep after 2 minutes.
- **Uploads tested.** The UI self-test now uploads a 3 MB photo and a 40 MB video picked through a page's file input, with a normal form, with `fetch` + `FormData`, and in 8 MB `XMLHttpRequest` chunks with progress events (the way Google Photos, YouTube and Drive upload).
- **4K video check.** The self-test reports VP9, AV1, H.264 and HEVC 4K60 decoding support and whether the GPU decodes it.

## Limits

- If an upload still fails on a particular site, please open an issue with the site name: the generic upload paths above all pass.
- HEVC playback needs the HEVC codec from Microsoft Store (BAKA Windows installs it at first logon).
- Store extensions don't update by themselves yet. To update one, remove it and add it again.
- The installer is not code-signed, so Windows SmartScreen may warn on first run.
