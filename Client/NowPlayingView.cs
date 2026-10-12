using System.Text.Json.Nodes;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Rvrb;

/// <summary>The channel's track: title and artists, who plays it, the votes, and a progress bar.</summary>
internal sealed class NowPlayingView : FrameView
{
    private readonly Label _track;
    private readonly ProgressBar _progress;
    private readonly Label _time;
    private readonly Label _upNext;

    private string _title = "";
    private string? _dj;
    private long _dopes, _nopes, _stars;
    private long _startLocalMs;
    private long _durationMs;

    public NowPlayingView ()
    {
        Title = "Now playing";

        _track = new () { X = 0, Y = 0, Width = Dim.Fill (), Text = "Nothing playing" };
        _time = new () { X = Pos.AnchorEnd (13), Y = 1, Width = 13, Text = "" };
        _progress = new () { X = 0, Y = 1, Width = Dim.Fill (14), ProgressBarStyle = ProgressBarStyle.Continuous };

        _upNext = new () { X = 0, Y = 2, Width = Dim.Fill (), Text = "" };

        Add (_track, _progress, _time, _upNext);
    }

    public bool IsPlaying => _durationMs > 0;

    /// <summary>The playing track's Spotify URI and name.</summary>
    public string? TrackUri { get; private set; }

    public string TrackTitle => _title;

    /// <summary>The user's id, to find their own entry in the votes.</summary>
    public string MyId { get; init; } = "";

    /// <summary>The user's votes on the playing track.</summary>
    public (bool Dope, bool Nope, bool Star) MyVote { get; private set; }

    /// <summary>After a successful `vote`: the server's next meter update confirms it, but show it right away.</summary>
    public void SetMyVote ((bool Dope, bool Nope, bool Star) vote)
    {
        MyVote = vote;
        Tick ();
    }

    /// <summary>The top of the user's own queue: what they play when it's their turn.</summary>
    public void SetUpNext (string text) => _upNext.Text = text;

    /// <summary>
    ///     From `playChannelTrack`. The server's clock can differ from ours, so its `syncTime` (the server's time
    ///     when it sent the message) is used to place `endsAt` on the local clock.
    /// </summary>
    public void SetTrack (JsonNode? p)
    {
        JsonNode? track = p? ["track"];
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
        long offset = (p? ["syncTime"].Long () ?? now) - now;

        _durationMs = track? ["duration_ms"].Long () ?? 0;
        TrackUri = track? ["uri"].Str ();
        long endsAt = p? ["endsAt"].Long () ?? now - offset + _durationMs;
        _startLocalMs = endsAt - offset - _durationMs;

        string artists = string.Join (", ", (track? ["artists"] as JsonArray ?? []).Select (a => a? ["name"].Str ()).OfType<string> ());
        _title = $"{track? ["name"].Str () ?? "?"} — {artists}";

        _dopes = _nopes = _stars = 0;
        MyVote = default;
        SetVotes (p? ["voting"]);
        Tick ();
    }

    public void Clear ()
    {
        _durationMs = 0;
        TrackUri = null;
        _title = "";
        _dopes = _nopes = _stars = 0;
        MyVote = default;
        Tick ();
    }

    public void SetDj (string? dj)
    {
        _dj = dj;
        Tick ();
    }

    /// <summary>
    ///     From `playChannelTrack` and `updateChannelMeter`. The spec promises `{ dope, nope, star }` counts, but
    ///     the server sends only per-voter entries keyed by user id, e.g. `{ "<id>": { "dope": 1, "nope": 0,
    ///     "star": 0 } }`, so those are counted. Counts (or lists/objects of voters) under dope/nope/star are still
    ///     read, should the server ever send them.
    /// </summary>
    public void SetVotes (JsonNode? voting)
    {
        if (voting is not JsonObject votes)
        {
            return;
        }

        Session.LogDebug ($"votes: {votes.ToJsonString ()}");

        // Entries hold 1/0 (seen: { "dope": 1, "nope": 0, "star": 0, "votedCount": 1, ... }); accept booleans too.
        static bool On (JsonNode? v) => v.Bool () ?? v.Long () > 0;

        // With plain counts the server doesn't say who voted: keep the vote this client last sent.
        bool Sent (string kind) => kind switch { "dope" => MyVote.Dope, "nope" => MyVote.Nope, _ => MyVote.Star };

        (long Count, bool Mine)? Read (string kind) =>
            votes [kind] switch
            {
                JsonValue v when v.Long () is { } n => (n, Sent (kind)),
                JsonArray ids => (ids.Count, ids.Any (id => id.Str () == MyId)),
                JsonObject byVoter => (byVoter.Count, MyId.Length > 0 && byVoter.ContainsKey (MyId)),
                _ => null
            };

        if (Read ("dope") is { } dope && Read ("nope") is { } nope)
        {
            (long Count, bool Mine) star = Read ("star") ?? (0, MyVote.Star);
            (_dopes, _nopes, _stars) = (dope.Count, nope.Count, star.Count);
            MyVote = (dope.Mine, nope.Mine, star.Mine);
        }
        else
        {
            // Only per-voter entries: count them.
            (_dopes, _nopes, _stars) = (0, 0, 0);

            foreach ((string voter, JsonNode? entry) in votes)
            {
                (bool d, bool n, bool st) = entry switch
                                            {
                                                JsonObject e => (On (e ["dope"]), On (e ["nope"]), On (e ["star"])),
                                                JsonValue e => (e.Str () == "dope", e.Str () == "nope", false),
                                                _ => (false, false, false)
                                            };

                _dopes += d ? 1 : 0;
                _nopes += n ? 1 : 0;
                _stars += st ? 1 : 0;

                if (voter == MyId)
                {
                    MyVote = (d, n, st);
                }
            }
        }

        Tick ();
    }

    /// <summary>"▲3 ▼0 ★1", with the user's own votes in brackets.</summary>
    private string Votes ()
    {
        static string Mark (bool mine, string text) => mine ? $"[{text}]" : text;

        return $"{Mark (MyVote.Dope, $"▲{_dopes}")} {Mark (MyVote.Nope, $"▼{_nopes}")} {Mark (MyVote.Star, $"★{_stars}")}";
    }

    /// <summary>Redraws the text and progress; called every second by the window.</summary>
    public void Tick ()
    {
        if (!IsPlaying)
        {
            _track.Text = "Nothing playing";
            _progress.Fraction = 0;
            _time.Text = "";

            return;
        }

        long elapsed = Math.Clamp (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds () - _startLocalMs, 0, _durationMs);

        _track.Text = $"♪ {_title}{(_dj is { } ? $"  · {_dj}" : "")}  {Votes ()}";
        _progress.Fraction = (float)elapsed / _durationMs;
        _time.Text = $"{Format (elapsed)} / {Format (_durationMs)}";
    }

    private static string Format (long ms) => TimeSpan.FromMilliseconds (ms).ToString (ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
}
