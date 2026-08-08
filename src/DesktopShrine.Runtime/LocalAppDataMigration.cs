using System.Security.Cryptography;
using DesktopShrine.Storage;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Runtime;

public sealed class LocalAppDataMigration(
    ILogger<LocalAppDataMigration> logger)
{
    public void Migrate(DesktopShrinePaths paths)
    {
        if (!Directory.Exists(paths.LegacyRoot))
            return;

        logger.LogInformation(
            "Desktop Shrine - migrating legacy LocalAppData path: {LegacyPath} -> {CanonicalPath}",
            paths.LegacyRoot,
            paths.Root);

        var failed = false;
        try
        {
            Directory.CreateDirectory(paths.Root);
            var sourceFiles = Directory.EnumerateFiles(
                    paths.LegacyRoot,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var sourcePath in sourceFiles)
            {
                var relativePath = Path.GetRelativePath(
                    paths.LegacyRoot,
                    sourcePath);
                try
                {
                    MigrateFile(paths, sourcePath, relativePath);
                }
                catch (Exception error)
                {
                    failed = true;
                    logger.LogError(
                        error,
                        "Desktop Shrine - legacy LocalAppData migration failed: {RelativePath}",
                        relativePath);
                }
            }

            RemoveEmptyDirectories(paths.LegacyRoot);
            if (!failed)
                logger.LogInformation(
                    "Desktop Shrine - legacy LocalAppData migration complete");
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "Desktop Shrine - legacy LocalAppData migration failed: {Exception}",
                error.Message);
        }
    }

    private void MigrateFile(
        DesktopShrinePaths paths,
        string sourcePath,
        string relativePath)
    {
        var destinationPath = Path.Combine(paths.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        if (!File.Exists(destinationPath))
        {
            File.Move(sourcePath, destinationPath);
            logger.LogInformation(
                "Desktop Shrine - migrated: {RelativePath}",
                relativePath);
            return;
        }

        if (FilesAreIdentical(sourcePath, destinationPath))
        {
            File.Delete(sourcePath);
            logger.LogInformation(
                "Desktop Shrine - migrated: {RelativePath}",
                relativePath);
            return;
        }

        var backupPath = Path.Combine(
            paths.LegacyMigrationBackupDirectory,
            relativePath);
        backupPath = FindAvailableBackupPath(sourcePath, backupPath);
        if (File.Exists(backupPath)
            && FilesAreIdentical(sourcePath, backupPath))
        {
            File.Delete(sourcePath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Move(sourcePath, backupPath);
        }

        logger.LogWarning(
            "Desktop Shrine - legacy conflict preserved: {RelativePath}",
            relativePath);
    }

    private static string FindAvailableBackupPath(
        string sourcePath,
        string preferredPath)
    {
        if (!File.Exists(preferredPath)
            || FilesAreIdentical(sourcePath, preferredPath))
            return preferredPath;

        var directory = Path.GetDirectoryName(preferredPath)!;
        var fileName = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        var identifier = FileIdentifier(sourcePath);
        var candidate = Path.Combine(
            directory,
            fileName + ".legacy-" + identifier + extension);
        if (!File.Exists(candidate)
            || FilesAreIdentical(sourcePath, candidate))
            return candidate;

        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(
                directory,
                fileName + ".legacy-" + identifier + "-" + index + extension);
            if (!File.Exists(candidate)
                || FilesAreIdentical(sourcePath, candidate))
                return candidate;
        }
    }

    private static bool FilesAreIdentical(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (first.Length != second.Length)
            return false;

        using var firstStream = File.OpenRead(firstPath);
        using var secondStream = File.OpenRead(secondPath);
        var firstBuffer = new byte[81920];
        var secondBuffer = new byte[firstBuffer.Length];
        while (true)
        {
            var firstRead = firstStream.Read(
                firstBuffer,
                0,
                firstBuffer.Length);
            var secondRead = secondStream.Read(
                secondBuffer,
                0,
                secondBuffer.Length);
            if (firstRead != secondRead)
                return false;
            if (firstRead == 0)
                return true;
            for (var index = 0; index < firstRead; index++)
            {
                if (firstBuffer[index] != secondBuffer[index])
                    return false;
            }
        }
    }

    private static string FileIdentifier(string path)
    {
        using var stream = File.OpenRead(path);
        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(stream);
        return string.Concat(hash.Take(6).Select(value => value.ToString("x2")));
    }

    private static void RemoveEmptyDirectories(string legacyRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     legacyRoot,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }

        if (!Directory.EnumerateFileSystemEntries(legacyRoot).Any())
            Directory.Delete(legacyRoot);
    }
}
