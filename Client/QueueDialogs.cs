using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Rvrb;

/// <summary>
///     The DJ queue: Enter plays a track next (moves it to the top), Shift+↑/↓ moves it, Del removes it, and
///     "Add tracks…" (or `a`) searches Spotify. Changes go straight to the server.
/// </summary>
internal sealed class QueueDialog : Dialog
{
    private readonly TrackQueue _queue;
    private readonly RvrbApi _api;
    private readonly ListView _list;
    private readonly Label _message;
    private readonly ObservableCollection<string> _rows = [];

    public QueueDialog (RvrbApi api, TrackQueue queue)
    {
        _api = api;
        _queue = queue;
        Title = "Your queue";
        Width = Dim.Percent (70);
        Height = Dim.Percent (80);

        Label help = new () { X = 0, Y = 0, Width = Dim.Fill (), Text = "Enter: play next · Shift+↑↓: move · Del: remove · a: add tracks" };
        // No type-to-find: it would swallow the `a` key.
        _list = new () { X = 0, Y = 1, Width = Dim.Fill (), Height = Dim.Fill (1), KeystrokeNavigator = null };
        _list.SetSource (_rows);
        _message = new () { X = 0, Y = Pos.AnchorEnd (1), Width = Dim.Fill (), Text = "" };
        Add (help, _list, _message);

        // Handle Enter here, so it doesn't bubble up and close the dialog.
        _list.Accepting += (_, e) =>
                           {
                               e.Handled = true;

                               if (_list.Value is int i and > 0)
                               {
                                   Run (_queue.MoveAsync (i, 0), select: 0);
                               }
                           };

        _list.KeyDown += (_, key) =>
                         {
                             if (_list.Value is not { } i)
                             {
                                 return;
                             }

                             if (key == Key.Delete || key == Key.Backspace)
                             {
                                 key.Handled = true;
                                 Run (_queue.RemoveAsync (i), select: Math.Max (0, Math.Min (i, _queue.Tracks.Count - 2)));
                             }
                             else if (key == Key.CursorUp.WithShift && i > 0)
                             {
                                 key.Handled = true;
                                 Run (_queue.MoveAsync (i, i - 1), select: i - 1);
                             }
                             else if (key == Key.CursorDown.WithShift && i < _queue.Tracks.Count - 1)
                             {
                                 key.Handled = true;
                                 Run (_queue.MoveAsync (i, i + 1), select: i + 1);
                             }
                             else if (key == Key.A)
                             {
                                 key.Handled = true;
                                 ShowSearch ();
                             }
                         };

        // No _hotkey: in a dialog a plain letter would trigger it, and `a` is handled on the list.
        Button add = new () { Text = "Add tracks…" };
        add.Accepting += (_, e) =>
                         {
                             // Keep the dialog open: the button only opens the search.
                             e.Handled = true;
                             ShowSearch ();
                         };

        Button close = new () { Text = "Close" };
        AddButton (add);
        AddButton (close);

        _queue.Changed += OnQueueChanged;

        Initialized += (_, _) =>
                       {
                           _list.SetFocus ();
                           Fill (select: 0);

                           if (!_queue.Loaded)
                           {
                               _message.Text = "Loading…";
                               Run (_queue.ReloadAsync (), select: 0);
                           }
                       };
    }

    private int? _select;

    private void OnQueueChanged () => App?.Invoke (() => Fill (_select ?? _list.Value ?? 0));

    private void ShowSearch ()
    {
        using SearchDialog search = new (_api, _queue);
        App!.Run (search);
    }

    private void Run (Task change, int select)
    {
        _select = select;
        _message.Text = "Saving…";

        _ = change.ContinueWith (t => App?.Invoke (() => _message.Text = t.Exception is { } ex ? $"Failed: {ex.InnerException?.Message}" : ""));
    }

    private void Fill (int select)
    {
        _select = null;
        _rows.Clear ();

        for (var i = 0; i < _queue.Tracks.Count; i++)
        {
            _rows.Add ($"{i + 1,3}. {TrackQueue.Describe (_queue.Tracks [i])}");
        }

        Title = _queue.Total > _queue.Tracks.Count ? $"Your queue ({_queue.Total}, showing the first {_queue.Tracks.Count})" : $"Your queue ({_queue.Total})";

        if (_queue.Loaded && _rows.Count == 0)
        {
            _message.Text = "The queue is empty: press a to add tracks.";
        }

        if (_rows.Count > 0)
        {
            _list.Value = Math.Clamp (select, 0, _rows.Count - 1);
        }
    }

    protected override void Dispose (bool disposing)
    {
        _queue.Changed -= OnQueueChanged;
        base.Dispose (disposing);
    }
}

/// <summary>Spotify search: Enter in the box searches, Enter on a result adds it to the bottom of the queue.</summary>
internal sealed class SearchDialog : Dialog
{
    private readonly ObservableCollection<string> _rows = [];
    private List<JsonNode> _results = [];

    public SearchDialog (RvrbApi api, TrackQueue queue)
    {
        Title = "Add tracks";
        Width = Dim.Percent (70);
        Height = Dim.Percent (80);

        TextField search = new () { X = 0, Y = 0, Width = Dim.Fill () };
        ListView list = new () { X = 0, Y = 1, Width = Dim.Fill (), Height = Dim.Fill (1) };
        list.SetSource (_rows);
        Label message = new () { X = 0, Y = Pos.AnchorEnd (1), Width = Dim.Fill (), Text = "Type a search and press Enter." };
        Add (search, list, message);

        search.Accepting += (_, e) =>
                            {
                                e.Handled = true;
                                string query = search.Text.Trim ();

                                if (query.Length == 0)
                                {
                                    return;
                                }

                                message.Text = "Searching…";

                                _ = api.SearchTracksAsync (query)
                                       .ContinueWith (t => App?.Invoke (() =>
                                                                        {
                                                                            if (t.Exception is { } ex)
                                                                            {
                                                                                message.Text = $"Search failed: {ex.InnerException?.Message}";

                                                                                return;
                                                                            }

                                                                            _results = t.Result.OfType<JsonNode> ().ToList ();
                                                                            _rows.Clear ();

                                                                            foreach (JsonNode track in _results)
                                                                            {
                                                                                _rows.Add (TrackQueue.Describe (track));
                                                                            }

                                                                            message.Text = _results.Count == 0
                                                                                               ? $"Nothing found for \"{query}\"."
                                                                                               : "Enter adds the selected track to the bottom of your queue.";

                                                                            if (_results.Count > 0)
                                                                            {
                                                                                list.Value = 0;
                                                                                list.SetFocus ();
                                                                            }
                                                                        }));
                            };

        search.KeyDown += (_, key) =>
                          {
                              if (key == Key.CursorDown && _results.Count > 0)
                              {
                                  key.Handled = true;
                                  list.SetFocus ();
                              }
                          };

        // Enter on a result adds it; handled, so the dialog stays open for more.
        list.Accepting += (_, e) =>
                          {
                              e.Handled = true;

                              if (list.Value is not { } i || i >= _results.Count || _results [i] ["id"].Str () is not { } id)
                              {
                                  return;
                              }

                              string name = _results [i] ["name"].Str () ?? id;
                              message.Text = $"Adding {name}…";

                              _ = queue.AddAsync (id)
                                       .ContinueWith (t => App?.Invoke (() => message.Text = t.Exception is { } ex
                                                                                                 ? $"Could not add {name}: {ex.InnerException?.Message}"
                                                                                                 : $"Added {name} (queue: {queue.Total})."));
                          };

        Button close = new () { Text = "Close" };
        AddButton (close);

        Initialized += (_, _) => search.SetFocus ();
    }
}
