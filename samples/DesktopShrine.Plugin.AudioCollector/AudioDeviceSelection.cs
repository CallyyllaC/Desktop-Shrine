namespace DesktopShrine.Plugin.AudioCollector;

internal sealed record AudioDeviceCandidate(string Id, string DisplayName);

internal sealed record AudioDeviceResolution(
    string SelectedId,
    bool UsedDefaultFallback,
    bool UsedLegacyDisplayName);

internal static class AudioDeviceSelection
{
    public static AudioDeviceResolution Resolve(
        string? configuredDevice,
        string defaultDeviceId,
        IReadOnlyList<AudioDeviceCandidate> activeDevices)
    {
        if (string.IsNullOrWhiteSpace(configuredDevice)
            || configuredDevice.Equals(
                "default",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(defaultDeviceId, false, false);
        }

        var exact = activeDevices.FirstOrDefault(device =>
            device.Id.Equals(
                configuredDevice,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return new(exact.Id, false, false);

        // Preserve compatibility with older configurations that stored a
        // friendly-name fragment. New UI writes the stable endpoint ID.
        var legacy = activeDevices.FirstOrDefault(device =>
            device.DisplayName.Contains(
                configuredDevice,
                StringComparison.OrdinalIgnoreCase));
        return legacy is not null
            ? new(legacy.Id, false, true)
            : new(defaultDeviceId, true, false);
    }
}
