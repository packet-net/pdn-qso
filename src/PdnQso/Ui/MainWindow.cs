using System.Collections.ObjectModel;
using Packet.SoundModem.Modems;
using PdnQso.Config;
using PdnQso.Link;
using PdnQso.Link.Devices;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>
/// The screen of design.md section 6: a status bar, the Monitor pane which is always on, an
/// activity pane below it, and a log pane at the foot.
/// </summary>
/// <remarks>
/// <para>
/// Monitor is a pane and not a mode, which is the decision the whole layout follows from: an
/// operator running a file transfer still wants to see what else is on the channel, and a tool
/// that hides the band while it is busy is a tool you cannot trust a report from. F4 gives
/// Monitor the whole screen; F1 to F3 put an activity underneath it.
/// </para>
/// <para>
/// Station events arrive on the capture thread, so everything that touches a view goes through
/// <c>IApplication.Invoke</c>. The formatting itself is in <see cref="MonitorLine"/> and
/// <see cref="StatusLine"/>, which are pure and tested; what is left here is layout and
/// plumbing.
/// </para>
/// </remarks>
public sealed class MainWindow : Window
{
    /// <summary>How many heard frames the pane keeps before the oldest fall off.</summary>
    public const int MonitorHistory = 500;

    /// <summary>How many lines the log pane keeps.</summary>
    public const int LogHistory = 500;

    private const int LogPaneHeight = 6;

    private readonly IApplication _app;
    private readonly StationHost _host;
    private readonly IReadOnlyList<IActivityView> _activities;
    private readonly string _configPath;

    private readonly Label _status = new();
    private readonly Label _keys = new();
    private readonly FrameView _monitorPane = new();
    private readonly ListView _monitorList = new();
    private readonly FrameView _activityPane = new();
    private readonly FrameView _logPane = new();
    private readonly ListView _logList = new();

    private readonly ObservableCollection<string> _monitorLines = [];
    private readonly ObservableCollection<string> _logLines = [];
    private readonly List<HeardFrame> _heard = [];

    private IActivityView? _activity;
    private IStation? _station;
    private PayloadView _payloadView = PayloadView.Text;
    private double? _lastSnrDb;
    private string? _correspondent;
    private string _power = "";
    private bool _modal;
    private object? _refresh;

    /// <summary>Builds the window over a host that has already been started.</summary>
    /// <param name="app">The application instance; everything is marshalled through it.</param>
    /// <param name="host">The station and its lifetime.</param>
    /// <param name="activities">Chat, File, Perf - in F-key order.</param>
    /// <param name="version">For the title bar.</param>
    /// <param name="configPath">The file the settings dialog writes to.</param>
    public MainWindow(
        IApplication app,
        StationHost host,
        IReadOnlyList<IActivityView> activities,
        string version,
        string configPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(activities);

        _app = app;
        _host = host;
        _activities = activities;
        _configPath = configPath;

        Title = $"pdn-qso {version}";
        BuildLayout();
        ShowActivity(null);

        _host.Log += OnLog;
        _host.StationChanged += OnStationChanged;

        if (_host.Station is IStation station)
        {
            AttachStation(station);
        }

        // One timer for everything that is a level rather than an event: the lamps, the last
        // SNR, and the power read back off the radio. Twice a second is faster than anyone can
        // read and slow enough to cost nothing.
        _refresh = _app.AddTimeout(TimeSpan.FromMilliseconds(500), OnTick);
        _app.Keyboard.KeyDown += OnGlobalKey;
    }

    /// <summary>One frame the pane is holding, so a change of view can re-render it.</summary>
    private readonly record struct HeardFrame(
        DateTimeOffset At, byte[] Frame, FrameQuality Quality);

    private void BuildLayout()
    {
        _status.X = 0;
        _status.Y = 0;
        _status.Width = Dim.Fill();
        _status.Height = 1;

        _monitorPane.Title = "Monitor";
        _monitorPane.X = 0;
        _monitorPane.Y = 1;
        _monitorPane.Width = Dim.Fill();

        var header = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            Text = MonitorLine.Header,
        };

        _monitorList.X = 0;
        _monitorList.Y = 1;
        _monitorList.Width = Dim.Fill();
        _monitorList.Height = Dim.Fill();
        _monitorList.SetSource(_monitorLines);
        _monitorPane.Add(header, _monitorList);

        _activityPane.Title = "Activity";
        _activityPane.X = 0;
        _activityPane.Width = Dim.Fill();
        _activityPane.Visible = false;

        _logPane.Title = "Log";
        _logPane.X = 0;
        _logPane.Y = Pos.AnchorEnd(LogPaneHeight + 1);
        _logPane.Width = Dim.Fill();
        _logPane.Height = LogPaneHeight;
        _logList.X = 0;
        _logList.Y = 0;
        _logList.Width = Dim.Fill();
        _logList.Height = Dim.Fill();
        _logList.SetSource(_logLines);
        _logPane.Add(_logList);

        _keys.X = 0;
        _keys.Y = Pos.AnchorEnd(1);
        _keys.Width = Dim.Fill();
        _keys.Height = 1;
        _keys.Text = KeyHints();

        Add(_status, _monitorPane, _activityPane, _logPane, _keys);
    }

    private static string KeyHints() =>
        "F1 Chat  F2 File  F3 Perf  F4 Monitor  F5 Settings  F6 Text/Hex  Ctrl+Q Quit";

    /// <summary>Switches the activity pane, or gives Monitor the whole screen with null.</summary>
    public void ShowActivity(IActivityView? activity)
    {
        if (activity is null)
        {
            _activityPane.Visible = false;
            _monitorPane.Height = Dim.Fill(LogPaneHeight + 1);
            SetNeedsLayout();
            return;
        }

        if (!ReferenceEquals(_activity, activity))
        {
            _activityPane.RemoveAll();
            _activityPane.Add(activity.View);
            _activity = activity;
            if (_station is IStation station)
            {
                activity.Attach(station);
            }
        }

        _activityPane.Title = activity.Title;
        _activityPane.Visible = true;
        _monitorPane.Height = Dim.Percent(45);
        _activityPane.Y = Pos.Bottom(_monitorPane);
        _activityPane.Height = Dim.Fill(LogPaneHeight + 1);
        SetNeedsLayout();
    }

    /// <summary>Adds a line to the log pane.</summary>
    public void WriteLog(string line)
    {
        _logLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        while (_logLines.Count > LogHistory)
        {
            _logLines.RemoveAt(0);
        }

        ScrollToEnd(_logList, _logLines.Count);
    }

    private void OnLog(string line) => _app.Invoke(() => WriteLog(line));

    private void OnStationChanged(IStation station) => _app.Invoke(() => AttachStation(station));

    private void AttachStation(IStation station)
    {
        if (_station is not null)
        {
            _station.RawFrameReceived -= OnRawFrame;
            _station.FrameReceived -= OnLinkFrame;
        }

        _station = station;
        station.RawFrameReceived += OnRawFrame;
        station.FrameReceived += OnLinkFrame;
        _correspondent = null;
        _lastSnrDb = null;
        _activity?.Attach(station);
        RefreshStatus();
    }

    private void OnRawFrame(byte[] frame, FrameQuality quality)
    {
        // The capture thread. Everything here is a copy into a queue; the drawing happens on
        // the UI thread, where Terminal.Gui requires it.
        var heard = new HeardFrame(DateTimeOffset.Now, frame, quality);
        _app.Invoke(() =>
        {
            _heard.Add(heard);
            if (_heard.Count > MonitorHistory)
            {
                _heard.RemoveAt(0);
                _monitorLines.RemoveAt(0);
            }

            _lastSnrDb = quality.SnrDb ?? _lastSnrDb;
            _monitorLines.Add(Render(heard));
            ScrollToEnd(_monitorList, _monitorLines.Count);
        });
    }

    private void OnLinkFrame(LinkFrame frame, FrameQuality quality)
    {
        // Whoever is talking to us is who the status bar calls the correspondent. Ours is not
        // interesting: on a loopback or a pipe pair we hear ourselves, and "with M0LTE-7" on
        // M0LTE-7's own screen would be a joke at the operator's expense.
        if (_station is IStation station
            && !string.Equals(frame.Source, station.Callsign, StringComparison.Ordinal))
        {
            string source = frame.Source;
            _app.Invoke(() => _correspondent = source);
        }
    }

    private string Render(HeardFrame heard) =>
        MonitorLine.Format(heard.At, heard.Frame, heard.Quality, _payloadView);

    private void RerenderMonitor()
    {
        _monitorLines.Clear();
        foreach (HeardFrame heard in _heard)
        {
            _monitorLines.Add(Render(heard));
        }

        ScrollToEnd(_monitorList, _monitorLines.Count);
    }

    private static void ScrollToEnd(ListView list, int count)
    {
        if (count == 0)
        {
            return;
        }

        list.SelectedItem = count - 1;
        list.EnsureSelectedItemVisible();
    }

    private bool OnTick()
    {
        RefreshStatus();
        _ = ReadPowerAsync();
        return true;
    }

    private async Task ReadPowerAsync()
    {
        if (_station?.Power is not IPowerControl control || control.Unit == PowerUnit.None)
        {
            _power = "";
            return;
        }

        try
        {
            PowerReading reading = await control.ReadAsync().ConfigureAwait(false);
            _power = reading.Display;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _power = "?";
        }
    }

    private void RefreshStatus()
    {
        QsoConfig config = _host.Config;
        _status.Text = StatusLine.Format(new StatusSnapshot(
            _station?.DeviceName ?? config.Device,
            // The mode as the operator set it, not as the modem describes itself: both ends
            // have to agree on this string, and "bpsk300-il2pc-multi9" is not the string.
            config.Mode,
            config.ResolvedAudioCentreHz,
            config.RfFrequencyHz,
            _power,
            _station?.Transmitting ?? false,
            _station?.Busy ?? false,
            _lastSnrDb,
            _correspondent,
            _host.MonitorOnly));
    }

    private void OnGlobalKey(object? sender, Key key)
    {
        if (_modal || key.Handled)
        {
            return;
        }

        if (key == Key.F1 || key == Key.F2 || key == Key.F3)
        {
            int index = key == Key.F1 ? 0 : key == Key.F2 ? 1 : 2;
            if (index < _activities.Count)
            {
                ShowActivity(_activities[index]);
            }

            key.Handled = true;
            return;
        }

        if (key == Key.F4)
        {
            ShowActivity(null);
            key.Handled = true;
            return;
        }

        if (key == Key.F5)
        {
            OpenSettings();
            key.Handled = true;
            return;
        }

        if (key == Key.F6)
        {
            _payloadView = _payloadView == PayloadView.Text ? PayloadView.Hex : PayloadView.Text;
            RerenderMonitor();
            key.Handled = true;
        }
    }

    private void OpenSettings()
    {
        _modal = true;
        try
        {
            QsoConfig? updated = SettingsDialog.Show(_app, _host.Config);
            if (updated is null || updated == _host.Config)
            {
                return;
            }

            try
            {
                updated.Save(_configPath);
                WriteLog($"settings: saved to {_configPath}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                WriteLog($"settings: could not save to {_configPath} - {e.Message}");
            }

            RestartFor(updated);
        }
        finally
        {
            _modal = false;
        }
    }

    private void RestartFor(QsoConfig config)
    {
        WriteLog("settings: restarting the station");
        _ = Task.Run(async () =>
        {
            try
            {
                await _host.ApplyAsync(config).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _app.Invoke(() => WriteLog($"station: could not restart - {e.Message}"));
            }
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.Keyboard.KeyDown -= OnGlobalKey;
            if (_refresh is not null)
            {
                _app.RemoveTimeout(_refresh);
                _refresh = null;
            }

            _host.Log -= OnLog;
            _host.StationChanged -= OnStationChanged;
            if (_station is not null)
            {
                _station.RawFrameReceived -= OnRawFrame;
                _station.FrameReceived -= OnLinkFrame;
                _station = null;
            }
        }

        base.Dispose(disposing);
    }
}
