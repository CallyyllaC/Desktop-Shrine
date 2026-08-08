using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using DesktopShrine.Plugin.GOverlay.Layout;
using DesktopShrine.Plugin.GOverlay.LegacySdk;
using GOverlayPlugin.Interfaces;
using Xunit;

namespace DesktopShrine.GOverlay.Legacy.Tests;

public sealed class GOverlayRenderSchedulingTests
{
    [Fact]
    public void MeaningfulWorkHasDistinctSemanticPriority()
    {
        var priorities = Enum.GetValues(typeof(GOverlayRenderWorkPriority))
            .Cast<GOverlayRenderWorkPriority>()
            .ToArray();

        Assert.Equal(priorities.Length, priorities.Distinct().Count());
        Assert.True(
            GOverlayRenderWorkPriority.RecoveryOrGenerationChange
                < GOverlayRenderWorkPriority.PrimaryState);
        Assert.True(
            GOverlayRenderWorkPriority.PrimaryState
                < GOverlayRenderWorkPriority.ArtworkStage1);
        Assert.True(
            GOverlayRenderWorkPriority.ArtworkStage1
                < GOverlayRenderWorkPriority.SecondaryState);
        Assert.True(
            GOverlayRenderWorkPriority.SecondaryState
                < GOverlayRenderWorkPriority.HardwareTelemetry);
        Assert.True(
            GOverlayRenderWorkPriority.HardwareTelemetry
                < GOverlayRenderWorkPriority.Progress);
        Assert.True(
            GOverlayRenderWorkPriority.Progress
                < GOverlayRenderWorkPriority.LiveVisual);
        Assert.True(
            GOverlayRenderWorkPriority.LiveVisual
                < GOverlayRenderWorkPriority.ArtworkStage2);
        Assert.True(
            GOverlayRenderWorkPriority.ArtworkStage2
                < GOverlayRenderWorkPriority.ArtworkStage3);
        Assert.True(
            GOverlayRenderWorkPriority.ArtworkStage3
                < GOverlayRenderWorkPriority.ArtworkStage4);
        Assert.True(
            GOverlayRenderWorkPriority.ArtworkStage4
                < GOverlayRenderWorkPriority.ArtworkStage5);
    }

    [Fact]
    public void CommandPolicySeparatesLargeAndContinuousWork()
    {
        var title = Text("content.title");
        var duration = Text("footer.duration");
        var elapsed = Text("footer.elapsed");
        var hardware = Text("hardware.gpu.0.text");

        Assert.Equal(
            GOverlayRenderWorkPriority.PrimaryState,
            GOverlayRenderWorkPolicy.ForCommand(
                title,
                GOverlayDashboardMode.Media,
                fullRedraw: false));
        Assert.Equal(
            GOverlayRenderWorkPriority.SecondaryState,
            GOverlayRenderWorkPolicy.ForCommand(
                duration,
                GOverlayDashboardMode.Media,
                fullRedraw: false));
        Assert.Equal(
            GOverlayRenderWorkPriority.Progress,
            GOverlayRenderWorkPolicy.ForCommand(
                elapsed,
                GOverlayDashboardMode.Media,
                fullRedraw: false));
        Assert.Equal(
            GOverlayRenderWorkPriority.HardwareTelemetry,
            GOverlayRenderWorkPolicy.ForCommand(
                hardware,
                GOverlayDashboardMode.Hardware,
                fullRedraw: false));
        Assert.Equal(
            GOverlayRenderWorkPriority.RecoveryOrGenerationChange,
            GOverlayRenderWorkPolicy.ForCommand(
                duration,
                GOverlayDashboardMode.Media,
                fullRedraw: true));
    }

    [Fact]
    public void BridgeRetainsOnlyNewestAudioSample()
    {
        using var bridge = new GOverlayBridgeClient(
            "DesktopShrine.GOverlay.Tests." + Guid.NewGuid().ToString("N"));
        bridge.Accept(StateWithWaterfall(100, 0.10f));
        bridge.Accept(StateWithWaterfall(101, 0.90f));

        var latest = bridge.ForDisplay;
        Assert.True(latest.HasWaterfallColumn);
        Assert.Equal(101, latest.WaterfallColumnSequence);
        Assert.Equal(0.90f, latest.WaterfallBands[0]);

        bridge.AcknowledgeWaterfall(7, 100);
        Assert.Equal(101, bridge.ForDisplay.WaterfallColumnSequence);
        Assert.True(bridge.ForDisplay.HasWaterfallColumn);

        bridge.AcknowledgeWaterfall(7, 101);
        Assert.False(bridge.ForDisplay.HasWaterfallColumn);
    }

    [Fact]
    public void BridgeRetainsOnlyNewestProgressAndHardwareState()
    {
        using var bridge = new GOverlayBridgeClient(
            "DesktopShrine.GOverlay.Tests." + Guid.NewGuid().ToString("N"));
        bridge.Accept(StateWithLiveValues(10, 97, "41%"));
        bridge.Accept(StateWithLiveValues(11, 98, "57%"));

        var latest = bridge.ForDisplay;
        Assert.Equal(11, latest.Revision);
        Assert.Equal(98, latest.PositionSeconds);
        Assert.Equal("57%", latest.GpuMetrics[0].DisplayValue);
    }

    [Fact]
    public void HardwareTelemetryChangeUsesIncrementalTelemetryPriority()
    {
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var layout = new GOverlayDashboardLayout();
        var first = HardwareState(1, "41%", 0.41);
        renderer.Render(
            host,
            layout.Compose(first, DateTime.UtcNow),
            first,
            1,
            () => true);

        var before = host.Messages.Count;
        var second = HardwareState(2, "57%", 0.57);
        renderer.Render(
            host,
            layout.Compose(second, DateTime.UtcNow),
            second,
            1,
            () => true);

        var messages = host.Messages.Skip(before).ToArray();
        Assert.Contains(
            messages,
            message => message.Contains("selected=HardwareTelemetry")
                && message.Contains("commands=2"));
        Assert.DoesNotContain(
            messages,
            message => message.Contains(
                "selected=RecoveryOrGenerationChange"));
    }

    [Fact]
    public void LiveUpdatesCannotStarveStageOneAndPrecedeStageTwo()
    {
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var noArtwork = Array.Empty<byte>();
        var artwork = CreateArtwork(64, 64);
        var revision = 1L;

        renderer.Render(
            host,
            MixedScene(noArtwork, string.Empty, revision),
            LiveState(revision),
            1,
            () => true);

        string[]? stageOnePass = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            revision++;
            var before = host.Messages.Count;
            renderer.Render(
                host,
                MixedScene(artwork, "artwork-a", revision),
                LiveState(revision),
                1,
                () => true);
            var currentPass = host.Messages.Skip(before).ToArray();
            if (!currentPass.Any(message =>
                    message.Contains("artwork stage 1/5")))
                return false;
            stageOnePass = currentPass;
            return true;
        }, TimeSpan.FromSeconds(10)));

        AssertSelectionOrder(
            stageOnePass!,
            "selected=PrimaryState",
            "selected=ArtworkStage1",
            "selected=Progress",
            "selected=LiveVisual");

        string[]? stageTwoPass = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            revision++;
            var before = host.Messages.Count;
            renderer.Render(
                host,
                MixedScene(artwork, "artwork-a", revision),
                LiveState(revision),
                1,
                () => true);
            var currentPass = host.Messages.Skip(before).ToArray();
            if (!currentPass.Any(message =>
                    message.Contains("artwork stage 2/5")))
                return false;
            stageTwoPass = currentPass;
            return true;
        }, TimeSpan.FromSeconds(10)));

        AssertSelectionOrder(
            stageTwoPass!,
            "selected=PrimaryState",
            "selected=Progress",
            "selected=LiveVisual",
            "selected=ArtworkStage2");
    }

    [Fact]
    public void ModeGenerationChangeDiscardsRefinementAndRunsCriticalStateFirst()
    {
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var artwork = CreateArtwork(64, 64);
        var revision = 1L;

        Assert.True(SpinWait.SpinUntil(() =>
        {
            renderer.Render(
                host,
                MixedScene(artwork, "old-artwork", revision++),
                LiveState(revision),
                1,
                () => true);
            return host.Messages.Any(message =>
                message.Contains("artwork stage 1/5"));
        }, TimeSpan.FromSeconds(10)));

        renderer.ResetForFullRedraw("dashboard-mode-change");
        var switchMessageIndex = host.Messages.Count;
        var hardwareState = new GOverlayDashboardState
        {
            Revision = revision,
            RenderGeneration = 99,
            Mode = GOverlayDashboardMode.Hardware
        };
        var hardwareScene = new GOverlayDashboardLayout().Compose(
            hardwareState,
            DateTime.UtcNow);
        renderer.Render(
            host,
            hardwareScene,
            hardwareState,
            1,
            () => true);

        var afterSwitch = host.Messages.Skip(switchMessageIndex).ToArray();
        Assert.Contains(
            afterSwitch,
            message => message.Contains(
                "selected=RecoveryOrGenerationChange"));
        Assert.DoesNotContain(
            afterSwitch,
            message => message.Contains("artwork stage"));
    }

    [Fact]
    public void DeviceGenerationChangeInvalidatesPendingPhysicalState()
    {
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var first = LiveState(1);
        var second = LiveState(2);
        var firstScene = MixedScene([], string.Empty, 1);
        var secondScene = MixedScene([], string.Empty, 2);
        renderer.Render(
            host,
            firstScene,
            first,
            1,
            () => true,
            new GOverlayUsbConnectionSnapshot(true, 1, DateTime.UtcNow));

        var before = host.Messages.Count;
        renderer.Render(
            host,
            secondScene,
            second,
            1,
            () => true,
            new GOverlayUsbConnectionSnapshot(true, 2, DateTime.UtcNow));

        var messages = host.Messages.Skip(before).ToArray();
        Assert.Contains(
            messages,
            message => message.Contains("device generation changed"));
        Assert.Contains(
            messages,
            message => message.Contains(
                "selected=RecoveryOrGenerationChange"));
    }

    private static GOverlayDashboardState StateWithWaterfall(
        long sequence,
        float value) =>
        new()
        {
            Revision = sequence,
            WaterfallResetSequence = 7,
            WaterfallColumnSequence = sequence,
            WaterfallWritePosition = (int)(sequence % 10),
            HasWaterfallColumn = true,
            WaterfallBands = Enumerable.Repeat(value, 8).ToArray()
        };

    private static GOverlayDashboardState StateWithLiveValues(
        long revision,
        double position,
        string hardwareValue) =>
        new()
        {
            Revision = revision,
            PositionSeconds = position,
            DurationSeconds = 200,
            GpuMetrics =
            [
                new GOverlayHardwareMetricState
                {
                    Label = "LOAD",
                    DisplayValue = hardwareValue,
                    IsAvailable = true
                }
            ]
        };

    private static GOverlayDashboardState LiveState(long revision) =>
        new()
        {
            Revision = revision,
            RenderGeneration = 42,
            DontUseDrawPixels = true,
            Mode = GOverlayDashboardMode.Media
        };

    private static GOverlayDashboardState HardwareState(
        long revision,
        string displayValue,
        double level) =>
        new()
        {
            Revision = revision,
            RenderGeneration = 77,
            Mode = GOverlayDashboardMode.Hardware,
            GpuName = "GPU",
            CpuName = "CPU",
            GpuMetrics =
            [
                new GOverlayHardwareMetricState
                {
                    Label = "LOAD",
                    DisplayValue = displayValue,
                    Level = level,
                    IsAvailable = true
                }
            ]
        };

    private static GOverlayDashboardScene MixedScene(
        byte[] artwork,
        string artworkKey,
        long revision)
    {
        var bounds = new GOverlayRectangle(0, 0, 64, 64);
        return new(
            new GOverlayRegionScene(
                "header",
                bounds,
                Array.Empty<GOverlayDrawCommand>()),
            new GOverlayRegionScene(
                "content",
                bounds,
                new GOverlayDrawCommand[]
                {
                    new GOverlayTextCommand(
                        "content.title",
                        bounds,
                        "Track " + revision,
                        12,
                        new GOverlayColour(255, 255, 255),
                        new GOverlayColour(0, 0, 0)),
                    new GOverlayArtworkCommand(
                        "content.artwork",
                        bounds,
                        artworkKey,
                        "image/png",
                        artwork,
                        new GOverlayColour(0, 0, 0),
                        new GOverlayColour(255, 255, 255)),
                    new GOverlayWaterfallCommand(
                        "content.waterfall",
                        new GOverlayRectangle(0, 0, 64, 64),
                        7,
                        revision,
                        (int)(revision % 16),
                        4,
                        true,
                        Enumerable.Repeat(0.5f, 8).ToArray(),
                        false,
                        new GOverlayColour(0, 0, 0),
                        new GOverlayColour(0, 0, 20),
                        new GOverlayColour(0, 20, 80),
                        new GOverlayColour(0, 200, 220),
                        new GOverlayColour(255, 255, 255))
                }),
            new GOverlayRegionScene(
                "footer",
                bounds,
                new GOverlayDrawCommand[]
                {
                    new GOverlayTextCommand(
                        "footer.elapsed",
                        bounds,
                        revision.ToString(),
                        12,
                        new GOverlayColour(255, 255, 255),
                        new GOverlayColour(0, 0, 0)),
                    new GOverlayProgressCommand(
                        "footer.progress",
                        bounds,
                        revision % 100 / 100d,
                        new GOverlayColour(0, 200, 220),
                        new GOverlayColour(0, 0, 0)),
                    new GOverlayTextCommand(
                        "footer.duration",
                        bounds,
                        "3:20",
                        12,
                        new GOverlayColour(255, 255, 255),
                        new GOverlayColour(0, 0, 0))
                }));
    }

    private static GOverlayTextCommand Text(string key) =>
        new(
            key,
            new GOverlayRectangle(0, 0, 10, 10),
            key,
            10,
            new GOverlayColour(255, 255, 255),
            new GOverlayColour(0, 0, 0));

    private static void AssertSelectionOrder(
        IReadOnlyList<string> messages,
        params string[] selections)
    {
        var previous = -1;
        foreach (var selection in selections)
        {
            var index = messages
                .Select((message, position) => (message, position))
                .Where(item => item.message.Contains(selection))
                .Select(item => item.position)
                .DefaultIfEmpty(-1)
                .First();
            Assert.True(
                index > previous,
                $"Expected {selection} after index {previous}. Messages: {string.Join(" | ", messages)}");
            previous = index;
        }
    }

    private static byte[] CreateArtwork(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        (x * 31 + y * 7) % 256,
                        (x * 11 + y * 23) % 256,
                        (x * 3 + y * 29) % 256));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private sealed class RecordingHost : IHost
    {
        public List<string> Messages { get; } = [];

        public void DebugMessage(string message) => Messages.Add(message);
        public object AccessHost(object one, object two, object three, object four) => new();
        public object setForm(object form) => form;
        public bool LCDSys2_Draw_Rectangle(int x1, int y1, int x2, int y2, int colour, int thickness, int rounded) => true;
        public bool LCDSys2_Draw_Text_Font(int x, int y, string text, int width, int foreground, int background, string font, int transparent, int alignment, string reserved, int reserveSpace) => true;
        public bool LCDSys2_Draw_Pixels(ArrayList pixels) => true;
        public bool LCDSys2_Draw_Lines(ArrayList points, bool continuous, int colour) => true;
        public bool LCDSys2_brightness(int value) => true;
        public bool LCDSys2_brightnessidle(int value) => true;
        public bool LCDSys2_Clear_Screen(int colour) => true;
        public bool LCDSys2_Draw_Circle(int x, int y, int radius, int colour, int thickness) => true;
        public bool LCDSys2_Draw_Corner(int x, int y, int width, int height, int colour, int corner) => true;
        public bool LCDSys2_Draw_Icon(int x, int y, string icon) => true;
        public bool LCDSys2_Draw_progress_bar(int x, int y, int width, int height, int value, int maximum, int foreground, int background) => true;
        public bool LCDSys2_Draw_Shape(int one, int two, int three, int four, int five, int six, int seven, int eight, int nine, int ten) => true;
        public bool LCDSys2_Draw_Triangle(int x1, int y1, int x2, int y2, int x3, int y3, int colour, int thickness) => true;
        public bool LCDSys2_Flash_Screen(int colour) => true;
        public bool LCDSys2_getpixel(int x, int y) => true;
        public bool LCDSys2_readTable() => true;
        public bool LCDSys2_SetBackground(int colour) => true;
        public bool LCDSys2_SetCharacterSpaceFont(int spacing) => true;
    }
}
