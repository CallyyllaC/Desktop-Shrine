using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace DesktopShrine.Plugin.SteamNowPlaying;

internal interface ISteamRuntime
{
    uint? GetRunningAppId();
    string? GetSteamDirectory();
}

internal sealed class WindowsSteamRuntime : ISteamRuntime
{
    private const string UserSteamKey = @"Software\Valve\Steam";
    private const string MachineSteamKey = @"Software\Valve\Steam";

    public uint? GetRunningAppId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserSteamKey);
        var raw = key?.GetValue("RunningAppID");
        if (raw is null)
            return null;

        try
        {
            var value = Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
            return value == 0 ? null : value;
        }
        catch (Exception exception) when (
            exception is FormatException
            or InvalidCastException
            or OverflowException)
        {
            return null;
        }
    }

    public string? GetSteamDirectory()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(UserSteamKey))
        {
            var path = NormaliseExistingDirectory(
                key?.GetValue("SteamPath") as string);
            if (path is not null)
                return path;
        }

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var machine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                view);
            using var key = machine.OpenSubKey(MachineSteamKey);
            var path = NormaliseExistingDirectory(
                key?.GetValue("InstallPath") as string);
            if (path is not null)
                return path;
        }

        return NormaliseExistingDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam"));
    }

    private static string? NormaliseExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fullPath = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(path));
        return Directory.Exists(fullPath) ? fullPath : null;
    }
}

internal sealed record SteamGame(
    uint AppId,
    string Name,
    string InstallDirectory,
    string SteamDirectory);

internal static class SteamLibraryCatalog
{
    public static IReadOnlyList<string> DiscoverLibraries(string steamDirectory)
    {
        var libraries = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        AddLibrary(libraries, steamDirectory);

        var file = Path.Combine(
            steamDirectory,
            "steamapps",
            "libraryfolders.vdf");
        if (!File.Exists(file))
            return [.. libraries];

        var document = SteamVdf.Parse(File.ReadAllText(file));
        var folders = document.GetObject("libraryfolders") ?? document;
        foreach (var (key, value) in folders)
        {
            if (!uint.TryParse(key, CultureInfo.InvariantCulture, out _))
                continue;

            if (value.ObjectValue?.GetString("path") is { } modernPath)
                AddLibrary(libraries, modernPath);
            else if (value.StringValue is { } legacyPath)
                AddLibrary(libraries, legacyPath);
        }

        return [.. libraries];
    }

    public static SteamGame? FindGame(
        string steamDirectory,
        uint appId)
    {
        foreach (var library in DiscoverLibraries(steamDirectory))
        {
            var manifestPath = Path.Combine(
                library,
                "steamapps",
                $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath))
                continue;

            var document = SteamVdf.Parse(File.ReadAllText(manifestPath));
            var state = document.GetObject("AppState") ?? document;
            var manifestId = state.GetString("appid");
            if (manifestId is not null
                && (!uint.TryParse(
                    manifestId,
                    CultureInfo.InvariantCulture,
                    out var parsedId)
                    || parsedId != appId))
            {
                continue;
            }

            var name = state.GetString("name");
            var installName = state.GetString("installdir");
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(installName))
            {
                continue;
            }

            var commonDirectory = Path.GetFullPath(Path.Combine(
                library,
                "steamapps",
                "common"));
            var installDirectory = Path.GetFullPath(Path.Combine(
                commonDirectory,
                installName));
            if (!IsWithin(installDirectory, commonDirectory))
                continue;

            return new(
                appId,
                name,
                installDirectory,
                steamDirectory);
        }

        return null;
    }

    private static void AddLibrary(
        HashSet<string> libraries,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(Path.Combine(fullPath, "steamapps")))
                libraries.Add(fullPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".."
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}

internal sealed class SteamVdf :
    Dictionary<string, SteamVdfValue>
{
    private SteamVdf() : base(StringComparer.OrdinalIgnoreCase) { }

    public string? GetString(string key) =>
        TryGetValue(key, out var value) ? value.StringValue : null;

    public SteamVdf? GetObject(string key) =>
        TryGetValue(key, out var value) ? value.ObjectValue : null;

    public static SteamVdf Parse(string text) => new Parser(text).Parse();

    private sealed class Parser(string text)
    {
        private int position;

        public SteamVdf Parse()
        {
            var result = ParseObject(false);
            SkipTrivia();
            if (position != text.Length)
                throw Error("Unexpected trailing content.");
            return result;
        }

        private SteamVdf ParseObject(bool expectClosingBrace)
        {
            var result = new SteamVdf();
            while (true)
            {
                SkipTrivia();
                if (position >= text.Length)
                {
                    if (expectClosingBrace)
                        throw Error("Missing closing brace.");
                    return result;
                }

                if (text[position] == '}')
                {
                    if (!expectClosingBrace)
                        throw Error("Unexpected closing brace.");
                    position++;
                    return result;
                }

                var key = ReadToken();
                SkipTrivia();
                if (position >= text.Length)
                    throw Error($"Missing value for '{key}'.");

                if (text[position] == '{')
                {
                    position++;
                    result[key] = new(ParseObject(true), null);
                }
                else
                {
                    result[key] = new(null, ReadToken());
                }
            }
        }

        private string ReadToken()
        {
            SkipTrivia();
            if (position >= text.Length)
                throw Error("Expected a token.");

            if (text[position] != '"')
            {
                var start = position;
                while (position < text.Length
                    && !char.IsWhiteSpace(text[position])
                    && text[position] is not '{' and not '}')
                {
                    position++;
                }
                if (start == position)
                    throw Error("Expected a token.");
                return text[start..position];
            }

            position++;
            var value = new StringBuilder();
            while (position < text.Length)
            {
                var current = text[position++];
                if (current == '"')
                    return value.ToString();
                if (current != '\\' || position >= text.Length)
                {
                    value.Append(current);
                    continue;
                }

                var escaped = text[position++];
                value.Append(escaped switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => $"\\{escaped}"
                });
            }

            throw Error("Unterminated quoted string.");
        }

        private void SkipTrivia()
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position]))
                {
                    position++;
                    continue;
                }

                if (position + 1 < text.Length
                    && text[position] == '/'
                    && text[position + 1] == '/')
                {
                    position += 2;
                    while (position < text.Length
                        && text[position] is not '\r' and not '\n')
                    {
                        position++;
                    }
                    continue;
                }

                break;
            }
        }

        private FormatException Error(string message) =>
            new($"{message} Position {position}.");
    }
}

internal sealed record SteamVdfValue(
    SteamVdf? ObjectValue,
    string? StringValue);
