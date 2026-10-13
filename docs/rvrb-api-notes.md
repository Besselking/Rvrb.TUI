# RVRB API: notes from building a client

Notes from building [Rvrb.TUI](https://github.com/Besselking/Rvrb.TUI), a terminal client for
<https://app.rvrb.one>, written down for starting a mobile app. The official specs are good and mostly right;
this file is about how the server actually behaved, where it differs from the specs, and the pitfalls hit on the way.

- REST: <https://app.rvrb.one/api-docs/openapi.yaml>
- Websocket: <https://app.rvrb.one/api-docs/asyncapi.yaml>

Everything below marked **observed** was seen on the live server (October 2026); **spec** means taken from the docs
and not verified.

## The shape of it

| Part | What | Used for |
|---|---|---|
| REST, `https://app.rvrb.one/api/...` | JSON over HTTPS, JWT in `authorization` | login check, channel list, chat history, Spotify devices/search, the DJ queue, playlists |
| Websocket, `wss://app.rvrb.one/ws` | JSON-RPC 2.0, one message per text frame | everything live: joining, chat, votes, DJs, playback, presence |

RVRB streams no audio. It drives the user's Spotify (Connect devices, or on mobile Spotify itself) with the
Spotify login it stores for the user. A client never needs its own Spotify API keys: search, playlists and
playback all go through RVRB.

## Authentication

- Login is Spotify OAuth only. `GET /api/login/spotify` (**observed**, missing from the spec) redirects to
  Spotify's authorize page. The `redirect_uri` is fixed to `https://app.rvrb.one/api/login/spotify/callback`, and the
  callback redirects to `https://app.rvrb.one/login/?session=<JWT>&spotifyToken=...`.
- So a native client can't receive the redirect itself. The TUI makes the user paste that URL (or the token).
  For mobile, an in-app web view that watches for navigation to `app.rvrb.one/login/?session=` and takes the `session`
  parameter should work. A cleaner option is asking the RVRB maintainers for a redirect to a custom URL scheme.
- REST: the JWT goes in the `authorization` header, with or without `Bearer `. `GET /api/login` validates it and
  returns `{ token, user, chatMaxLength }`. `user._id`, `user.displayName` and `user.userName` are what a client needs.
  A bad token gives 401 with the plain-text body `Forbidden`.
- Websocket: the JWT goes in the `token` query parameter, without `Bearer `.
- Token refresh (**spec**, never seen): REST responses can carry an `update-token` header, and websocket results or
  errors an `updateToken`. The spec doesn't give `updateToken`'s shape; the TUI accepts a string or an object with
  `token`/`session`/`jwt`. Store the new token whenever one arrives.
- One pasted JWT lasted a full day of use; its real lifetime is unknown. There's no refresh endpoint: when it's
  rejected, log in again.

## Websocket

### Connecting and staying connected

- Connect to `/ws?token=<JWT>&clientId=<id>`. Send nothing before the server's `ready`.
- The server sends `keepAwake` about once a minute, as a bare `{ method, params }` without `jsonrpc`. Answer
  `{"jsonrpc":"2.0","method":"stayAwake"}`.
- **`clientId` matters (observed).** Two connections with the same `clientId` bump each other off: no `closed`
  message, the socket just drops, both reconnect, and it loops every second or two. This happened when a stray second
  copy of the TUI was running with the same saved id. Make the id unique per running app instance. It only exists so
  a reconnect resumes the same connection.
- A closed connection gets 10 seconds to come back before the user leaves their channel (**spec**). Even so, quitting
  without sending `leave` left the account in the channel (**observed**), so send `leave` on exit.
- **Mobile (spec, not tried):** send `appState { hidden: true/false, mobile }`. A hidden mobile client gets no
  heartbeats and is kept for an hour after its socket closes. For a mobile client with Spotify, the server starts
  playback itself, and `playChannelTrack` arrives with `playTrack: false`.

### Messages

- A request has `method`, `params` and an `id`; a request without `id` is never answered, not even with an error.
- **`pushMessage` (chat) is never answered (observed)**, even with an id, so a client waiting for the reply times out.
  Send it without an id. The message comes back to the sender as `pushChannelMessage`.
- `join`, `leave`, `joinDjs`, `leaveDjs`, `vote`, `playTrack` and `pauseTrack` are answered. Mostly the reply is
  `{ "message": "success" }`; `join` returns `{ stepDownAfterPlay }`.
- Errors are `{ "message": ... }`, or a bare string for a few: `Not Allowed`, `Could not refresh spotify token: 403`.
  `join` refusals carry a code before the first `:`, e.g. `PR:Password required to join <title>`, `BN:`, `PW:`, `UC:`.
- **The server also sends requests.** `nextChannelTrack` has an `id` (from a server-wide counter, not in sequence) and
  must be answered with that id. See [DJing](#djing).

### Joining a channel

- **Right after its reply to `join`, the server pushes `playChannelTrack` (if a track is playing),
  `updateChannelDjs` and `updateChannelUsers` (observed).** Reset the channel state before sending `join`, not after
  the reply, or that state gets wiped.
- `GET /api/channel/{id}/chat` (the last 100 messages) only works once joined. It overlaps with live messages, so
  de-duplicate; `syncTime` + `userId` works as a key.
- After a reconnect the server has forgotten the channel: join it again and reload the chat.

### Chat

- `pushChannelMessage.payload` is sanitised HTML; tables come as objects, images as `data:image...`. The TUI
  strips tags and decodes entities.
- `type` is `chat`, `whisper`, `alert` (system announcements, e.g. "… is being played for the first time!"), `play`
  ("Artist::Title"), `expandable`, `hay` or `botResponse`. `userId` is `server` or `bot` for system and bot messages.
- **`userId` is sometimes a number, not a string (observed).** The specs' types aren't reliable, so read JSON
  leniently; strict typed deserialisation crashed the TUI on the first channel it joined.
- Mentions are plain text: `@` + the display name, e.g. `Welcome @Bess 🐸`. The TUI matches the display name and
  the username to ring the bell.
- `clearChannelMessages` removes messages by `syncTime` (or everything without it).

### Users and DJs

- `updateChannelUsers.users` is the full list each time. **Users have no `role` field (observed)**, despite the
  spec; roles live in the channel's `roles` (ids by role, not explored).
- `updateChannelDjs.djs` is user ids in play order: the first is the DJ playing now. A DJ that isn't in the user
  list (e.g. a bot) needs a fallback name.

### Playback

- `playChannelTrack`: `track` (a full Spotify track object), `startAt`, `endsAt` (epoch ms), `syncTime` (server time),
  `voting`, `playTrack`. The clocks can differ, so place `endsAt` on the local clock with `syncTime - localNow` as
  the offset.
- To play it, send `playTrack { uri, device_id? }`. **Playback started fine this way (observed)**; the server
  is meant to start at the channel's position (it "measures the latency"), but the exact sync wasn't checked. Without `device_id` the server picks the active device, then the preferred one, any device, or
  the web player. `activePlayer { device_id }` reports where it went.
- `pauseChannelTrack`: nothing is playing; send `pauseTrack` if you started playback.
- Playback control needs Spotify Premium, and Spotify only lists devices that are running.

### Votes

- **What the server sends (observed) differs from the spec.** The spec promises `{ dope, nope, star }` totals;
  the server sends only an entry per voter, keyed by user id, with numbers:

  ```json
  "voting": { "635f69be2f9b8fe2ed7209f8": { "dope": 1, "nope": 0, "star": 0, "boofStar": 0, "votedCount": 1, "chat": 0 } }
  ```

  Count the totals yourself, and find the user's own vote under their id. `voting` comes in `playChannelTrack` and in
  `updateChannelMeter`. It's `{}` when a new track starts.
- `vote { dope?, nope?, star? }`: each key sent sets that vote. Send only the one being toggled, so a vote made in
  another client isn't overwritten. Dope and nope exclude each other on the server.
- Voting on your own track fails with `Cannot vote for yourself`.
- **A star doesn't add the track to the bookmark playlist (observed)**; the web app does that itself. To match it:
  `GET /api/music/playlist/defaults` → `result.spotifyStarredUri` (if missing, `PUT` the same route to create the
  defaults), then `POST /api/music/playlist/{id}/tracks { uris: [trackUri] }`. The id can be the playlist URI's last
  part.

### DJing

- `joinDjs` / `leaveDjs` step up and down; they can fail with `Max DJS Already`.
- When it's the user's turn, the server sends `nextChannelTrack { userId, channelId }` with an `id`. Answer
  `{ jsonrpc, id, result: { track } }` within **2.5 seconds**, with a full Spotify track object (`duration_ms`,
  `album.name`, `album.images`, `artists`, `uri`, `id` and `name` are used). An error answer takes the DJ off the decks,
  which is the right answer for an empty queue. Keep the queue's top track cached to answer in time.
- **The server doesn't take the track off the queue (observed)**, although the spec suggests `refreshDjStack`
  means it did. The same track played again on the next turn. The TUI removes it itself once `playChannelTrack`
  arrives with the track it answered. It first checks the top is still that track, in case something else removed it.
  `refreshDjStack` was never seen.

## REST

### Channels

- `GET /api/channels/popular/{page}`: public channels with at least one user, most users first, 16 per page.
  `GET /api/channels/all/{page}?query=...` searches slug, genre and title (any value other than `popular`/`my`/
  `followed` lists all). The response is `{ count, channels, more }`; `channel.users` is an array, so its length is
  the listener count. A channel's `nowPlaying` holds the current track (with `voting`).
- `channel.greeting` is a free-text greeting to show on joining.

### The DJ queue ("stacks")

- Stacks are saved track lists. **The DJ queue is the one with `activeQueue: true`.** On this account it's
  "RVRB DJ Queue", described as "the tracks that you will play while DJing". Create one when it's missing.
- `GET /api/music/stack` → `{ result: [Stack] }`. Its `tracks` are only Spotify ids.
- `GET /api/music/stack/{id}?page=0&limit=50` → `{ result: { ..., tracks: [full Spotify track objects], totalItems,
  more, next } }`; `limit` is capped at 50.
- `POST /api/music/stack/{id}/tracks { trackIds, position }`: **`position` defaults to 0, the top.** To add to the
  end, pass `position: totalItems`.
- `PATCH /api/music/stack/{id}/tracks { position, targetPosition, length }` moves; `DELETE .../tracks/{position}`
  removes one. Positions are zero-based.

### Spotify

- `GET /api/music/search?q=...&type=track&limit=30`: Spotify's search response passed through
  (`tracks.items` are full track objects).
- `GET /api/music/devices` → `{ devices }` (Spotify device objects, plus `is_preferred`).
  `PUT /api/music/devices/preferred { device_name }` saves the preferred device by name (it survives id changes).
  `PUT /api/music/devices/active { device_id }` transfers playback now. `PUT /api/music/devices/volume` also exists.

### Errors

- Error bodies are `{ "error": "..." }` or plain text. Read both.
- `GET /api/logout` closes all of the user's connections, including other clients (e.g. the browser). Don't call it
  when only one app should disconnect.

## Spec vs. what was observed

| Spec | Observed |
|---|---|
| `GET /api/login/spotify` not documented | Exists; starts the OAuth redirect |
| `pushMessage` answered when it has an id | Never answered; send without id |
| `ChatMessage.userId` is a string | Sometimes a number |
| `ChannelUser.role` | Not present |
| `Voting` has `dope`/`nope`/`star` totals | Only per-voter entries with 1/0 numbers |
| Server takes the DJ's track off the queue, sends `refreshDjStack` | Track stays on the queue; no `refreshDjStack` seen |
| Star = bookmark | Server records the star, but doesn't add it to the bookmark playlist |
| Second client → `closed { reason: 1 }` | With the same `clientId`, the socket just drops, no `closed` |

## Not explored

`appState` and the mobile playback path, skipping (`POST /api/channel/{id}/skip`), whispers (`targetUserId`), typing
and AFK (`userStatus`), `updateChannelUserStatus`, notifications over REST, password-protected channels (`join`'s
`password`), profiles and images, statistics and leaderboards, external bots (`/ws-bot`), channel admin routes.

## Working on it

- Test in your own channels; a test client joins as the real account, and everyone in the room sees it.
- Playback and votes act on the real Spotify account and RVRB profile.
- Log every websocket frame in and out, plus connects and closes, behind a debug flag. Most of the findings above
  came from reading such a log.
- In the TUI: [`RvrbSocket.cs`](../Client/RvrbSocket.cs) (JSON-RPC, keepalive, reconnect, server requests),
  [`RvrbApi.cs`](../Client/RvrbApi.cs) (REST), [`TrackQueue.cs`](../Client/TrackQueue.cs) (queue and DJ answers),
  [`NowPlayingView.cs`](../Client/NowPlayingView.cs) (track timing, votes), [`Json.cs`](../Client/Json.cs) (lenient
  reads).
