using DesktopShrine.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Runtime;

public sealed class ApplicationShutdownCoordinator(
    IHostApplicationLifetime lifetime,
    OutputShutdownCoordinator outputShutdown,
    ILogger<ApplicationShutdownCoordinator> logger) :
    IApplicationControl,
    IDisposable
{
    private int requestedKind = -1;
    private int stopRequested;

    private readonly CancellationTokenRegistration stoppingRegistration =
        lifetime.ApplicationStopping.Register(outputShutdown.BeginShutdown);

    public bool RestartRequested => Volatile.Read(ref requestedKind)
        == (int)ApplicationShutdownKind.Restart;

    public void RequestShutdown(ApplicationShutdownKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        if (kind == ApplicationShutdownKind.Restart)
        {
            Interlocked.Exchange(ref requestedKind, (int)kind);
        }
        else
        {
            Interlocked.CompareExchange(
                ref requestedKind,
                (int)kind,
                comparand: -1);
        }

        outputShutdown.BeginShutdown();
        if (Interlocked.Exchange(ref stopRequested, 1) != 0)
            return;

        logger.LogInformation(
            "Desktop Shrine {ShutdownKind} requested; beginning shared host shutdown",
            kind);
        lifetime.StopApplication();
    }

    public void Dispose() => stoppingRegistration.Dispose();
}
