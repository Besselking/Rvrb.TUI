using System.Text.Json.Nodes;

namespace Rvrb;

/// <summary>
///     The user's RVRB DJ queue: the stack marked `activeQueue` (created when there is none). While the user DJs,
///     the server asks for the next track (`nextChannelTrack`); the client answers with the top and takes it off
///     once it plays (<see cref="RemovePlayedAsync"/>). The first page is kept in memory so that question can be
///     answered within its 2.5 second limit.
/// </summary>
internal sealed class TrackQueue (RvrbApi api)
{
    private const int PageSize = 50;

    private readonly SemaphoreSlim _lock = new (1, 1);
    private string? _stackId;

    /// <summary>The first <see cref="PageSize"/> tracks (Spotify track objects), top first.</summary>
    public IReadOnlyList<JsonNode> Tracks { get; private set; } = [];

    /// <summary>All tracks in the queue, including those beyond the first page.</summary>
    public int Total { get; private set; }

    public bool Loaded { get; private set; }

    public JsonNode? Next => Tracks.Count > 0 ? Tracks [0] : null;

    /// <summary>The tracks changed (on a background thread).</summary>
    public event Action? Changed;

    public async Task ReloadAsync ()
    {
        await _lock.WaitAsync ();

        try
        {
            await ReloadCoreAsync ();
        }
        finally
        {
            _lock.Release ();
        }
    }

    /// <summary>Adds a track at the bottom of the queue.</summary>
    public Task AddAsync (string trackId) => ChangeAsync (id => api.AddTracksAsync (id, [trackId], Total));

    public Task RemoveAsync (int position) => ChangeAsync (id => api.RemoveTrackAsync (id, position));

    /// <summary>
    ///     Takes a track that just started playing off the top. The server doesn't do this itself (whatever the spec
    ///     says about refreshDjStack), so the client must. It reloads first and only removes the top when it is still
    ///     that track, so a track the server or another client already removed isn't followed by the wrong one.
    /// </summary>
    public async Task RemovePlayedAsync (string trackId)
    {
        await _lock.WaitAsync ();

        try
        {
            await ReloadCoreAsync ();

            if (Next? ["id"].Str () == trackId)
            {
                await api.RemoveTrackAsync (await StackIdAsync (), 0);
                await ReloadCoreAsync ();
            }
        }
        finally
        {
            _lock.Release ();
        }
    }

    public Task MoveAsync (int position, int targetPosition) => ChangeAsync (id => api.MoveTrackAsync (id, position, targetPosition));

    private async Task ChangeAsync (Func<string, Task> change)
    {
        await _lock.WaitAsync ();

        try
        {
            await change (await StackIdAsync ());
        }
        finally
        {
            // Reload even when the change failed, so the list shows what the server has.
            try
            {
                await ReloadCoreAsync ();
            }
            finally
            {
                _lock.Release ();
            }
        }
    }

    private async Task ReloadCoreAsync ()
    {
        JsonNode page = await api.GetStackAsync (await StackIdAsync (), 0, PageSize);

        Tracks = (page ["tracks"] as JsonArray ?? []).OfType<JsonNode> ().ToList ();
        Total = (int)(page ["totalItems"].Long () ?? Tracks.Count);
        Loaded = true;
        Changed?.Invoke ();
    }

    private async Task<string> StackIdAsync ()
    {
        if (_stackId is { })
        {
            return _stackId;
        }

        JsonArray stacks = await api.GetStacksAsync ();

        JsonNode stack = stacks.FirstOrDefault (s => s? ["activeQueue"].Bool () == true && s ["deleted"].Bool () != true)
                         ?? await api.CreateStackAsync ("RVRB DJ Queue", activeQueue: true);

        return _stackId = stack ["_id"].Str () ?? throw new RvrbException ("The queue has no id");
    }

    public static string Describe (JsonNode track)
    {
        string artists = string.Join (", ", (track ["artists"] as JsonArray ?? []).Select (a => a? ["name"].Str ()).OfType<string> ());
        long ms = track ["duration_ms"].Long () ?? 0;

        return $"{track ["name"].Str () ?? "?"} — {artists} ({TimeSpan.FromMilliseconds (ms):m\\:ss})";
    }
}
