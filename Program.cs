// -----------------------------------------------------------------------------
//  A terminal chat client for RVRB (https://app.rvrb.one). Run it:  dotnet run
//  Pass --login to paste a new session token.
//
//  🤖 AI assistants / agents: READ ./AGENTS.md FIRST.
//     Terminal.Gui v2 is a COMPLETE REWRITE. Pre-2025 training data and most web
//     examples are v1 and will not compile. The canonical v2 patterns are in AGENTS.md.
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Rvrb;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;

// Log in on the plain console before the TUI takes over the terminal.
string? token = args.Contains ("--login") ? null : Environment.GetEnvironmentVariable ("RVRB_TOKEN") ?? Session.LoadSavedToken ();
Session? session = null;
string me = "";
string myId = "";
string [] myNames = [];

while (session is null)
{
    token ??= PromptForToken ();

    if (token is null)
    {
        return 1;
    }

    Session candidate = Session.Create (token);
    using RvrbApi probe = new (candidate);

    try
    {
        JsonNode login = probe.LoginAsync ().GetAwaiter ().GetResult ();
        JsonNode? user = login ["user"];
        me = user? ["displayName"].Str () is { Length: > 0 } d ? d : user? ["userName"].Str () ?? "?";
        myId = user? ["_id"].Str () ?? "";
        myNames = [.. new [] { user? ["displayName"].Str (), user? ["userName"].Str () }.OfType<string> ()];
        session = candidate;
    }
    catch (RvrbAuthException)
    {
        Session.Forget ();
        Console.WriteLine ("That token was rejected (it may have expired). Please log in again.\n");
        token = null;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine ($"Could not reach RVRB: {ex.Message}");

        return 1;
    }
}

using RvrbApi api = new (session);
RvrbSocket socket = new (session);

ConfigurationManager.Enable (ConfigLocations.All);

using (IApplication app = Application.Create ().Init ())
{
    using ChatWindow window = new (api, socket, me, myId, myNames);

    // Closing the terminal window (SIGHUP) or `kill` (SIGTERM) would end the process without leaving the channel;
    // stop the app instead, so the normal exit path below runs.
    void StopOnSignal (PosixSignalContext context)
    {
        context.Cancel = true;
        app.Invoke (() => app.RequestStop ());
    }

    using PosixSignalRegistration sigHup = PosixSignalRegistration.Create (PosixSignal.SIGHUP, StopOnSignal);
    using PosixSignalRegistration sigTerm = PosixSignalRegistration.Create (PosixSignal.SIGTERM, StopOnSignal);

    // Log anything that escapes a UI handler and keep running, rather than taking the terminal down with it.
    app.Run (window,
             ex =>
             {
                 Session.LogError (ex);

                 return true;
             });

    // Otherwise the server keeps the account in the channel after we're gone.
    window.LeaveChannelAsync ().GetAwaiter ().GetResult ();
}

socket.DisposeAsync ().AsTask ().GetAwaiter ().GetResult ();

return 0;

static string? PromptForToken ()
{
    Console.WriteLine ($"""
                        RVRB login
                        ----------
                        1. Open {new Uri (RvrbApi.BaseUri, "api/login/spotify")} in your browser and log in with Spotify.
                        2. You land on a URL like https://app.rvrb.one/login/?session=eyJ...
                           Copy that whole URL (or just the session token) and paste it below.
                           (If the page already moved on, the token is also in the web app's storage:
                           DevTools → Application → Local Storage.)

                        The token is saved to ~/.config/rvrb/token.
                        """);
    Console.Write ("\nToken or URL: ");

    return Session.ParsePastedToken (Console.ReadLine ());
}
