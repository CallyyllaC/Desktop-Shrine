using System.IO.Pipes;
using DesktopShrine.Plugin.GOverlay.Layout;

var hardware = args.Length == 2 && args[0] == "--hardware";
var pipeCapture = args.Length == 3 && args[0] == "--pipe";
var outputPath = pipeCapture
    ? args[2]
    : hardware
        ? args[1]
        : args.SingleOrDefault();
if (string.IsNullOrWhiteSpace(outputPath))
    throw new ArgumentException(
        "Pass an output SVG path, --hardware <path>, or --pipe <name> <path>.");

var artworkPath = Path.Combine(
    "samples",
    "DesktopShrine.Plugin.GOverlay",
    "Preview",
    "sample-artwork.png");

var state = pipeCapture
    ? await CaptureAsync(args[1])
    : hardware
        ? HardwareState()
        : MediaState();
var scene = new GOverlayDashboardLayout().Compose(
    state,
    new DateTime(2026, 7, 30, 14, 32, 10));
var svg = GOverlaySvgRenderer.Render(scene);
Directory.CreateDirectory(
    Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllText(outputPath, svg);

GOverlayDashboardState MediaState() => new()
{
    Revision = 1,
    IsAvailable = true,
    PlaybackStatus = "ACTIVE",
    Title = "Moonlit Shrine",
    Artist = "Foxglove Assembly",
    Context = "Night Signals / Spotify",
    PositionSeconds = 102,
    DurationSeconds = 238,
    Dominant = new(92, 76, 180),
    Accent = new(74, 210, 226),
    Dark = new(12, 16, 25),
    Light = new(224, 232, 240),
    HasPalette = true,
    AudioActive = true,
    WaterfallResetSequence = 1,
    WaterfallColumnSequence = 41,
    WaterfallWritePosition = 40,
    HasWaterfallColumn = true,
    WaterfallBands =
    [
        0.04f,
        0.12f,
        0.38f,
        0.91f,
        0.54f,
        0.28f,
        0.73f,
        0.16f
    ],
    ArtworkKey = "sample-artwork",
    ArtworkContentType = "image/png",
    ArtworkData = File.ReadAllBytes(artworkPath)
};

static GOverlayDashboardState HardwareState() => new()
{
    Revision = 2,
    Mode = GOverlayDashboardMode.Hardware,
    IsAvailable = true,
    HardwareProvider = "LibreHardwareMonitor",
    HardwareProviderVersion = "0.9.6.0",
    HardwareCapturedAtUnixMilliseconds =
        new DateTimeOffset(
            2026,
            7,
            30,
            13,
            32,
            10,
            TimeSpan.Zero).ToUnixTimeMilliseconds(),
    GpuName = "AMD Radeon RX 5700 XT",
    CpuName = "AMD Ryzen 9 5950X",
    GpuMetrics =
    [
        Metric("LOAD", "92%", 0.92),
        Metric("VRAM", "46%", 0.46),
        Metric("HOT", "84°", 84d / 110),
        Metric("PWR", "176W", 176d / 350),
        Metric("CLK", "1.9G", 1_900d / 3_000)
    ],
    CpuMetrics =
    [
        Metric("LOAD", "34%", 0.34),
        Metric("CMOS", "3.12V", 3.12 / 3.6),
        Metric("TEMP", "63°", 0.63),
        Metric("PWR", "74W", 74d / 142),
        Metric("CLK", "4.5G", 4_500d / 5_050)
    ],
    PhysicalMemorySummary = "RAM  32.0 / 64.0 GB",
    VirtualMemorySummary = "VIRT  40.0 / 96.0 GB"
};

static GOverlayHardwareMetricState Metric(
    string label,
    string value,
    double level) =>
    new()
    {
        Label = label,
        DisplayValue = value,
        Level = level,
        IsAvailable = true
    };

static async Task<GOverlayDashboardState> CaptureAsync(string pipeName)
{
    await using var pipe = new NamedPipeClientStream(
        ".",
        pipeName,
        PipeDirection.In,
        PipeOptions.Asynchronous);
    await pipe.ConnectAsync(5_000);
    return GOverlayBridgeCodec.ReadFrame(pipe);
}
