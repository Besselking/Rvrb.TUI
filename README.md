# RVRB TUI

A terminal chat client for [RVRB](https://app.rvrb.one), built with [Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) **v2**.
Chat only: no playback, voting or DJing (yet).

```bash
dotnet run             # launch (Esc quits)
dotnet run -- --login  # paste a new session token
```

## Logging in
RVRB logs in through Spotify and only redirects back to its own web app, so the token is pasted by hand once:

1. Open <https://app.rvrb.one/api/login/spotify> and log in.
2. Copy the URL you land on (`https://app.rvrb.one/login/?session=eyJ...`), or just the token, into the prompt.

The token is saved to `~/.config/rvrb/token` (mode 600) and refreshed tokens from the server are saved over it.
`RVRB_TOKEN` in the environment takes precedence over the saved one.

## Using it
- **Channels** (left): popular channels are listed; type in the box and press Enter to search. Enter on a channel joins it.
- **Chat** (middle): the last 100 messages, then live messages.
- **Message** (bottom): Enter sends. `/commands` are passed through to the server.
- **DJs** (top right): the DJ queue in play order, the playing DJ first (▶).
- **Listeners** (bottom right): everyone else in the channel.

Quitting (Esc, closing the terminal, or `kill`) leaves the channel first.

## Project layout
| File | Purpose |
|---|---|
| `Program.cs` | Console login, then runs the TUI. |
| `Client/Session.cs` | Session token + client id, persisted in `~/.config/rvrb`. |
| `Client/RvrbApi.cs` | REST routes used (`/api/login`, `/api/channels`, `/api/channel/{id}/chat`). |
| `Client/RvrbSocket.cs` | JSON-RPC 2.0 websocket (`wss://app.rvrb.one/ws`): requests, keepalive, reconnect. |
| `Client/ChatText.cs` | Turns the HTML chat payloads into plain text. |
| `Client/ChatWindow.cs` | The UI. |
| `AGENTS.md` | **Canonical Terminal.Gui v2 patterns + gotchas.** Read before editing UI code. |

API docs: [openapi.yaml](https://app.rvrb.one/api-docs/openapi.yaml) (REST), [asyncapi.yaml](https://app.rvrb.one/api-docs/asyncapi.yaml) (websocket).
