namespace DesktopShrine.Plugin.GOverlay.Layout;

public enum GOverlayDashboardMode
{
    Media,
    Hardware
}

public enum GOverlayRenderCompatibilityMode
{
    IpsSafe,
    Standard
}

public sealed class GOverlayHardwareMetricState
{
    public string Label { get; set; } = string.Empty;
    public string DisplayValue { get; set; } = "--";
    public double Level { get; set; }
    public bool IsAvailable { get; set; }
}

public sealed class GOverlayDashboardState
{
    public long Revision { get; set; }
    public long RenderGeneration { get; set; }
    public GOverlayRenderCompatibilityMode RenderCompatibilityMode { get; set; }
    public string DeviceFirmwareRevision { get; set; } = string.Empty;
    public int MaximumCommandsPerRefresh { get; set; } = 48;
    public int MaximumArtworkBatchesPerRefresh { get; set; } = 4;
    public int MaximumDrawMilliseconds { get; set; } = 100;
    public int AudioRefreshDivisor { get; set; } = 1;
    public int ReconnectStabilizationMilliseconds { get; set; } = 1500;
    public GOverlayDashboardMode Mode { get; set; }
    public bool IsAvailable { get; set; }
    public string PlaybackStatus { get; set; } = "INACTIVE";
    public string Title { get; set; } = "Nothing playing";
    public string Artist { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string FontName { get; set; } = "Oxanium-Bold_20px.bin";
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public bool HasRating { get; set; }
    public long PositiveRatingCount { get; set; }
    public long NegativeRatingCount { get; set; }
    public string? RatingSummary { get; set; }
    public GOverlayColour Dominant { get; set; } = new(68, 74, 84);
    public GOverlayColour Accent { get; set; } = new(83, 196, 255);
    public GOverlayColour Dark { get; set; } = new(14, 17, 22);
    public GOverlayColour Light { get; set; } = new(226, 232, 240);
    public bool HasPalette { get; set; }
    public bool AudioActive { get; set; }
    public long WaterfallResetSequence { get; set; }
    public long WaterfallColumnSequence { get; set; }
    public int WaterfallWritePosition { get; set; }
    public int WaterfallColumnWidth { get; set; } =
        GOverlayWaterfallGeometry.ColumnWidth;
    public bool HasWaterfallColumn { get; set; }
    public float[] WaterfallBands { get; set; } = Array.Empty<float>();
    public string ArtworkKey { get; set; } = string.Empty;
    public string? ArtworkContentType { get; set; }
    public byte[] ArtworkData { get; set; } = Array.Empty<byte>();
    public string HardwareProvider { get; set; } = string.Empty;
    public string HardwareProviderVersion { get; set; } = string.Empty;
    public long HardwareCapturedAtUnixMilliseconds { get; set; }
    public string GpuName { get; set; } = "GPU";
    public string CpuName { get; set; } = "CPU";
    public GOverlayHardwareMetricState[] GpuMetrics { get; set; } =
        Array.Empty<GOverlayHardwareMetricState>();
    public GOverlayHardwareMetricState[] CpuMetrics { get; set; } =
        Array.Empty<GOverlayHardwareMetricState>();
    public string PhysicalMemorySummary { get; set; } = "RAM  -- / --";
    public string VirtualMemorySummary { get; set; } = "VIRT  -- / --";
    public double PhysicalMemoryLevel { get; set; }
    public double VirtualMemoryLevel { get; set; }

    public double Progress =>
        DurationSeconds <= 0
            ? 0
            : Math.Max(0, Math.Min(1, PositionSeconds / DurationSeconds));
}
