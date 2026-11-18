# Security

## Reporting a vulnerability
Please don't open a public issue for security problems. Use GitHub's private reporting instead: **Security → Report a vulnerability** on this repository. I'll reply as soon as I can.

## What Zipcap does with your keys
- Your Notion integration secret and Todoist API token are saved only in `%APPDATA%\Zipcap`, encrypted with Windows DPAPI (CurrentUser scope).
- They are sent only in the `Authorization` header to `api.notion.com` and `api.todoist.com` over HTTPS. They are never logged, shown or sent anywhere else.
- Zipcap has no server, telemetry or analytics.
- Update checks call `api.github.com` with no identifying data. The updater only runs `ZipcapSetup.exe` downloaded from this repository's release assets.

If you think a key has leaked, revoke it: Notion → Settings → Connections / integrations, or Todoist → Settings → Integrations → Developer. Then paste a new one from the Zipcap tray menu.
