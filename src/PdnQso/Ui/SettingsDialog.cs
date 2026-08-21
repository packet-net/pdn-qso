using System.Globalization;
using PdnQso.Config;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>
/// The settings dialog of design.md section 6: every field of the config, in one place, checked
/// before it is saved.
/// </summary>
/// <remarks>
/// <para>
/// Validation happens on Save and refuses to close over a config that will not start a station,
/// listing everything wrong at once rather than one thing at a time. Which is the whole point
/// of having <see cref="QsoConfig.Validate"/> as a pure function: the same check runs here, at
/// start-up, and in the tests.
/// </para>
/// <para>
/// The ARQ and fountain fields are here because design.md puts them in the one dialog. Nothing
/// in this phase reads them; the activities that will are being built alongside.
/// </para>
/// </remarks>
public static class SettingsDialog
{
    /// <summary>
    /// Shows the dialog over <paramref name="current"/> and returns what was saved, or null if
    /// the operator backed out.
    /// </summary>
    public static QsoConfig? Show(IApplication app, QsoConfig current)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(current);

        using var dialog = new Dialog
        {
            Title = "Settings",
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
        };

        var fields = new Fields(dialog, current);
        QsoConfig? saved = null;

        var save = new Button
        {
            Text = "_Save",
            IsDefault = true,
            X = Pos.AnchorEnd(22),
            Y = Pos.AnchorEnd(1),
        };
        var cancel = new Button
        {
            Text = "_Cancel",
            X = Pos.AnchorEnd(11),
            Y = Pos.AnchorEnd(1),
        };

        save.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (!fields.TryRead(current, out QsoConfig? candidate, out string? why))
            {
                MessageBox.ErrorQuery(app, "Settings", why ?? "these settings will not do", "OK");
                return;
            }

            saved = candidate;
            app.RequestStop(dialog);
        };

        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            app.RequestStop(dialog);
        };

        dialog.Add(save, cancel);
        app.Run(dialog);
        return saved;
    }

    /// <summary>Every editable field, laid out in two columns.</summary>
    private sealed class Fields
    {
        private const int LabelWidth = 18;
        private const int FieldWidth = 26;
        private const int SecondColumn = 50;

        private readonly TextField _device;
        private readonly TextField _callsign;
        private readonly TextField _mode;
        private readonly TextField _audioCentre;
        private readonly TextField _rfFrequency;
        private readonly TextField _txDelay;
        private readonly TextField _inputGain;
        private readonly TextField _outputGain;
        private readonly TextField _captureRate;
        private readonly TextField _power;
        private readonly TextField _pttType;
        private readonly TextField _pttDevice;
        private readonly TextField _pttGpio;
        private readonly TextField _pttSerialLine;
        private readonly CheckBox _lowerSideband;

        private readonly CheckBox _identEnabled;
        private readonly TextField _identCallsign;
        private readonly TextField _identInterval;
        private readonly TextField _identWpm;
        private readonly TextField _ackTimeout;
        private readonly TextField _maxRetries;
        private readonly CheckBox _stepWaveform;
        private readonly TextField _downloadDirectory;
        private readonly TextField _perfCsv;
        private readonly TextField _fountainC;
        private readonly TextField _fountainDelta;
        private readonly TextField _frameLog;
        private readonly TextField _flexDax;
        private readonly TextField _flexAntenna;
        private readonly TextField _uberSdrMode;
        private readonly TextField _uberSdrPassword;
        private readonly CheckBox _useMixerPower;

        public Fields(View parent, QsoConfig config)
        {
            int left = 0;
            _device = Add(parent, 1, ref left, "Device", config.Device);
            _callsign = Add(parent, 1, ref left, "Callsign", config.Callsign);
            _mode = Add(parent, 1, ref left, "Mode", config.Mode);
            _audioCentre = Add(parent, 1, ref left, "Audio centre Hz", Text(config.AudioCentreHz));
            _rfFrequency = Add(parent, 1, ref left, "RF frequency Hz", Text(config.RfFrequencyHz));
            _txDelay = Add(parent, 1, ref left, "TX delay ms", Text(config.TxDelayMs));
            _inputGain = Add(parent, 1, ref left, "Audio in gain", Text(config.InputGain));
            _outputGain = Add(parent, 1, ref left, "Audio out gain", Text(config.OutputGain));
            _captureRate = Add(parent, 1, ref left, "Capture rate Hz", Text(config.CaptureRateHz));
            _power = Add(parent, 1, ref left, "Power (W or %)", Text(config.Power));
            _pttType = Add(parent, 1, ref left, "PTT none/cm108/serial", config.PttType);
            _pttDevice = Add(parent, 1, ref left, "PTT device", config.PttDevice ?? "");
            _pttGpio = Add(parent, 1, ref left, "PTT GPIO", Text(config.PttGpio));
            _pttSerialLine = Add(parent, 1, ref left, "PTT line rts/dtr", config.PttSerialLine);
            _lowerSideband = Check(parent, 1, ref left, "Lower sideband", config.LowerSideband);

            int right = 0;
            _identEnabled = Check(parent, SecondColumn, ref right, "Ident enabled", config.IdentEnabled);
            _identCallsign = Add(parent, SecondColumn, ref right, "Ident callsign", config.IdentCallsign ?? "");
            _identInterval = Add(parent, SecondColumn, ref right, "Ident every min", Text(config.IdentIntervalMinutes));
            _identWpm = Add(parent, SecondColumn, ref right, "Ident wpm", Text(config.IdentWpm));
            _ackTimeout = Add(parent, SecondColumn, ref right, "Ack margin ms", Text(config.AckTimeoutMs));
            _maxRetries = Add(parent, SecondColumn, ref right, "Max retries", Text(config.MaxRetries));
            _stepWaveform = Check(parent, SecondColumn, ref right, "Step waveform", config.StepWaveform);
            _fountainC = Add(parent, SecondColumn, ref right, "Fountain c", Text(config.FountainC));
            _fountainDelta = Add(parent, SecondColumn, ref right, "Fountain delta", Text(config.FountainDelta));
            _frameLog = Add(parent, SecondColumn, ref right, "Frame log path", config.FrameLogPath ?? "");
            _downloadDirectory = Add(
                parent, SecondColumn, ref right, "Downloads to", config.DownloadDirectory ?? "");
            _perfCsv = Add(parent, SecondColumn, ref right, "Perf CSV path", config.PerfCsvPath ?? "");
            _flexDax = Add(parent, SecondColumn, ref right, "Flex DAX channel", config.FlexDaxChannel);
            _flexAntenna = Add(parent, SecondColumn, ref right, "Flex antenna", config.FlexAntenna);
            _uberSdrMode = Add(parent, SecondColumn, ref right, "UberSDR mode", config.UberSdrMode);
            _uberSdrPassword = Add(parent, SecondColumn, ref right, "UberSDR password", config.UberSdrPassword ?? "");
            _useMixerPower = Check(parent, SecondColumn, ref right, "Mixer as power", config.UseMixerPower);

            parent.Add(new Label
            {
                X = 1,
                Y = Pos.AnchorEnd(2),
                Width = Dim.Fill(2),
                Height = 1,
                Text = "Blank means the default. Changing the device, mode or centre restarts the station.",
            });
        }

        /// <summary>
        /// Reads the fields back into a config, or explains what will not parse and what will
        /// not start a station.
        /// </summary>
        public bool TryRead(QsoConfig current, out QsoConfig? config, out string? problem)
        {
            config = null;
            problem = null;
            var badNumbers = new List<string>();

            QsoConfig candidate = current with
            {
                Device = _device.Text.Trim(),
                Callsign = _callsign.Text.Trim().ToUpperInvariant(),
                Mode = _mode.Text.Trim(),
                AudioCentreHz = OptionalDouble(_audioCentre, "Audio centre", badNumbers),
                RfFrequencyHz = OptionalDouble(_rfFrequency, "RF frequency", badNumbers),
                TxDelayMs = Integer(_txDelay, "TX delay", current.TxDelayMs, badNumbers),
                InputGain = Double(_inputGain, "Audio in gain", current.InputGain, badNumbers),
                OutputGain = Double(_outputGain, "Audio out gain", current.OutputGain, badNumbers),
                CaptureRateHz = Integer(_captureRate, "Capture rate", current.CaptureRateHz, badNumbers),
                Power = OptionalDouble(_power, "Power", badNumbers),
                PttType = _pttType.Text.Trim().ToLowerInvariant(),
                PttDevice = Empty(_pttDevice),
                PttGpio = Integer(_pttGpio, "PTT GPIO", current.PttGpio, badNumbers),
                PttSerialLine = _pttSerialLine.Text.Trim().ToLowerInvariant(),
                LowerSideband = _lowerSideband.Value == CheckState.Checked,
                IdentEnabled = _identEnabled.Value == CheckState.Checked,
                IdentCallsign = Empty(_identCallsign)?.ToUpperInvariant(),
                IdentIntervalMinutes = Integer(
                    _identInterval, "Ident interval", current.IdentIntervalMinutes, badNumbers),
                IdentWpm = Double(_identWpm, "Ident wpm", current.IdentWpm, badNumbers),
                AckTimeoutMs = Integer(_ackTimeout, "Ack timeout", current.AckTimeoutMs, badNumbers),
                MaxRetries = Integer(_maxRetries, "Max retries", current.MaxRetries, badNumbers),
                StepWaveform = _stepWaveform.Value == CheckState.Checked,
                DownloadDirectory = Empty(_downloadDirectory),
                PerfCsvPath = Empty(_perfCsv),
                FountainC = Double(_fountainC, "Fountain c", current.FountainC, badNumbers),
                FountainDelta = Double(_fountainDelta, "Fountain delta", current.FountainDelta, badNumbers),
                FrameLogPath = _frameLog.Text.Trim().Length == 0 ? null : _frameLog.Text.Trim(),
                FlexDaxChannel = _flexDax.Text.Trim(),
                FlexAntenna = _flexAntenna.Text.Trim(),
                UberSdrMode = _uberSdrMode.Text.Trim(),
                UberSdrPassword = Empty(_uberSdrPassword),
                UseMixerPower = _useMixerPower.Value == CheckState.Checked,
            };

            if (badNumbers.Count > 0)
            {
                problem = "These are not numbers: " + string.Join(", ", badNumbers) + ".";
                return false;
            }

            IReadOnlyList<string> problems = candidate.Validate();
            if (problems.Count > 0)
            {
                problem = string.Join("\n", problems);
                return false;
            }

            config = candidate;
            return true;
        }

        private static TextField Add(View parent, int x, ref int row, string label, string value)
        {
            parent.Add(new Label
            {
                X = x,
                Y = row,
                Width = LabelWidth,
                Height = 1,
                Text = label,
            });

            var field = new TextField
            {
                X = x + LabelWidth + 1,
                Y = row,
                Width = FieldWidth,
                Height = 1,
                Text = value,
            };
            parent.Add(field);
            row++;
            return field;
        }

        private static CheckBox Check(View parent, int x, ref int row, string label, bool value)
        {
            var box = new CheckBox
            {
                X = x,
                Y = row,
                Text = label,
                Value = value ? CheckState.Checked : CheckState.UnChecked,
            };
            parent.Add(box);
            row++;
            return box;
        }

        private static string? Empty(TextField field) =>
            field.Text.Trim().Length == 0 ? null : field.Text.Trim();

        private static string Text(double? value) =>
            value is double number ? number.ToString("0.######", CultureInfo.InvariantCulture) : "";

        private static string Text(double value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);

        private static string Text(int value) =>
            value.ToString(CultureInfo.InvariantCulture);

        private static double? OptionalDouble(TextField field, string name, List<string> bad)
        {
            string text = field.Text.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }

            bad.Add(name);
            return null;
        }

        private static double Double(TextField field, string name, double fallback, List<string> bad)
        {
            string text = field.Text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }

            bad.Add(name);
            return fallback;
        }

        private static int Integer(TextField field, string name, int fallback, List<string> bad)
        {
            string text = field.Text.Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }

            bad.Add(name);
            return fallback;
        }
    }
}
