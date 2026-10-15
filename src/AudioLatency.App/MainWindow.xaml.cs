using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioLatency.App.Services;
using AudioLatency.Core;
using Microsoft.Win32;

namespace AudioLatency.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AudioMeasurementService _measurementService = new();
    private readonly ObservableCollection<TrialResult> _trialRows = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private MeasurementSession? _currentSession;
    private MeasurementSession? _referenceSession;
    private bool _closeAfterRun;
    private bool _allowClose;

    public ObservableCollection<TrialResult> TrialRows => _trialRows;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        ClearError();
        var oldOutputId = (OutputDeviceCombo.SelectedItem as AudioDeviceInfo)?.Id;
        var oldInputId = (InputDeviceCombo.SelectedItem as AudioDeviceInfo)?.Id;

        try
        {
            var devices = AudioMeasurementService.GetDevices();
            var outputs = devices.Outputs.ToList();
            var inputs = devices.Inputs.ToList();
            OutputDeviceCombo.ItemsSource = outputs;
            InputDeviceCombo.ItemsSource = inputs;
            OutputDeviceCombo.SelectedItem = outputs.FirstOrDefault(d => d.Id == oldOutputId)
                ?? outputs.FirstOrDefault(d => d.IsDefault)
                ?? outputs.FirstOrDefault();
            InputDeviceCombo.SelectedItem = inputs.FirstOrDefault(d => d.Id == oldInputId)
                ?? inputs.FirstOrDefault(d => d.IsDefault)
                ?? inputs.FirstOrDefault();

            if (outputs.Count == 0 || inputs.Count == 0)
            {
                var missing = outputs.Count == 0 && inputs.Count == 0
                    ? "No output devices or microphone inputs were found. Connect an audio device, then refresh."
                    : outputs.Count == 0
                        ? "No output devices were found. Connect a speaker or headset, then refresh."
                        : "No microphone inputs were found. Connect a built-in or USB microphone, then refresh.";
                ShowError(missing);
            }
        }
        catch (Exception ex)
        {
            ShowError($"Windows audio devices could not be listed. Check that Windows audio is available, then refresh. Details: {ex.GetBaseException().Message}");
        }
    }

    private void TrialCountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TrialCountText is not null)
            TrialCountText.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText is not null)
            VolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    private void DistanceTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var decimalChar = decimalSeparator.Length == 1 ? decimalSeparator[0] : '.';
        e.Handled = e.Text.Any(character => !char.IsDigit(character) && character != decimalChar && character != '.' && character != ',');
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runTask is not null)
            return;

        ClearError();
        if (OutputDeviceCombo.SelectedItem is not AudioDeviceInfo output ||
            InputDeviceCombo.SelectedItem is not AudioDeviceInfo input)
        {
            ShowError("Choose both an output device and a microphone before starting the test.");
            return;
        }

        if (!double.TryParse(DistanceTextBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var distanceCm) &&
            !double.TryParse(DistanceTextBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out distanceCm))
        {
            ShowError("Enter a distance from 0 to 1000 cm. Use a number such as 0, 25, or 25.5.");
            return;
        }

        var options = new MeasurementOptions(
            output.Id,
            input.Id,
            (int)Math.Round(TrialCountSlider.Value),
            VolumeSlider.Value / 100.0,
            distanceCm);

        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            ShowError($"Check the measurement settings. {ex.Message}");
            return;
        }

        _currentSession = null;
        _trialRows.Clear();
        UpdateResults();
        _runCancellation = new CancellationTokenSource();
        _runTask = RunMeasurementAsync(options, _runCancellation);
        await _runTask;
        _runTask = null;
    }

    private async Task RunMeasurementAsync(MeasurementOptions options, CancellationTokenSource cancellation)
    {
        SetBusy(true, options.TrialCount);
        // Progress captures this window's dispatcher so table and meter updates stay on the UI thread.
        var progress = new Progress<MeasurementProgress>(UpdateProgress);

        try
        {
            var session = await _measurementService.RunAsync(options, progress, cancellation.Token);
            ValidateSession(session);
            _currentSession = session;
            ReplaceTrials(session.Trials);
            UpdateResults();
            ActivityMessage.Text = session.AcceptedCount >= 3
                ? "Measurement complete. Review the accepted trials below."
                : "Measurement finished with fewer than three valid trials. Check mic placement and input selection.";
        }
        catch (OperationCanceledException)
        {
            ActivityMessage.Text = "Test canceled. Partial trials remain listed for review.";
        }
        catch (Exception ex)
        {
            ActivityMessage.Text = "The test could not be completed.";
            ShowError($"The measurement could not be completed. Check that the selected microphone is connected and available to Windows, then try again. Details: {ex.GetBaseException().Message}");
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_runCancellation, cancellation))
                _runCancellation = null;
            SetBusy(false, options.TrialCount);
        }
    }

    private void UpdateProgress(MeasurementProgress progress)
    {
        ActivityMessage.Text = progress.Message;
        var total = Math.Max(progress.Total, 1);
        var completed = Math.Clamp(progress.Completed, 0, total);
        ProgressCountText.Text = $"{completed} / {progress.Total}";
        TrialProgressBar.Value = 100.0 * completed / total;
        var peak = double.IsFinite(progress.MicPeak) ? Math.Clamp(progress.MicPeak, 0, 1) : 0;
        MicMeter.Value = peak;
        MicMeterText.Text = peak.ToString("P0", CultureInfo.CurrentCulture);

        if (progress.Trial is not null)
            AddOrReplaceTrial(progress.Trial);
    }

    private void AddOrReplaceTrial(TrialResult trial)
    {
        var existingIndex = -1;
        for (var index = 0; index < _trialRows.Count; index++)
        {
            if (_trialRows[index].Number == trial.Number)
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
            _trialRows[existingIndex] = trial;
        else
            _trialRows.Add(trial);

        DrawTrialChart();
    }

    private void ReplaceTrials(IEnumerable<TrialResult> trials)
    {
        _trialRows.Clear();
        foreach (var trial in trials.OrderBy(t => t.Number))
            _trialRows.Add(trial);
        DrawTrialChart();
    }

    private void SetBusy(bool isBusy, int trialCount)
    {
        ActivityPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = isBusy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = isBusy;
        AppStatusText.Text = isBusy ? "TESTING" : "READY";
        OutputDeviceCombo.IsEnabled = !isBusy;
        InputDeviceCombo.IsEnabled = !isBusy;
        RefreshDevicesButton.IsEnabled = !isBusy;
        TrialCountSlider.IsEnabled = !isBusy;
        VolumeSlider.IsEnabled = !isBusy;
        DistanceTextBox.IsEnabled = !isBusy;
        ExportJsonButton.IsEnabled = !isBusy && _currentSession is not null;
        ExportCsvButton.IsEnabled = !isBusy && _currentSession is not null;
        SetReferenceButton.IsEnabled = !isBusy && _currentSession is not null;
        SaveReferenceButton.IsEnabled = !isBusy && _referenceSession is not null;
        LoadReferenceButton.IsEnabled = !isBusy;
        ClearReferenceButton.IsEnabled = !isBusy && _referenceSession is not null;
        ProgressCountText.Text = $"0 / {trialCount}";
        TrialProgressBar.Value = 0;
        MicMeter.Value = 0;
        MicMeterText.Text = "—";
        if (isBusy)
            ClearError();
    }

    private void UpdateResults()
    {
        var current = _currentSession;
        MedianResultText.Text = FormatMs(current?.MedianQueueAdjustedMs);
        RawMedianResultText.Text = $"Raw app-to-mic median: {FormatMs(current?.MedianMs)}";
        QueueMedianResultText.Text = $"App queue median: {FormatMs(current?.MedianQueueMs)}";
        JitterResultText.Text = FormatMs(current?.QueueAdjustedJitterMs);
        NegativeQueueAdjustedWarning.Visibility = current?.HasNegativeQueueAdjustedValues == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        RunSummaryText.Text = current is null
            ? "Complete a test to see the queue-adjusted estimate and trial details."
            : $"{current.CreatedAt.ToLocalTime():g}  ·  {current.AcceptedCount}/{current.Trials.Count} accepted  ·  {current.Output.Name} → {current.Input.Name}";

        if (_referenceSession is null)
        {
            ExtraDelayText.Text = "—";
            ComparisonStatusText.Text = current is null
                ? "Set a wired reference to estimate the extra delay through another output path."
                : "Set a wired reference to estimate the extra delay through another output path.";
        }
        else if (current is null)
        {
            ExtraDelayText.Text = "—";
            ComparisonStatusText.Text = "A wired reference is loaded. Run a different output device using the same microphone.";
        }
        else if (ReferenceComparison.Incompatibility(current, _referenceSession) is string reason)
        {
            ExtraDelayText.Text = "—";
            ComparisonStatusText.Text = reason;
        }
        else
        {
            ExtraDelayText.Text = FormatSignedMs(ReferenceComparison.ExtraDelayMs(current, _referenceSession));
            ComparisonStatusText.Text = "Difference between queue-adjusted median accepted times for the current run and wired reference. This remains an estimate.";
        }

        if (_referenceSession is null)
        {
            ReferenceStateText.Text = "No wired reference loaded.";
        }
        else
        {
            var accepted = _referenceSession.AcceptedCount;
            ReferenceStateText.Text = $"Wired reference · {_referenceSession.Output.Name} → {_referenceSession.Input.Name} · {accepted}/{_referenceSession.Trials.Count} accepted · {_referenceSession.CreatedAt.ToLocalTime():g}";
        }

        ExportJsonButton.IsEnabled = _runCancellation is null && current is not null;
        ExportCsvButton.IsEnabled = _runCancellation is null && current is not null;
        SetReferenceButton.IsEnabled = _runCancellation is null && current is not null;
        SaveReferenceButton.IsEnabled = _runCancellation is null && _referenceSession is not null;
        ClearReferenceButton.IsEnabled = _runCancellation is null && _referenceSession is not null;
        DrawTrialChart();
    }

    private static string FormatMs(double? value) => value is double ms && double.IsFinite(ms)
        ? $"{ms:F1} ms"
        : "—";

    private static string FormatSignedMs(double? value) => value is double ms && double.IsFinite(ms)
        ? $"{ms:+0.0;-0.0;0.0} ms"
        : "—";

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runCancellation?.Cancel();
            ActivityMessage.Text = "Canceling and releasing audio devices…";
            CancelButton.IsEnabled = false;
        }
        catch (ObjectDisposedException)
        {
            // The run completed while the cancel button was being clicked.
        }
    }

    private void SetReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSession is null)
            return;

        _referenceSession = _currentSession;
        UpdateResults();
    }

    private void ClearReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        _referenceSession = null;
        UpdateResults();
    }

    private void SaveReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_referenceSession is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save wired reference",
            Filter = "Audio latency reference (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "audio-latency-wired-reference.json"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_referenceSession, JsonOptions), Encoding.UTF8);
            ShowInfo($"Wired reference saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowError($"The reference file could not be saved. Check that the destination is writable. Details: {ex.GetBaseException().Message}");
        }
    }

    private void LoadReferenceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load wired reference",
            Filter = "Audio latency reference (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            var session = JsonSerializer.Deserialize<MeasurementSession>(json, JsonOptions)
                ?? throw new InvalidDataException("The selected file does not contain a measurement session.");
            ValidateSession(session);
            _referenceSession = session;
            UpdateResults();
        }
        catch (Exception ex)
        {
            ShowError($"The reference file is invalid or could not be read. Choose a JSON file exported by Audio Latency Lab. Details: {ex.GetBaseException().Message}");
        }
    }

    private void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSession is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export measurement data as JSON",
            Filter = "JSON data (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "audio-latency-run.json"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_currentSession, JsonOptions), Encoding.UTF8);
            ShowInfo($"Measurement data exported to {dialog.FileName}. No audio recording is included.");
        }
        catch (Exception ex)
        {
            ShowError($"The JSON export could not be saved. Check that the destination is writable. Details: {ex.GetBaseException().Message}");
        }
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSession is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export measurement data as CSV",
            Filter = "CSV data (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = "audio-latency-run.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildCsv(_currentSession), new UTF8Encoding(true));
            ShowInfo($"Measurement data exported to {dialog.FileName}. No audio recording is included.");
        }
        catch (Exception ex)
        {
            ShowError($"The CSV export could not be saved. Check that the destination is writable. Details: {ex.GetBaseException().Message}");
        }
    }

    private static string BuildCsv(MeasurementSession session)
    {
        var builder = new StringBuilder();
        builder.AppendLine("created_at,output_name,output_id,input_name,input_id,trial_count,volume,distance_cm,output_sample_rate,input_sample_rate,output_buffer_ms,input_stream_latency_ms,trial,latency_ms,app_queue_ms,correlation,peak,status,queue_adjusted_latency_ms");
        foreach (var trial in session.Trials.OrderBy(t => t.Number))
        {
            var values = new string?[]
            {
                session.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                session.Output.Name,
                session.Output.Id,
                session.Input.Name,
                session.Input.Id,
                session.Options.TrialCount.ToString(CultureInfo.InvariantCulture),
                session.Options.Volume.ToString("R", CultureInfo.InvariantCulture),
                session.Options.DistanceCm.ToString("R", CultureInfo.InvariantCulture),
                session.OutputSampleRate.ToString(CultureInfo.InvariantCulture),
                session.InputSampleRate.ToString(CultureInfo.InvariantCulture),
                session.OutputBufferMs.ToString("R", CultureInfo.InvariantCulture),
                session.InputStreamLatencyMs.ToString("R", CultureInfo.InvariantCulture),
                trial.Number.ToString(CultureInfo.InvariantCulture),
                trial.LatencyMs?.ToString("R", CultureInfo.InvariantCulture),
                trial.QueueMs.ToString("R", CultureInfo.InvariantCulture),
                trial.Correlation.ToString("R", CultureInfo.InvariantCulture),
                trial.Peak.ToString("R", CultureInfo.InvariantCulture),
                trial.Status,
                trial.QueueAdjustedLatencyMs?.ToString("R", CultureInfo.InvariantCulture)
            };
            builder.AppendLine(string.Join(',', values.Select(CsvField)));
        }
        return builder.ToString();
    }

    private static string CsvField(string? value)
    {
        value ??= string.Empty;
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static void ValidateSession(MeasurementSession session)
    {
        if (session.Output is null || session.Input is null || session.Options is null || session.Trials is null)
            throw new InvalidDataException("The measurement session is missing required device, settings, or trial data.");
        session.Options.Validate();
        if (session.Trials.Count != session.Options.TrialCount)
            throw new InvalidDataException("The measurement session trial count does not match its saved settings.");
        if (session.OutputSampleRate <= 0 || session.InputSampleRate <= 0)
            throw new InvalidDataException("The measurement session has an invalid sample rate.");
        if (string.IsNullOrWhiteSpace(session.Output.Id) || string.IsNullOrWhiteSpace(session.Input.Id) ||
            string.IsNullOrWhiteSpace(session.Output.Name) || string.IsNullOrWhiteSpace(session.Input.Name))
            throw new InvalidDataException("The measurement session is missing an output or microphone name or identifier.");
        if (!double.IsFinite(session.OutputBufferMs) || session.OutputBufferMs < 0 ||
            !double.IsFinite(session.InputStreamLatencyMs) || session.InputStreamLatencyMs < 0)
            throw new InvalidDataException("The measurement session contains an invalid device latency value.");
        if (session.Trials.Any(trial => trial is null ||
            (trial.LatencyMs is double latency && (!double.IsFinite(latency) || latency < 0)) ||
            !double.IsFinite(trial.QueueMs) || trial.QueueMs < 0 ||
            !double.IsFinite(trial.Correlation) || trial.Correlation is < 0 or > 1 ||
            !double.IsFinite(trial.Peak) || trial.Peak < 0 ||
            string.IsNullOrWhiteSpace(trial.Status)))
            throw new InvalidDataException("The measurement session contains an invalid trial result.");
        if (session.Output.Id != session.Options.OutputDeviceId || session.Input.Id != session.Options.InputDeviceId)
            throw new InvalidDataException("The session device identifiers do not match its saved settings.");
        if (session.Trials.Any(trial => trial.Number < 1 || trial.Number > session.Options.TrialCount) ||
            session.Trials.Select(trial => trial.Number).Distinct().Count() != session.Trials.Count)
            throw new InvalidDataException("The measurement session contains duplicate or out-of-range trial numbers.");
    }

    private void TrialChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTrialChart();

    private void DrawTrialChart()
    {
        if (TrialChartCanvas is null)
            return;

        var canvas = TrialChartCanvas;
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 80 || height < 60)
            return;

        var left = 42.0;
        var right = 12.0;
        var top = 20.0;
        var bottom = 25.0;
        var plotWidth = Math.Max(width - left - right, 10);
        var plotHeight = Math.Max(height - top - bottom, 10);
        var accepted = _trialRows.Where(t => t.QueueAdjustedLatencyMs.HasValue).OrderBy(t => t.Number).ToList();

        if (accepted.Count == 0)
        {
            AddChartText(canvas, "Accepted queue-adjusted trial values will appear here", width / 2 - 145, height / 2 - 8, "#8793A2", 11);
            AddChartText(canvas, "ms", 7, 0, "#7F8B99", 10);
            return;
        }

        var minimum = accepted.Min(t => t.QueueAdjustedLatencyMs!.Value);
        var maximum = accepted.Max(t => t.QueueAdjustedLatencyMs!.Value);
        if (Math.Abs(maximum - minimum) < 0.01)
        {
            minimum -= 1;
            maximum += 1;
        }
        else
        {
            var padding = (maximum - minimum) * 0.15;
            minimum -= padding;
            maximum += padding;
        }

        var maxTrial = Math.Max(_trialRows.Count == 0 ? 1 : _trialRows.Max(t => t.Number), 2);
        AddChartText(canvas, "ms", 7, 0, "#7F8B99", 10);
        for (var lineIndex = 0; lineIndex <= 2; lineIndex++)
        {
            var ratio = lineIndex / 2.0;
            var y = top + plotHeight * ratio;
            AddChartLine(canvas, left, y, left + plotWidth, y, "#29313B", 1);
            var value = maximum - (maximum - minimum) * ratio;
            AddChartText(canvas, value.ToString("F1", CultureInfo.CurrentCulture), 4, y - 7, "#8190A0", 9);
        }

        var points = accepted.Select(trial => new Point(
            left + (trial.Number - 1.0) / (maxTrial - 1.0) * plotWidth,
            top + (maximum - trial.QueueAdjustedLatencyMs!.Value) / (maximum - minimum) * plotHeight)).ToList();
        for (var index = 1; index < points.Count; index++)
            AddChartLine(canvas, points[index - 1].X, points[index - 1].Y, points[index].X, points[index].Y, "#65D6C0", 2);

        for (var index = 0; index < accepted.Count; index++)
        {
            var point = points[index];
            var marker = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(101, 214, 192)),
                Stroke = new SolidColorBrush(Color.FromRgb(15, 33, 31)),
                StrokeThickness = 2,
                ToolTip = $"Trial {accepted[index].Number} · Queue-adjusted: {accepted[index].QueueAdjustedLatencyText} · Raw app-to-mic: {accepted[index].LatencyText}"
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            canvas.Children.Add(marker);
        }

        AddChartText(canvas, "1", left - 2, height - 18, "#8190A0", 9);
        AddChartText(canvas, maxTrial.ToString(CultureInfo.InvariantCulture), left + plotWidth - 7, height - 18, "#8190A0", 9);
        AddChartText(canvas, "trial", left + plotWidth / 2 - 10, height - 18, "#8190A0", 9);
    }

    private static void AddChartLine(Canvas canvas, double x1, double y1, double x2, double y2, string color, double thickness)
    {
        canvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            StrokeThickness = thickness
        });
    }

    private static void AddChartText(Canvas canvas, string text, double left, double top, string color, double fontSize)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            FontSize = fontSize,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, Math.Max(0, left));
        Canvas.SetTop(label, Math.Max(0, top));
        canvas.Children.Add(label);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Background = new SolidColorBrush(Color.FromRgb(58, 35, 38));
        ErrorBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(116, 64, 71));
        ErrorText.Foreground = new SolidColorBrush(Color.FromRgb(255, 208, 210));
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ShowInfo(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Background = new SolidColorBrush(Color.FromRgb(23, 48, 43));
        ErrorBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(45, 105, 91));
        ErrorText.Foreground = new SolidColorBrush(Color.FromRgb(198, 246, 230));
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        ErrorBanner.Background = new SolidColorBrush(Color.FromRgb(58, 35, 38));
        ErrorBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(116, 64, 71));
        ErrorText.Foreground = new SolidColorBrush(Color.FromRgb(255, 208, 210));
        ErrorText.Text = string.Empty;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || _runTask is null)
            return;

        e.Cancel = true;
        if (_closeAfterRun)
            return;

        _closeAfterRun = true;
        try
        {
            // Closing a window must not interrupt the service's audio-device cleanup.
            _runCancellation?.Cancel();
            ActivityMessage.Text = "Closing after audio cleanup…";
            CancelButton.IsEnabled = false;
        }
        catch (ObjectDisposedException)
        {
            // The run is already unwinding; wait for its task before closing.
        }
        _ = CloseAfterRunAsync(_runTask);
    }

    private async Task CloseAfterRunAsync(Task runTask)
    {
        try
        {
            await runTask;
        }
        catch
        {
            // RunMeasurementAsync reports failures in the window and always releases its task state.
        }
        _allowClose = true;
        Close();
    }
}
