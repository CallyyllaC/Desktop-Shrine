using DesktopShrine.Contracts.Audio;

namespace DesktopShrine.Plugin.GOverlay;

internal readonly record struct GOverlayFrequencyBand(
    double MinimumHz,
    double MaximumHz);

internal sealed record GOverlayWaterfallOptions
{
    public static readonly IReadOnlyList<GOverlayFrequencyBand> DefaultBands =
    [
        new(60, 120),
        new(120, 250),
        new(250, 500),
        new(500, 1_000),
        new(1_000, 2_000),
        new(2_000, 4_000),
        new(4_000, 8_000),
        new(8_000, 16_000)
    ];

    public IReadOnlyList<GOverlayFrequencyBand> Bands { get; init; } =
        DefaultBands;
    public double RmsWeight { get; init; } = 0.65;
    public double PeakWeight { get; init; } = 0.35;
    public double NoiseFloorDb { get; init; } = -55;
    public double VisualCeilingDb { get; init; } = -6;
    public TimeSpan Release { get; init; } =
        TimeSpan.FromMilliseconds(550);
    public TimeSpan ColumnInterval { get; init; } =
        TimeSpan.FromMilliseconds(500);
    public TimeSpan InactiveAfter { get; init; } =
        TimeSpan.FromMilliseconds(750);
}

internal readonly record struct GOverlayWaterfallInterval(
    bool HasColumn,
    bool BecameInactive,
    float[] Values)
{
    public static GOverlayWaterfallInterval None =>
        new(false, false, []);

    public static GOverlayWaterfallInterval Inactive =>
        new(false, true, []);
}

internal sealed class GOverlayWaterfallAggregator
{
    private const double MinimumAmplitude = 1e-12;
    private readonly GOverlayWaterfallOptions options;
    private readonly double[] intervalSquareSums;
    private readonly double[] intervalPeaks;
    private readonly float[] smoothed;
    private int intervalFrameCount;
    private DateTimeOffset? lastFrameAt;
    private DateTimeOffset? lastConsumedAt;
    private bool active;

    public GOverlayWaterfallAggregator(GOverlayWaterfallOptions options)
    {
        this.options = options;
        Validate(options);
        intervalSquareSums = new double[options.Bands.Count];
        intervalPeaks = new double[options.Bands.Count];
        smoothed = new float[options.Bands.Count];
    }

    public int BandCount => options.Bands.Count;

    public void Add(AudioSpectrumFrame frame)
    {
        if (frame.SampleRate <= 0
            || frame.FftSize <= 1
            || frame.Spectrum.Count == 0)
            return;

        var normalisation = 4d / frame.FftSize;
        for (var bandIndex = 0;
             bandIndex < options.Bands.Count;
             bandIndex++)
        {
            var band = options.Bands[bandIndex];
            var firstBin = Math.Max(
                0,
                (int)Math.Ceiling(
                    band.MinimumHz * frame.FftSize / frame.SampleRate));
            var finalBin = Math.Min(
                frame.Spectrum.Count,
                (int)Math.Ceiling(
                    band.MaximumHz * frame.FftSize / frame.SampleRate));
            if (finalBin <= firstBin)
                continue;

            var squareSum = 0d;
            var peak = 0d;
            for (var bin = firstBin; bin < finalBin; bin++)
            {
                var amplitude = Math.Abs(frame.Spectrum[bin])
                    * normalisation;
                squareSum += amplitude * amplitude;
                peak = Math.Max(peak, amplitude);
            }

            var frameRms = Math.Sqrt(
                squareSum / (finalBin - firstBin));
            intervalSquareSums[bandIndex] += frameRms * frameRms;
            intervalPeaks[bandIndex] = Math.Max(
                intervalPeaks[bandIndex],
                peak);
        }

        intervalFrameCount++;
        if (lastFrameAt is null || frame.CapturedAt > lastFrameAt)
            lastFrameAt = frame.CapturedAt;
    }

    public GOverlayWaterfallInterval Consume(DateTimeOffset now)
    {
        if (intervalFrameCount > 0)
        {
            if (lastConsumedAt is { } lastConsumed
                && now - lastConsumed < options.ColumnInterval)
                return GOverlayWaterfallInterval.None;

            var elapsed = lastConsumedAt is { } previous
                ? now - previous
                : TimeSpan.Zero;
            var values = new float[options.Bands.Count];
            for (var band = 0; band < values.Length; band++)
            {
                var rms = Math.Sqrt(
                    intervalSquareSums[band] / intervalFrameCount);
                var combined =
                    (options.RmsWeight * rms)
                    + (options.PeakWeight * intervalPeaks[band]);
                var target = Normalise(combined);
                values[band] = Smooth(
                    smoothed[band],
                    target,
                    elapsed);
                smoothed[band] = values[band];
            }

            ClearInterval();
            lastConsumedAt = now;
            active = true;
            return new(true, false, values);
        }

        if (active
            && lastFrameAt is { } last
            && now - last >= options.InactiveAfter)
        {
            ResetAll();
            return GOverlayWaterfallInterval.Inactive;
        }

        return GOverlayWaterfallInterval.None;
    }

    public void ResetVisualHistory()
    {
        ClearInterval();
        Array.Clear(smoothed);
        lastConsumedAt = null;
    }

    private float Normalise(double amplitude)
    {
        var decibels = 20 * Math.Log10(
            Math.Max(MinimumAmplitude, amplitude));
        var normalised = (decibels - options.NoiseFloorDb)
            / (options.VisualCeilingDb - options.NoiseFloorDb);
        return (float)Math.Clamp(normalised, 0, 1);
    }

    private float Smooth(
        float previous,
        float target,
        TimeSpan elapsed)
    {
        if (target >= previous || elapsed <= TimeSpan.Zero)
            return target;

        var retention = Math.Exp(
            -elapsed.TotalSeconds / options.Release.TotalSeconds);
        return (float)(target + ((previous - target) * retention));
    }

    private void ResetAll()
    {
        ResetVisualHistory();
        lastFrameAt = null;
        active = false;
    }

    private void ClearInterval()
    {
        Array.Clear(intervalSquareSums);
        Array.Clear(intervalPeaks);
        intervalFrameCount = 0;
    }

    private static void Validate(GOverlayWaterfallOptions value)
    {
        if (value.Bands.Count != 8)
            throw new ArgumentException(
                "The GOverlay waterfall requires exactly eight bands.");
        if (value.RmsWeight < 0
            || value.PeakWeight < 0
            || value.RmsWeight + value.PeakWeight <= 0)
            throw new ArgumentException(
                "Waterfall RMS and peak weights must be non-negative.");
        if (value.VisualCeilingDb <= value.NoiseFloorDb)
            throw new ArgumentException(
                "The waterfall visual ceiling must exceed its noise floor.");
        if (value.Release <= TimeSpan.Zero)
            throw new ArgumentException(
                "The waterfall release duration must be positive.");
        if (value.ColumnInterval <= TimeSpan.Zero)
            throw new ArgumentException(
                "The waterfall column interval must be positive.");
        if (value.InactiveAfter <= TimeSpan.Zero)
            throw new ArgumentException(
                "The waterfall inactivity duration must be positive.");
        foreach (var band in value.Bands)
            if (band.MinimumHz < 0 || band.MaximumHz <= band.MinimumHz)
                throw new ArgumentException(
                    "Waterfall frequency bands must be ordered positive ranges.");
    }
}
