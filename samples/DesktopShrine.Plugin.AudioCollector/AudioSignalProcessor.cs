using System.Numerics;

namespace DesktopShrine.Plugin.AudioCollector;

internal sealed class AudioSignalProcessor
{
    private readonly object gate = new();
    private readonly float[] samples;
    private readonly float[] snapshot;
    private readonly float[] fftReal;
    private readonly float[] fftImaginary;
    private readonly float[] window;
    private readonly TwiddleStage[] twiddleStages;
    private readonly bool useSimd;

    public AudioSignalProcessor(int fftSize, bool useSimd = true)
    {
        if (fftSize < 2 || !BitOperations.IsPow2((uint)fftSize))
            throw new ArgumentOutOfRangeException(nameof(fftSize), "FFT size must be a power of two greater than one.");

        samples = new float[fftSize];
        snapshot = new float[fftSize];
        fftReal = new float[fftSize];
        fftImaginary = new float[fftSize];
        window = Enumerable.Range(0, fftSize)
            .Select(index => (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * index / (fftSize - 1)))))
            .ToArray();
        twiddleStages = CreateTwiddleStages(fftSize);
        this.useSimd = useSimd && Vector.IsHardwareAccelerated;
    }

    public int FftSize => samples.Length;
    internal bool IsSimdEnabled => useSimd;
    internal int SimdWidth => useSimd ? Vector<float>.Count : 1;

    public void Append(ReadOnlySpan<float> mono)
    {
        if (mono.IsEmpty)
            return;

        lock (gate)
        {
            if (mono.Length >= samples.Length)
            {
                mono[^samples.Length..].CopyTo(samples);
                return;
            }

            samples.AsSpan(mono.Length).CopyTo(samples);
            mono.CopyTo(samples.AsSpan(samples.Length - mono.Length));
        }
    }

    public ProcessedAudio Snapshot(int waveformSamples)
    {
        if (waveformSamples < 2)
            throw new ArgumentOutOfRangeException(nameof(waveformSamples));

        lock (gate)
            samples.CopyTo(snapshot, 0);

        ApplyWindow(snapshot, window, fftReal);
        Array.Clear(fftImaginary);
        Transform(fftReal, fftImaginary);

        var spectrum = new float[(snapshot.Length / 2) + 1];
        CalculateMagnitudes(fftReal, fftImaginary, spectrum);
        return new(spectrum, Resample(snapshot, waveformSamples));
    }

    internal static float[] MixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));

        var frameCount = interleaved.Length / channels;
        var mono = new float[frameCount];
        if (channels == 1)
        {
            interleaved[..frameCount].CopyTo(mono);
            return mono;
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
                sum += interleaved[(frame * channels) + channel];
            mono[frame] = sum / channels;
        }

        return mono;
    }

    private void ApplyWindow(float[] source, float[] coefficients, float[] destination)
    {
        var index = 0;
        if (useSimd)
        {
            var finalVectorStart = source.Length - Vector<float>.Count;
            for (; index <= finalVectorStart; index += Vector<float>.Count)
            {
                var values = new Vector<float>(source, index);
                var weights = new Vector<float>(coefficients, index);
                (values * weights).CopyTo(destination, index);
            }
        }

        for (; index < source.Length; index++)
            destination[index] = source[index] * coefficients[index];
    }

    private void Transform(float[] real, float[] imaginary)
    {
        var bits = BitOperations.Log2((uint)real.Length);
        for (var index = 1; index < real.Length; index++)
        {
            var reversed = BitReverse(index, bits);
            if (reversed <= index)
                continue;

            (real[index], real[reversed]) = (real[reversed], real[index]);
            (imaginary[index], imaginary[reversed]) = (imaginary[reversed], imaginary[index]);
        }

        foreach (var stage in twiddleStages)
        {
            for (var offset = 0; offset < real.Length; offset += stage.Length)
            {
                var index = 0;
                if (useSimd)
                {
                    var finalVectorStart = stage.HalfLength - Vector<float>.Count;
                    for (; index <= finalVectorStart; index += Vector<float>.Count)
                    {
                        var evenReal = new Vector<float>(real, offset + index);
                        var evenImaginary = new Vector<float>(imaginary, offset + index);
                        var oddReal = new Vector<float>(real, offset + index + stage.HalfLength);
                        var oddImaginary = new Vector<float>(imaginary, offset + index + stage.HalfLength);
                        var twiddleReal = new Vector<float>(stage.Real, index);
                        var twiddleImaginary = new Vector<float>(stage.Imaginary, index);
                        var rotatedReal = (oddReal * twiddleReal) - (oddImaginary * twiddleImaginary);
                        var rotatedImaginary = (oddReal * twiddleImaginary) + (oddImaginary * twiddleReal);

                        (evenReal + rotatedReal).CopyTo(real, offset + index);
                        (evenImaginary + rotatedImaginary).CopyTo(imaginary, offset + index);
                        (evenReal - rotatedReal).CopyTo(real, offset + index + stage.HalfLength);
                        (evenImaginary - rotatedImaginary).CopyTo(imaginary, offset + index + stage.HalfLength);
                    }
                }

                for (; index < stage.HalfLength; index++)
                {
                    var evenIndex = offset + index;
                    var oddIndex = evenIndex + stage.HalfLength;
                    var rotatedReal = (real[oddIndex] * stage.Real[index])
                        - (imaginary[oddIndex] * stage.Imaginary[index]);
                    var rotatedImaginary = (real[oddIndex] * stage.Imaginary[index])
                        + (imaginary[oddIndex] * stage.Real[index]);
                    var evenReal = real[evenIndex];
                    var evenImaginary = imaginary[evenIndex];

                    real[evenIndex] = evenReal + rotatedReal;
                    imaginary[evenIndex] = evenImaginary + rotatedImaginary;
                    real[oddIndex] = evenReal - rotatedReal;
                    imaginary[oddIndex] = evenImaginary - rotatedImaginary;
                }
            }
        }
    }

    private void CalculateMagnitudes(float[] real, float[] imaginary, float[] destination)
    {
        var index = 0;
        if (useSimd)
        {
            var finalVectorStart = destination.Length - Vector<float>.Count;
            for (; index <= finalVectorStart; index += Vector<float>.Count)
            {
                var realValues = new Vector<float>(real, index);
                var imaginaryValues = new Vector<float>(imaginary, index);
                Vector.SquareRoot((realValues * realValues) + (imaginaryValues * imaginaryValues))
                    .CopyTo(destination, index);
            }
        }

        for (; index < destination.Length; index++)
            destination[index] = MathF.Sqrt((real[index] * real[index]) + (imaginary[index] * imaginary[index]));
    }

    private static float[] Resample(ReadOnlySpan<float> source, int outputLength)
    {
        var output = new float[outputLength];
        if (source.IsEmpty)
            return output;
        if (source.Length == 1)
        {
            Array.Fill(output, source[0]);
            return output;
        }

        var scale = (source.Length - 1d) / (outputLength - 1d);
        for (var index = 0; index < output.Length; index++)
        {
            var position = index * scale;
            var left = (int)position;
            var right = Math.Min(left + 1, source.Length - 1);
            var fraction = (float)(position - left);
            output[index] = source[left] + ((source[right] - source[left]) * fraction);
        }

        return output;
    }

    private static TwiddleStage[] CreateTwiddleStages(int fftSize)
    {
        var stages = new List<TwiddleStage>();
        for (var length = 2; length <= fftSize; length *= 2)
        {
            var halfLength = length / 2;
            var real = new float[halfLength];
            var imaginary = new float[halfLength];
            for (var index = 0; index < halfLength; index++)
            {
                var phase = -2 * Math.PI * index / length;
                real[index] = (float)Math.Cos(phase);
                imaginary[index] = (float)Math.Sin(phase);
            }
            stages.Add(new(length, real, imaginary));
        }
        return [.. stages];
    }

    private static int BitReverse(int value, int bits)
    {
        var result = 0;
        for (var index = 0; index < bits; index++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }

    private sealed record TwiddleStage(int Length, float[] Real, float[] Imaginary)
    {
        public int HalfLength => Length / 2;
    }
}

internal sealed record ProcessedAudio(float[] Spectrum, float[] Waveform);
