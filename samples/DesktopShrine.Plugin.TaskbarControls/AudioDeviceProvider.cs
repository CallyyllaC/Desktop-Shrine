using NAudio.CoreAudioApi;
using System.Runtime.ExceptionServices;

namespace DesktopShrine.Plugin.TaskbarControls;

internal interface IAudioDeviceProvider
{
    IReadOnlyList<TaskbarChoice> GetDevices(string captureMode);
}

internal sealed record AudioEndpointDescriptor(string Id, string DisplayName);

internal sealed record AudioEndpointSnapshot(
    AudioEndpointDescriptor? Default,
    IReadOnlyList<AudioEndpointDescriptor> Active);

internal interface IAudioEndpointCatalog
{
    AudioEndpointSnapshot Enumerate(bool captureInput);
}

internal interface IAudioEndpointDispatcher
{
    AudioEndpointSnapshot Enumerate(
        IAudioEndpointCatalog catalog,
        bool captureInput);
}

internal sealed class WindowsAudioEndpointCatalog : IAudioEndpointCatalog
{
    public AudioEndpointSnapshot Enumerate(bool captureInput)
    {
        var flow = captureInput ? DataFlow.Capture : DataFlow.Render;
        using var enumerator = new MMDeviceEnumerator();
        AudioEndpointDescriptor? defaultEndpoint = null;
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(
                flow,
                Role.Multimedia);
            defaultEndpoint = ReadEndpoint(device);
        }
        catch
        {
            // Windows can temporarily have no default while devices change.
            // Active endpoint enumeration can still provide useful choices.
        }

        var active = new List<AudioEndpointDescriptor>();
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         flow,
                         DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        active.Add(ReadEndpoint(device));
                    }
                    catch
                    {
                        // One stale property store must not hide every other
                        // usable endpoint from the tray selector.
                    }
                }
            }
        }
        catch
        {
            // Preserve the default entry if the endpoint collection itself is
            // temporarily unavailable.
        }
        return new(defaultEndpoint, active);
    }

    private static AudioEndpointDescriptor ReadEndpoint(MMDevice device) =>
        new(device.ID, device.FriendlyName);
}

internal sealed class WindowsAudioDeviceProvider(
    IAudioEndpointCatalog? catalog = null,
    IAudioEndpointDispatcher? dispatcher = null) : IAudioDeviceProvider
{
    private readonly IAudioEndpointCatalog catalog =
        catalog ?? new WindowsAudioEndpointCatalog();
    private readonly IAudioEndpointDispatcher dispatcher =
        dispatcher ?? new MtaAudioEndpointDispatcher();

    public IReadOnlyList<TaskbarChoice> GetDevices(string captureMode)
    {
        var captureInput = string.Equals(
            captureMode,
            "input",
            StringComparison.OrdinalIgnoreCase);
        AudioEndpointSnapshot snapshot;
        try
        {
            snapshot = dispatcher.Enumerate(catalog, captureInput);
            if (snapshot.Active.Count <= 1)
            {
                // The endpoint catalog deliberately tolerates transient COM
                // failures per operation. A suspiciously sparse MTA snapshot
                // is therefore retried on the existing tray apartment and the
                // observations are merged. A machine with one endpoint still
                // receives the same stable result.
                try
                {
                    snapshot = Merge(
                        snapshot,
                        catalog.Enumerate(captureInput));
                }
                catch
                {
                    // Retain a usable MTA result if only the retry fails.
                }
            }
        }
        catch
        {
            // Some elevated/session launch combinations reject creation or
            // apartment initialisation of the short-lived MTA worker. The
            // endpoint API is also valid from the tray STA, so retry there
            // rather than collapsing the selector to its synthetic default.
            snapshot = catalog.Enumerate(captureInput);
        }
        var choices = new List<TaskbarChoice>
        {
            new(
                "default",
                snapshot.Default is null
                    ? "Default device"
                    : $"Default — {snapshot.Default.DisplayName}")
        };
        choices.AddRange(snapshot.Active
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Id))
            .GroupBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(
                endpoint => endpoint.DisplayName,
                StringComparer.CurrentCultureIgnoreCase)
            .Select(endpoint => new TaskbarChoice(
                endpoint.Id,
                endpoint.DisplayName)));
        return choices;
    }

    private static AudioEndpointSnapshot Merge(
        AudioEndpointSnapshot primary,
        AudioEndpointSnapshot retry) => new(
        primary.Default ?? retry.Default,
        primary.Active
            .Concat(retry.Active)
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Id))
            .GroupBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray());
}

internal sealed class MtaAudioEndpointDispatcher : IAudioEndpointDispatcher
{
    public AudioEndpointSnapshot Enumerate(
        IAudioEndpointCatalog catalog,
        bool captureInput)
    {
        AudioEndpointSnapshot? snapshot = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                snapshot = catalog.Enumerate(captureInput);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Desktop Shrine audio endpoint enumeration"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return snapshot
            ?? throw new InvalidOperationException(
                "Audio endpoint enumeration returned no snapshot.");
    }
}
