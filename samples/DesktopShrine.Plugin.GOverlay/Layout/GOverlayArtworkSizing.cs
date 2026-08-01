namespace DesktopShrine.Plugin.GOverlay.Layout;

public static class GOverlayArtworkSizing
{
    public static GOverlayRectangle Contain(
        int sourceWidth,
        int sourceHeight,
        GOverlayRectangle target)
    {
        if (sourceWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (target.Width <= 0 || target.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(target));

        int width;
        int height;
        if ((long)sourceWidth * target.Height
            > (long)sourceHeight * target.Width)
        {
            width = target.Width;
            height = Math.Max(
                1,
                (int)((long)sourceHeight * target.Width / sourceWidth));
        }
        else
        {
            height = target.Height;
            width = Math.Max(
                1,
                (int)((long)sourceWidth * target.Height / sourceHeight));
        }

        return new(
            target.X + (target.Width - width) / 2,
            target.Y + (target.Height - height) / 2,
            width,
            height);
    }
}
