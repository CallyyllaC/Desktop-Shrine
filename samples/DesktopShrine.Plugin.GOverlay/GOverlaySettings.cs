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
    public bool DontUseDrawPixels { get; init; } = true;
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
        var dontUseDrawPixels = ReadDontUseDrawPixels(configuration);
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
        return new()
        {
            PipeName = pipeName,
            UpdatesPerSecond = updatesPerSecond,
            FontName = fontName,
            WaterfallColumnWidth = waterfallColumnWidth,
            DontUseDrawPixels = dontUseDrawPixels,
            Waterfall = waterfall
        };
    }

    private static bool ReadDontUseDrawPixels(
        IConfiguration configuration)
    {
        var configured = configuration["DontUseDrawPixels"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (bool.TryParse(configured, out var parsed))
                return parsed;
            throw new InvalidOperationException(
                "DontUseDrawPixels must be true or false.");
        }

        // Migrate the retired setting in memory for existing installations.
        // New configuration only writes DontUseDrawPixels.
        var legacy = configuration["CompatibilityMode"];
        if (string.IsNullOrWhiteSpace(legacy)
            || legacy.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            || legacy.Equals("IpsSafe", StringComparison.OrdinalIgnoreCase))
            return true;
        if (legacy.Equals("Standard", StringComparison.OrdinalIgnoreCase)
            || legacy.Equals("Normal", StringComparison.OrdinalIgnoreCase)
            || legacy.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return false;
        throw new InvalidOperationException(
            "Legacy CompatibilityMode must be Auto, IpsSafe, Standard, Normal, or Default.");
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
