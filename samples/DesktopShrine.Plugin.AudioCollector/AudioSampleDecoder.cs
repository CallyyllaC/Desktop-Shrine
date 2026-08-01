using System.Buffers.Binary;

namespace DesktopShrine.Plugin.AudioCollector;

internal enum AudioSampleEncoding { Float, Pcm }

internal static class AudioSampleDecoder
{
    public static float[] DecodeToMono(ReadOnlySpan<byte> data, int channels, int bitsPerSample, AudioSampleEncoding encoding)
    {
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));

        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample < 1 || data.Length < bytesPerSample * channels)
            return [];

        var sampleCount = data.Length / bytesPerSample;
        var interleaved = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
            interleaved[index] = Decode(data.Slice(index * bytesPerSample, bytesPerSample), bitsPerSample, encoding);

        return AudioSignalProcessor.MixToMono(interleaved, channels);
    }

    private static float Decode(ReadOnlySpan<byte> sample, int bits, AudioSampleEncoding encoding)
    {
        if (encoding == AudioSampleEncoding.Float && bits == 32)
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
        if (encoding != AudioSampleEncoding.Pcm)
            throw new NotSupportedException($"Unsupported {bits}-bit {encoding} audio format.");

        return bits switch
        {
            8 => (sample[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            24 => ReadInt24(sample) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
            _ => throw new NotSupportedException($"Unsupported {bits}-bit PCM audio format.")
        };
    }

    private static int ReadInt24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xff000000);
    }
}
