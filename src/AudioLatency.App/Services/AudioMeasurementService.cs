using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioLatency.Core;
using NAudio.CoreAudioApi;

namespace AudioLatency.App.Services;

/// <summary>
/// Measures an acoustic path using a QPC timestamp taken when the first chirp frame is submitted
/// to WASAPI and a QPC timestamp supplied by the microphone driver for the detected sample.
/// UI dispatch, callback arrival time and the Play/Start call are not timing references.
/// </summary>
public sealed class AudioMeasurementService
{
    private const double MaximumLatencySeconds = 1.5;
    private const double TrialIntervalSeconds = 2.0;
    private sealed record CapturePacket(double Time, long Position, float[] Samples, bool Invalid);
    private sealed record Emission(int Number, double Time, double QueueMs, double SubmissionSpanMs);

    public static (IReadOnlyList<AudioDeviceInfo> Outputs, IReadOnlyList<AudioDeviceInfo> Inputs) GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return (Enumerate(DataFlow.Render), Enumerate(DataFlow.Capture));

        IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
        {
            string? defaultId = null;
            try { using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = device.ID; }
            catch (COMException) { /* An endpoint list can exist without a default device. */ }
            var result = new List<AudioDeviceInfo>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) result.Add(new(device.ID, device.FriendlyName, device.ID == defaultId));
            }
            return result.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToArray();
        }
    }

    public Task<MeasurementSession> RunAsync(MeasurementOptions options,
        IProgress<MeasurementProgress> progress, CancellationToken cancellationToken)
    {
        options.Validate();
        // A dedicated MTA thread owns activation, buffers and disposal. No COM audio objects are
        // passed between the UI thread and the measuring thread.
        return Task.Factory.StartNew(() => Measure(options, progress, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static MeasurementSession Measure(MeasurementOptions options,
        IProgress<MeasurementProgress> progress, CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var output = enumerator.GetDevice(options.OutputDeviceId);
        using var input = enumerator.GetDevice(options.InputDeviceId);
        if (output.DataFlow != DataFlow.Render || input.DataFlow != DataFlow.Capture)
            throw new ArgumentException("Select a playback device and a microphone.");
        using var render = output.AudioClient;
        using var capture = input.AudioClient;
        var outputFormat = render.MixFormat;
        var inputFormat = capture.MixFormat;
        var outputCodec = new AudioSampleCodec(outputFormat);
        var inputCodec = new AudioSampleCodec(inputFormat);

        // Shared mode represents everyday Windows playback. Keep a modest queue instead of
        // exclusive mode, which many Bluetooth endpoints do not support.
        render.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.NoPersist,
            400_000, 0, outputFormat, Guid.Empty);
        capture.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.NoPersist,
            1_000_000, 0, inputFormat, Guid.Empty);
        int outputRate = outputFormat.SampleRate;
        int inputRate = inputFormat.SampleRate;
        var renderer = render.AudioRenderClient;
        var recorder = capture.AudioCaptureClient;
        double highHz = Math.Min(6500, Math.Min(outputRate, inputRate) * 0.40);
        double lowHz = Math.Min(1500, highHz * 0.35);
        var chirp = ChirpSignal.Create(outputRate, lowHz, highHz, options.Volume);
        var template = ChirpSignal.Create(inputRate, lowHz, highHz);
        var packets = new List<CapturePacket>();
        var emissions = new List<Emission>();
        var underruns = new List<double>();
        var outputSamples = new float[render.BufferSize];
        var outputBytes = new byte[render.BufferSize * outputFormat.BlockAlign];
        var inputBytes = new byte[Math.Max(capture.BufferSize * inputFormat.BlockAlign, inputFormat.AverageBytesPerSecond)];
        double inputStreamLatency = capture.StreamLatency / 10_000.0;
        double outputBufferMs = render.BufferSize * 1000.0 / outputRate;
        int chirpPosition = chirp.Length;
        int emitted = 0;
        double peak = 0;
        double lastMeterTime = 0;
        double startedAt = Now();
        double nextPulse = startedAt + 1.0;
        long? expectedPosition = null;
        double? expectedPacketTime = null;
        bool captureStarted = false;
        bool renderStarted = false;
        var previousPriority = Thread.CurrentThread.Priority;

        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            // Start capturing before playback, and prime the output queue with silence.
            var initialBuffer = renderer.GetBuffer(render.BufferSize);
            _ = initialBuffer;
            renderer.ReleaseBuffer(render.BufferSize, AudioClientBufferFlags.Silent);
            capture.Start(); captureStarted = true;
            render.Start(); renderStarted = true;
            startedAt = Now();
            nextPulse = startedAt + 1.0;
            double finishAt = double.PositiveInfinity;
            progress.Report(new("Warming up the audio streams…", 0, options.TrialCount, 0));

            while (Now() < finishAt)
            {
                token.ThrowIfCancellationRequested();
                if (Now() - startedAt > options.TrialCount * TrialIntervalSeconds + 10)
                    throw new TimeoutException("The audio endpoint stopped progressing. Reconnect the device and retry.");
                // Drain capture first. GetBuffer and ReleaseBuffer must occur on the same thread.
                while (recorder.GetNextPacketSize() > 0)
                {
                    var pointer = recorder.GetBuffer(out int frames, out var flags, out long position, out long qpc);
                    try
                    {
                        if (frames == 0) break;
                        int length = frames * inputFormat.BlockAlign;
                        if (inputBytes.Length < length) inputBytes = new byte[length];
                        float[] samples;
                        if ((flags & AudioClientBufferFlags.Silent) != 0) samples = new float[frames];
                        else
                        {
                            Marshal.Copy(pointer, inputBytes, 0, length);
                            samples = inputCodec.DecodeFirstChannel(inputBytes, frames);
                        }
                        double packetTime = qpc / 10_000_000.0;
                        bool invalid = (flags & AudioClientBufferFlags.TimestampError) != 0 || qpc <= 0 ||
                            Math.Abs(packetTime - Now()) > 5 ||
                            (expectedPosition.HasValue && (position != expectedPosition ||
                                (flags & AudioClientBufferFlags.DataDiscontinuity) != 0)) ||
                            (expectedPacketTime.HasValue && Math.Abs(packetTime - expectedPacketTime.Value) > 0.010);
                        expectedPosition = position + frames;
                        expectedPacketTime = packetTime + (double)frames / inputRate;
                        packets.Add(new(packetTime, position, samples, invalid));
                        foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
                    }
                    finally { recorder.ReleaseBuffer(frames); }
                }

                double now = Now();
                int padding = render.CurrentPadding;
                if (padding == 0 && now - startedAt > 0.25) underruns.Add(now);
                int available = render.BufferSize - padding;
                if (available > 0)
                {
                    bool beginPulse = emitted < options.TrialCount && chirpPosition >= chirp.Length && now >= nextPulse;
                    if (beginPulse) chirpPosition = 0;
                    Array.Clear(outputSamples);
                    int copied = Math.Min(available, chirp.Length - chirpPosition);
                    if (copied > 0) Array.Copy(chirp, chirpPosition, outputSamples, 0, copied);
                    chirpPosition += copied;
                    outputCodec.EncodeMono(outputSamples, outputBytes, available);
                    var pointer = renderer.GetBuffer(available);
                    Marshal.Copy(outputBytes, 0, pointer, available * outputFormat.BlockAlign);
                    // The first chirp sample is frame 0 of this buffer. Bracket submission so a
                    // scheduling pause during ReleaseBuffer can be rejected instead of hidden.
                    double beforeSubmit = Now();
                    renderer.ReleaseBuffer(available, copied == 0 ? AudioClientBufferFlags.Silent : AudioClientBufferFlags.None);
                    double afterSubmit = Now();
                    if (beginPulse)
                    {
                        emitted++;
                        emissions.Add(new(emitted, (beforeSubmit + afterSubmit) / 2,
                            padding * 1000.0 / outputRate, (afterSubmit - beforeSubmit) * 1000));
                        nextPulse = afterSubmit + TrialIntervalSeconds;
                        if (emitted == options.TrialCount)
                            finishAt = afterSubmit + MaximumLatencySeconds + ChirpSignal.DurationSeconds + 0.25;
                    }
                }
                if (now - lastMeterTime > 0.08)
                {
                    progress.Report(new(emitted == 0 ? "Warming up the audio streams…" :
                        $"Listening · chirp {emitted} of {options.TrialCount}", 0, options.TrialCount, peak));
                    peak = 0;
                    lastMeterTime = now;
                }
                token.WaitHandle.WaitOne(2);
            }
        }
        finally
        {
            // Stop both streams even if a disconnect, denied microphone access or cancellation
            // interrupted the run. AudioClient owns and disposes its service interfaces.
            try { if (renderStarted) render.Stop(); }
            finally
            {
                try { if (captureStarted) capture.Stop(); }
                finally { Thread.CurrentThread.Priority = previousPriority; }
            }
        }

        var results = new List<TrialResult>();
        foreach (var emission in emissions)
        {
            token.ThrowIfCancellationRequested();
            var result = Analyze(emission, packets, underruns, template, inputRate, options.DistanceCm);
            results.Add(result);
            progress.Report(new($"Analyzing chirp {emission.Number} of {options.TrialCount}",
                results.Count, options.TrialCount, 0, result));
        }
        return new(DateTimeOffset.Now, new(output.ID, output.FriendlyName, false),
            new(input.ID, input.FriendlyName, false), options, outputRate, inputRate,
            outputBufferMs, inputStreamLatency, results);
    }

    private static TrialResult Analyze(Emission emission, List<CapturePacket> packets,
        List<double> underruns, float[] template, int sampleRate, double distanceCm)
    {
        var window = packets.Where(p => p.Time + (double)p.Samples.Length / sampleRate >= emission.Time - 0.02 &&
            p.Time <= emission.Time + MaximumLatencySeconds + ChirpSignal.DurationSeconds).ToArray();
        TrialResult Reject(string reason) => new(emission.Number, null, emission.QueueMs, 0, 0, reason);
        if (emission.SubmissionSpanMs > 2) return Reject("Submission timing interrupted; retry");
        if (window.Length == 0) return Reject("No timestamped microphone data");
        if (window.Any(p => p.Invalid)) return Reject("Microphone timestamp error or discontinuity");
        if (underruns.Any(t => t >= emission.Time - 0.05 && t <= emission.Time + MaximumLatencySeconds))
            return Reject("Playback underrun; close busy applications and retry");
        for (int i = 1; i < window.Length; i++)
            if (window[i].Position != window[i - 1].Position + window[i - 1].Samples.Length)
                return Reject("Missing microphone samples");

        var samples = new float[window.Sum(p => p.Samples.Length)];
        int offset = 0;
        foreach (var packet in window)
        {
            packet.Samples.CopyTo(samples, offset);
            offset += packet.Samples.Length;
        }
        int minimum = (int)Math.Ceiling((emission.Time - window[0].Time) * sampleRate);
        int maximum = (int)Math.Floor((emission.Time + MaximumLatencySeconds - window[0].Time) * sampleRate);
        var detection = SignalDetector.Find(samples, template, minimum, maximum);
        if (!detection.Found)
            return new(emission.Number, null, emission.QueueMs, detection.Correlation, detection.Peak, detection.Reason);

        // Use the matching packet's timestamp to limit the effect of independent endpoint clock
        // drift. Packet delivery time never contributes to the measured sample's arrival time.
        int matchOffset = detection.SampleOffset;
        var matchedPacket = window[0];
        foreach (var packet in window)
        {
            if (matchOffset < packet.Samples.Length) { matchedPacket = packet; break; }
            matchOffset -= packet.Samples.Length;
        }
        double arrival = matchedPacket.Time + (double)matchOffset / sampleRate;
        double latencyMs = (arrival - emission.Time) * 1000 - distanceCm / 34.3;
        if (latencyMs < 0) return Reject("Negative delay; check distance and device timestamps");
        return new(emission.Number, latencyMs, emission.QueueMs, detection.Correlation, detection.Peak, "Valid");
    }

    private static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
