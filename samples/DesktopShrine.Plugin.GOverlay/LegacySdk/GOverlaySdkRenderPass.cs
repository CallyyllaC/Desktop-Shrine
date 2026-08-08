using System.Collections;
using System.Diagnostics;
using System.Text;
using DesktopShrine.Plugin.GOverlay.Layout;
using GOverlayPlugin.Interfaces;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal sealed class GOverlaySdkRenderPass
{
    private readonly IHost host;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private string region = "pass";
    private long estimatedBytes;

    public GOverlaySdkRenderPass(IHost host, long id)
    {
        this.host = host;
        Id = id;
    }

    public long Id { get; }
    public int CommandCount { get; private set; }
    public long ElapsedMilliseconds => elapsed.ElapsedMilliseconds;
    public long LastCommandSequence { get; private set; }

    public void Start(string details)
        => GOverlayLcdCommandTrace.RenderStart(
            Id,
            Environment.CurrentManagedThreadId,
            "DesktopShrineDashboard",
            details);

    public IDisposable BeginRegion(string value)
    {
        var previous = region;
        var startCommands = CommandCount;
        var startMilliseconds = ElapsedMilliseconds;
        region = value;
        return new RegionScope(() =>
        {
            if (CommandCount != startCommands)
                host.DebugMessage(
                    $"Desktop Shrine - render pass {Id} region={value} commands={CommandCount - startCommands} elapsedMs={ElapsedMilliseconds - startMilliseconds} thread={Environment.CurrentManagedThreadId} insideDisplayOnLCD=true");
            region = previous;
        });
    }

    public IDisposable UseRegion(string value)
    {
        var previous = region;
        region = value;
        return new RegionScope(() => region = previous);
    }

    public void Rectangle(
        GOverlayRectangle bounds,
        GOverlayColour colour,
        int thickness = 0)
    {
        var coordinates = GOverlaySdkRectangleCoordinates.From(bounds);
        Invoke(
            "LCDSys2_Draw_Rectangle",
            $"x1={coordinates.X1} y1={coordinates.Y1} x2={coordinates.X2Exclusive} y2={coordinates.Y2Exclusive} endpointMode=exclusive pixelX2Inclusive={coordinates.PixelX2Inclusive} pixelY2Inclusive={coordinates.PixelY2Inclusive} width={bounds.Width} height={bounds.Height} colourRgb565={colour.Rgb565} thickness={thickness} rounded=0 filled={thickness == 0}",
            14,
            () => host.LCDSys2_Draw_Rectangle(
                coordinates.X1,
                coordinates.Y1,
                coordinates.X2Exclusive,
                coordinates.Y2Exclusive,
                colour.Rgb565,
                thickness,
                0));
    }

    public void Text(
        GOverlayTextCommand text,
        string font)
        => Invoke(
            "LCDSys2_Draw_Text_Font",
            $"x={text.Bounds.X} y={text.Bounds.Y} width={text.Bounds.Width} height={text.Bounds.Height} font={GOverlayLcdCommandTrace.Quote(font, 80)} foregroundRgb565={text.Colour.Rgb565} backgroundRgb565={text.Background.Rgb565} transparent=0 alignment={(int)text.Alignment} reserveSpace=0 textLength={text.Text.Length} text={GOverlayLcdCommandTrace.Quote(text.Text, 96)}",
            20 + Encoding.UTF8.GetByteCount(text.Text ?? string.Empty),
            () => host.LCDSys2_Draw_Text_Font(
                text.Bounds.X,
                text.Bounds.Y,
                text.Text,
                text.Bounds.Width,
                text.Colour.Rgb565,
                text.Background.Rgb565,
                font,
                0,
                (int)text.Alignment,
                string.Empty,
                0));

    public void Pixels(ArrayList pixels, string context)
        => Invoke(
            "LCDSys2_Draw_Pixels",
            PixelDetails(pixels, context),
            pixels.Count * 6L,
            () => host.LCDSys2_Draw_Pixels(pixels));

    public void Lines(ArrayList points, int colour)
        => Invoke(
            "LCDSys2_Draw_Lines",
            LineDetails(points, colour),
            points.Count * 6L + 4,
            () => host.LCDSys2_Draw_Lines(points, true, colour));

    public void Complete(string status)
    {
        elapsed.Stop();
        GOverlayLcdCommandTrace.RenderEnd(
            Id,
            Environment.CurrentManagedThreadId,
            status,
            CommandCount,
            estimatedBytes,
            elapsed.ElapsedTicks,
            LastCommandSequence);
        host.DebugMessage(
            $"Desktop Shrine - render pass {Id} end status={status} commands={CommandCount} elapsedMs={ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId} insideDisplayOnLCD=true");
    }

    private void Invoke(
        string operation,
        string details,
        long estimatedOperationBytes,
        Func<bool> action)
    {
        var token = GOverlayLcdCommandTrace.Before(
            Id,
            region,
            operation,
            details,
            estimatedOperationBytes);
        LastCommandSequence = token.Sequence;
        estimatedBytes += estimatedOperationBytes;

        bool succeeded;
        try
        {
            succeeded = action();
            GOverlayLcdCommandTrace.After(token, succeeded);
        }
        catch (Exception error)
        {
            GOverlayLcdCommandTrace.Error(token, error);
            CommandCount++;
            throw new GOverlayDeviceCommandException(
                operation,
                region,
                error);
        }

        CommandCount++;
        if (!succeeded)
            throw new GOverlayDeviceCommandException(operation, region);
    }

    private static string PixelDetails(ArrayList pixels, string context)
    {
        try
        {
            var minimumX = int.MaxValue;
            var minimumY = int.MaxValue;
            var maximumX = int.MinValue;
            var maximumY = int.MinValue;
            string first = "none";
            string last = "none";
            for (var index = 0; index < pixels.Count; index++)
            {
                if (pixels[index] is not ArrayList pixel || pixel.Count < 3)
                    continue;
                var x = Convert.ToInt32(pixel[0]);
                var y = Convert.ToInt32(pixel[1]);
                var colour = Convert.ToInt32(pixel[2]);
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
                var description = $"{x},{y},{colour}";
                if (first == "none")
                    first = description;
                last = description;
            }

            var bounds = minimumX == int.MaxValue
                ? "bounds=none"
                : $"x={minimumX} y={minimumY} width={maximumX - minimumX + 1} height={maximumY - minimumY + 1}";
            return $"pixelCount={pixels.Count} rgb565Bytes={pixels.Count * 2L} {bounds} first={first} last={last} {context}";
        }
        catch (Exception error)
        {
            return $"pixelCount={pixels.Count} context={context} diagnosticParseError={GOverlayLcdCommandTrace.Quote(error.Message)}";
        }
    }

    private static string LineDetails(ArrayList points, int colour)
    {
        try
        {
            var descriptions = new List<string>();
            foreach (var item in points)
            {
                if (item is not ArrayList point || point.Count < 2)
                    continue;
                descriptions.Add(
                    Convert.ToInt32(point[0])
                    + ","
                    + Convert.ToInt32(point[1])
                    + (point.Count >= 3
                        ? "," + Convert.ToInt32(point[2])
                        : string.Empty));
            }
            return $"pointCount={points.Count} continuous=true forceColourRgb565={colour} points={GOverlayLcdCommandTrace.Quote(string.Join(";", descriptions), 160)}";
        }
        catch (Exception error)
        {
            return $"pointCount={points.Count} forceColourRgb565={colour} diagnosticParseError={GOverlayLcdCommandTrace.Quote(error.Message)}";
        }
    }

    private sealed class RegionScope(Action dispose) : IDisposable
    {
        private Action? action = dispose;

        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}

internal readonly struct GOverlaySdkRectangleCoordinates
{
    private GOverlaySdkRectangleCoordinates(
        int x1,
        int y1,
        int x2Exclusive,
        int y2Exclusive)
    {
        X1 = x1;
        Y1 = y1;
        X2Exclusive = x2Exclusive;
        Y2Exclusive = y2Exclusive;
    }

    public int X1 { get; }
    public int Y1 { get; }
    public int X2Exclusive { get; }
    public int Y2Exclusive { get; }
    public int PixelX2Inclusive => X2Exclusive - 1;
    public int PixelY2Inclusive => Y2Exclusive - 1;

    public static GOverlaySdkRectangleCoordinates From(
        GOverlayRectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                "LCD rectangles must have positive dimensions.");
        return new(
            bounds.X,
            bounds.Y,
            checked(bounds.X + bounds.Width),
            checked(bounds.Y + bounds.Height));
    }
}

internal sealed class GOverlayDeviceCommandException : Exception
{
    public GOverlayDeviceCommandException(
        string operation,
        string region,
        Exception? inner = null)
        : base(
            $"{operation} failed in render region {region}.",
            inner)
    {
        Operation = operation;
        Region = region;
    }

    public string Operation { get; }
    public string Region { get; }
}
