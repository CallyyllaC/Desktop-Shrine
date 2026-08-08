using DesktopShrine.Plugin.GOverlay.Layout;
using DesktopShrine.Plugin.GOverlay.LegacySdk;
using Xunit;

namespace DesktopShrine.GOverlay.Legacy.Tests;

public sealed class GOverlayRectangleCoordinateTests
{
    [Fact]
    public void AdjacentRectanglesCoverConsecutivePixelsWithoutAGap()
    {
        var first = GOverlaySdkRectangleCoordinates.From(
            new GOverlayRectangle(0, 3, 8, 5));
        var second = GOverlaySdkRectangleCoordinates.From(
            new GOverlayRectangle(8, 3, 8, 5));

        Assert.Equal(0, first.X1);
        Assert.Equal(8, first.X2Exclusive);
        Assert.Equal(7, first.PixelX2Inclusive);
        Assert.Equal(8, second.X1);
        Assert.Equal(16, second.X2Exclusive);
        Assert.Equal(15, second.PixelX2Inclusive);
        Assert.Equal(first.X2Exclusive, second.X1);
        Assert.Equal(first.PixelX2Inclusive + 1, second.X1);
    }

    [Fact]
    public void AdjacentRectanglesDoNotExceedTargetPixelBounds()
    {
        var target = new GOverlayRectangle(17, 23, 16, 10);
        var first = GOverlaySdkRectangleCoordinates.From(
            new GOverlayRectangle(17, 23, 8, 10));
        var second = GOverlaySdkRectangleCoordinates.From(
            new GOverlayRectangle(25, 23, 8, 10));

        Assert.Equal(target.Right - 1, second.PixelX2Inclusive);
        Assert.Equal(target.Bottom - 1, second.PixelY2Inclusive);
        Assert.True(first.PixelX2Inclusive < target.Right);
        Assert.True(second.PixelX2Inclusive < target.Right);
        Assert.True(first.PixelY2Inclusive < target.Bottom);
        Assert.True(second.PixelY2Inclusive < target.Bottom);
    }

    [Fact]
    public void OnePixelBlocksUseOnePixelExclusiveRanges()
    {
        var vertical = GOverlaySdkRectangleCoordinates.From(
            new GOverlayRectangle(11, 13, 1, 7));
        var horizontal = GOverlaySdkRectangleCoordinates.From(
            new GOverlayRectangle(19, 29, 9, 1));

        Assert.Equal(11, vertical.X1);
        Assert.Equal(12, vertical.X2Exclusive);
        Assert.Equal(11, vertical.PixelX2Inclusive);
        Assert.Equal(13, vertical.Y1);
        Assert.Equal(20, vertical.Y2Exclusive);
        Assert.Equal(19, vertical.PixelY2Inclusive);
        Assert.Equal(29, horizontal.Y1);
        Assert.Equal(30, horizontal.Y2Exclusive);
        Assert.Equal(29, horizontal.PixelY2Inclusive);
    }

    [Fact]
    public void InclusivePixelEndpointsEqualOriginPlusSizeMinusOne()
    {
        var bounds = new GOverlayRectangle(239, 101, 8, 6);

        var coordinates = GOverlaySdkRectangleCoordinates.From(bounds);

        Assert.Equal(bounds.X + bounds.Width - 1,
            coordinates.PixelX2Inclusive);
        Assert.Equal(bounds.Y + bounds.Height - 1,
            coordinates.PixelY2Inclusive);
        Assert.Equal(bounds.Right, coordinates.X2Exclusive);
        Assert.Equal(bounds.Bottom, coordinates.Y2Exclusive);
    }
}
