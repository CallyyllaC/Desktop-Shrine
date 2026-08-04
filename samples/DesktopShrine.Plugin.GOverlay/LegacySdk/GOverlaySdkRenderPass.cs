using System.Collections;
using System.Diagnostics;
using DesktopShrine.Plugin.GOverlay.Layout;
using GOverlayPlugin.Interfaces;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal sealed class GOverlaySdkRenderPass
{
    private readonly IHost host;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly int maximumCommands;
    private readonly int maximumMilliseconds;
    private string region = "pass";
    private bool budgetLogged;

    public GOverlaySdkRenderPass(
        IHost host,
        long id,
        bool compatibilityMode,
        int maximumCommands,
        int maximumMilliseconds)
    {
        this.host = host;
        Id = id;
        this.maximumCommands = compatibilityMode
            ? Math.Max(1, maximumCommands)
            : int.MaxValue;
        this.maximumMilliseconds = compatibilityMode
            ? Math.Max(1, maximumMilliseconds)
            : int.MaxValue;
    }

    public long Id { get; }
    public int CommandCount { get; private set; }
    public long ElapsedMilliseconds => elapsed.ElapsedMilliseconds;
    public bool BudgetExhausted { get; private set; }

    public IDisposable BeginRegion(string value)
    {
        var previous = region;
        var startCommands = CommandCount;
        var startMilliseconds = ElapsedMilliseconds;
        var startedExhausted = BudgetExhausted;
        region = value;
        return new RegionScope(() =>
        {
            if (CommandCount != startCommands
                || BudgetExhausted != startedExhausted)
                host.DebugMessage(
                    $"Desktop Shrine - render pass {Id} region={value} commands={CommandCount - startCommands} elapsedMs={ElapsedMilliseconds - startMilliseconds} thread={Environment.CurrentManagedThreadId} insideDisplayOnLCD=true");
            region = previous;
        });
    }

    public bool CanIssue(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (CommandCount + count <= maximumCommands
            && ElapsedMilliseconds < maximumMilliseconds)
            return true;

        BudgetExhausted = true;
        if (!budgetLogged)
        {
            budgetLogged = true;
            host.DebugMessage(
                $"Desktop Shrine - render pass {Id} budget exhausted region={region} commands={CommandCount}/{maximumCommands} elapsedMs={ElapsedMilliseconds}/{maximumMilliseconds}; dirty work deferred");
        }
        return false;
    }

    public void Rectangle(
        GOverlayRectangle bounds,
        GOverlayColour colour,
        int thickness = 0)
        => Invoke(
            "LCDSys2_Draw_Rectangle",
            () => host.LCDSys2_Draw_Rectangle(
                bounds.X,
                bounds.Y,
                bounds.Right - 1,
                bounds.Bottom - 1,
                colour.Rgb565,
                thickness,
                0));

    public void Text(
        GOverlayTextCommand text,
        string font)
        => Invoke(
            "LCDSys2_Draw_Text_Font",
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

    public void Pixels(ArrayList pixels)
        => Invoke(
            "LCDSys2_Draw_Pixels",
            () => host.LCDSys2_Draw_Pixels(pixels));

    public void Lines(ArrayList points, int colour)
        => Invoke(
            "LCDSys2_Draw_Lines",
            () => host.LCDSys2_Draw_Lines(points, true, colour));

    public void Complete(string status)
    {
        elapsed.Stop();
        host.DebugMessage(
            $"Desktop Shrine - render pass {Id} end status={status} commands={CommandCount} elapsedMs={ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId} insideDisplayOnLCD=true");
    }

    private void Invoke(string operation, Func<bool> action)
    {
        if (!CanIssue(1))
            throw new InvalidOperationException(
                "A physical command was attempted after its budget check failed.");

        bool succeeded;
        try
        {
            succeeded = action();
        }
        catch (Exception error)
        {
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

    private sealed class RegionScope(Action dispose) : IDisposable
    {
        private Action? action = dispose;

        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
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
