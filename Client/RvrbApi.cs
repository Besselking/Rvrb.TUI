using System.Net;
using System.Text.Json.Nodes;

namespace Rvrb;

internal class RvrbException (string message) : Exception (message);

internal sealed class RvrbAuthException (string message) : RvrbException (message);

/// <summary>The REST routes the client needs (see https://app.rvrb.one/api-docs/openapi.yaml).</summary>
internal sealed class RvrbApi (Session session) : IDisposable
{
    public static readonly Uri BaseUri = new ("https://app.rvrb.one/");

    private readonly HttpClient _http = new () { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds (20) };

    /// <summary>`GET /api/login`: validates the token; returns `{ token, user, chatMaxLength }`.</summary>
    public Task<JsonNode> LoginAsync () => GetAsync ("api/login");

    /// <summary>
    ///     `GET /api/channels/{query}/{page}`: 16 per page. Without search text this lists the popular channels
    ///     (public, at least one user); with it, all channels matching slug, genre or title.
    /// </summary>
    public Task<JsonNode> ListChannelsAsync (string? search, int page = 0)
    {
        string path = string.IsNullOrWhiteSpace (search)
                          ? $"api/channels/popular/{page}"
                          : $"api/channels/all/{page}?query={Uri.EscapeDataString (search.Trim ())}";

        return GetAsync (path);
    }

    /// <summary>`GET /api/channel/{id}/chat`: the latest messages, only once the websocket has joined the channel.</summary>
    public async Task<JsonArray> GetChatAsync (string channelId)
    {
        JsonNode result = await GetAsync ($"api/channel/{Uri.EscapeDataString (channelId)}/chat");

        return result ["chat"] as JsonArray ?? [];
    }

    /// <summary>
    ///     `GET /api/music/devices`: the user's Spotify Connect devices, as Spotify returns them (`id`, `name`, `type`,
    ///     `is_active`, ...), with `is_preferred` on the saved preferred one.
    /// </summary>
    public async Task<JsonArray> GetDevicesAsync ()
    {
        JsonNode result = await GetAsync ("api/music/devices");

        return result ["devices"] as JsonArray ?? [];
    }

    /// <summary>`PUT /api/music/devices/preferred`: saves the preferred device by name (so it survives id changes).</summary>
    public Task SetPreferredDeviceAsync (string deviceName) =>
        SendAsync (HttpMethod.Put, "api/music/devices/preferred", new JsonObject { ["device_name"] = deviceName });

    /// <summary>`GET /api/music/search`: Spotify track search; returns Spotify track objects.</summary>
    public async Task<JsonArray> SearchTracksAsync (string query, int limit = 30)
    {
        JsonNode result = await GetAsync ($"api/music/search?type=track&limit={limit}&q={Uri.EscapeDataString (query)}");

        return result ["tracks"]? ["items"] as JsonArray ?? [];
    }

    /// <summary>
    ///     The user's bookmark ("starred") Spotify playlist URI, from `GET /api/music/playlist/defaults`. When the
    ///     user has no default playlists yet, `PUT` creates them (on Spotify) first.
    /// </summary>
    public async Task<string> GetStarredPlaylistAsync ()
    {
        JsonNode? defaults = (await GetAsync ("api/music/playlist/defaults")) ["result"];

        if (defaults? ["spotifyStarredUri"].Str () is not { Length: > 0 })
        {
            defaults = (await SendAsync (HttpMethod.Put, "api/music/playlist/defaults", new JsonObject ())) ["result"];
        }

        return defaults? ["spotifyStarredUri"].Str () ?? throw new RvrbException ("You have no bookmark playlist");
    }

    /// <summary>`POST /api/music/playlist/{id}/tracks`: appends tracks (Spotify URIs) to a Spotify playlist.</summary>
    public Task AddToPlaylistAsync (string playlistUri, IEnumerable<string> trackUris) =>
        SendAsync (HttpMethod.Post, $"api/music/playlist/{Uri.EscapeDataString (playlistUri.Split (':') [^1])}/tracks",
                   new JsonObject { ["uris"] = new JsonArray (trackUris.Select (u => (JsonNode)u).ToArray ()) });

    /// <summary>`GET /api/music/stack`: the user's stacks (saved track lists); the DJ queue has `activeQueue: true`.</summary>
    public async Task<JsonArray> GetStacksAsync ()
    {
        JsonNode result = await GetAsync ("api/music/stack");

        return result ["result"] as JsonArray ?? [];
    }

    /// <summary>`POST /api/music/stack`: returns the new stack.</summary>
    public async Task<JsonNode> CreateStackAsync (string name, bool activeQueue)
    {
        JsonNode result = await SendAsync (HttpMethod.Post, "api/music/stack",
                                           new JsonObject { ["name"] = name, ["activeQueue"] = activeQueue, ["tracks"] = new JsonArray () });

        return result ["result"] ?? throw new RvrbException ("The new stack was not returned");
    }

    /// <summary>`GET /api/music/stack/{id}`: a page of full track objects (`tracks`) and the stack's `totalItems`.</summary>
    public async Task<JsonNode> GetStackAsync (string stackId, int page, int limit)
    {
        JsonNode result = await GetAsync ($"api/music/stack/{Uri.EscapeDataString (stackId)}?page={page}&limit={limit}");

        return result ["result"] ?? throw new RvrbException ("The stack was not returned");
    }

    /// <summary>`POST /api/music/stack/{id}/tracks`: inserts the tracks at `position`.</summary>
    public Task AddTracksAsync (string stackId, IEnumerable<string> trackIds, int position) =>
        SendAsync (HttpMethod.Post, $"api/music/stack/{Uri.EscapeDataString (stackId)}/tracks",
                   new JsonObject { ["trackIds"] = new JsonArray (trackIds.Select (id => (JsonNode)id).ToArray ()), ["position"] = position });

    /// <summary>`PATCH /api/music/stack/{id}/tracks`: moves one track; `targetPosition` counts after its removal.</summary>
    public Task MoveTrackAsync (string stackId, int position, int targetPosition) =>
        SendAsync (HttpMethod.Patch, $"api/music/stack/{Uri.EscapeDataString (stackId)}/tracks",
                   new JsonObject { ["position"] = position, ["targetPosition"] = targetPosition, ["length"] = 1 });

    /// <summary>`DELETE /api/music/stack/{id}/tracks/{position}`.</summary>
    public Task RemoveTrackAsync (string stackId, int position) =>
        SendAsync (HttpMethod.Delete, $"api/music/stack/{Uri.EscapeDataString (stackId)}/tracks/{position}", null);

    private Task<JsonNode> GetAsync (string path) => SendAsync (HttpMethod.Get, path, null);

    private async Task<JsonNode> SendAsync (HttpMethod method, string path, JsonNode? body)
    {
        using HttpRequestMessage request = new (method, path);
        request.Headers.TryAddWithoutValidation ("authorization", $"Bearer {session.Token}");

        if (body is { })
        {
            request.Content = new StringContent (body.ToJsonString (), System.Text.Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync (request);

        if (response.Headers.TryGetValues ("update-token", out IEnumerable<string>? values)
            && values.FirstOrDefault () is { Length: > 0 } updated)
        {
            session.Update (updated);
        }

        string text = await response.Content.ReadAsStringAsync ();

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RvrbAuthException (string.IsNullOrWhiteSpace (text) ? response.ReasonPhrase ?? "Unauthorized" : text);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Errors are `{ error }` (see the Error schema) or a plain string.
            string message = text;

            try
            {
                message = JsonNode.Parse (text)? ["error"].Str () ?? text;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            throw new RvrbException ($"{(int)response.StatusCode}: {message}");
        }

        return JsonNode.Parse (text) ?? throw new RvrbException ($"Empty response from {path}");
    }

    public void Dispose () => _http.Dispose ();
}
