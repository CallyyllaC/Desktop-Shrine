using DesktopShrine.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Runtime;

public sealed class ApplicationShutdownCoordinator(
    IHostApplicationLifetime lifetime,
    ILogger<ApplicationShutdownCoordinator> logger) : IApplicationControl
{
    private int requestedKind = -1;

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
        logger.LogInformation(
            "Desktop Shrine {ShutdownKind} requested; beginning shared host shutdown",
            kind);
        lifetime.StopApplication();
    }
}
