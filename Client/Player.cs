using System.Text.Json.Nodes;

namespace Rvrb;

/// <summary>
///     Plays the channel's tracks on one of the user's Spotify Connect devices. RVRB does the Spotify calls with the
///     login it keeps for the user: the client only sends `playTrack` (with the track and an optional device) and
///     `pauseTrack`. Without a device id the server picks one: active, preferred, any, or the web player.
/// </summary>
internal sealed class Player (RvrbSocket socket)
{
    private string? _uri;
    private bool _playing;

    public bool Enabled { get; private set; } = true;

    /// <summary>The device to play on, or null to let the server choose.</summary>
    public string? DeviceId { get; private set; }

    public string? DeviceName { get; private set; }

    /// <summary>Playback failed, e.g. no Spotify device is running.</summary>
    public event Action<string>? Error;

    /// <summary>`playChannelTrack`: play it, unless the server says it already did (`playTrack: false`).</summary>
    public async Task OnChannelTrackAsync (string? uri, bool serverPlays)
    {
        _uri = uri;

        if (Enabled && !serverPlays && uri is { })
        {
            await PlayAsync ();
        }
    }

    /// <summary>`pauseChannelTrack`, or leaving the channel: nothing is playing any more.</summary>
    public async Task StopAsync ()
    {
        _uri = null;

        if (_playing)
        {
            await PauseAsync ();
        }
    }

    public async Task SetEnabledAsync (bool enabled)
    {
        Enabled = enabled;

        if (enabled && _uri is { })
        {
            await PlayAsync ();
        }
        else if (!enabled && _playing)
        {
            await PauseAsync ();
        }
    }

    /// <summary>Switches to a device; the playing track continues there at the channel's position.</summary>
    public async Task UseDeviceAsync (string id, string? name)
    {
        DeviceId = id;
        DeviceName = name;

        if (Enabled && _uri is { })
        {
            await PlayAsync ();
        }
    }

    /// <summary>`activePlayer`: the server switched playback to this device.</summary>
    public void OnActivePlayer (string? deviceId)
    {
        if (deviceId is { } && deviceId != DeviceId)
        {
            DeviceId = deviceId;
            DeviceName = null;
        }
    }

    private async Task PlayAsync ()
    {
        JsonObject p = new () { ["uri"] = _uri };

        if (DeviceId is { })
        {
            p ["device_id"] = DeviceId;
        }

        try
        {
            await socket.RequestAsync ("playTrack", p);
            _playing = true;
        }
        catch (Exception ex)
        {
            Error?.Invoke (ex.Message);
        }
    }

    private async Task PauseAsync ()
    {
        try
        {
            await socket.RequestAsync ("pauseTrack", timeout: TimeSpan.FromSeconds (3));
        }
        catch (Exception ex)
        {
            Error?.Invoke (ex.Message);
        }

        _playing = false;
    }
}
