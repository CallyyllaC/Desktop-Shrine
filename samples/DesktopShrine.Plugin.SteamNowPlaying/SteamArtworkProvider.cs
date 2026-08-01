using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DesktopShrine.Contracts.Media;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.SteamNowPlaying;

internal sealed class SteamArtworkProvider(
    HttpClient httpClient,
    ILogger logger,
    SteamNowPlayingSettings settings)
{
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png"];

    public async Task<MediaArtwork?> GetAsync(
        uint appId,
        string steamDirectory,
        string? installDirectory,
        CancellationToken token)
    {
        var cacheDirectory = Path.Combine(
            settings.ArtworkCacheDirectory,
            appId.ToString());

        var cached = ReadFirstValid(EnumerateCached(cacheDirectory));
        if (cached is not null)
            return cached;

        var local = FindLocalLibraryArtwork(appId, steamDirectory);
        if (local is not null)
        {
            var artwork = ReadArtwork(local);
            if (artwork is not null)
            {
                await CacheAsync(
                    cacheDirectory,
                    "cover",
                    artwork,
                    token);
                return artwork;
            }
        }

        if (settings.DownloadArtwork)
        {
            var downloaded = await DownloadCoverAsync(appId, token);
            if (downloaded is not null)
            {
                await CacheAsync(
                    cacheDirectory,
                    "cover",
                    downloaded,
                    token);
                return downloaded;
            }
        }

        var icon = ExtractExecutableIcon(installDirectory);
        if (icon is not null)
        {
            await CacheAsync(
                cacheDirectory,
                "icon",
                icon,
                token);
        }
        return icon;
    }

    private IEnumerable<string> EnumerateCached(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
            yield break;

        foreach (var name in new[]
        {
            "cover.jpg",
            "cover.jpeg",
            "cover.png",
            "icon.png"
        })
        {
            var path = Path.Combine(cacheDirectory, name);
            if (File.Exists(path))
                yield return path;
        }
    }

    private MediaArtwork? ReadFirstValid(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var artwork = ReadArtwork(file);
            if (artwork is not null)
                return artwork;
        }
        return null;
    }

    private string? FindLocalLibraryArtwork(
        uint appId,
        string steamDirectory)
    {
        if (string.IsNullOrWhiteSpace(steamDirectory))
            return null;

        var libraryCache = Path.Combine(
            steamDirectory,
            "appcache",
            "librarycache");
        if (!Directory.Exists(libraryCache))
            return null;

        var namedCandidates = ImageExtensions.SelectMany(extension => new[]
        {
            Path.Combine(
                libraryCache,
                $"{appId}_library_600x900_2x{extension}"),
            Path.Combine(
                libraryCache,
                $"{appId}_library_600x900{extension}")
        });
        var named = namedCandidates.FirstOrDefault(File.Exists);
        if (named is not null)
            return named;

        var appDirectory = Path.Combine(
            libraryCache,
            appId.ToString());
        if (!Directory.Exists(appDirectory))
            return null;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 3
        };
        var images = Directory.EnumerateFiles(
                appDirectory,
                "*.*",
                options)
            .Where(path => ImageExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .Take(256)
            .ToArray();

        var libraryCover = images.FirstOrDefault(path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return name.Equals(
                    "library_capsule",
                    StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(
                    "library_600x900",
                    StringComparison.OrdinalIgnoreCase);
        });
        if (libraryCover is not null)
            return libraryCover;

        return images
            .Select(path => (Path: path, Score: ScorePortraitImage(path)))
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static long ScorePortraitImage(string path)
    {
        try
        {
            using var image = Image.FromFile(path);
            if (image.Width <= 0 || image.Height <= image.Width)
                return -1;

            var aspect = image.Width / (double)image.Height;
            if (Math.Abs(aspect - (2d / 3d)) > 0.12)
                return -1;
            return (long)image.Width * image.Height;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or ExternalException
            or OutOfMemoryException)
        {
            return -1;
        }
    }

    private async Task<MediaArtwork?> DownloadCoverAsync(
        uint appId,
        CancellationToken token)
    {
        var urls = new[]
        {
            $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
            $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg"
        };

        foreach (var url in urls)
        {
            try
            {
                using var response = await httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    token);
                if (!response.IsSuccessStatusCode)
                    continue;

                var length = response.Content.Headers.ContentLength;
                if (length is > 0
                    && length > settings.MaximumArtworkBytes)
                {
                    continue;
                }

                await using var stream =
                    await response.Content.ReadAsStreamAsync(token);
                var bytes = await ReadUpToLimitAsync(
                    stream,
                    settings.MaximumArtworkBytes,
                    token);
                if (bytes is null || !LooksLikeImage(bytes))
                    continue;

                return new()
                {
                    ContentType = NormaliseContentType(
                        response.Content.Headers.ContentType?.MediaType,
                        bytes),
                    Data = bytes
                };
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or IOException
                or TaskCanceledException)
            {
                logger.LogDebug(
                    exception,
                    "Could not download Steam artwork for app {AppId}",
                    appId);
            }
        }

        return null;
    }

    private MediaArtwork? ExtractExecutableIcon(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory)
            || !Directory.Exists(installDirectory))
        {
            return null;
        }

        try
        {
            var executable = FindLikelyExecutable(installDirectory);
            if (executable is null)
                return null;

            using var icon = Icon.ExtractAssociatedIcon(executable);
            if (icon is null)
                return null;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            if (stream.Length > settings.MaximumArtworkBytes)
                return null;
            return new()
            {
                ContentType = "image/png",
                Data = stream.ToArray()
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or ExternalException
            or IOException
            or UnauthorizedAccessException)
        {
            logger.LogDebug(
                exception,
                "Could not extract an executable icon from {InstallDirectory}",
                installDirectory);
            return null;
        }
    }

    private static string? FindLikelyExecutable(string installDirectory)
    {
        var directoryName = Path.GetFileName(installDirectory);
        var normalisedName = NormaliseName(directoryName);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 4
        };

        return Directory.EnumerateFiles(
                installDirectory,
                "*.exe",
                options)
            .Take(256)
            .Select(path => (Path: path, Score: ScoreExecutable(
                path,
                installDirectory,
                normalisedName)))
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static int ScoreExecutable(
        string path,
        string installDirectory,
        string normalisedInstallName)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var normalisedFileName = NormaliseName(fileName);
        var relative = Path.GetRelativePath(installDirectory, path);
        var depth = relative.Count(character =>
            character == Path.DirectorySeparatorChar);
        var score = 100 - (depth * 10);

        if (normalisedFileName == normalisedInstallName)
            score += 100;
        else if (normalisedFileName.Contains(
            normalisedInstallName,
            StringComparison.Ordinal)
            || normalisedInstallName.Contains(
                normalisedFileName,
                StringComparison.Ordinal))
        {
            score += 40;
        }

        if (fileName.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            score -= 20;
        if (fileName.Contains("unins", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || relative.Contains(
                "_CommonRedist",
                StringComparison.OrdinalIgnoreCase))
        {
            score -= 200;
        }

        return score;
    }

    private MediaArtwork? ReadArtwork(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0
                || info.Length > settings.MaximumArtworkBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (!LooksLikeImage(bytes))
                return null;
            return new()
            {
                ContentType = NormaliseContentType(null, bytes),
                Data = bytes
            };
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            logger.LogDebug(
                exception,
                "Could not read Steam artwork {ArtworkPath}",
                path);
            return null;
        }
    }

    private static async Task<byte[]?> ReadUpToLimitAsync(
        Stream source,
        long maximumBytes,
        CancellationToken token)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > maximumBytes)
                return null;
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static async Task CacheAsync(
        string directory,
        string name,
        MediaArtwork artwork,
        CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var extension = artwork.ContentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            _ => ".bin"
        };
        var target = Path.Combine(directory, name + extension);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, artwork.Data, token);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8
        && ((bytes[0] == 0xFF
                && bytes[1] == 0xD8
                && bytes[2] == 0xFF)
            || (bytes[0] == 0x89
                && bytes[1] == 0x50
                && bytes[2] == 0x4E
                && bytes[3] == 0x47
                && bytes[4] == 0x0D
                && bytes[5] == 0x0A
                && bytes[6] == 0x1A
                && bytes[7] == 0x0A));

    private static string NormaliseContentType(
        string? contentType,
        ReadOnlySpan<byte> bytes)
    {
        if (string.Equals(
            contentType,
            "image/png",
            StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        return bytes[0] == 0x89 ? "image/png" : "image/jpeg";
    }

    private static string NormaliseName(string value) =>
        string.Concat(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant));
}
