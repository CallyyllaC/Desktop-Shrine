using DesktopShrine.Runtime;
using DesktopShrine.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DesktopShrine.Runtime.Tests;

public sealed class LocalAppDataTests
{
    [Fact]
    public void CanonicalRootUsesTheNoSpaceDirectoryName()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "DesktopShrine");

        Assert.Equal(expected, DesktopShrinePaths.Current.Root);
        Assert.Equal(
            Path.Combine(expected, "logs", "goverlay-lcd-command-trace.log"),
            DesktopShrinePaths.Current.GOverlayCommandTraceFile);
    }

    [Fact]
    public void ProductionCodeOnlyResolvesLocalAppDataThroughThePathProvider()
    {
        var repositoryRoot = FindRepositoryRoot();
        var productionFiles = new[] { "src", "samples" }
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !path.Contains(
                Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var violations = productionFiles
            .Where(path => !path.EndsWith(
                Path.Combine(
                    "DesktopShrine.Storage",
                    "DesktopShrinePaths.cs"),
                StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                "SpecialFolder.LocalApplicationData",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Empty(violations);
        Assert.DoesNotContain(
            @"{localappdata}\Desktop Shrine",
            File.ReadAllText(Path.Combine(
                repositoryRoot,
                "installer",
                "DesktopShrine.iss")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationMovesFilesRemovesDuplicatesAndBacksUpConflicts()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new DesktopShrinePaths(temporary.Path);
        var logger = new RecordingLogger<LocalAppDataMigration>();
        var migration = new LocalAppDataMigration(logger);

        Write(paths.LegacyRoot, "configuration/missing.json", "legacy-only");
        Write(paths.LegacyRoot, "configuration/same.json", "same");
        Write(paths.Root, "configuration/same.json", "same");
        Write(paths.LegacyRoot, "configuration/conflict.json", "legacy");
        Write(paths.Root, "configuration/conflict.json", "canonical");

        migration.Migrate(paths);

        Assert.Equal(
            "legacy-only",
            Read(paths.Root, "configuration/missing.json"));
        Assert.Equal("same", Read(paths.Root, "configuration/same.json"));
        Assert.Equal(
            "canonical",
            Read(paths.Root, "configuration/conflict.json"));
        Assert.Equal(
            "legacy",
            Read(
                paths.LegacyMigrationBackupDirectory,
                "configuration/conflict.json"));
        Assert.False(Directory.Exists(paths.LegacyRoot));
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                "legacy conflict preserved: configuration",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new DesktopShrinePaths(temporary.Path);
        var logger = new RecordingLogger<LocalAppDataMigration>();
        var migration = new LocalAppDataMigration(logger);
        Write(paths.LegacyRoot, "nested/value.txt", "value");

        migration.Migrate(paths);
        var firstContents = Read(paths.Root, "nested/value.txt");
        migration.Migrate(paths);

        Assert.Equal("value", firstContents);
        Assert.Equal("value", Read(paths.Root, "nested/value.txt"));
        Assert.False(Directory.Exists(paths.LegacyRoot));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DesktopShrine.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("DesktopShrine.slnx was not found.");
    }

    private static void Write(string root, string relativePath, string value)
    {
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DesktopShrineTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
