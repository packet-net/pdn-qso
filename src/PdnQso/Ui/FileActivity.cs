using System.Collections.ObjectModel;
using PdnQso.Link;
using PdnQso.Link.Transfer;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>
/// The File pane: a receiver that is always listening, a field to send one, and a bar for each
/// direction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The receiver runs whether anyone asked for it or not.</b> A fountain transfer has no
/// handshake to miss: the sender simply starts pouring, and a station that was not already
/// listening has lost the beginning of it. So attaching a station starts a
/// <see cref="FileReceiver"/> loop on it, and every completed or abandoned transfer starts the
/// next one. That is what lets one operator send a file to a station whose operator has gone to
/// make tea.
/// </para>
/// <para>
/// <b>Offers are accepted automatically</b>, which design.md's "one radio, one correspondent"
/// is the licence for: this is a tool two people point at each other on purpose, not a public
/// drop box. The one offer that is refused is our own, heard back off a device that loops its
/// own transmit round - accepting that would have the station decode the file it is sending.
/// </para>
/// <para>
/// <b>Threading.</b> Both ends raise their progress on the station's threads, so every view
/// touch goes through <see cref="IApplication.Invoke"/>. <see cref="Attach"/> cancels the
/// previous station's receiver loop and any transfer in flight before starting the new one.
/// </para>
/// </remarks>
public sealed class FileActivity : IActivityView
{
    private readonly IApplication _app;
    private readonly Func<string> _directory;
    private readonly Func<FileTransferOptions> _options;
    private readonly Action<string> _log;
    private readonly FileActivityModel _model = new();
    private readonly ObservableCollection<string> _lines = [];

    private readonly Label _header = new();
    private readonly TextField _path = new();
    private readonly Label _sendLine = new();
    private readonly ProgressBar _sendBar = new();
    private readonly Label _receiveLine = new();
    private readonly ProgressBar _receiveBar = new();
    private readonly ListView _list = new();

    private IStation? _station;
    private CancellationTokenSource? _stop;
    private volatile bool _sendingOurOwn;

    /// <summary>Builds the File activity. No station until <see cref="Attach"/>.</summary>
    /// <param name="app">The application; every view touch is marshalled through it.</param>
    /// <param name="directory">Where received files are written, read afresh at each attach.</param>
    /// <param name="options">Block size and fountain shape, read afresh at each attach.</param>
    /// <param name="log">The window's log pane.</param>
    public FileActivity(
        IApplication app,
        Func<string> directory,
        Func<FileTransferOptions> options,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _app = app;
        _directory = directory;
        _options = options;
        _log = log;

        View = BuildLayout();
    }

    /// <inheritdoc />
    public string Title => "File";

    /// <inheritdoc />
    public View View { get; }

    /// <summary>What this pane is showing.</summary>
    public FileActivityModel Model => _model;

    /// <inheritdoc />
    public void Shown() => _ = _path.SetFocus();

    /// <inheritdoc />
    public void Attach(IStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        Drop();
        _station = station;
        _model.Clear();

        string directory = _directory();
        FileTransferOptions options = _options();
        _header.Text = $"receiving into {directory}";

        var stop = new CancellationTokenSource();
        _stop = stop;
        _ = Task.Run(() => ReceiveLoopAsync(station, directory, options, stop.Token));

        Redraw();
    }

    /// <summary>Sends one file, exactly as pressing Enter in the path field does.</summary>
    /// <param name="path">The file to send.</param>
    /// <returns>False when there was nothing to send, or the station cannot.</returns>
    public bool Send(string path)
    {
        if (_station is not IStation station)
        {
            return false;
        }

        string file = (path ?? "").Trim();
        if (file.Length == 0)
        {
            return false;
        }

        // ~ is what an operator types and what no API expands.
        if (file.StartsWith("~/", StringComparison.Ordinal))
        {
            file = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolderOption.DoNotVerify),
                file[2..]);
        }

        if (!File.Exists(file))
        {
            NoteLine($"no such file: {file}");
            return false;
        }

        if (!station.CanTransmit)
        {
            NoteLine("this station is receive only - nothing was sent");
            return false;
        }

        if (_sendingOurOwn)
        {
            NoteLine("a transfer is already going out - one at a time");
            return false;
        }

        FileTransferOptions options = _options();
        CancellationToken token = _stop?.Token ?? CancellationToken.None;
        _sendingOurOwn = true;

        _ = Task.Run(async () =>
        {
            var sender = new FileSender(station, options);
            sender.Progress += OnProgress;
            sender.Completed += OnCompleted;
            sender.Failed += reason => OnFailed(FileTransferRole.Sender, reason);
            try
            {
                await sender.SendAsync(file, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The station went away underneath the transfer; Attach has already said so.
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                OnFailed(FileTransferRole.Sender, e.Message);
            }
            finally
            {
                _sendingOurOwn = false;
            }
        });

        NoteLine($"sending {Path.GetFileName(file)}");
        return true;
    }

    private async Task ReceiveLoopAsync(
        IStation station, string directory, FileTransferOptions options, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var receiver = new FileReceiver(station, directory, options)
            {
                // Everything except our own transmission coming back at us off a device that
                // loops it round: taking that offer would have this station decode the file it
                // is in the middle of sending.
                AcceptOffer = _ => !_sendingOurOwn,
            };
            receiver.OfferHeard += OnOffer;
            receiver.Progress += OnProgress;
            receiver.Completed += OnCompleted;
            receiver.Failed += reason => OnFailed(FileTransferRole.Receiver, reason);

            try
            {
                await receiver.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                OnFailed(FileTransferRole.Receiver, e.Message);

                // A receiver that failed for a reason of its own - an unwritable directory,
                // say - would otherwise spin. A second between attempts is long enough not to
                // fill the log and short enough not to miss the next offer.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private View BuildLayout()
    {
        var frame = new View
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),

            // A container that cannot take focus cannot pass it to its children either,
            // so without this the input below is unreachable from the keyboard.
            CanFocus = true,
        };

        _header.X = 0;
        _header.Y = 0;
        _header.Width = Dim.Fill();
        _header.Height = 1;
        _header.Text = "no station";

        var label = new Label { X = 0, Y = 1, Width = 6, Height = 1, Text = "send" };
        _path.X = 6;
        _path.Y = 1;
        _path.Width = Dim.Fill();
        _path.Height = 1;
        _path.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (Send(_path.Text))
            {
                _path.Text = "";
            }
        };

        Row(_sendLine, _sendBar, 2);
        Row(_receiveLine, _receiveBar, 4);

        _list.X = 0;
        _list.Y = 6;
        _list.Width = Dim.Fill();
        _list.Height = Dim.Fill();
        _list.SetSource(_lines);

        frame.Add(_header, label, _path, _sendLine, _sendBar, _receiveLine, _receiveBar, _list);
        return frame;

        static void Row(Label line, ProgressBar bar, int y)
        {
            line.X = 0;
            line.Y = y;
            line.Width = Dim.Fill();
            line.Height = 1;
            bar.X = 0;
            bar.Y = y + 1;
            bar.Width = Dim.Fill();
            bar.Height = 1;
            bar.Fraction = 0;
        }
    }

    private void OnOffer(FileOfferPayload offer, bool accepted) => _app.Invoke(() =>
    {
        _model.NoteOffer(offer, accepted, DateTimeOffset.Now);
        Redraw();
    });

    private void OnProgress(FileProgress progress) => _app.Invoke(() =>
    {
        _model.Note(progress);
        Redraw();
    });

    private void OnCompleted(FileTransferResult result) => _app.Invoke(() =>
    {
        _model.NoteResult(result, DateTimeOffset.Now);
        _log($"file: {result}");
        Redraw();
    });

    private void OnFailed(FileTransferRole role, string reason) => _app.Invoke(() =>
    {
        _model.NoteFailure(role, reason, DateTimeOffset.Now);
        _log($"file: {role.ToString().ToLowerInvariant()} failed - {reason}");
        Redraw();
    });

    private void NoteLine(string text)
    {
        _model.NoteLine(text, DateTimeOffset.Now);
        Redraw();
    }

    private void Redraw()
    {
        _sendLine.Text = _model.SendLine;
        _receiveLine.Text = _model.ReceiveLine;
        _sendBar.Fraction = (float)_model.SendFraction;
        _receiveBar.Fraction = (float)_model.ReceiveFraction;

        _lines.Clear();
        foreach (string line in _model.Lines)
        {
            _lines.Add(line);
        }

        if (_lines.Count > 0)
        {
            _list.SelectedItem = _lines.Count - 1;
            _list.EnsureSelectedItemVisible();
        }
    }

    private void Drop()
    {
        _station = null;
        _sendingOurOwn = false;
        if (_stop is not CancellationTokenSource stop)
        {
            return;
        }

        _stop = null;
        stop.Cancel();
        stop.Dispose();
    }
}
