using Microsoft.Extensions.Configuration;

namespace DesktopShrine.Plugin.ArtworkPalette;

internal sealed record ArtworkPaletteSettings
{
    public byte AlphaThreshold { get; init; } = 24;
    public int MaximumAnalysisDimension { get; init; } = 128;
    public double BorderPercent { get; init; } = 0.05;
    public double BorderWeight { get; init; } = 0.15;
    public double MaximumAccentLightness { get; init; } = 0.72;
    public double MinimumDistinctAccentDistance { get; init; } = 0.035;
    public double MinimumAccentAverageAlpha { get; init; } = 0.25;

    public static ArtworkPaletteSettings FromConfiguration(IConfiguration configuration)
    {
        var settings = new ArtworkPaletteSettings
        {
            AlphaThreshold = ReadByte(configuration, "AlphaThreshold", 24),
            MaximumAnalysisDimension = ReadInteger(
                configuration,
                "MaximumAnalysisDimension",
                128),
            BorderPercent = ReadDouble(configuration, "BorderPercent", 0.05),
            BorderWeight = ReadDouble(configuration, "BorderWeight", 0.15),
            MaximumAccentLightness = ReadDouble(
                configuration,
                "MaximumAccentLightness",
                0.72),
            MinimumDistinctAccentDistance = ReadDouble(
                configuration,
                "MinimumDistinctAccentDistance",
                0.035),
            MinimumAccentAverageAlpha = ReadDouble(
                configuration,
                "MinimumAccentAverageAlpha",
                0.25)
        };

        if (settings.MaximumAnalysisDimension is < 16 or > 2_048)
            throw new InvalidOperationException(
                "MaximumAnalysisDimension must be between 16 and 2048.");
        ValidateUnit(settings.BorderPercent, "BorderPercent");
        ValidateUnit(settings.BorderWeight, "BorderWeight");
        ValidateUnit(settings.MaximumAccentLightness, "MaximumAccentLightness");
        ValidateUnit(
            settings.MinimumDistinctAccentDistance,
            "MinimumDistinctAccentDistance");
        ValidateUnit(
            settings.MinimumAccentAverageAlpha,
            "MinimumAccentAverageAlpha");
        return settings;
    }

    private static byte ReadByte(
        IConfiguration configuration,
        string key,
        byte fallback) =>
        byte.TryParse(configuration[key], out var value) ? value : fallback;

    private static int ReadInteger(
        IConfiguration configuration,
        string key,
        int fallback) =>
        int.TryParse(configuration[key], out var value) ? value : fallback;

    private static double ReadDouble(
        IConfiguration configuration,
        string key,
        double fallback) =>
        double.TryParse(configuration[key], out var value)
        && double.IsFinite(value)
            ? value
            : fallback;

    private static void ValidateUnit(double value, string key)
    {
        if (value is < 0 or > 1)
            throw new InvalidOperationException($"{key} must be between 0 and 1.");
    }
}
