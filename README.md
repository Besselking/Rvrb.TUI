# RVRB TUI

A terminal client for [RVRB](https://app.rvrb.one), built with [Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) **v2**:
chat, DJing from your RVRB queue, and the channel's music played on one of your Spotify devices. No voting (yet).

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
- **F2 Channels**: opens at startup. Popular channels are listed; type and press Enter to search, Enter again joins.
- **F3 Device**: the Spotify device to play on (also saved as your preferred device on RVRB).
- **F4 Playback**: turns playing the channel's tracks on your device on or off (on by default).
- **F5 Queue**: your RVRB DJ queue. Enter plays a track next, Shift+↑/↓ moves it, Del removes it, `a` adds tracks.
- **F6 Add tracks**: Spotify search; Enter in the box searches, Enter on a result adds it to the bottom of your queue.
- **F7 Step up / Step down**: join or leave the decks. While you DJ, your turn plays the top of your queue.
- **Now playing** (top): track, DJ, votes (▲ dope ▼ nope ★ star) and progress.
- **Chat** (middle): the last 100 messages, then live messages.
- **Message** (bottom): Enter sends. `/commands` are passed through to the server.
- **DJs** (top right): the DJ queue in play order, the playing DJ first (▶).
- **Listeners** (bottom right): everyone else in the channel.

Quitting (Esc or Ctrl+Q, closing the terminal, or `kill`) pauses playback and leaves the channel first.

### Playback
RVRB streams nothing itself: it tells Spotify to play the channel's track on one of your Spotify Connect devices
(the Spotify app on a computer or phone, a speaker, or `spotifyd`), using the Spotify login RVRB already has.
Spotify only lists devices that are running, and controlling playback needs Spotify Premium.

## Project layout
| File | Purpose |
|---|---|
| `Program.cs` | Console login, then runs the TUI. |
| `Client/Session.cs` | Session token + client id, persisted in `~/.config/rvrb`. |
| `Client/RvrbApi.cs` | REST routes used (login, channels, chat history, Spotify devices). |
| `Client/RvrbSocket.cs` | JSON-RPC 2.0 websocket (`wss://app.rvrb.one/ws`): requests, keepalive, reconnect. |
| `Client/ChatText.cs` | Turns the HTML chat payloads into plain text. |
| `Client/Player.cs` | Plays channel tracks on a Spotify device (`playTrack` / `pauseTrack`). |
| `Client/ChatWindow.cs` | The main window. |
| `Client/NowPlayingView.cs` | Track, DJ, votes and progress bar. |
| `Client/Pickers.cs` | Channel and Spotify device dialogs. |
| `Client/TrackQueue.cs` | The DJ queue (the stack with `activeQueue`), cached for answering `nextChannelTrack`. |
| `Client/QueueDialogs.cs` | Queue and Spotify search dialogs. |
| `AGENTS.md` | **Canonical Terminal.Gui v2 patterns + gotchas.** Read before editing UI code. |

API docs: [openapi.yaml](https://app.rvrb.one/api-docs/openapi.yaml) (REST), [asyncapi.yaml](https://app.rvrb.one/api-docs/asyncapi.yaml) (websocket).
