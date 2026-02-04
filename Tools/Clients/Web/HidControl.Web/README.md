# HidControl.Web (Skeleton)

Minimal web UI that drives the server's HID WebSocket endpoint (`/ws/hid`) via `HidControl.ClientSdk`.

## Run

Prereqs:
- .NET SDK 9+
- `HidControlServer` running (default: `http://127.0.0.1:8080`)

Environment variables (server target):
- `HIDBRIDGE_SERVER_URL` (default `http://127.0.0.1:8080`)
- `HIDBRIDGE_TOKEN` (optional, sent as `X-HID-Token`)

Run:

```powershell
dotnet run --project Tools\Clients\Web\HidControl.Web
```

Open the printed URL and use:
- **Shortcuts** (`Ctrl+C`, `Alt+Tab`, `Win+R`, etc.)
- **Keyboard text**: ASCII by default. For non-ASCII (layout-dependent), see `Docs/text_input_i18n.md`.
- **Mouse**: relative move + click

