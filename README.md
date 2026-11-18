# Zipcap

**Catch every thought.** Press <kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd> anywhere, type, press <kbd>Enter</kbd>. The note lands at the bottom of a Notion page you pick. Start it with `t:` and it becomes a Todoist task instead.

Free and open source, for Windows 10 and 11. **Currently in beta.**

**[Download ZipcapSetup.exe](https://github.com/ZahinArman/zipcap/releases/latest/download/ZipcapSetup.exe)** · [Website and setup guide](https://zahinarman.github.io/zipcap/)

## Features
- A small floating panel that opens over any app. <kbd>Enter</kbd> saves, <kbd>Shift</kbd>+<kbd>Enter</kbd> adds a new line, <kbd>Ctrl</kbd>+<kbd>↓</kbd> makes it taller, <kbd>Esc</kbd> closes it.
- `t: submit lab fri 8pm #Study p1` becomes a Todoist task. Todoist's Quick Add understands the dates, `#project`, `@label` and `p1`–`p4`.
- Works offline: every note is written to disk first and sent when you're back online. Nothing is lost if the PC shuts down mid-send.
- Follows the Windows light/dark theme.
- Checks GitHub for new releases and offers a one-click update from the tray.

## Install
1. Download `ZipcapSetup.exe` from [Releases](https://github.com/ZahinArman/zipcap/releases/latest) and run it. Windows SmartScreen may warn because the installer isn't code-signed yet: click **More info → Run anyway**.
2. Zipcap asks for a Notion API token (Notion → Developer tools → New connection), then the link of the page to write to (give the connection that page under Content access first). The [website](https://zahinarman.github.io/zipcap/#notion) has short videos of every step.

It installs per user (no admin) into `%LOCALAPPDATA%\Programs\Zipcap`. Uninstall it from **Settings → Apps**.

## Privacy and security
- Your Notion API token and Todoist token are stored only on your PC, in `%APPDATA%\Zipcap`. They are encrypted with Windows DPAPI, so only your Windows account can read them.
- Zipcap connects only to `api.notion.com`, `api.todoist.com` and `api.github.com` (for update checks), all over HTTPS. It has no telemetry, no analytics, and no server of its own.
- The updater only downloads installers published as release assets of this repository.

See [SECURITY.md](SECURITY.md) to report a vulnerability.

## Build from source
No SDK needed. It uses the C# compiler that ships with Windows (.NET Framework 4.8):

```
build.cmd
```

This produces `Zipcap.exe`. To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and run `ISCC installer\zipcap.iss`.

Maintainers publish a release with `.\release.ps1 0.2.0 "notes"`. It bumps the version, builds the exe and installer, runs `tools\scan-secrets.ps1`, tags, pushes and creates the GitHub release.

Testing hooks (environment variables):
- `ZIPCAP_API`: Notion API base URL. Point it at a dead address to simulate being offline.
- `ZIPCAP_TODOIST`: Todoist API base URL.
- `ZIPCAP_THEME`: `light` or `dark`.

## License
[MIT](LICENSE)

Made by [Zahin Arman](https://github.com/ZahinArman).
