using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Rvrb;

/// <summary>
///     A modal list to choose one item from: Enter (or the default button) picks the selected row, Esc cancels.
///     The rows load asynchronously; <see cref="Selected"/> is set when the dialog closes on a choice.
/// </summary>
internal abstract class PickerDialog : Dialog
{
    private readonly ObservableCollection<string> _rows = [];
    private List<JsonNode> _items = [];

    protected PickerDialog (string title, string pickText)
    {
        Title = title;
        Width = Dim.Percent (60);
        Height = Dim.Percent (70);

        Message = new () { X = 0, Y = Pos.AnchorEnd (1), Width = Dim.Fill (), Text = "Loading…" };
        List = new () { X = 0, Y = 0, Width = Dim.Fill (), Height = Dim.Fill (1) };
        List.SetSource (_rows);

        // Enter on a row picks it; the Accept then bubbles up and closes the dialog.
        List.Accepting += (_, _) => Selected = Current ();

        Add (List, Message);

        Button cancel = new () { Text = "Cancel" };
        Button pick = new () { Text = pickText };
        pick.Accepting += (_, _) => Selected = Current ();

        // The last button is the default (Enter).
        AddButton (cancel);
        AddButton (pick);
    }

    public JsonNode? Selected { get; private set; }

    protected ListView List { get; }

    protected Label Message { get; }

    protected abstract string Describe (JsonNode item);

    protected void SetItems (IEnumerable<JsonNode> items, string emptyMessage)
    {
        _items = items.ToList ();
        _rows.Clear ();

        foreach (JsonNode item in _items)
        {
            _rows.Add (Describe (item));
        }

        Message.Text = _items.Count == 0 ? emptyMessage : "";

        if (_items.Count > 0)
        {
            List.Value = 0;
        }
    }

    protected void Ui (Action action) => App?.Invoke (action);

    private JsonNode? Current () => List.Value is { } i && i < _items.Count ? _items [i] : null;
}

/// <summary>Popular channels, or a search: type in the box and press Enter.</summary>
internal sealed class ChannelPicker : PickerDialog
{
    private readonly RvrbApi _api;

    public ChannelPicker (RvrbApi api) : base ("Channels", "_Join")
    {
        _api = api;

        TextField search = new () { X = 0, Y = 0, Width = Dim.Fill () };
        List.Y = 1;
        Add (search);

        // Enter in the search box searches instead of closing the dialog; the results then get the focus, so a
        // second Enter joins. Down also moves to the results.
        search.Accepting += (_, e) =>
                            {
                                e.Handled = true;
                                _ = LoadAsync (search.Text, focusResults: true);
                            };

        search.KeyDown += (_, key) =>
                          {
                              if (key == Key.CursorDown)
                              {
                                  key.Handled = true;
                                  List.SetFocus ();
                              }
                          };

        Initialized += (_, _) =>
                       {
                           search.SetFocus ();
                           _ = LoadAsync (null, focusResults: false);
                       };
    }

    protected override string Describe (JsonNode channel) =>
        $"{ChannelTitle (channel)}  ({(channel ["users"] as JsonArray)?.Count ?? 0})";

    public static string ChannelTitle (JsonNode channel) =>
        channel ["title"].Str () is { Length: > 0 } t ? t : channel ["slug"].Str () ?? "?";

    private async Task LoadAsync (string? search, bool focusResults)
    {
        Ui (() => Message.Text = "Loading…");

        try
        {
            JsonNode result = await _api.ListChannelsAsync (search);
            JsonArray channels = result ["channels"] as JsonArray ?? [];

            Ui (() =>
                {
                    SetItems (channels.OfType<JsonNode> (),
                              string.IsNullOrWhiteSpace (search)
                                  ? "No active channels right now; type to search and press Enter."
                                  : $"No channels match \"{search}\".");

                    if (focusResults && channels.Count > 0)
                    {
                        List.SetFocus ();
                    }
                });
        }
        catch (Exception ex)
        {
            Ui (() => Message.Text = $"Could not load channels: {ex.Message}");
        }
    }
}

/// <summary>The user's Spotify Connect devices. Spotify only lists devices that are running.</summary>
internal sealed class DevicePicker : PickerDialog
{
    public DevicePicker (RvrbApi api, string? currentId) : base ("Spotify device", "_Use")
    {
        Initialized += async (_, _) =>
                       {
                           List.SetFocus ();

                           try
                           {
                               JsonArray devices = await api.GetDevicesAsync ();

                               Ui (() =>
                                   {
                                       SetItems (devices.OfType<JsonNode> (),
                                                 "No devices. Open Spotify on a computer, phone or speaker, then try again.");

                                       int current = devices.ToList ().FindIndex (d => d? ["id"].Str () == currentId);

                                       if (current >= 0)
                                       {
                                           List.Value = current;
                                       }
                                   });
                           }
                           catch (Exception ex)
                           {
                               Ui (() => Message.Text = $"Could not load devices: {ex.Message}");
                           }
                       };
    }

    protected override string Describe (JsonNode device)
    {
        List<string> tags = [];

        if (device ["is_active"].Bool () == true)
        {
            tags.Add ("active");
        }

        if (device ["is_preferred"].Bool () == true)
        {
            tags.Add ("preferred");
        }

        string type = device ["type"].Str () is { Length: > 0 } t ? $" ({t})" : "";

        return $"{device ["name"].Str () ?? "?"}{type}{(tags.Count > 0 ? "  · " + string.Join (", ", tags) : "")}";
    }
}
