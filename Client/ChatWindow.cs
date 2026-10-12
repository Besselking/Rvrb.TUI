using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Rvrb;

/// <summary>
///     The now-playing bar and the chat of the joined channel, with the DJ queue and listeners on the right and the
///     message box below. Channels and the Spotify device are picked in dialogs from the status bar (F2, F3).
///     Socket events arrive on a background thread and are marshalled with App.Invoke.
/// </summary>
internal sealed class ChatWindow : Window
{
    private const int MaxLines = 1000;
    private const int UsersWidth = 26;

    private readonly RvrbApi _api;
    private readonly RvrbSocket _socket;
    private readonly Player _player;
    private readonly string _me;

    private readonly NowPlayingView _nowPlaying;
    private readonly Shortcut _playbackShortcut;
    private readonly FrameView _chatFrame;

    // TextView is marked obsolete in favour of tui-cs/Editor, but it is still the built-in view with word wrap.
#pragma warning disable CS0618
    private readonly TextView _log;
#pragma warning restore CS0618
    private readonly TextField _input;

    private readonly FrameView _djsFrame;
    private readonly ObservableCollection<string> _djItems = [];
    private readonly FrameView _listenersFrame;
    private readonly ObservableCollection<string> _listenerItems = [];
    private readonly Dictionary<string, string> _names = [];

    // Users in the channel (sorted by role, then name) and the DJ ids in play order: the first is playing.
    private List<(string Id, string Name, int Role)> _users = [];
    private List<string> _djs = [];

    private readonly List<(long SyncTime, string Key, string Text, bool Dim)> _lines = [];
    private readonly HashSet<string> _seen = [];

    // Alerts ("… is being played for the first time!") and the client's own notices are drawn faint, like on RVRB.
    private Attribute? _normal;
    private Attribute? _dim;

    // The log follows new messages until the user scrolls up, and follows again once they scroll back to the bottom.
    private bool _follow = true;
    private bool _rendering;
    private int _lastTop;

    private string? _channelId;
    private string? _channelTitle;
    private JsonNode? _pendingJoin;
    private string _status = "starting…";

    public ChatWindow (RvrbApi api, RvrbSocket socket, string me)
    {
        _api = api;
        _socket = socket;
        _player = new (socket);
        _me = me;
        UpdateTitle ();

        _nowPlaying = new () { X = 0, Y = 0, Width = Dim.Fill (UsersWidth), Height = 4 };

        // Leave 3 rows for the message box and 1 for the status bar.
        _chatFrame = new ()
        {
            Title = "Chat",
            X = 0,
            Y = Pos.Bottom (_nowPlaying),
            Width = Dim.Fill (UsersWidth),
            Height = Dim.Fill (4)
        };

        _log = new () { Width = Dim.Fill (), Height = Dim.Fill (), ReadOnly = true, WordWrap = true };
        _chatFrame.Add (_log);

        _log.ViewportChanged += (_, _) =>
                                {
                                    if (_rendering)
                                    {
                                        return;
                                    }

                                    if (_log.Viewport.Y != _lastTop)
                                    {
                                        // The user scrolled (keys or mouse wheel).
                                        _lastTop = _log.Viewport.Y;
                                        _follow = IsAtBottom ();
                                    }
                                    else if (_follow && !IsAtBottom ())
                                    {
                                        // Resized while following: stay pinned to the bottom.
                                        ScrollLog (toBottom: true, top: 0);
                                    }
                                };

        FrameView inputFrame = new ()
        {
            Title = "Message",
            X = 0,
            Y = Pos.Bottom (_chatFrame),
            Width = Dim.Fill (UsersWidth),
            Height = 3
        };

        _input = new () { Width = Dim.Fill () };
        inputFrame.Add (_input);

        // The DJ queue sits above the listeners and grows with the queue (see RefreshPeople).
        _djsFrame = new ()
        {
            Title = "DJs",
            X = Pos.AnchorEnd (UsersWidth),
            Y = 0,
            Width = UsersWidth,
            Height = 3
        };

        ListView djList = new () { Width = Dim.Fill (), Height = Dim.Fill () };
        djList.SetSource (_djItems);
        _djsFrame.Add (djList);

        _listenersFrame = new ()
        {
            Title = "Listeners",
            X = Pos.AnchorEnd (UsersWidth),
            Y = Pos.Bottom (_djsFrame),
            Width = UsersWidth,
            Height = Dim.Fill (1)
        };

        ListView listenerList = new () { Width = Dim.Fill (), Height = Dim.Fill () };
        listenerList.SetSource (_listenerItems);
        _listenersFrame.Add (listenerList);

        // StatusBar positions itself at the bottom. Its keys work app-wide.
        StatusBar statusBar = new ();
        Shortcut channels = new () { Title = "Channels", Key = Key.F2, BindKeyToApplication = true, Action = ShowChannelPicker };
        Shortcut device = new () { Title = "Device", Key = Key.F3, BindKeyToApplication = true, Action = ShowDevicePicker };
        _playbackShortcut = new () { Key = Key.F4, BindKeyToApplication = true, Action = TogglePlayback };
        Shortcut quit = new () { Title = "Quit", Key = Key.Q.WithCtrl, BindKeyToApplication = true, Action = () => App?.RequestStop () };
        statusBar.Add (channels, device, _playbackShortcut, quit);
        UpdatePlaybackShortcut ();

        Add (_nowPlaying, _chatFrame, inputFrame, _djsFrame, _listenersFrame, statusBar);

        // Enter in a TextField raises Accepting; handle it so it doesn't bubble up to the window.
        _input.Accepting += (_, e) =>
                            {
                                e.Handled = true;
                                _ = SendAsync ();
                            };

        _socket.StatusChanged += s => Ui (() =>
                                          {
                                              _status = s;
                                              UpdateTitle ();
                                          });
        _socket.Ready += () => Ui (() =>
                                   {
                                       if (_pendingJoin is { } pending)
                                       {
                                           _pendingJoin = null;
                                           _ = JoinAsync (pending);
                                       }
                                       else
                                       {
                                           _ = RejoinAsync ();
                                       }
                                   });
        _socket.Notification += (method, p) => Ui (() => OnNotification (method, p));
        _player.Error += message => Ui (() => AddSystemLine ($"Playback: {message} (F3 picks a Spotify device, F4 turns playback off)"));

        Initialized += (_, _) =>
                       {
                           // A focused/read-only TextView is drawn with the highlighted (yellow) roles; keep the log on
                           // the frame's normal colours so it reads like a plain chat.
                           Scheme frame = _chatFrame.GetScheme ();

                           _log.SetScheme (new (frame)
                                           {
                                               Focus = frame.Normal,
                                               Editable = frame.Normal,
                                               ReadOnly = frame.Normal
                                           });

                           _normal = frame.Normal;
                           _dim = new Attribute (frame.Normal.Foreground, frame.Normal.Background, TextStyle.Faint);

                           DimBorder (this);

                           foreach (View view in SubViews.OfType<FrameView> ())
                           {
                               DimBorder (view);
                           }

                           AddSystemLine ("F2 picks a channel, F3 the Spotify device to play on, F4 turns playback on or off.");
                           _socket.Start ();

                           // Advance the progress bar.
                           App!.AddTimeout (TimeSpan.FromSeconds (1),
                                            () =>
                                            {
                                                _nowPlaying.Tick ();

                                                return true;
                                            });
                       };

        // Start by picking a channel. Not from a timer: a modal run from inside a timer callback never processes
        // App.Invoke, so the dialog's list would never fill.
        IsRunningChanged += (_, e) =>
                            {
                                if (e.Value && _channelId is null)
                                {
                                    ShowChannelPicker ();
                                }
                            };
    }

    private void Ui (Action action) => App?.Invoke (action);

    private void ShowChannelPicker ()
    {
        using ChannelPicker picker = new (_api);
        App!.Run (picker);

        if (picker.Selected is { } channel)
        {
            _ = JoinAsync (channel);
        }
    }

    private void ShowDevicePicker ()
    {
        using DevicePicker picker = new (_api, _player.DeviceId);
        App!.Run (picker);

        if (picker.Selected is not { } device || device ["id"].Str () is not { } id)
        {
            return;
        }

        string? name = device ["name"].Str ();
        AddSystemLine ($"Playing on {name ?? id}.");
        _ = _player.UseDeviceAsync (id, name);

        // Also make it the preferred device, which the server falls back to (and the web app shows).
        if (name is { })
        {
            _ = SavePreferredAsync (name);
        }
    }

    private async Task SavePreferredAsync (string name)
    {
        try
        {
            await _api.SetPreferredDeviceAsync (name);
        }
        catch (Exception ex)
        {
            Ui (() => AddSystemLine ($"Could not save the preferred device: {ex.Message}"));
        }
    }

    private void TogglePlayback ()
    {
        bool enabled = !_player.Enabled;
        _ = _player.SetEnabledAsync (enabled);
        UpdatePlaybackShortcut ();
        AddSystemLine (enabled ? "Playback on." : "Playback off: tracks are no longer played on your Spotify device.");
    }

    private void UpdatePlaybackShortcut () => _playbackShortcut.Title = _player.Enabled ? "Playback: on" : "Playback: off";

    /// <summary>Stops playback and leaves the joined channel; called on exit. Waits briefly, and never throws.</summary>
    public async Task LeaveChannelAsync ()
    {
        if (_channelId is null)
        {
            return;
        }

        await _player.StopAsync ();

        try
        {
            await _socket.RequestAsync ("leave", timeout: TimeSpan.FromSeconds (3));
        }
        catch
        {
            // Offline or already removed: the server drops the user on its own a few seconds after disconnecting.
        }

        _channelId = null;
    }

    /// <summary>
    ///     Draws a view's border (and its title) faint. Borders are drawn by a separate BorderView, so it gets its
    ///     own scheme and the content keeps the normal colours.
    /// </summary>
    private static void DimBorder (View view)
    {
        if (view.Border.GetOrCreateView () is not { } border)
        {
            return;
        }

        Scheme scheme = border.GetScheme ();
        Attribute dim = new (scheme.Normal.Foreground, scheme.Normal.Background, TextStyle.Faint);
        border.SetScheme (new (scheme) { Normal = dim, Focus = dim });
    }

    private void UpdateTitle () => Title = $"RVRB — {_me} — {_status}";

    private async Task JoinAsync (JsonNode channel)
    {
        string? id = channel ["_id"].Str ();

        if (id is null || id == _channelId)
        {
            return;
        }

        // Picked before the websocket is ready (e.g. right at startup): join once it is.
        if (!_socket.IsReady)
        {
            _pendingJoin = channel;
            AddSystemLine ($"Connecting… joining {ChannelPicker.ChannelTitle (channel)} once connected.");

            return;
        }

        // Reset the people panels before joining: the server sends the users and DJs right after its reply to
        // join, which can be handled before the code after the await runs.
        _users = [];
        _names.Clear ();
        _djs = (channel ["djs"] as JsonArray)?.Select (d => d.Str ()).OfType<string> ().ToList () ?? [];
        RefreshPeople ();

        _nowPlaying.Clear ();

        try
        {
            if (_channelId is { })
            {
                // The new channel's track (if any) arrives after joining; don't keep playing the old one meanwhile.
                await _player.StopAsync ();
                await _socket.RequestAsync ("leave");
            }

            await _socket.RequestAsync ("join", new JsonObject { ["channelId"] = id });
        }
        catch (Exception ex)
        {
            // Join refusals look like "PR:Password required to join <title>"; show the part after the code.
            string message = ex.Message;
            int colon = message.IndexOf (':');
            Ui (() => AddSystemLine ($"Could not join: {(colon is > 0 and <= 4 ? message [(colon + 1)..] : message)}"));

            return;
        }

        Ui (() =>
            {
                _channelId = id;
                _channelTitle = ChannelPicker.ChannelTitle (channel);
                _chatFrame.Title = _channelTitle;
                _lines.Clear ();
                _seen.Clear ();
                _follow = true;

                if (channel ["greeting"].Str () is { Length: > 0 } greeting)
                {
                    AddSystemLine (ChatText.FromPayload (greeting));
                }

                Render ();
                _input.SetFocus ();
            });

        await LoadBacklogAsync (id);
    }

    /// <summary>After a reconnect the server has forgotten the channel: join it again and fill any gap in the chat.</summary>
    private async Task RejoinAsync ()
    {
        if (_channelId is not { } id)
        {
            return;
        }

        try
        {
            await _socket.RequestAsync ("join", new JsonObject { ["channelId"] = id });
            await LoadBacklogAsync (id);
        }
        catch (Exception ex)
        {
            Ui (() => AddSystemLine ($"Could not rejoin {_channelTitle}: {ex.Message}"));
        }
    }

    private async Task LoadBacklogAsync (string channelId)
    {
        try
        {
            JsonArray chat = await _api.GetChatAsync (channelId);

            Ui (() =>
                {
                    if (channelId != _channelId)
                    {
                        return;
                    }

                    foreach (JsonNode? message in chat)
                    {
                        AddMessage (message, render: false);
                    }

                    Render ();
                });
        }
        catch (Exception ex)
        {
            Ui (() => AddSystemLine ($"Could not load chat history: {ex.Message}"));
        }
    }

    private async Task SendAsync ()
    {
        string text = _input.Text.Trim ();

        if (text.Length == 0)
        {
            return;
        }

        if (_channelId is null)
        {
            AddSystemLine ("Join a channel first.");

            return;
        }

        _input.Text = "";

        try
        {
            // The server never answers pushMessage, so it is sent without an id. The message comes back as
            // pushChannelMessage, so it isn't added to the log here.
            await _socket.NotifyAsync ("pushMessage", new JsonObject { ["payload"] = text, ["type"] = "chat" });
        }
        catch (Exception ex)
        {
            Ui (() =>
                {
                    AddSystemLine ($"Not sent: {ex.Message}");

                    if (_input.Text.Length == 0)
                    {
                        _input.Text = text;
                    }
                });
        }
    }

    private void OnNotification (string method, JsonNode? p)
    {
        switch (method)
        {
            case "pushChannelMessage":
                AddMessage (p, render: true);

                break;

            case "updateChannelUsers":
                UpdateUsers (p? ["users"] as JsonArray);

                break;

            case "updateChannelDjs":
                if (p? ["djs"] is JsonArray djs)
                {
                    _djs = djs.Select (d => d.Str ()).OfType<string> ().ToList ();
                    RefreshPeople ();
                }

                break;

            case "playChannelTrack":
                _nowPlaying.SetTrack (p);
                _ = _player.OnChannelTrackAsync (p? ["track"]? ["uri"].Str (), serverPlays: p? ["playTrack"].Bool () == false);

                break;

            case "pauseChannelTrack":
                _nowPlaying.Clear ();
                _ = _player.StopAsync ();

                break;

            case "updateChannelMeter":
                _nowPlaying.SetVotes (p? ["voting"]);

                break;

            case "activePlayer":
                _player.OnActivePlayer (p? ["device_id"].Str ());

                break;

            case "clearChannelMessages":
                if (p? ["syncTime"] is JsonArray times)
                {
                    HashSet<long> removed = times.Select (t => t.Long () ?? 0).ToHashSet ();
                    _lines.RemoveAll (l => removed.Contains (l.SyncTime));
                }
                else
                {
                    _lines.Clear ();
                    _follow = true;
                    AddSystemLine ("Chat was cleared.");
                }

                Render ();

                break;

            case "leaveChannel":
                if (p? ["channelId"].Str () == _channelId)
                {
                    AddSystemLine ($"You are no longer in {_channelTitle}.");
                    _channelId = null;
                    _channelTitle = null;
                    _chatFrame.Title = "Chat";
                    _users = [];
                    _djs = [];
                    RefreshPeople ();
                    _nowPlaying.Clear ();
                    _ = _player.StopAsync ();
                }

                break;

            case "pushNotification":
                if (p? ["notification"] is { } n)
                {
                    AddSystemLine ($"🔔 {n ["title"].Str ()}: {ChatText.FromPayload (n ["message"])}");
                }

                break;
        }
    }

    private void UpdateUsers (JsonArray? users)
    {
        if (users is null)
        {
            return;
        }

        _users = users.OfType<JsonObject> ()
                      .Select (u => (Id: u ["_id"].Str () ?? "",
                                     Name: u ["displayName"].Str () is { Length: > 0 } d ? d : u ["userName"].Str () ?? "?",
                                     Role: u ["role"].Int () ?? 0))
                      .OrderByDescending (u => u.Role)
                      .ThenBy (u => u.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList ();

        _names.Clear ();

        foreach ((string id, string name, _) in _users)
        {
            _names [id] = name;
        }

        RefreshPeople ();
    }

    /// <summary>Fills the DJ queue (playing DJ first, marked ▶) and the listeners (everyone not on the decks).</summary>
    private void RefreshPeople ()
    {
        _djItems.Clear ();

        for (var i = 0; i < _djs.Count; i++)
        {
            string name = _names.TryGetValue (_djs [i], out string? n) ? n : "(unknown)";
            _djItems.Add ($"{(i == 0 ? "▶" : $"{i + 1}.")} {name}");
        }

        HashSet<string> onDecks = [.. _djs];
        _listenerItems.Clear ();

        foreach ((string id, string name, int role) in _users.Where (u => !onDecks.Contains (u.Id)))
        {
            string badge = role switch
                           {
                               >= 10 => "★ ",
                               >= 5 => "@ ",
                               >= 3 => "+ ",
                               _ => "  "
                           };
            _listenerItems.Add (badge + name);
        }

        _nowPlaying.SetDj (_djs.Count > 0 && _names.TryGetValue (_djs [0], out string? dj) ? dj : null);

        _djsFrame.Title = $"DJs ({_djs.Count})";
        _listenersFrame.Title = $"Listeners ({_listenerItems.Count})";

        // Room for every DJ up to 10 rows (plus the border), and at least one row so the frame stays visible.
        _djsFrame.Height = Math.Clamp (_djs.Count, 1, 10) + 2;
    }

    private void AddMessage (JsonNode? message, bool render)
    {
        if (message is not JsonObject m || m ["deleted"].Bool () == true)
        {
            return;
        }

        long syncTime = m ["syncTime"].Long () ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
        string userId = m ["userId"].Str () ?? "";
        string key = $"{syncTime}:{userId}";

        // The backlog and the live stream overlap after joining or reconnecting.
        if (!_seen.Add (key))
        {
            return;
        }

        string name = userId switch
                      {
                          "server" => "RVRB",
                          _ when _names.TryGetValue (userId, out string? n) => n,
                          _ => m ["userName"].Str () ?? "?"
                      };

        string body = ChatText.FromPayload (m ["payload"]);

        if (m ["expandable"].Bool () == true && m ["summary"].Str () is { Length: > 0 } summary)
        {
            body = $"{ChatText.FromPayload (summary)}\n{body}";
        }

        string time = DateTimeOffset.FromUnixTimeMilliseconds (syncTime).ToLocalTime ().ToString ("HH:mm");

        string? type = m ["type"].Str ();

        string line = type switch
                      {
                          "alert" => $"{time} ** {body}",
                          "play" => $"{time} ♪ {body}",
                          "whisper" => $"{time} [whisper] {name}: {body}",
                          _ => $"{time} {name}: {body}"
                      };

        // Indent continuation lines under the message.
        _lines.Add ((syncTime, key, line.Replace ("\n", "\n      "), type == "alert"));

        if (render)
        {
            Render ();
        }
    }

    private void AddSystemLine (string text)
    {
        _lines.Add ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds (), "", $"-- {text}", true));
        Render ();
    }

    private void Render ()
    {
        // Only trim while following, so lines don't shift under a user who scrolled up to read.
        if (_follow && _lines.Count > MaxLines)
        {
            _lines.RemoveRange (0, _lines.Count - MaxLines);
        }

        // Loading cells (rather than setting Text) lets each line carry its own attribute.
        List<List<Cell>> cells = [];

        foreach ((_, _, string text, bool dim) in _lines)
        {
            foreach (string row in text.Split ('\n'))
            {
                // Load() looks up the cell under the cursor, so no row may be empty.
                cells.Add (Cell.ToCellList (row.Length > 0 ? row : " ", dim ? _dim : _normal));
            }
        }

        if (cells.Count == 0)
        {
            cells.Add (Cell.ToCellList (" ", _normal));
        }

        int top = _log.Viewport.Y;
        _rendering = true;

        try
        {
            // Load() checks the cursor's old position against the new content; park it at 0,0 first.
            _log.MoveHome ();
            _log.Load (cells);

            // MoveEnd() makes the TextView re-wrap the new content, so the content height below is right.
            _log.MoveEnd ();
        }
        finally
        {
            _rendering = false;
        }

        ScrollLog (_follow, top);
    }

    private bool IsAtBottom () => _log.Viewport.Y + _log.Viewport.Height >= _log.GetContentSize ().Height;

    private void ScrollLog (bool toBottom, int top)
    {
        int bottom = Math.Max (0, _log.GetContentSize ().Height - _log.Viewport.Height);
        _rendering = true;

        try
        {
            _log.Viewport = _log.Viewport with { Y = toBottom ? bottom : Math.Min (top, bottom) };
            _lastTop = _log.Viewport.Y;
        }
        finally
        {
            _rendering = false;
        }
    }
}
