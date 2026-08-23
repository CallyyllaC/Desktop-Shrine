using DesktopShrine.Abstractions;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Runtime;

public sealed class OutputShutdownCoordinator(
    ILogger<OutputShutdownCoordinator> logger)
{
    private readonly object gate = new();
    private readonly List<IShutdownOutputParticipant> participants = [];
    private int shutdownInProgress;

    public bool ShutdownInProgress =>
        Volatile.Read(ref shutdownInProgress) != 0;

    public void Register(IShutdownOutputParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        lock (gate)
        {
            if (!ShutdownInProgress)
            {
                participants.Add(participant);
                return;
            }
        }

        // Shutdown can begin while plugins are still loading. A late output
        // must inherit the process-lifetime mute instead of starting normally.
        Mute(participant);
        Blackout(participant);
    }

    public void BeginShutdown()
    {
        if (Interlocked.Exchange(ref shutdownInProgress, 1) != 0)
            return;

        IShutdownOutputParticipant[] snapshot;
        lock (gate)
            snapshot = [.. participants];

        foreach (var participant in snapshot)
            Mute(participant);
        logger.LogInformation("LED output muted");

        logger.LogInformation(
            "Sending final blackout frame to {OutputCount} active LED output(s)",
            snapshot.Length);
        foreach (var participant in snapshot)
            Blackout(participant);
        logger.LogInformation("LED blackout complete");
    }

    private void Mute(IShutdownOutputParticipant participant)
    {
        try
        {
            participant.MuteOutputForShutdown();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not mute LED output {OutputType}; continuing shutdown blackout",
                participant.GetType().FullName);
        }
    }

    private void Blackout(IShutdownOutputParticipant participant)
    {
        try
        {
            participant.BlackoutForShutdown();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not blackout LED output {OutputType}; continuing shutdown blackout",
                participant.GetType().FullName);
        }
    }
}
