using System.Collections;
using DesktopShrine.Plugin.GOverlay.Layout;
using DesktopShrine.Plugin.GOverlay.LegacySdk;
using DesktopShrine.Storage;
using GOverlayPlugin.Interfaces;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DesktopShrine.GOverlay.Legacy.Tests;

public sealed class GOverlayLcdCommandTraceTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "DesktopShrineGOverlayTests",
        Guid.NewGuid().ToString("N"));

    public GOverlayLcdCommandTraceTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        GOverlayLcdCommandTrace.ResetForTests();
    }

    [Fact]
    public void InitializationCreatesDirectoryFileAndStartupLine()
    {
        var path = Path.Combine(
            temporaryDirectory,
            "missing",
            "trace.log");
        Environment.SetEnvironmentVariable(
            DesktopShrinePaths.GOverlayTraceEnvironmentVariable,
            path);
        var host = new RecordingHost();

        GOverlayLcdCommandTrace.EnsureInitialized(host);

        Assert.True(File.Exists(path));
        Assert.Contains("TRACE INITIALIZED", ReadTrace(path));
        Assert.Contains(
            host.Messages,
            message => message ==
                "Desktop Shrine - LCD command trace path: "
                + Path.GetFullPath(path));
    }

    [Fact]
    public void InitializationFailureIsReportedThroughTheHost()
    {
        Environment.SetEnvironmentVariable(
            DesktopShrinePaths.GOverlayTraceEnvironmentVariable,
            temporaryDirectory);
        var host = new RecordingHost();

        GOverlayLcdCommandTrace.EnsureInitialized(host);

        Assert.Contains(
            host.Messages,
            message => message.StartsWith(
                "Desktop Shrine - LCD command trace FAILED: ",
                StringComparison.Ordinal));
    }

    [Fact]
    public void PhysicalWrapperWritesBeforeAndAfterOnSuccess()
    {
        var path = ConfigureTracePath();
        var host = new RecordingHost();
        GOverlayLcdCommandTrace.EnsureInitialized(host);
        var pass = new GOverlaySdkRenderPass(host, 41);

        pass.Rectangle(
            new GOverlayRectangle(1, 2, 3, 4),
            new GOverlayColour(5, 6, 7));

        Assert.Equal(1, host.LastRectangleX1);
        Assert.Equal(2, host.LastRectangleY1);
        Assert.Equal(4, host.LastRectangleX2);
        Assert.Equal(6, host.LastRectangleY2);
        var contents = ReadTrace(path);
        Assert.Contains("LCDCMD BEFORE", contents);
        Assert.Contains("op=LCDSys2_Draw_Rectangle", contents);
        Assert.Contains("x1=1 y1=2 x2=4 y2=6", contents);
        Assert.Contains("endpointMode=exclusive", contents);
        Assert.Contains("pixelX2Inclusive=3 pixelY2Inclusive=5", contents);
        Assert.Contains("LCDCMD AFTER", contents);
        Assert.DoesNotContain("LCDCMD ERROR", contents);
    }

    [Fact]
    public void PhysicalWrapperWritesBeforeAndErrorOnException()
    {
        var path = ConfigureTracePath();
        var host = new RecordingHost { ThrowPhysicalCommands = true };
        GOverlayLcdCommandTrace.EnsureInitialized(host);
        var pass = new GOverlaySdkRenderPass(host, 42);

        Assert.Throws<GOverlayDeviceCommandException>(() => pass.Rectangle(
            new GOverlayRectangle(1, 2, 3, 4),
            new GOverlayColour(5, 6, 7)));

        var contents = ReadTrace(path);
        Assert.Contains("LCDCMD BEFORE", contents);
        Assert.Contains("LCDCMD ERROR", contents);
        Assert.DoesNotContain("LCDCMD AFTER", contents);
    }

    public void Dispose()
    {
        GOverlayLcdCommandTrace.ResetForTests();
        Environment.SetEnvironmentVariable(
            DesktopShrinePaths.GOverlayTraceEnvironmentVariable,
            null);
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, recursive: true);
    }

    private string ConfigureTracePath()
    {
        var path = Path.Combine(temporaryDirectory, "trace.log");
        Environment.SetEnvironmentVariable(
            DesktopShrinePaths.GOverlayTraceEnvironmentVariable,
            path);
        return path;
    }

    private static string ReadTrace(string path)
    {
        GOverlayLcdCommandTrace.ResetForTests();
        return File.ReadAllText(path);
    }

    private sealed class RecordingHost : IHost
    {
        public List<string> Messages { get; } = [];
        public bool ThrowPhysicalCommands { get; set; }
        public int LastRectangleX1 { get; private set; }
        public int LastRectangleY1 { get; private set; }
        public int LastRectangleX2 { get; private set; }
        public int LastRectangleY2 { get; private set; }

        public void DebugMessage(string message) => Messages.Add(message);
        public object AccessHost(object one, object two, object three, object four) => new();
        public object setForm(object form) => form;
        public bool LCDSys2_Draw_Rectangle(int x1, int y1, int x2, int y2, int colour, int thickness, int rounded)
        {
            LastRectangleX1 = x1;
            LastRectangleY1 = y1;
            LastRectangleX2 = x2;
            LastRectangleY2 = y2;
            return Physical();
        }
        public bool LCDSys2_Draw_Text_Font(int x, int y, string text, int width, int foreground, int background, string font, int transparent, int alignment, string reserved, int reserveSpace) => Physical();
        public bool LCDSys2_Draw_Pixels(ArrayList pixels) => Physical();
        public bool LCDSys2_Draw_Lines(ArrayList points, bool continuous, int colour) => Physical();
        public bool LCDSys2_brightness(int value) => Physical();
        public bool LCDSys2_brightnessidle(int value) => Physical();
        public bool LCDSys2_Clear_Screen(int colour) => Physical();
        public bool LCDSys2_Draw_Circle(int x, int y, int radius, int colour, int thickness) => Physical();
        public bool LCDSys2_Draw_Corner(int x, int y, int width, int height, int colour, int corner) => Physical();
        public bool LCDSys2_Draw_Icon(int x, int y, string icon) => Physical();
        public bool LCDSys2_Draw_progress_bar(int x, int y, int width, int height, int value, int maximum, int foreground, int background) => Physical();
        public bool LCDSys2_Draw_Shape(int one, int two, int three, int four, int five, int six, int seven, int eight, int nine, int ten) => Physical();
        public bool LCDSys2_Draw_Triangle(int x1, int y1, int x2, int y2, int x3, int y3, int colour, int thickness) => Physical();
        public bool LCDSys2_Flash_Screen(int colour) => Physical();
        public bool LCDSys2_getpixel(int x, int y) => Physical();
        public bool LCDSys2_readTable() => Physical();
        public bool LCDSys2_SetBackground(int colour) => Physical();
        public bool LCDSys2_SetCharacterSpaceFont(int spacing) => Physical();

        private bool Physical()
        {
            if (ThrowPhysicalCommands)
                throw new InvalidOperationException("simulated transport failure");
            return true;
        }
    }
}
