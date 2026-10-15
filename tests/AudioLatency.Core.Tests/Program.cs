using AudioLatency.Core;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("matched sweep tolerates seeded noise, polarity inversion, and gain change", DetectDelayedInvertedSweep),
    ("physical chirp survives output-to-input sample-rate conversion", DetectResampledChirp),
    ("silence and seeded noise are rejected", RejectSilenceAndNoise),
    ("clipped microphone capture is rejected", RejectClippedCapture),
    ("two competing equal chirps are rejected as ambiguous", RejectCompetingChirps),
    ("search bounds are inclusive and incomplete windows are rejected", SearchBounds),
    ("median and population standard deviation handle odd, even, and empty input", StatisticsCases),
    ("reference comparison removes per-trial queue delay", ReferenceRemovesPerTrialQueue),
    ("reference comparison rejects incompatible runs", ReferenceRejectsIncompatibleRuns),
    ("chirp generation validates format and returns the expected duration", ChirpGeneration),
    ("queue-adjusted statistics pair trials and exclude rejected results", QueueAdjustedStatistics),
    ("negative queue-adjusted estimates remain visible", NegativeQueueAdjustedEstimate),
    ("legacy references retain raw values when loaded and exported", LegacyReferenceCompatibility),
};

int failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} regression checks passed.");
return failures == 0 ? 0 : 1;

static void DetectDelayedInvertedSweep()
{
    const int sampleRate = 48_000;
    const int expectedOffset = 4_321;
    float[] chirp = ChirpSignal.Create(sampleRate, 1_300, 9_200);
    float[] capture = Noise(12_000, seed: 17, sigma: 0.009);
    Add(capture, chirp, expectedOffset, gain: -0.34f);

    DetectionResult result = SignalDetector.Find(capture, chirp, 0, capture.Length - chirp.Length);
    Check.True(result.Found, $"Expected a detection; got '{result.Reason}' ({result.Correlation:P1}).");
    Check.Equal(expectedOffset, result.SampleOffset, "The sweep offset should be recovered exactly.");
    Check.True(result.Correlation > 0.90, $"Correlation was only {result.Correlation:F3}.");
    Check.True(result.Peak is > 0.05 and < 0.5, $"Unexpected capture peak {result.Peak:F3}.");
}

static void DetectResampledChirp()
{
    // The speaker renders at its own rate, while the mic samples the resulting continuous
    // waveform at its rate. Linear interpolation models that conversion without resampling
    // the detector template, which is generated directly at the microphone rate.
    foreach ((int outputRate, int inputRate, int expectedOffset) in new[]
    {
        (24_000, 44_100, 2_047),
        (48_000, 44_100, 3_333),
        (24_000, 48_000, 1_777),
    })
    {
        float[] playback = ChirpSignal.Create(outputRate, 900, 10_000, 0.8);
        float[] micTemplate = ChirpSignal.Create(inputRate, 900, 10_000);
        float[] resampled = ResampleLinear(playback, outputRate, inputRate, micTemplate.Length);
        float[] capture = Noise(expectedOffset + micTemplate.Length + 2_500, seed: outputRate + inputRate, sigma: 0.006);
        Add(capture, resampled, expectedOffset, gain: 0.41f);

        DetectionResult result = SignalDetector.Find(capture, micTemplate, 0, capture.Length - micTemplate.Length);
        Check.True(result.Found,
            $"{outputRate}->{inputRate} Hz: expected detection; got '{result.Reason}' ({result.Correlation:P1}).");
        Check.True(Math.Abs(result.SampleOffset - expectedOffset) <= 1,
            $"{outputRate}->{inputRate} Hz: expected offset {expectedOffset}, got {result.SampleOffset}.");
        Check.True(result.Correlation > 0.90,
            $"{outputRate}->{inputRate} Hz: correlation was only {result.Correlation:F3}.");
    }
}

static void RejectSilenceAndNoise()
{
    float[] template = ChirpSignal.Create(44_100, 1_000, 8_500);
    DetectionResult silence = SignalDetector.Find(new float[10_000], template, 0, 8_000);
    Check.False(silence.Found, "Silence must not be accepted as a chirp.");

    float[] noise = Noise(10_000, seed: 93, sigma: 0.06);
    DetectionResult noisy = SignalDetector.Find(noise, template, 0, 8_000);
    Check.False(noisy.Found, $"Noise must not be accepted; got correlation {noisy.Correlation:F3}.");
}

static void RejectClippedCapture()
{
    float[] chirp = ChirpSignal.Create(48_000, 1_100, 9_000, 0.8);
    var capture = new float[9_000];
    for (int i = 0; i < chirp.Length; i++)
        capture[1_200 + i] = Math.Clamp(chirp[i] * 2.0f, -1.0f, 1.0f);

    DetectionResult result = SignalDetector.Find(capture, chirp, 0, capture.Length - chirp.Length);
    Check.False(result.Found, "A clipped waveform must not be accepted.");
    Check.True(result.Reason.Contains("clipped", StringComparison.OrdinalIgnoreCase),
        $"Expected a clipping-specific rejection; got '{result.Reason}'.");
}

static void RejectCompetingChirps()
{
    float[] chirp = ChirpSignal.Create(44_100, 1_000, 8_800, 0.6);
    var capture = new float[12_000];
    Add(capture, chirp, 700, gain: 1);
    Add(capture, chirp, 6_100, gain: 1);

    DetectionResult result = SignalDetector.Find(capture, chirp, 0, capture.Length - chirp.Length);
    Check.False(result.Found, "Two equally strong distant chirps must be rejected as ambiguous.");
    Check.True(result.Reason.Contains("ambiguous", StringComparison.OrdinalIgnoreCase),
        $"Expected an ambiguity-specific rejection; got '{result.Reason}'.");
}

static void SearchBounds()
{
    float[] chirp = ChirpSignal.Create(44_100, 1_000, 8_500, 0.5);

    var atMinimum = new float[8_000];
    Add(atMinimum, chirp, 135, 1);
    DetectionResult minResult = SignalDetector.Find(atMinimum, chirp, 135, 135);
    Check.True(minResult.Found, $"Inclusive minimum lag was missed: {minResult.Reason}.");
    Check.Equal(135, minResult.SampleOffset, "The requested minimum lag should be searched.");

    var atMaximum = new float[8_000];
    int lastCompleteOffset = atMaximum.Length - chirp.Length;
    Add(atMaximum, chirp, lastCompleteOffset, 1);
    DetectionResult maxResult = SignalDetector.Find(atMaximum, chirp, lastCompleteOffset, int.MaxValue);
    Check.True(maxResult.Found, $"Inclusive final complete lag was missed: {maxResult.Reason}.");
    Check.Equal(lastCompleteOffset, maxResult.SampleOffset, "The last complete template window should be searched.");

    DetectionResult clamped = SignalDetector.Find(atMinimum, chirp, -20, 135);
    Check.True(clamped.Found, $"A negative minimum should clamp to zero: {clamped.Reason}.");
    Check.Equal(135, clamped.SampleOffset, "Clamping the minimum must preserve the in-range chirp.");

    DetectionResult incomplete = SignalDetector.Find(atMinimum, chirp, 7_000, 7_100);
    Check.False(incomplete.Found, "A range containing no complete template window must fail.");
}

static void StatisticsCases()
{
    Check.Equal(2.5, Statistics.Median(new[] { 4.0, 1.0, 3.0, 2.0 })!.Value, 1e-12,
        "Even-sized median should average the middle values.");
    Check.Equal(7.0, Statistics.Median(new[] { 7.0, 2.0, 9.0 })!.Value, 1e-12,
        "Odd-sized median should return the middle value after sorting.");
    Check.True(Statistics.Median(Array.Empty<double>()) is null, "Empty median should be null.");
    Check.Equal(Math.Sqrt(2.0 / 3.0), Statistics.StandardDeviation(new[] { 1.0, 2.0, 3.0 })!.Value,
        1e-12, "Standard deviation should use the population denominator.");
    Check.True(Statistics.StandardDeviation(Array.Empty<double>()) is null,
        "Empty standard deviation should be null.");
}

static void ReferenceRemovesPerTrialQueue()
{
    MeasurementSession current = Session("speaker-b", "mic", 48_000,
        new TrialResult(1, 40, 5, 0.9, 0.2, "Valid"),
        new TrialResult(2, 50, 20, 0.9, 0.2, "Valid"),
        new TrialResult(3, 100, 30, 0.9, 0.2, "Valid"),
        new TrialResult(4, null, 100, 0.1, 0.1, "Rejected"));
    MeasurementSession reference = Session("wired", "mic", 48_000,
        new TrialResult(1, 20, 15, 0.9, 0.2, "Valid"),
        new TrialResult(2, 30, 2, 0.9, 0.2, "Valid"),
        new TrialResult(3, 40, 1, 0.9, 0.2, "Valid"));

    double? extra = ReferenceComparison.ExtraDelayMs(current, reference);
    Check.True(extra.HasValue, "Compatible runs with three valid trials should produce a comparison.");
    Check.Equal(7.0, extra!.Value, 1e-12,
        "Comparison should take the median of per-trial (latency - queue) values.");
}

static void ReferenceRejectsIncompatibleRuns()
{
    MeasurementSession baseline = Session("wired", "mic", 48_000,
        ValidTrials(3, 20, 4));
    Check.True(ReferenceComparison.Incompatibility(Session("speaker", "other-mic", 48_000,
        ValidTrials(3, 40, 5)), baseline)?.Contains("same microphone", StringComparison.OrdinalIgnoreCase) == true,
        "A different microphone should be rejected.");
    Check.True(ReferenceComparison.Incompatibility(Session("wired", "mic", 48_000,
        ValidTrials(3, 40, 5)), baseline)?.Contains("different output", StringComparison.OrdinalIgnoreCase) == true,
        "Reusing the reference output should be rejected.");
    Check.True(ReferenceComparison.Incompatibility(Session("speaker", "mic", 44_100,
        ValidTrials(3, 40, 5)), baseline)?.Contains("sample rate", StringComparison.OrdinalIgnoreCase) == true,
        "A changed microphone sample rate should be rejected.");

    MeasurementSession tooFew = Session("speaker", "mic", 48_000,
        new TrialResult(1, 40, 5, 0.9, 0.2, "Valid"),
        new TrialResult(2, 40, 5, 0.9, 0.2, "Valid"),
        new TrialResult(3, null, 0, 0.1, 0.0, "Rejected"));
    Check.True(ReferenceComparison.Incompatibility(tooFew, baseline)?.Contains("three valid trials", StringComparison.OrdinalIgnoreCase) == true,
        "Fewer than three accepted trials should be rejected.");
    Check.True(ReferenceComparison.ExtraDelayMs(tooFew, baseline) is null,
        "An incompatible pair should not yield an extra-delay value.");
}

static void ChirpGeneration()
{
    Check.Equal(1_323, ChirpSignal.Create(44_100, 1_000, 8_000).Length,
        "Chirp sample count should represent the configured 30 ms duration.");
    Check.Throws<ArgumentOutOfRangeException>(() => ChirpSignal.Create(7_999, 1_000, 3_000));
    Check.Throws<ArgumentOutOfRangeException>(() => ChirpSignal.Create(48_000, 10_000, 24_000));
}

static void QueueAdjustedStatistics()
{
    var session = Session("speaker", "mic", 48_000,
        new TrialResult(1, 40, 5, 0.9, 0.2, "Valid"),
        new TrialResult(2, 50, 20, 0.9, 0.2, "Valid"),
        new TrialResult(3, 100, 30, 0.9, 0.2, "Valid"),
        new TrialResult(4, null, 100, 0.1, 0.1, "Rejected"));
    Check.Equal(35.0, session.MedianQueueAdjustedMs!.Value, 1e-12,
        "Pair each raw value with its queue before taking the median.");
    Check.Equal(50.0, session.MedianMs!.Value, 1e-12, "The raw median must remain available.");
    Check.Equal(20.0, session.MedianQueueMs!.Value, 1e-12, "The queue summary must exclude rejected trials.");
    Check.Equal(3, session.AcceptedCount, "Adding derived metrics must not change trial acceptance.");
    Check.True(session.Trials[3].QueueAdjustedLatencyMs is null,
        "A missing raw detection must remain missing after queue adjustment.");

    var varyingQueue = Session("speaker", "mic", 48_000,
        new TrialResult(1, 60, 20, 0.9, 0.2, "Valid"),
        new TrialResult(2, 80, 40, 0.9, 0.2, "Valid"),
        new TrialResult(3, 100, 60, 0.9, 0.2, "Valid"));
    Check.Equal(40.0, varyingQueue.MedianQueueAdjustedMs!.Value, 1e-12,
        "The result should remove a varying queue from a constant downstream delay.");
    Check.Equal(0.0, varyingQueue.QueueAdjustedJitterMs!.Value, 1e-12,
        "Queue variation alone must not appear as adjusted jitter.");
    Check.True(varyingQueue.JitterMs > 0, "Raw jitter must still reflect the variable total path.");

    var rejected = Session("speaker", "mic", 48_000,
        Enumerable.Range(1, 3).Select(i => new TrialResult(i, null, 20, 0, 0, "Not detected")).ToArray());
    Check.True(rejected.MedianQueueAdjustedMs is null && rejected.QueueAdjustedJitterMs is null,
        "An all-rejected run must have no adjusted result or jitter.");
    Check.False(rejected.HasNegativeQueueAdjustedValues, "Rejected trials must not cause a negative-value warning.");
}

static void NegativeQueueAdjustedEstimate()
{
    var session = Session("speaker", "mic", 48_000,
        new TrialResult(1, 10, 20, 0.9, 0.2, "Valid"),
        new TrialResult(2, 15, 25, 0.9, 0.2, "Valid"),
        new TrialResult(3, 20, 30, 0.9, 0.2, "Valid"));
    Check.Equal(-10.0, session.MedianQueueAdjustedMs!.Value, 1e-12,
        "Preserve a negative estimate instead of silently clamping it to zero.");
    Check.True(session.HasNegativeQueueAdjustedValues, "Negative estimates must be flagged for the UI.");
    Check.Equal(15.0, session.MedianMs!.Value, 1e-12, "The raw measurement must remain positive and unchanged.");
}

static void LegacyReferenceCompatibility()
{
    // This is the original raw-data format, without any queue-adjusted derived fields.
    const string legacyJson = """
        {
          "CreatedAt": "2026-10-15T00:00:00+00:00",
          "Output": { "Id": "wired", "Name": "Wired output", "IsDefault": false },
          "Input": { "Id": "mic", "Name": "Microphone", "IsDefault": true },
          "Options": { "OutputDeviceId": "wired", "InputDeviceId": "mic", "TrialCount": 3, "Volume": 0.2, "DistanceCm": 0 },
          "OutputSampleRate": 48000, "InputSampleRate": 48000,
          "OutputBufferMs": 40, "InputStreamLatencyMs": 10,
          "Trials": [
            { "Number": 1, "LatencyMs": 50, "QueueMs": 10, "Correlation": 0.9, "Peak": 0.2, "Status": "Valid" },
            { "Number": 2, "LatencyMs": 60, "QueueMs": 20, "Correlation": 0.9, "Peak": 0.2, "Status": "Valid" },
            { "Number": 3, "LatencyMs": 70, "QueueMs": 30, "Correlation": 0.9, "Peak": 0.2, "Status": "Valid" }
          ]
        }
        """;
    var legacy = JsonSerializer.Deserialize<MeasurementSession>(legacyJson)!;
    legacy.Options.Validate();
    Check.Equal(60.0, legacy.MedianMs!.Value, 1e-12, "Legacy latencies must keep their raw meaning.");
    Check.Equal(40.0, legacy.MedianQueueAdjustedMs!.Value, 1e-12, "Legacy data must derive the adjusted result on load.");
    var current = Session("bluetooth", "mic", 48_000, ValidTrials(3, 362, 20));
    Check.Equal(302.0, ReferenceComparison.ExtraDelayMs(current, legacy)!.Value, 1e-12,
        "The existing reference comparison must subtract the queue once, including legacy references.");

    string json = JsonSerializer.Serialize(legacy);
    using var document = JsonDocument.Parse(json);
    Check.Equal(60.0, document.RootElement.GetProperty("Trials")[1].GetProperty("LatencyMs").GetDouble(), 1e-12,
        "Exported raw fields must preserve their old meaning.");
    Check.Equal(40.0, document.RootElement.GetProperty("Trials")[1].GetProperty("QueueAdjustedLatencyMs").GetDouble(), 1e-12,
        "Exports must include separately named adjusted fields.");
    var roundTrip = JsonSerializer.Deserialize<MeasurementSession>(json)!;
    Check.Equal(legacy.MedianQueueAdjustedMs!.Value, roundTrip.MedianQueueAdjustedMs!.Value, 1e-12,
        "Exporting and reloading a reference must not apply the adjustment twice.");
}

static MeasurementSession Session(string outputId, string inputId, int inputRate, params TrialResult[] trials) =>
    new(DateTimeOffset.UnixEpoch,
        new AudioDeviceInfo(outputId, outputId, false),
        new AudioDeviceInfo(inputId, inputId, false),
        new MeasurementOptions(outputId, inputId, TrialCount: trials.Length),
        OutputSampleRate: 48_000,
        InputSampleRate: inputRate,
        OutputBufferMs: 0,
        InputStreamLatencyMs: 0,
        Trials: trials);

static TrialResult[] ValidTrials(int count, double latency, double queue) =>
    Enumerable.Range(1, count).Select(i => new TrialResult(i, latency, queue, 0.9, 0.2, "Valid")).ToArray();

static float[] Noise(int length, int seed, double sigma)
{
    var random = new Random(seed);
    var output = new float[length];
    // Box-Muller Gaussian noise approximates a quiet room plus microphone self-noise.
    for (int i = 0; i < length; i += 2)
    {
        double u1 = Math.Max(random.NextDouble(), double.Epsilon);
        double u2 = random.NextDouble();
        double magnitude = Math.Sqrt(-2 * Math.Log(u1));
        output[i] = (float)(sigma * magnitude * Math.Cos(2 * Math.PI * u2));
        if (i + 1 < length)
            output[i + 1] = (float)(sigma * magnitude * Math.Sin(2 * Math.PI * u2));
    }
    return output;
}

static void Add(float[] destination, float[] source, int offset, float gain)
{
    if (offset < 0 || offset + source.Length > destination.Length)
        throw new ArgumentOutOfRangeException(nameof(offset), "The source must fit in the destination.");
    for (int i = 0; i < source.Length; i++) destination[offset + i] += gain * source[i];
}

static float[] ResampleLinear(float[] source, int sourceRate, int targetRate, int outputLength)
{
    var output = new float[outputLength];
    for (int i = 0; i < output.Length; i++)
    {
        double sourcePosition = (double)i * sourceRate / targetRate;
        int lower = (int)Math.Floor(sourcePosition);
        if (lower >= source.Length - 1)
        {
            output[i] = source[^1];
            continue;
        }
        double fraction = sourcePosition - lower;
        output[i] = (float)(source[lower] * (1 - fraction) + source[lower + 1] * fraction);
    }
    return output;
}

static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}; got {actual}.");
    }

    public static void Equal(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message} Expected {expected}; got {actual}.");
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
