using System.IO.Pipes;
using DesktopShrine.Plugin.GOverlay.Layout;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.GOverlay;

internal sealed class GOverlayBridgeServer(
    string pipeName,
    TimeSpan updateInterval,
    ILogger logger,
    Func<DateTimeOffset, GOverlayDashboardState>? displayStateProvider = null)
    : IAsyncDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? worker;
    private GOverlayDashboardState latest = new();
    private long latestVersion;

    public void Update(GOverlayDashboardState state)
    {
        lock (gate)
        {
            latest = state;
            latestVersion++;
        }
    }

    public void Start(CancellationToken token)
    {
        if (worker is not null)
            return;

        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        worker = RunAsync(stop.Token);
    }

    public async ValueTask StopAsync()
    {
        var cancellation = stop;
        var task = worker;
        stop = null;
        worker = null;
        if (cancellation is null)
            return;

        await cancellation.CancelAsync();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            logger.LogInformation(
                "GOverlay SDK bridge waiting on pipe {PipeName}",
                pipeName);
            await pipe.WaitForConnectionAsync(token);
            logger.LogInformation("GOverlay SDK bridge connected");

            var sentVersion = -1L;
            var sentArtworkKey = string.Empty;
            using var timer = new PeriodicTimer(updateInterval);
            try
            {
                while (pipe.IsConnected
                       && await timer.WaitForNextTickAsync(token))
                {
                    GOverlayDashboardState state;
                    long version;
                    if (displayStateProvider is not null)
                    {
                        state = displayStateProvider(DateTimeOffset.UtcNow);
                        version = state.Revision;
                    }
                    else
                    {
                        lock (gate)
                        {
                            state = latest;
                            version = latestVersion;
                        }
                    }

                    if (version == sentVersion)
                        continue;

                    var includeArtwork = state.ArtworkKey != sentArtworkKey;
                    GOverlayBridgeCodec.WriteFrame(
                        pipe,
                        state,
                        includeArtwork);
                    sentVersion = version;
                    sentArtworkKey = state.ArtworkKey;
                }
            }
            catch (IOException)
            {
                logger.LogInformation(
                    "GOverlay SDK bridge disconnected; waiting for it to return");
            }
        }
    }
}
