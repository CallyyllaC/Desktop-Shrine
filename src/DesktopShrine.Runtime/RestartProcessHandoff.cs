using System.Diagnostics;

namespace DesktopShrine.Runtime;

public static class RestartProcessHandoff
{
    internal const string Argument = "--desktop-shrine-restart-after-pid";

    public static string[] CreateArguments(
        int processId,
        IReadOnlyList<string> forwardedArguments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        var arguments = new string[forwardedArguments.Count + 2];
        arguments[0] = Argument;
        arguments[1] = processId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < forwardedArguments.Count; index++)
            arguments[index + 2] = forwardedArguments[index];
        return arguments;
    }

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out int processId,
        out string[] forwardedArguments)
    {
        processId = 0;
        forwardedArguments = [];
        if (arguments.Count < 2
            || !string.Equals(
                arguments[0],
                Argument,
                StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(
                arguments[1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out processId)
            || processId <= 0)
        {
            processId = 0;
            return false;
        }

        forwardedArguments = arguments.Skip(2).ToArray();
        return true;
    }

    public static async Task WaitForExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            // The old process completed before the handoff process opened it.
        }
    }
}
