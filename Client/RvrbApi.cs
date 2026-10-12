using System.Net;
using System.Text.Json.Nodes;

namespace Rvrb;

internal class RvrbException (string message) : Exception (message);

internal sealed class RvrbAuthException (string message) : RvrbException (message);

/// <summary>The handful of REST routes the chat client needs (see https://app.rvrb.one/api-docs/openapi.yaml).</summary>
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

    private async Task<JsonNode> GetAsync (string path)
    {
        using HttpRequestMessage request = new (HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation ("authorization", $"Bearer {session.Token}");

        using HttpResponseMessage response = await _http.SendAsync (request);

        if (response.Headers.TryGetValues ("update-token", out IEnumerable<string>? values)
            && values.FirstOrDefault () is { Length: > 0 } updated)
        {
            session.Update (updated);
        }

        string body = await response.Content.ReadAsStringAsync ();

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RvrbAuthException (string.IsNullOrWhiteSpace (body) ? response.ReasonPhrase ?? "Unauthorized" : body);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RvrbException ($"{(int)response.StatusCode}: {body}");
        }

        return JsonNode.Parse (body) ?? throw new RvrbException ($"Empty response from {path}");
    }

    public void Dispose () => _http.Dispose ();
}
