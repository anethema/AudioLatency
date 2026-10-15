namespace AudioLatency.Core;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => Name + (IsDefault ? " (default)" : "");
}

public sealed record MeasurementOptions(string OutputDeviceId, string InputDeviceId,
    int TrialCount = 10, double Volume = 0.20, double DistanceCm = 0)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OutputDeviceId) || string.IsNullOrWhiteSpace(InputDeviceId))
            throw new ArgumentException("Select an output device and microphone.");
        if (TrialCount is < 3 or > 30) throw new ArgumentOutOfRangeException(nameof(TrialCount));
        if (!double.IsFinite(Volume) || Volume is < 0.01 or > 0.8)
            throw new ArgumentOutOfRangeException(nameof(Volume));
        if (!double.IsFinite(DistanceCm) || DistanceCm is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(DistanceCm));
    }
}

/// <summary>Times refer to a submitted chirp and a microphone sample, not a UI event.</summary>
public sealed record TrialResult(int Number, double? LatencyMs, double QueueMs,
    double Correlation, double Peak, string Status)
{
    public bool Accepted => LatencyMs.HasValue;
    public string LatencyText => LatencyMs is double value ? $"{value:F1} ms" : "—";
    // Keep the measured value intact for saved references and exports. Negative adjusted values
    // remain visible as a sign of queue/timestamp uncertainty instead of being clamped to zero.
    public double? QueueAdjustedLatencyMs => LatencyMs - QueueMs;
    public string QueueAdjustedLatencyText => QueueAdjustedLatencyMs is double value ? $"{value:F1} ms" : "—";
    public string QualityText => $"{Correlation:P0}";
}

public sealed record MeasurementProgress(string Message, int Completed, int Total,
    double MicPeak, TrialResult? Trial = null);

public sealed record MeasurementSession(DateTimeOffset CreatedAt, AudioDeviceInfo Output,
    AudioDeviceInfo Input, MeasurementOptions Options, int OutputSampleRate, int InputSampleRate,
    double OutputBufferMs, double InputStreamLatencyMs, IReadOnlyList<TrialResult> Trials)
{
    public double? MedianMs => Statistics.Median(Trials.Where(t => t.Accepted).Select(t => t.LatencyMs!.Value));
    public double? JitterMs => Statistics.StandardDeviation(Trials.Where(t => t.Accepted).Select(t => t.LatencyMs!.Value));
    public double? MedianQueueMs => Statistics.Median(Trials.Where(t => t.Accepted).Select(t => t.QueueMs));
    // Adjust each accepted pair before aggregation: median(raw) - median(queue) is generally
    // different from median(raw - queue), and queue variability should not inflate adjusted jitter.
    public double? MedianQueueAdjustedMs => Statistics.Median(Trials.Where(t => t.Accepted).Select(t => t.QueueAdjustedLatencyMs!.Value));
    public double? QueueAdjustedJitterMs => Statistics.StandardDeviation(Trials.Where(t => t.Accepted).Select(t => t.QueueAdjustedLatencyMs!.Value));
    public bool HasNegativeQueueAdjustedValues => Trials.Any(t => t.QueueAdjustedLatencyMs < 0);
    public int AcceptedCount => Trials.Count(t => t.Accepted);
}

/// <summary>
/// A wired reference cancels shared delays only approximately. Device and microphone geometry
/// must remain comparable; the difference is never presented as an isolated Bluetooth codec delay.
/// </summary>
public static class ReferenceComparison
{
    public static string? Incompatibility(MeasurementSession current, MeasurementSession reference)
    {
        if (current.Input.Id != reference.Input.Id) return "Use the same microphone as the reference.";
        if (current.InputSampleRate != reference.InputSampleRate) return "The microphone sample rate changed.";
        if (current.Output.Id == reference.Output.Id) return "Choose a different output from the wired reference.";
        if (current.AcceptedCount < 3 || reference.AcceptedCount < 3) return "At least three valid trials are needed in each run.";
        return null;
    }

    public static double? ExtraDelayMs(MeasurementSession current, MeasurementSession reference)
    {
        if (Incompatibility(current, reference) != null) return null;
        // Remove only the measured application queue from each path, rather than subtracting
        // the driver's advertised latency (which may already be represented in timestamps).
        return current.MedianQueueAdjustedMs - reference.MedianQueueAdjustedMs;
    }
}
