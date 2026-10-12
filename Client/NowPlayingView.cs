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
    private string _votes = "";
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
        long endsAt = p? ["endsAt"].Long () ?? now - offset + _durationMs;
        _startLocalMs = endsAt - offset - _durationMs;

        string artists = string.Join (", ", (track? ["artists"] as JsonArray ?? []).Select (a => a? ["name"].Str ()).OfType<string> ());
        _title = $"{track? ["name"].Str () ?? "?"} — {artists}";

        SetVotes (p? ["voting"]);
        Tick ();
    }

    public void Clear ()
    {
        _durationMs = 0;
        _title = "";
        _votes = "";
        Tick ();
    }

    public void SetDj (string? dj)
    {
        _dj = dj;
        Tick ();
    }

    /// <summary>From `playChannelTrack` and `updateChannelMeter`: the totals; voters' entries are skipped.</summary>
    public void SetVotes (JsonNode? voting)
    {
        if (voting is null)
        {
            return;
        }

        _votes = $"▲{voting ["dope"].Long () ?? 0} ▼{voting ["nope"].Long () ?? 0} ★{voting ["star"].Long () ?? 0}";
        Tick ();
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

        _track.Text = $"♪ {_title}{(_dj is { } ? $"  · {_dj}" : "")}  {_votes}";
        _progress.Fraction = (float)elapsed / _durationMs;
        _time.Text = $"{Format (elapsed)} / {Format (_durationMs)}";
    }

    private static string Format (long ms) => TimeSpan.FromMilliseconds (ms).ToString (ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
}
