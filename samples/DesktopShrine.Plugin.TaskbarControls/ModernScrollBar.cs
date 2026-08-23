using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal sealed class BrandedFlowLayoutPanel : FlowLayoutPanel
{
    private const int HorizontalScrollBar = 0;
    private const int VerticalScrollBar = 1;

    internal event EventHandler? BrandedScrollPositionChanged;

    internal int BrandedScrollRange => Math.Max(
        0,
        DisplayRectangle.Height - ClientSize.Height);

    internal int BrandedScrollPosition => Math.Clamp(
        -DisplayRectangle.Y,
        0,
        BrandedScrollRange);

    internal void SetBrandedScrollPosition(int position)
    {
        var next = Math.Clamp(position, 0, BrandedScrollRange);
        if (next == BrandedScrollPosition)
            return;

        SetDisplayRectLocation(0, -next);
        HideNativeScrollBars();
        BrandedScrollPositionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        HideNativeScrollBars();
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        base.OnLayout(args);
        HideNativeScrollBars();
    }

    protected override void OnScroll(ScrollEventArgs args)
    {
        base.OnScroll(args);
        HideNativeScrollBars();
    }

    protected override void OnMouseWheel(MouseEventArgs args)
    {
        if (BrandedScrollRange <= 0)
        {
            base.OnMouseWheel(args);
            return;
        }

        var lines = SystemInformation.MouseWheelScrollLines;
        var distance = lines < 0
            ? Math.Max(1, ClientSize.Height - Font.Height * 2)
            : Math.Max(Font.Height, lines * Font.Height);
        var delta = (int)Math.Round(
            -args.Delta / 120d * distance,
            MidpointRounding.AwayFromZero);
        if (delta == 0 && args.Delta != 0)
            delta = -Math.Sign(args.Delta) * Font.Height;
        SetBrandedScrollPosition(BrandedScrollPosition + delta);
    }

    private void HideNativeScrollBars()
    {
        if (!IsHandleCreated)
            return;
        _ = ShowScrollBar(Handle, HorizontalScrollBar, false);
        _ = ShowScrollBar(Handle, VerticalScrollBar, false);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(nint window, int bar, bool show);
}

internal sealed class ModernVerticalScrollBar : Control
{
    private readonly BrandedFlowLayoutPanel target;
    private bool dragging;
    private int dragOffset;

    public ModernVerticalScrollBar(BrandedFlowLayoutPanel target)
    {
        this.target = target;
        BackColor = DesktopShrineTheme.Background;
        Cursor = Cursors.Hand;
        TabStop = false;
        Width = 12;
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        target.BrandedScrollPositionChanged += (_, _) => Invalidate();
        target.Layout += (_, _) => Invalidate();
        target.SizeChanged += (_, _) => Invalidate();
        target.ControlAdded += (_, _) => Invalidate();
        target.ControlRemoved += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(DesktopShrineTheme.Background);
        if (!CanScroll)
            return;
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = new SolidBrush(DesktopShrineTheme.Track);
        ShrineDrawing.FillRoundedRectangle(args.Graphics, track,
            new RectangleF(3, 4, 6, Math.Max(1, Height - 8)), 3);
        using var thumb = new LinearGradientBrush(ThumbBounds,
            DesktopShrineTheme.Cyan, DesktopShrineTheme.Blue,
            LinearGradientMode.Vertical);
        ShrineDrawing.FillRoundedRectangle(args.Graphics, thumb, ThumbBounds, 4);
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left || !CanScroll)
            return;
        var thumb = ThumbBounds;
        if (thumb.Contains(args.Location))
        {
            dragging = true;
            dragOffset = args.Y - (int)thumb.Top;
            Capture = true;
        }
        else
        {
            SetPosition(PositionFromThumb(args.Y - thumb.Height / 2f));
        }
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (dragging)
            SetPosition(PositionFromThumb(args.Y - dragOffset));
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.Button != MouseButtons.Left)
            return;
        dragging = false;
        Capture = false;
    }

    private bool CanScroll => ScrollRange > 0 && Height > 20;
    private int ScrollRange => target.BrandedScrollRange;
    private int ScrollPosition => target.BrandedScrollPosition;
    private float TrackHeight => Math.Max(1, Height - 8);
    private float ThumbHeight => Math.Clamp(
        TrackHeight * target.ClientSize.Height
            / Math.Max(target.ClientSize.Height, target.DisplayRectangle.Height),
        34,
        TrackHeight);

    private RectangleF ThumbBounds
    {
        get
        {
            var travel = Math.Max(0, TrackHeight - ThumbHeight);
            var top = 4f + (ScrollRange == 0 ? 0 : travel * ScrollPosition / ScrollRange);
            return new(2, top, 8, ThumbHeight);
        }
    }

    private int PositionFromThumb(float thumbTop)
    {
        var travel = Math.Max(1, TrackHeight - ThumbHeight);
        var ratio = Math.Clamp((thumbTop - 4f) / travel, 0f, 1f);
        return (int)Math.Round(ratio * ScrollRange, MidpointRounding.AwayFromZero);
    }

    private void SetPosition(int position)
    {
        target.SetBrandedScrollPosition(position);
        Invalidate();
    }
}
