using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rvrb;

/// <summary>
///     Lenient reads of server JSON. The specs don't always match what the server sends (e.g. a chat message's
///     `userId` can be a number), and JsonNode's explicit casts throw on any type mismatch.
/// </summary>
internal static class Json
{
    public static string? Str (this JsonNode? node) =>
        node is JsonValue v
            ? v.GetValueKind () switch
              {
                  JsonValueKind.String => v.GetValue<string> (),
                  JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString (),
                  _ => null
              }
            : null;

    public static long? Long (this JsonNode? node) =>
        node is JsonValue v
            ? v.GetValueKind () switch
              {
                  JsonValueKind.Number => v.TryGetValue (out long l) ? l : (long)v.GetValue<double> (),
                  JsonValueKind.String when long.TryParse (v.GetValue<string> (), CultureInfo.InvariantCulture, out long l) => l,
                  _ => null
              }
            : null;

    public static int? Int (this JsonNode? node) => node.Long () is { } l ? (int)l : null;

    public static bool? Bool (this JsonNode? node) =>
        node is JsonValue v
            ? v.GetValueKind () switch
              {
                  JsonValueKind.True => true,
                  JsonValueKind.False => false,
                  _ => null
              }
            : null;
}
