using DesktopShrine.Plugin.GOverlay.Layout;
using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace DesktopShrine.Plugin.GOverlay;

internal sealed record GOverlaySettings
{
    public const string DefaultPipeName = "DesktopShrine.GOverlay";

    public string PipeName { get; init; } = DefaultPipeName;
    public double UpdatesPerSecond { get; init; } = 10;
    public string FontName { get; init; } = "Oxanium-Bold_20px.bin";
    public int WaterfallColumnWidth { get; init; } =
        GOverlayWaterfallGeometry.ColumnWidth;
    public string CompatibilityMode { get; init; } = "Auto";
    public int MaximumCommandsPerRefresh { get; init; } = 48;
    public int MaximumArtworkBatchesPerRefresh { get; init; } = 4;
    public int MaximumDrawMilliseconds { get; init; } = 100;
    public int AudioRefreshDivisor { get; init; } = 1;
    public int ReconnectStabilizationMilliseconds { get; init; } = 1500;
    public GOverlayWaterfallOptions Waterfall { get; init; } = new();

    public TimeSpan UpdateInterval =>
        TimeSpan.FromSeconds(1 / UpdatesPerSecond);

    public static GOverlaySettings FromConfiguration(
        IConfiguration configuration)
    {
        var pipeName = string.IsNullOrWhiteSpace(configuration["PipeName"])
            ? DefaultPipeName
            : configuration["PipeName"]!;
        var updatesPerSecond = double.TryParse(
            configuration["UpdatesPerSecond"],
            out var parsed)
            ? parsed
            : 10;
        var fontName = string.IsNullOrWhiteSpace(configuration["FontName"])
            ? "Oxanium-Bold_20px.bin"
            : configuration["FontName"]!;
        var waterfallColumnWidth = ReadInt(
            configuration,
            "WaterfallColumnWidthPixels",
            GOverlayWaterfallGeometry.ColumnWidth);
        var compatibilityMode = string.IsNullOrWhiteSpace(
            configuration["CompatibilityMode"])
            ? "Auto"
            : configuration["CompatibilityMode"]!;
        var maximumCommands = ReadInt(
            configuration,
            "MaximumCommandsPerRefresh",
            48);
        var maximumArtworkBatches = ReadInt(
            configuration,
            "MaximumArtworkBatchesPerRefresh",
            4);
        var maximumDrawMilliseconds = ReadInt(
            configuration,
            "MaximumDrawMilliseconds",
            100);
        var audioRefreshDivisor = ReadInt(
            configuration,
            "AudioRefreshDivisor",
            1);
        var reconnectStabilizationMilliseconds = ReadInt(
            configuration,
            "ReconnectStabilizationMilliseconds",
            1500);
        var waterfall = new GOverlayWaterfallOptions
        {
            NoiseFloorDb = ReadDouble(
                configuration,
                "WaterfallNoiseFloorDb",
                -55),
            VisualCeilingDb = ReadDouble(
                configuration,
                "WaterfallVisualCeilingDb",
                -6),
            RmsWeight = ReadDouble(
                configuration,
                "WaterfallRmsWeight",
                0.65),
            PeakWeight = ReadDouble(
                configuration,
                "WaterfallPeakWeight",
                0.35),
            Release = TimeSpan.FromMilliseconds(ReadDouble(
                configuration,
                "WaterfallReleaseMilliseconds",
                550)),
            ColumnInterval = TimeSpan.FromMilliseconds(ReadDouble(
                configuration,
                "WaterfallIntervalMilliseconds",
                500)),
            InactiveAfter = TimeSpan.FromMilliseconds(ReadDouble(
                configuration,
                "WaterfallInactiveMilliseconds",
                750))
        };

        if (pipeName.IndexOfAny(['\\', '/', ':']) >= 0)
            throw new InvalidOperationException(
                "PipeName must be a simple local named-pipe name.");
        if (updatesPerSecond is < 1 or > 10)
            throw new InvalidOperationException(
                "UpdatesPerSecond must be between 1 and 10.");
        if (!fontName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FontName must name a .bin font.");
        if (waterfallColumnWidth is < 1 or > 16)
            throw new InvalidOperationException(
                "WaterfallColumnWidthPixels must be between 1 and 16.");
        if (!compatibilityMode.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && !compatibilityMode.Equals("Standard", StringComparison.OrdinalIgnoreCase)
            && !compatibilityMode.Equals("IpsSafe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "CompatibilityMode must be Auto, Standard, or IpsSafe.");
        if (maximumCommands is < 36 or > 512)
            throw new InvalidOperationException(
                "MaximumCommandsPerRefresh must be between 36 and 512 so one audio-map update remains atomic.");
        if (maximumArtworkBatches is < 1 or > 32)
            throw new InvalidOperationException(
                "MaximumArtworkBatchesPerRefresh must be between 1 and 32.");
        if (maximumDrawMilliseconds is < 10 or > 5000)
            throw new InvalidOperationException(
                "MaximumDrawMilliseconds must be between 10 and 5000.");
        if (audioRefreshDivisor is < 1 or > 20)
            throw new InvalidOperationException(
                "AudioRefreshDivisor must be between 1 and 20.");
        if (reconnectStabilizationMilliseconds is < 250 or > 30000)
            throw new InvalidOperationException(
                "ReconnectStabilizationMilliseconds must be between 250 and 30000.");

        return new()
        {
            PipeName = pipeName,
            UpdatesPerSecond = updatesPerSecond,
            FontName = fontName,
            WaterfallColumnWidth = waterfallColumnWidth,
            CompatibilityMode = compatibilityMode,
            MaximumCommandsPerRefresh = maximumCommands,
            MaximumArtworkBatchesPerRefresh = maximumArtworkBatches,
            MaximumDrawMilliseconds = maximumDrawMilliseconds,
            AudioRefreshDivisor = audioRefreshDivisor,
            ReconnectStabilizationMilliseconds = reconnectStabilizationMilliseconds,
            Waterfall = waterfall
        };
    }

    private static double ReadDouble(
        IConfiguration configuration,
        string key,
        double fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            && double.IsFinite(value))
            return value;

        throw new InvalidOperationException(
            $"GOverlay setting {key} must be a finite number.");
    }

    private static int ReadInt(
        IConfiguration configuration,
        string key,
        int fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
            return value;

        throw new InvalidOperationException(
            $"GOverlay setting {key} must be an integer.");
    }
}
