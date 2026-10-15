using System.Numerics;

namespace AudioLatency.Core;

public sealed record DetectionResult(bool Found, int SampleOffset, double Correlation, double Peak, string Reason);

public static class SignalDetector
{
    /// <summary>
    /// Normalized matched filtering locates the sweep independently of gain and polarity.
    /// FFT convolution avoids a sample-by-sample dot product across the entire capture window.
    /// Only complete template windows in the requested inclusive lag range are candidates.
    /// </summary>
    public static DetectionResult Find(float[] samples, float[] template, int minimumOffset, int maximumOffset)
    {
        if (template.Length < 2) throw new ArgumentException("The template is too short.", nameof(template));
        minimumOffset = Math.Max(0, minimumOffset);
        maximumOffset = Math.Min(maximumOffset, samples.Length - template.Length);
        if (maximumOffset < minimumOffset) return new(false, 0, 0, 0, "Incomplete capture window");
        if (samples.Any(x => !float.IsFinite(x)) || template.Any(x => !float.IsFinite(x)))
            return new(false, 0, 0, 0, "Invalid audio samples");

        int size = 1;
        while (size < samples.Length + template.Length - 1) size <<= 1;
        var spectrum = new Complex[size];
        var filter = new Complex[size];
        var energy = new double[samples.Length + 1];
        double templateEnergy = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            spectrum[i] = new Complex(samples[i], 0);
            energy[i + 1] = energy[i] + (double)samples[i] * samples[i];
        }
        for (int i = 0; i < template.Length; i++)
        {
            filter[i] = new Complex(template[template.Length - 1 - i], 0);
            templateEnergy += (double)template[i] * template[i];
        }
        if (templateEnergy < 1e-12) throw new ArgumentException("The template is silent.", nameof(template));
        Transform(spectrum, false);
        Transform(filter, false);
        for (int i = 0; i < size; i++) spectrum[i] *= filter[i];
        Transform(spectrum, true);

        double best = 0;
        int offset = minimumOffset;
        var scores = new double[maximumOffset - minimumOffset + 1];
        for (int lag = minimumOffset; lag <= maximumOffset; lag++)
        {
            double windowEnergy = Math.Max(0, energy[lag + template.Length] - energy[lag]);
            double score = windowEnergy < 1e-12 ? 0 : Math.Min(1,
                Math.Abs(spectrum[lag + template.Length - 1].Real) / Math.Sqrt(windowEnergy * templateEnergy));
            scores[lag - minimumOffset] = score;
            if (score > best) { best = score; offset = lag; }
        }
        double peak = 0;
        int clipped = 0;
        for (int i = offset; i < offset + template.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
            if (Math.Abs(samples[i]) >= 0.999) clipped++;
        }
        if (best < 0.25 || peak < 0.002) return new(false, offset, best, peak, "Chirp too weak or not detected");
        if (clipped > template.Length / 100) return new(false, offset, best, peak, "Microphone clipped; reduce volume");
        // Ignore the immediate correlation lobe, but reject a separate nearly equal match.
        // That can indicate another sound source or a strong room reflection.
        for (int lag = minimumOffset; lag <= maximumOffset; lag++)
            if (Math.Abs(lag - offset) > template.Length / 3 && scores[lag - minimumOffset] > best * 0.90)
                return new(false, offset, best, peak, "Ambiguous match; move the microphone closer");
        return new(true, offset, best, peak, "Valid");
    }

    // In-place radix-2 transform. Inverse normalization occurs once at the end.
    private static void Transform(Complex[] values, bool inverse)
    {
        int n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            var root = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                var rotation = Complex.One;
                for (int k = 0; k < length / 2; k++)
                {
                    var left = values[start + k];
                    var right = rotation * values[start + k + length / 2];
                    values[start + k] = left + right;
                    values[start + k + length / 2] = left - right;
                    rotation *= root;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) values[i] /= n;
    }
}
