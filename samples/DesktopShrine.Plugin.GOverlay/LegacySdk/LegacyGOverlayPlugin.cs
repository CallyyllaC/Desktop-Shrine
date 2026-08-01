using System.Collections;
using DesktopShrine.Plugin.GOverlay.Layout;
using GOverlayPlugin.Interfaces;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

public sealed class LegacyGOverlayPlugin : IPlugin
{
    private const string DashboardElementId = "desktopshrine.dashboard";
#if DEBUG
    private const string PluginDisplayName =
        "Desktop Shrine GOverlay Bridge (Debug)";
#else
    private const string PluginDisplayName =
        "Desktop Shrine GOverlay Bridge (Release)";
#endif
    private readonly GOverlayBridgeClient bridge =
        new("DesktopShrine.GOverlay");
    private readonly GOverlayDashboardLayout layout = new();
    private readonly GOverlaySdkSceneRenderer renderer = new();
    private IHost? host;
    private long renderedGeneration = long.MinValue;
    private bool initialized;

    internal static string CurrentFontName { get; private set; } =
        "Oxanium-Bold_20px.bin";

    public string Name => PluginDisplayName;
    public string Description =>
        "Displays the Desktop Shrine media dashboard on LCDSysInfo 3.5.";
    public string Display => "lcdsys";

    public void Initialize(IHost value)
    {
        host = value;

        if (initialized)
            return;

        initialized = true;
        bridge.Start();
    }

    public Hashtable CallBacks(string method) => new();

    public bool SensorHasCustomDraw(string method) =>
        method == DashboardElementId;

    public Hashtable ComboBoxes() => new();

    public Dictionary<string, string> AvailableSensors(
        Hashtable pluginOptions) =>
        new();

    public Dictionary<string, string> LCDSys2_AvailableSensors(
        Hashtable pluginOptions) =>
        new()
        {
            [DashboardElementId] = "Desktop Shrine media dashboard"
        };

    public Hashtable PluginOptionsDefault() => new();

    public Hashtable PluginOptions(Hashtable pluginData) => new();

    public Hashtable CreateOptions(
        string sensorId,
        Hashtable elementData) =>
        new();

    public Hashtable LCDSys2_CreateOptions(
        string sensorId,
        Hashtable elementData) =>
        new();

    public Hashtable SetDefaultOptions(
        string sensorId,
        Hashtable elementData)
    {
        if (sensorId == DashboardElementId)
        {
            elementData["width"] = 480;
            elementData["height"] = 320;
        }
        return elementData;
    }

    public ArrayList DisplayOnLCD(
        string sensorId,
        Hashtable elementData,
        Hashtable pluginOptions,
        int cacheRuns) =>
        new();

    public ArrayList LCDSys2_DisplayOnLCD(
        string sensorId,
        Hashtable elementData,
        Hashtable pluginOptions,
        int cacheRuns)
    {
        if (sensorId != DashboardElementId || host is null)
            return new();

        var state = bridge.ForDisplay;
        if (state.RenderGeneration != renderedGeneration)
        {
            renderer.ResetForFullRedraw();
            renderedGeneration = state.RenderGeneration;
        }
        CurrentFontName = string.IsNullOrWhiteSpace(state.FontName)
            ? "Oxanium-Bold_20px.bin"
            : state.FontName;
        var scene = layout.Compose(state, DateTime.Now);
        var artworkState = ArtworkTransferState.Capture(state);
        var renderedWaterfallSequence = renderer.Render(
            host,
            scene,
            cacheRuns,
            () => artworkState.Matches(bridge.Latest));
        if (renderedWaterfallSequence.HasValue)
        {
            bridge.AcknowledgeWaterfall(
                state.WaterfallResetSequence,
                renderedWaterfallSequence.Value);
        }
        return new();
    }

    private sealed class ArtworkTransferState
    {
        private readonly bool isAvailable;
        private readonly GOverlayColour dark;
        private readonly GOverlayColour light;
        private readonly string artworkKey;

        private ArtworkTransferState(GOverlayDashboardState state)
        {
            isAvailable = state.IsAvailable;
            dark = state.Dark;
            light = state.Light;
            artworkKey = state.ArtworkKey;
        }

        public static ArtworkTransferState Capture(
            GOverlayDashboardState state) =>
            new(state);

        public bool Matches(GOverlayDashboardState state) =>
            isAvailable == state.IsAvailable
            && dark.Equals(state.Dark)
            && light.Equals(state.Light)
            && artworkKey == state.ArtworkKey;
    }
}
