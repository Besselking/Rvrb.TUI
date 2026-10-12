namespace Rvrb;

/// <summary>
///     Holds the RVRB session JWT and persists it to ~/.config/rvrb, so the token only has to be pasted once.
///     The server can hand out a refreshed token at any time (REST `update-token` header, websocket `updateToken`);
///     <see cref="Update"/> stores it.
/// </summary>
internal sealed class Session
{
    private static readonly string Dir = Path.Combine (
        Environment.GetFolderPath (Environment.SpecialFolder.UserProfile), ".config", "rvrb");

    private static readonly string TokenFile = Path.Combine (Dir, "token");
    private static readonly string ClientIdFile = Path.Combine (Dir, "client-id");

    private readonly object _lock = new ();
    private string _token;

    private Session (string token, string clientId)
    {
        _token = token;
        ClientId = clientId;
    }

    public string Token
    {
        get
        {
            lock (_lock)
            {
                return _token;
            }
        }
    }

    /// <summary>Identifies this TUI to the websocket, so it doesn't share a connection with the browser.</summary>
    public string ClientId { get; }

    public static string? LoadSavedToken () => File.Exists (TokenFile) ? File.ReadAllText (TokenFile).Trim () : null;

    public static Session Create (string token)
    {
        string clientId;

        if (File.Exists (ClientIdFile))
        {
            clientId = File.ReadAllText (ClientIdFile).Trim ();
        }
        else
        {
            clientId = $"tui-{Guid.NewGuid ():N}";
            WritePrivate (ClientIdFile, clientId);
        }

        Session session = new (token, clientId);
        session.Save ();

        return session;
    }

    public void Update (string token)
    {
        lock (_lock)
        {
            if (token == _token)
            {
                return;
            }

            _token = token;
        }

        Save ();
    }

    public static void Forget ()
    {
        if (File.Exists (TokenFile))
        {
            File.Delete (TokenFile);
        }
    }

    public static readonly string ErrorLog = Path.Combine (Dir, "error.log");

    public static void LogError (Exception ex)
    {
        Directory.CreateDirectory (Dir);
        File.AppendAllText (ErrorLog, $"[{DateTimeOffset.Now:O}] {ex}\n\n");
    }

    private static readonly bool Debug = Environment.GetEnvironmentVariable ("RVRB_DEBUG") is { Length: > 0 };

    /// <summary>With RVRB_DEBUG set, appends raw websocket traffic to ~/.config/rvrb/debug.log.</summary>
    public static void LogDebug (string line)
    {
        if (Debug)
        {
            Directory.CreateDirectory (Dir);
            File.AppendAllText (Path.Combine (Dir, "debug.log"), $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {line}\n");
        }
    }

    private void Save () => WritePrivate (TokenFile, Token);

    private static void WritePrivate (string path, string contents)
    {
        Directory.CreateDirectory (Dir);
        File.WriteAllText (path, contents);

        if (!OperatingSystem.IsWindows ())
        {
            File.SetUnixFileMode (path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    ///     Accepts a bare JWT, a "Bearer ..." header value, or the whole `/login/?session=...` URL the login redirects to.
    /// </summary>
    public static string? ParsePastedToken (string? input)
    {
        if (string.IsNullOrWhiteSpace (input))
        {
            return null;
        }

        input = input.Trim ();

        int idx = input.IndexOf ("session=", StringComparison.Ordinal);

        if (idx >= 0)
        {
            string rest = input [(idx + "session=".Length)..];
            int end = rest.IndexOf ('&');
            input = Uri.UnescapeDataString (end >= 0 ? rest [..end] : rest);
        }

        if (input.StartsWith ("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            input = input ["Bearer ".Length..].Trim ();
        }

        return input.Trim ('"', '\'');
    }
}
