using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Rvrb;

/// <summary>
///     JSON-RPC 2.0 over the RVRB websocket (see https://app.rvrb.one/api-docs/asyncapi.yaml). Keeps the connection
///     alive (keepAwake → stayAwake), reconnects with the same clientId, and raises server notifications.
///     Events fire on a background thread: marshal to the UI with App.Invoke.
/// </summary>
internal sealed class RvrbSocket (Session session) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new ();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new ();
    private readonly SemaphoreSlim _sendLock = new (1, 1);
    private ClientWebSocket? _ws;
    private int _nextId;
    private Task? _loop;
    private bool _replaced;

    /// <summary>A server notification: its method and params.</summary>
    public event Action<string, JsonNode?>? Notification;

    /// <summary>The server sent `ready`: (re)join the channel now.</summary>
    public event Action? Ready;

    public event Action<string>? StatusChanged;

    public void Start () => _loop = Task.Run (() => RunAsync (_cts.Token));

    /// <summary>Sends a request and waits for its response; throws <see cref="RvrbException"/> on an error reply.</summary>
    public async Task<JsonNode?> RequestAsync (string method, JsonObject? @params = null)
    {
        int id = Interlocked.Increment (ref _nextId);
        TaskCompletionSource<JsonNode?> tcs = new (TaskCreationOptions.RunContinuationsAsynchronously);
        _pending [id] = tcs;

        try
        {
            JsonObject message = new () { ["jsonrpc"] = "2.0", ["method"] = method, ["id"] = id };

            if (@params is { })
            {
                message ["params"] = @params;
            }

            await SendAsync (message.ToJsonString ());

            return await tcs.Task.WaitAsync (TimeSpan.FromSeconds (15));
        }
        catch (TimeoutException)
        {
            throw new RvrbException ($"No answer to {method}");
        }
        finally
        {
            _pending.TryRemove (id, out _);
        }
    }

    /// <summary>Sends a request without an id, for methods the server doesn't answer.</summary>
    public Task NotifyAsync (string method, JsonObject? @params = null)
    {
        JsonObject message = new () { ["jsonrpc"] = "2.0", ["method"] = method };

        if (@params is { })
        {
            message ["params"] = @params;
        }

        return SendAsync (message.ToJsonString ());
    }

    private async Task RunAsync (CancellationToken ct)
    {
        TimeSpan delay = TimeSpan.FromSeconds (1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke ("connecting…");

                using ClientWebSocket ws = new ();
                _ws = ws;

                Uri uri = new (
                    $"wss://{RvrbApi.BaseUri.Host}/ws?token={Uri.EscapeDataString (session.Token)}"
                    + $"&clientId={Uri.EscapeDataString (session.ClientId)}");

                await ws.ConnectAsync (uri, ct);
                delay = TimeSpan.FromSeconds (1);
                await ReceiveLoopAsync (ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke ($"connection error: {ex.Message}");
            }
            finally
            {
                _ws = null;

                foreach (TaskCompletionSource<JsonNode?> tcs in _pending.Values)
                {
                    tcs.TrySetException (new RvrbException ("Disconnected"));
                }
            }

            if (_replaced)
            {
                StatusChanged?.Invoke ("disconnected: this account connected from another client");

                return;
            }

            StatusChanged?.Invoke ($"disconnected, retrying in {delay.TotalSeconds:0}s");

            try
            {
                await Task.Delay (delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds (Math.Min (delay.TotalSeconds * 2, 30));
        }
    }

    private async Task ReceiveLoopAsync (ClientWebSocket ws, CancellationToken ct)
    {
        byte [] buffer = new byte [16 * 1024];
        using MemoryStream frame = new ();

        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync (buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            frame.Write (buffer, 0, result.Count);

            if (!result.EndOfMessage)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString (frame.GetBuffer (), 0, (int)frame.Length);
            frame.SetLength (0);
            Session.LogDebug ($"<< {text}");
            await HandleAsync (text);
        }
    }

    private async Task HandleAsync (string text)
    {
        if (text is "pong" or "null")
        {
            return;
        }

        JsonNode? message;

        try
        {
            message = JsonNode.Parse (text);
        }
        catch
        {
            return;
        }

        if (message is not JsonObject obj)
        {
            return;
        }

        string? method = obj ["method"].Str ();

        // A response to one of our requests.
        if (method is null)
        {
            if (obj ["id"] is not { } idNode || !_pending.TryGetValue ((int)(idNode.Long () ?? -1), out TaskCompletionSource<JsonNode?>? tcs))
            {
                return;
            }

            if (obj ["error"] is { } error)
            {
                ApplyUpdateToken (error);
                tcs.TrySetException (new RvrbException (error is JsonObject ? error ["message"].Str () ?? error.ToJsonString () : error.ToString ()));
            }
            else
            {
                ApplyUpdateToken (obj ["result"]);
                tcs.TrySetResult (obj ["result"]);
            }

            return;
        }

        switch (method)
        {
            case "keepAwake":
                await SendAsync ("""{"jsonrpc":"2.0","method":"stayAwake"}""");

                break;

            case "ready":
                StatusChanged?.Invoke ("connected");
                Ready?.Invoke ();

                break;

            case "closed":
                _replaced = obj ["params"]? ["reason"].Int () == 1;

                break;

            default:
                Notification?.Invoke (method, obj ["params"]);

                break;
        }
    }

    /// <summary>`updateToken` is documented only as "a new session token": accept a string or an object holding one.</summary>
    private void ApplyUpdateToken (JsonNode? container)
    {
        if (container is not JsonObject obj || obj ["updateToken"] is not { } node)
        {
            return;
        }

        string? token = node is JsonValue ? node.Str () : (node ["token"] ?? node ["session"] ?? node ["jwt"]).Str ();

        if (!string.IsNullOrEmpty (token))
        {
            session.Update (token);
        }
    }

    private async Task SendAsync (string text)
    {
        ClientWebSocket ws = _ws is { State: WebSocketState.Open } open ? open : throw new RvrbException ("Not connected");

        await _sendLock.WaitAsync ();

        try
        {
            await ws.SendAsync (Encoding.UTF8.GetBytes (text), WebSocketMessageType.Text, true, _cts.Token);
        }
        finally
        {
            _sendLock.Release ();
        }
    }

    public async ValueTask DisposeAsync ()
    {
        _cts.Cancel ();

        if (_ws is { State: WebSocketState.Open } ws)
        {
            try
            {
                using CancellationTokenSource timeout = new (TimeSpan.FromSeconds (2));
                await ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
            catch
            {
                // Closing is best effort.
            }
        }

        if (_loop is { })
        {
            await _loop.ContinueWith (_ => { });
        }

        _cts.Dispose ();
    }
}
