using System.IO.Pipes;
using System.Threading;
using DesktopShrine.Plugin.GOverlay.Layout;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal sealed class GOverlayBridgeClient : IDisposable
{
    private readonly object stateGate = new();
    private readonly string pipeName;
    private readonly Thread worker;
    private WaterfallSample? latestWaterfall;
    private volatile bool stopping;
    private GOverlayDashboardState? latest;
    private long queuedResetSequence = long.MinValue;
    private long lastQueuedColumnSequence = long.MinValue;

    public GOverlayBridgeClient(string pipeName)
    {
        this.pipeName = pipeName;
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "Desktop Shrine GOverlay bridge"
        };
    }

    public GOverlayDashboardState Latest
    {
        get
        {
            lock (stateGate)
                return latest ?? new GOverlayDashboardState();
        }
    }

    public GOverlayDashboardState ForDisplay
    {
        get
        {
            lock (stateGate)
            {
                var current = latest ?? new GOverlayDashboardState();
                return CopyForDisplay(current, latestWaterfall);
            }
        }
    }

    public void AcknowledgeWaterfall(
        long resetSequence,
        long columnSequence)
    {
        lock (stateGate)
        {
            if (resetSequence != queuedResetSequence)
                return;

            if (latestWaterfall is not null
                && latestWaterfall.ColumnSequence <= columnSequence)
                latestWaterfall = null;
        }
    }

    public void Start() => worker.Start();

    public void Dispose()
    {
        stopping = true;
        if (worker.IsAlive)
            worker.Join(1500);
    }

    private void Run()
    {
        while (!stopping)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.In,
                    PipeOptions.None);
                pipe.Connect(1000);
                while (!stopping && pipe.IsConnected)
                {
                    GOverlayDashboardState? previous;
                    lock (stateGate)
                        previous = latest;

                    var next = GOverlayBridgeCodec.ReadFrame(pipe, previous);
                    lock (stateGate)
                    {
                        latest = next;
                        QueueWaterfall(next);
                    }
                }
            }
            catch (Exception) when (!stopping)
            {
                Thread.Sleep(750);
            }
        }
    }

    private void QueueWaterfall(GOverlayDashboardState state)
    {
        if (state.WaterfallResetSequence != queuedResetSequence)
        {
            latestWaterfall = null;
            queuedResetSequence = state.WaterfallResetSequence;
            lastQueuedColumnSequence = long.MinValue;
        }

        if (!state.HasWaterfallColumn
            || state.WaterfallColumnSequence
                <= lastQueuedColumnSequence)
            return;

        // The LCD refresh is the consumer clock. Retain only the newest audio
        // column so a paused or disconnected display cannot accumulate a burst
        // of stale physical draw work.
        latestWaterfall = new WaterfallSample(
            state.WaterfallResetSequence,
            state.WaterfallColumnSequence,
            state.WaterfallWritePosition,
            state.WaterfallColumnWidth,
            (float[])state.WaterfallBands.Clone());
        lastQueuedColumnSequence = state.WaterfallColumnSequence;
    }

    private static GOverlayDashboardState CopyForDisplay(
        GOverlayDashboardState source,
        WaterfallSample? waterfall)
    {
        return new GOverlayDashboardState
        {
            Revision = source.Revision,
            RenderGeneration = source.RenderGeneration,
            RenderCompatibilityMode = source.RenderCompatibilityMode,
            DeviceFirmwareRevision = source.DeviceFirmwareRevision,
            MaximumCommandsPerRefresh = source.MaximumCommandsPerRefresh,
            MaximumArtworkBatchesPerRefresh =
                source.MaximumArtworkBatchesPerRefresh,
            MaximumDrawMilliseconds = source.MaximumDrawMilliseconds,
            AudioRefreshDivisor = source.AudioRefreshDivisor,
            ReconnectStabilizationMilliseconds =
                source.ReconnectStabilizationMilliseconds,
            IsAvailable = source.IsAvailable,
            PlaybackStatus = source.PlaybackStatus,
            Title = source.Title,
            Artist = source.Artist,
            Context = source.Context,
            FontName = source.FontName,
            PositionSeconds = source.PositionSeconds,
            DurationSeconds = source.DurationSeconds,
            HasRating = source.HasRating,
            PositiveRatingCount = source.PositiveRatingCount,
            NegativeRatingCount = source.NegativeRatingCount,
            RatingSummary = source.RatingSummary,
            Dominant = source.Dominant,
            Accent = source.Accent,
            Dark = source.Dark,
            Light = source.Light,
            HasPalette = source.HasPalette,
            AudioActive = source.AudioActive,
            WaterfallResetSequence = waterfall?.ResetSequence
                ?? source.WaterfallResetSequence,
            WaterfallColumnSequence = waterfall?.ColumnSequence
                ?? source.WaterfallColumnSequence,
            WaterfallWritePosition = waterfall?.WritePosition
                ?? source.WaterfallWritePosition,
            WaterfallColumnWidth = waterfall?.ColumnWidth
                ?? source.WaterfallColumnWidth,
            HasWaterfallColumn = waterfall is not null,
            WaterfallBands = waterfall?.Bands
                ?? Array.Empty<float>(),
            ArtworkKey = source.ArtworkKey,
            ArtworkContentType = source.ArtworkContentType,
            ArtworkData = source.ArtworkData,
            Mode = source.Mode,
            HardwareProvider = source.HardwareProvider,
            HardwareProviderVersion = source.HardwareProviderVersion,
            HardwareCapturedAtUnixMilliseconds =
                source.HardwareCapturedAtUnixMilliseconds,
            GpuName = source.GpuName,
            CpuName = source.CpuName,
            GpuMetrics = source.GpuMetrics,
            CpuMetrics = source.CpuMetrics,
            PhysicalMemorySummary = source.PhysicalMemorySummary,
            VirtualMemorySummary = source.VirtualMemorySummary,
            PhysicalMemoryLevel = source.PhysicalMemoryLevel,
            VirtualMemoryLevel = source.VirtualMemoryLevel
        };
    }

    private sealed class WaterfallSample(
        long resetSequence,
        long columnSequence,
        int writePosition,
        int columnWidth,
        float[] bands)
    {
        public long ResetSequence { get; } = resetSequence;
        public long ColumnSequence { get; } = columnSequence;
        public int WritePosition { get; } = writePosition;
        public int ColumnWidth { get; } = columnWidth;
        public float[] Bands { get; } = bands;
    }
}
