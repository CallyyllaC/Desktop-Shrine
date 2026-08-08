using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using DesktopShrine.Plugin.GOverlay.Layout;
using DesktopShrine.Plugin.GOverlay.LegacySdk;
using DesktopShrine.Storage;
using GOverlayPlugin.Interfaces;
using Xunit;

namespace DesktopShrine.GOverlay.Legacy.Tests;

public sealed class GOverlayArtworkTransferModeTests : IDisposable
{
    private readonly string tracePath = Path.Combine(
        Path.GetTempPath(),
        "DesktopShrineGOverlayArtworkTests",
        Guid.NewGuid().ToString("N"),
        "trace.log");

    public GOverlayArtworkTransferModeTests()
    {
        Environment.SetEnvironmentVariable(
            DesktopShrinePaths.GOverlayTraceEnvironmentVariable,
            tracePath);
        GOverlayLcdCommandTrace.ResetForTests();
    }

    [Fact]
    public void DontUseDrawPixelsTrueUsesOnlyRectanglesForArtwork()
    {
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var scene = ArtworkScene(CreateArtwork());
        var state = new GOverlayDashboardState
        {
            DontUseDrawPixels = true
        };
        GOverlayLcdCommandTrace.EnsureInitialized(host);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            renderer.Render(host, scene, state, 1, () => true);
            return host.Messages.Contains(
                "Desktop Shrine - artwork rectangle transfer complete");
        }, TimeSpan.FromSeconds(10)));

        Assert.Equal(0, host.PixelCallCount);
        Assert.True(host.RectangleCallCount > 0);
        Assert.Contains(
            host.Messages,
            message => message.Contains("ARTWORK RECTCOMP PLAN")
                && message.Contains("stages=5")
                && message.Contains("stageTargets=150,400,900,1400")
                && message.Contains("stage5Budget=400")
                && message.Contains("minBlockStage5=1")
                && message.Contains("minBlock=2"));
        for (var stage = 1; stage <= 4; stage++)
            Assert.Contains(
                host.Messages,
                message => message.Contains($"artwork stage {stage}/5"));
        Assert.Contains(
            host.Messages,
            message => message.Contains("artwork stage 5/5")
                || message.Contains("artwork stage 5 skipped"));
        GOverlayLcdCommandTrace.ResetForTests();
        var trace = File.ReadAllText(tracePath);
        Assert.Contains(
            "region=Artwork op=LCDSys2_Draw_Rectangle",
            trace);
        Assert.DoesNotContain(
            "region=Artwork op=LCDSys2_Draw_Pixels",
            trace);
    }

    [Fact]
    public void DontUseDrawPixelsFalseRetainsOriginalPixelPath()
    {
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var scene = ArtworkScene(CreateArtwork());
        var state = new GOverlayDashboardState
        {
            DontUseDrawPixels = false
        };
        GOverlayLcdCommandTrace.EnsureInitialized(host);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            renderer.Render(host, scene, state, 1, () => true);
            return host.PixelCallCount > 0;
        }, TimeSpan.FromSeconds(10)));

        Assert.True(host.PixelCallCount > 0);
        Assert.DoesNotContain(
            host.Messages,
            message => message.Contains("ARTWORK RECTCOMP"));
        GOverlayLcdCommandTrace.ResetForTests();
        Assert.Contains(
            "region=Artwork op=LCDSys2_Draw_Pixels",
            File.ReadAllText(tracePath));
    }

    [Fact]
    public void FlatArtworkSkipsStageFiveWithoutDrawPixels()
    {
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var scene = ArtworkScene(CreateSolidArtwork());
        var state = new GOverlayDashboardState
        {
            DontUseDrawPixels = true
        };
        GOverlayLcdCommandTrace.EnsureInitialized(host);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            renderer.Render(host, scene, state, 1, () => true);
            return host.Messages.Contains(
                "Desktop Shrine - artwork rectangle transfer complete");
        }, TimeSpan.FromSeconds(10)));

        Assert.Contains(
            host.Messages,
            message => message.Contains(
                "artwork stage 5 skipped reason=remaining-error-below-threshold"));
        Assert.Contains(
            host.Messages,
            message => message.Contains("stage5Eligible=false")
                && message.Contains("stage5Rectangles=0"));
        Assert.Equal(0, host.PixelCallCount);
    }

    [Fact]
    public void ArtworkChangeAbandonsPendingStageFive()
    {
        const int dimension = 80;
        const string firstKey = "test-artwork-a";
        const string secondKey = "test-artwork-b";
        var host = new RecordingHost();
        var renderer = new GOverlaySdkSceneRenderer();
        var firstScene = ArtworkScene(
            CreateArtwork(dimension, dimension, 41),
            firstKey,
            dimension,
            dimension);
        var secondScene = ArtworkScene(
            CreateArtwork(dimension, dimension, 73),
            secondKey,
            dimension,
            dimension);
        var state = new GOverlayDashboardState
        {
            DontUseDrawPixels = true
        };
        var firstId = GOverlayLcdCommandTrace.ShortIdentifier(firstKey);
        var secondId = GOverlayLcdCommandTrace.ShortIdentifier(secondKey);
        GOverlayLcdCommandTrace.EnsureInitialized(host);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            renderer.Render(host, firstScene, state, 1, () => true);
            return host.Messages.Any(message =>
                message.Contains("artwork stage 4/5")
                && message.Contains($"artworkId={firstId}"));
        }, TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(
            host.Messages,
            message => message.Contains("artwork stage 5/5")
                && message.Contains($"artworkId={firstId}"));

        var switchMessageIndex = host.Messages.Count;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            renderer.Render(host, secondScene, state, 1, () => true);
            return host.Messages.Any(message =>
                message.Contains("artwork stage 1/5")
                && message.Contains($"artworkId={secondId}"));
        }, TimeSpan.FromSeconds(10)));

        var afterSwitch = host.Messages.Skip(switchMessageIndex).ToArray();
        Assert.Contains(
            afterSwitch,
            message => message.Contains("abandoned artwork refinement")
                && message.Contains($"artworkId={firstId}")
                && message.Contains("completedStages=4/5"));
        Assert.DoesNotContain(
            afterSwitch,
            message => message.Contains("artwork stage")
                && message.Contains($"artworkId={firstId}"));
        Assert.Equal(0, host.PixelCallCount);
    }

    public void Dispose()
    {
        GOverlayLcdCommandTrace.ResetForTests();
        Environment.SetEnvironmentVariable(
            DesktopShrinePaths.GOverlayTraceEnvironmentVariable,
            null);
        var directory = Path.GetDirectoryName(tracePath)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static GOverlayDashboardScene ArtworkScene(
        byte[] artwork,
        string artworkKey = "test-artwork",
        int width = 19,
        int height = 17)
    {
        var emptyBounds = new GOverlayRectangle(0, 0, 1, 1);
        var empty = Array.Empty<GOverlayDrawCommand>();
        return new(
            new GOverlayRegionScene("header", emptyBounds, empty),
            new GOverlayRegionScene(
                "content",
                new GOverlayRectangle(0, 0, width, height),
                new GOverlayDrawCommand[]
                {
                    new GOverlayArtworkCommand(
                        "artwork",
                        new GOverlayRectangle(7, 11, width, height),
                        artworkKey,
                        "image/png",
                        artwork,
                        new GOverlayColour(0, 0, 0),
                        new GOverlayColour(255, 255, 255))
                }),
            new GOverlayRegionScene("footer", emptyBounds, empty));
    }

    private static byte[] CreateArtwork(
        int width = 19,
        int height = 17,
        int seed = 0)
    {
        using var bitmap = new Bitmap(width, height);
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        (x * 31 + y * 7 + seed * 3) % 256,
                        (x * 11 + y * 23 + seed * 5) % 256,
                        (x * 3 + y * 29 + seed * 7) % 256));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] CreateSolidArtwork()
    {
        using var bitmap = new Bitmap(19, 17);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.Clear(Color.MediumPurple);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private sealed class RecordingHost : IHost
    {
        public List<string> Messages { get; } = [];
        public int RectangleCallCount { get; private set; }
        public int PixelCallCount { get; private set; }

        public void DebugMessage(string message) => Messages.Add(message);
        public object AccessHost(object one, object two, object three, object four) => new();
        public object setForm(object form) => form;
        public bool LCDSys2_Draw_Rectangle(int x1, int y1, int x2, int y2, int colour, int thickness, int rounded)
        {
            RectangleCallCount++;
            return true;
        }
        public bool LCDSys2_Draw_Text_Font(int x, int y, string text, int width, int foreground, int background, string font, int transparent, int alignment, string reserved, int reserveSpace) => true;
        public bool LCDSys2_Draw_Pixels(ArrayList pixels)
        {
            PixelCallCount++;
            return true;
        }
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
