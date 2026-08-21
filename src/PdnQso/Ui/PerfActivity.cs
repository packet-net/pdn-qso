using System.Collections.ObjectModel;
using System.Globalization;
using PdnQso.Link;
using PdnQso.Link.Perf;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>
/// What Perf needs to know about the link it is measuring, beyond the station itself.
/// </summary>
/// <param name="DspRateHz">The rate the modem runs at, which turns a modulated burst's sample
/// count into the air time goodput is measured against.</param>
/// <param name="TxDelayMilliseconds">TXDELAY: part of every frame's air time, so part of the
/// answer.</param>
/// <param name="CentreHz">The audio centre, for the record the report carries.</param>
/// <param name="CsvPath">Where Export appends its row.</param>
public readonly record struct PerfLinkSettings(
    int DspRateHz,
    int TxDelayMilliseconds,
    double? CentreHz,
    string CsvPath);

/// <summary>
/// The Perf pane: pick a procedure and its parameters, press Start, and watch the table fill
/// in. And, when nothing has been asked of it, be the far end of somebody else's run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The responders run while this station is idle.</b> A measurement needs two stations and
/// only one operator should have to do anything: attaching a station here starts
/// <see cref="PerfRun.RunStreamReceiverAsync"/> and <see cref="PerfRun.RunPongResponderAsync"/>
/// on it, so the far end answers a stream's wrap-up request and every ping without its operator
/// touching a key. Pressing Start stops them for the duration of this station's own run and
/// starts them again afterwards, which is what "when idle" means and why the status line says
/// which state it is in.
/// </para>
/// <para>
/// The two responders do not tread on each other: the pong responder answers only probes that
/// carry a payload, and a stream's wrap-up request carries none; the stream receiver acts on a
/// request only once it has already counted frames on that session.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="PerfRun"/> raises its progress from whichever thread the frame
/// arrived on, so every view touch goes through <see cref="IApplication.Invoke"/>.
/// </para>
/// </remarks>
public sealed class PerfActivity : IActivityView
{
    private readonly IApplication _app;
    private readonly Func<PerfLinkSettings> _settings;
    private readonly Action<string> _log;
    private readonly PerfActivityModel _model = new();
    private readonly ObservableCollection<string> _table = [];

    private readonly Label _status = new();
    private readonly Button _procedure = new();
    private readonly Button _start = new();
    private readonly Button _export = new();
    private readonly TextField _frames = new();
    private readonly TextField _payload = new();
    private readonly TextField _gap = new();
    private readonly ListView _list = new();

    private IStation? _station;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _responderStop;
    private Task _responders = Task.CompletedTask;

    /// <summary>Builds the Perf activity. No station until <see cref="Attach"/>.</summary>
    /// <param name="app">The application; every view touch is marshalled through it.</param>
    /// <param name="settings">The link's rate, TXDELAY, centre and CSV path, read afresh at
    /// each attach and at each run.</param>
    /// <param name="log">The window's log pane, where the text summary goes.</param>
    public PerfActivity(IApplication app, Func<PerfLinkSettings> settings, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        _app = app;
        _settings = settings;
        _log = log;

        View = BuildLayout();
    }

    /// <inheritdoc />
    public string Title => "Perf";

    /// <inheritdoc />
    public View View { get; }

    /// <summary>What this pane is showing.</summary>
    public PerfActivityModel Model => _model;

    /// <inheritdoc />
    public void Shown() => _ = _start.SetFocus();

    /// <inheritdoc />
    public void Attach(IStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        Drop();
        _station = station;
        _model.Clear();

        var stop = new CancellationTokenSource();
        _stop = stop;
        StartResponders(station);
        Redraw();
    }

    /// <summary>Runs the measurement the fields describe, exactly as Start does.</summary>
    /// <returns>False when the parameters will not do, or a run is already going.</returns>
    public bool Start()
    {
        if (_station is not IStation station || _model.RunInProgress)
        {
            return false;
        }

        if (!ReadFields())
        {
            return false;
        }

        IReadOnlyList<string> problems = _model.Validate();
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
            {
                _log($"perf: {problem}");
            }

            return false;
        }

        if (!station.CanTransmit)
        {
            _log("perf: this station is receive only - it can be the far end but not the near one");
            return false;
        }

        PerfLinkSettings settings = _settings();
        PerfProcedure procedure = _model.Procedure;
        _model.StartRun();
        Redraw();

        _ = Task.Run(async () =>
        {
            CancellationToken token = _stop?.Token ?? CancellationToken.None;
            try
            {
                // Idle means idle: this station stops answering for somebody else while it is
                // making a measurement of its own.
                await StopRespondersAsync().ConfigureAwait(false);

                var run = new PerfRun();
                run.Progress += OnReport;
                run.Completed += OnReport;

                PerfReport report = procedure == PerfProcedure.Ping
                    ? await run.RunPingAsync(
                        station, _model.ToPingOptions(settings.CentreHz), token).ConfigureAwait(false)
                    : await run.RunStreamSenderAsync(
                        station,
                        station.Modem,
                        settings.DspRateHz,
                        _model.ToStreamOptions(settings.TxDelayMilliseconds, settings.CentreHz),
                        token).ConfigureAwait(false);

                _app.Invoke(() => _log($"perf: {PerfActivityModel.Name(procedure)} finished, "
                    + $"{report.FramesHeard} of {report.FramesSent} heard"));
            }
            catch (OperationCanceledException)
            {
                _app.Invoke(() => _log("perf: run cancelled"));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _app.Invoke(() => _log($"perf: run failed - {e.Message}"));
            }
            finally
            {
                _app.Invoke(() =>
                {
                    _model.FinishRun();
                    Redraw();
                });

                if (_station is IStation still && ReferenceEquals(still, station))
                {
                    StartResponders(station);
                }
            }
        });

        return true;
    }

    /// <summary>Writes the latest report to the CSV and the text summary to the log.</summary>
    /// <returns>False when nothing has been measured yet.</returns>
    public bool Export()
    {
        PerfLinkSettings settings = _settings();
        try
        {
            string? summary = _model.Export(settings.CsvPath);
            if (summary is null)
            {
                _log("perf: nothing measured yet, so nothing to export");
                return false;
            }

            foreach (string line in summary.Split('\n'))
            {
                _log($"perf: {line}");
            }

            _log($"perf: row appended to {settings.CsvPath}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log($"perf: could not write {settings.CsvPath} - {e.Message}");
            return false;
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

        _status.X = 0;
        _status.Y = 0;
        _status.Width = Dim.Fill();
        _status.Height = 1;

        _procedure.X = 0;
        _procedure.Y = 1;
        _procedure.Text = ProcedureLabel();
        _procedure.Accepting += (_, e) =>
        {
            e.Handled = true;
            _model.Procedure =
                _model.Procedure == PerfProcedure.Stream ? PerfProcedure.Ping : PerfProcedure.Stream;
            _procedure.Text = ProcedureLabel();
            Redraw();
        };

        _start.X = Pos.Right(_procedure) + 2;
        _start.Y = 1;
        _start.Text = "_Start";
        _start.Accepting += (_, e) =>
        {
            e.Handled = true;
            Start();
        };

        _export.X = Pos.Right(_start) + 2;
        _export.Y = 1;
        _export.Text = "E_xport";
        _export.Accepting += (_, e) =>
        {
            e.Handled = true;
            Export();
        };

        Field(frame, "frames", 0, _frames, _model.FrameCount);
        Field(frame, "payload", 16, _payload, _model.PayloadSize);
        Field(frame, "gap ms", 32, _gap, _model.GapMilliseconds);

        _list.X = 0;
        _list.Y = 4;
        _list.Width = Dim.Fill();
        _list.Height = Dim.Fill();
        _list.SetSource(_table);

        frame.Add(_status, _procedure, _start, _export, _list);
        return frame;

        static void Field(View parent, string label, int x, TextField field, int value)
        {
            parent.Add(new Label { X = x, Y = 3, Width = 8, Height = 1, Text = label });
            field.X = x + 8;
            field.Y = 3;
            field.Width = 7;
            field.Height = 1;
            field.Text = value.ToString(CultureInfo.InvariantCulture);
            parent.Add(field);
        }
    }

    private string ProcedureLabel() => $"_Procedure: {PerfActivityModel.Name(_model.Procedure)}";

    /// <summary>Reads the three fields into the model, complaining about anything that is not a number.</summary>
    private bool ReadFields()
    {
        var bad = new List<string>(3);
        _model.FrameCount = Integer(_frames, "frames", _model.FrameCount, bad);
        _model.PayloadSize = Integer(_payload, "payload", _model.PayloadSize, bad);
        _model.GapMilliseconds = Integer(_gap, "gap ms", _model.GapMilliseconds, bad);
        if (bad.Count == 0)
        {
            return true;
        }

        _log("perf: these are not numbers: " + string.Join(", ", bad));
        return false;

        static int Integer(TextField field, string name, int fallback, List<string> bad)
        {
            if (int.TryParse(
                field.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }

            bad.Add(name);
            return fallback;
        }
    }

    private void StartResponders(IStation station)
    {
        var stop = new CancellationTokenSource();
        _responderStop = stop;
        _responders = Task.Run(() => RespondLoopAsync(station, stop.Token));
    }

    private async Task StopRespondersAsync()
    {
        if (_responderStop is not CancellationTokenSource stop)
        {
            return;
        }

        _responderStop = null;
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _responders.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever it was, the responders are stopped now, which is all this was for.
            _ = e;
        }

        stop.Dispose();
        _responders = Task.CompletedTask;
    }

    private async Task RespondLoopAsync(IStation station, CancellationToken token)
    {
        var run = new PerfRun();
        run.Progress += OnReport;
        run.Completed += OnReport;

        _app.Invoke(() =>
        {
            _model.SetResponder(true);
            Redraw();
        });

        Task pong = run.RunPongResponderAsync(station, token);
        try
        {
            // One stream receiver after another: each one ends when the far end asks it to wrap
            // up, and the next one is waiting before the operator over there can start again.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await run.RunStreamReceiverAsync(station, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _app.Invoke(() => _log($"perf: responder - {e.Message}"));
                    break;
                }
            }

            await pong.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped on request, which is how this loop always ends.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _app.Invoke(() => _log($"perf: responder stopped - {e.Message}"));
        }
        finally
        {
            run.Progress -= OnReport;
            run.Completed -= OnReport;
            _app.Invoke(() =>
            {
                _model.SetResponder(false);
                Redraw();
            });
        }
    }

    private void OnReport(PerfReport report) => _app.Invoke(() =>
    {
        _model.NoteReport(report);
        Redraw();
    });

    private void Redraw()
    {
        _status.Text = _model.StatusLine;
        _table.Clear();
        foreach (string line in _model.Table)
        {
            _table.Add(line);
        }
    }

    private void Drop()
    {
        _station = null;
        CancellationTokenSource? stop = _stop;
        CancellationTokenSource? responders = _responderStop;
        _stop = null;
        _responderStop = null;

        responders?.Cancel();
        stop?.Cancel();

        // Disposed on a background thread: cancelling a responder means waiting for whatever it
        // is inside, and the UI thread is not where that wait belongs.
        Task running = _responders;
        _responders = Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _ = e;
            }

            responders?.Dispose();
            stop?.Dispose();
        });
    }
}
