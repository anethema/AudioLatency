namespace AudioLatency.Core;

public static class ChirpSignal
{
    public const double DurationSeconds = 0.030;

    /// <summary>
    /// Generate the same continuous-time sweep at either endpoint's sample rate. A Hann envelope
    /// avoids a sharp click; the frequency span stays below both devices' Nyquist frequencies.
    /// </summary>
    public static float[] Create(int sampleRate, double lowHz, double highHz, double amplitude = 1)
    {
        if (sampleRate < 8000 || !double.IsFinite(lowHz) || !double.IsFinite(highHz) ||
            lowHz <= 0 || highHz <= lowHz || highHz >= sampleRate / 2.0 ||
            !double.IsFinite(amplitude) || amplitude is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Invalid chirp format or amplitude.");
        int count = (int)Math.Round(sampleRate * DurationSeconds);
        var signal = new float[count];
        double slope = (highHz - lowHz) / DurationSeconds;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / sampleRate;
            double envelope = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (count - 1));
            signal[i] = (float)(amplitude * envelope * Math.Sin(2 * Math.PI * (lowHz * t + 0.5 * slope * t * t)));
        }
        return signal;
    }
}
