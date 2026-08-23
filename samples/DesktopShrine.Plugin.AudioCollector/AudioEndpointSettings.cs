using DesktopShrine.Abstractions;
using Microsoft.Extensions.Configuration;

namespace DesktopShrine.Plugin.AudioCollector;

internal sealed record AudioEndpointSettings(
    string CaptureMode,
    string Device)
{
    public bool IsLoopback => string.Equals(
        CaptureMode,
        "loopback",
        StringComparison.OrdinalIgnoreCase);

    public static AudioEndpointSettings FromConfiguration(
        IConfiguration configuration) => new(
            configuration["CaptureMode"] ?? "loopback",
            configuration["Device"] ?? "default");

    public static ConfigurationValidationResult Validate(
        AudioEndpointSettings value)
    {
        if (!string.Equals(value.CaptureMode, "loopback", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value.CaptureMode, "input", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigurationValidationResult.Failure(
                "CaptureMode must be loopback or input.");
        }

        return string.IsNullOrWhiteSpace(value.Device)
            ? ConfigurationValidationResult.Failure("Device is required.")
            : ConfigurationValidationResult.Success;
    }
}
