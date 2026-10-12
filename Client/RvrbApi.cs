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
