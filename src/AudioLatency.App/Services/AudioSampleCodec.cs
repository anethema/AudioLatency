using System.Buffers.Binary;
using NAudio.Wave;

namespace AudioLatency.App.Services;

/// <summary>Converts the endpoint's native shared-mode mix format without requesting a new device format.</summary>
internal sealed class AudioSampleCodec
{
    private readonly WaveFormat format;
    private readonly bool floatingPoint;
    private readonly int bytesPerSample;

    public AudioSampleCodec(WaveFormat format)
    {
        this.format = format;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extended)
        {
            if (extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
                encoding = WaveFormatEncoding.IeeeFloat;
            else if (extended.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"))
                encoding = WaveFormatEncoding.Pcm;
        }
        floatingPoint = encoding == WaveFormatEncoding.IeeeFloat;
        if ((!floatingPoint && encoding != WaveFormatEncoding.Pcm) ||
            (floatingPoint && format.BitsPerSample != 32) ||
            (!floatingPoint && format.BitsPerSample is not (16 or 24 or 32)) ||
            format.Channels < 1 || format.SampleRate is < 8000 or > 192000)
            throw new NotSupportedException($"Unsupported device format: {format}. Choose a PCM or float format in Windows Sound settings.");
        bytesPerSample = format.BitsPerSample / 8;
    }

    // Capture channel 1 consistently. Averaging a microphone array's channels can cancel a pulse
    // when the driver provides phase-inverted channels or unrelated array elements.
    public float[] DecodeFirstChannel(byte[] bytes, int frameCount)
    {
        var result = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            var sample = bytes.AsSpan(i * format.BlockAlign, bytesPerSample);
            result[i] = floatingPoint ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)) :
                bytesPerSample switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                    3 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608f,
                    _ => (float)(BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0)
                };
        }
        return result;
    }

    public void EncodeMono(float[] mono, byte[] destination, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            double value = Math.Clamp(mono[i], -1, 1);
            for (int channel = 0; channel < format.Channels; channel++)
            {
                var sample = destination.AsSpan(i * format.BlockAlign + channel * bytesPerSample, bytesPerSample);
                if (floatingPoint)
                    BinaryPrimitives.WriteInt32LittleEndian(sample, BitConverter.SingleToInt32Bits((float)value));
                else if (bytesPerSample == 2)
                    BinaryPrimitives.WriteInt16LittleEndian(sample, (short)Math.Round(value * short.MaxValue));
                else if (bytesPerSample == 3)
                {
                    int integer = (int)Math.Round(value * 8388607);
                    sample[0] = (byte)integer;
                    sample[1] = (byte)(integer >> 8);
                    sample[2] = (byte)(integer >> 16);
                }
                else BinaryPrimitives.WriteInt32LittleEndian(sample, (int)Math.Round(value * int.MaxValue));
            }
        }
    }
}
