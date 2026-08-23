using System.Drawing;
using System.Windows.Forms;

namespace DesktopShrine.Plugin.TaskbarControls;

internal static class DesktopShrineTheme
{
    public static readonly Color Background = Color.FromArgb(7, 13, 23);
    public static readonly Color Surface = Color.FromArgb(11, 20, 35);
    public static readonly Color SurfaceRaised = Color.FromArgb(22, 34, 52);
    public static readonly Color Hover = Color.FromArgb(29, 43, 64);
    public static readonly Color Pressed = Color.FromArgb(35, 52, 75);
    public static readonly Color Border = Color.FromArgb(45, 61, 82);
    public static readonly Color Divider = Color.FromArgb(35, 49, 67);
    public static readonly Color Text = Color.FromArgb(244, 247, 251);
    public static readonly Color MutedText = Color.FromArgb(148, 160, 181);
    public static readonly Color Cyan = Color.FromArgb(24, 219, 240);
    public static readonly Color Blue = Color.FromArgb(91, 119, 235);
    public static readonly Color Magenta = Color.FromArgb(247, 35, 157);
    public static readonly Color Track = Color.FromArgb(24, 35, 51);
}

internal sealed class DesktopShrineColourTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => DesktopShrineTheme.Background;
    public override Color MenuBorder => DesktopShrineTheme.Border;
    public override Color MenuItemBorder => DesktopShrineTheme.Border;
    public override Color MenuItemSelected => DesktopShrineTheme.SurfaceRaised;
    public override Color MenuItemSelectedGradientBegin => DesktopShrineTheme.SurfaceRaised;
    public override Color MenuItemSelectedGradientEnd => DesktopShrineTheme.SurfaceRaised;
    public override Color ImageMarginGradientBegin => DesktopShrineTheme.Background;
    public override Color ImageMarginGradientMiddle => DesktopShrineTheme.Background;
    public override Color ImageMarginGradientEnd => DesktopShrineTheme.Background;
    public override Color SeparatorDark => DesktopShrineTheme.Border;
    public override Color SeparatorLight => DesktopShrineTheme.Border;
}

internal sealed class DesktopShrineMenuRenderer() :
    ToolStripProfessionalRenderer(new DesktopShrineColourTable());
