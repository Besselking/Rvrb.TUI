using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rvrb;

/// <summary>Turns RVRB chat payloads (sanitised HTML, tables, images) into plain terminal text.</summary>
internal static partial class ChatText
{
    public static string FromPayload (JsonNode? payload)
    {
        if (payload is not JsonValue value || !value.TryGetValue (out string? html))
        {
            return payload is JsonObject ? "[table]" : "";
        }

        if (html.StartsWith ("data:image", StringComparison.Ordinal))
        {
            return "[image]";
        }

        string text = LineBreak ().Replace (html, "\n");
        text = Image ().Replace (text, m => m.Groups [1].Success ? $"[{m.Groups [1].Value}]" : "[image]");
        text = Tag ().Replace (text, "");
        text = WebUtility.HtmlDecode (text);

        return MultipleBlankLines ().Replace (text, "\n").Trim ();
    }

    [GeneratedRegex (@"<br\s*/?>|</p>|</div>|</li>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak ();

    [GeneratedRegex ("""<img\b[^>]*?(?:alt\s*=\s*["']([^"']+)["'][^>]*)?>""", RegexOptions.IgnoreCase)]
    private static partial Regex Image ();

    [GeneratedRegex ("<[^>]+>")]
    private static partial Regex Tag ();

    [GeneratedRegex (@"\n\s*\n+")]
    private static partial Regex MultipleBlankLines ();
}
