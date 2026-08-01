using System.Text.Json;
using DesktopShrine.Contracts.Media;
using Microsoft.Extensions.Logging;

namespace DesktopShrine.Plugin.SteamNowPlaying;

internal sealed record SteamStoreMetadata(
    string? Publisher,
    AudienceRating? Rating);

internal sealed class SteamStoreMetadataProvider(
    HttpClient httpClient,
    ILogger logger,
    SteamNowPlayingSettings settings)
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<SteamStoreMetadata?> GetAsync(
        uint appId,
        CancellationToken token)
    {
        var path = Path.Combine(
            settings.StoreMetadataCacheDirectory,
            $"{appId}.json");
        var cached = await ReadCacheAsync(path, token);
        if (cached is not null
            && DateTimeOffset.UtcNow - cached.CapturedAt
                < TimeSpan.FromHours(settings.StoreMetadataCacheHours))
        {
            return cached.ToMetadata();
        }

        if (!settings.DownloadStoreMetadata)
            return cached?.ToMetadata();

        var publisherTask = DownloadPublisherAsync(appId, token);
        var ratingTask = DownloadRatingAsync(appId, token);
        await Task.WhenAll(publisherTask, ratingTask);
        var publisher = await publisherTask;
        var rating = await ratingTask;
        if (publisher is null && rating is null)
            return cached?.ToMetadata();

        var refreshed = new CacheEntry
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Publisher = publisher ?? cached?.Publisher,
            PositiveCount =
                rating?.PositiveCount ?? cached?.PositiveCount,
            NegativeCount =
                rating?.NegativeCount ?? cached?.NegativeCount,
            RatingSummary =
                rating?.Summary ?? cached?.RatingSummary
        };
        await WriteCacheAsync(path, refreshed, token);
        return refreshed.ToMetadata();
    }

    private async Task<string?> DownloadPublisherAsync(
        uint appId,
        CancellationToken token)
    {
        var document = await DownloadJsonAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english",
            appId,
            "store details",
            token);
        if (document is null)
            return null;

        using (document)
        {
            if (!document.RootElement.TryGetProperty(
                    appId.ToString(),
                    out var app)
                || !app.TryGetProperty("success", out var success)
                || !success.GetBoolean()
                || !app.TryGetProperty("data", out var data)
                || !data.TryGetProperty("publishers", out var publishers)
                || publishers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var names = publishers
                .EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return names.Length == 0 ? null : string.Join(", ", names);
        }
    }

    private async Task<AudienceRating?> DownloadRatingAsync(
        uint appId,
        CancellationToken token)
    {
        var document = await DownloadJsonAsync(
            $"https://store.steampowered.com/appreviews/{appId}?json=1&filter=all&day_range=365&cursor=*&review_type=all&language=all&purchase_type=all&num_per_page=1",
            appId,
            "review summary",
            token);
        if (document is null)
            return null;

        using (document)
        {
            if (!document.RootElement.TryGetProperty(
                    "query_summary",
                    out var summary)
                || !summary.TryGetProperty(
                    "total_positive",
                    out var positive)
                || !summary.TryGetProperty(
                    "total_negative",
                    out var negative)
                || !positive.TryGetInt64(out var positiveCount)
                || !negative.TryGetInt64(out var negativeCount))
            {
                return null;
            }

            var description =
                summary.TryGetProperty(
                    "review_score_desc",
                    out var descriptionValue)
                && descriptionValue.ValueKind == JsonValueKind.String
                    ? descriptionValue.GetString()
                    : null;
            var safePositiveCount = Math.Max(0, positiveCount);
            var safeNegativeCount = Math.Max(0, negativeCount);
            if (safePositiveCount == 0 && safeNegativeCount == 0)
                return null;

            return new()
            {
                PositiveCount = safePositiveCount,
                NegativeCount = safeNegativeCount,
                Summary = string.IsNullOrWhiteSpace(description)
                    ? null
                    : description.Trim()
            };
        }
    }

    private async Task<JsonDocument?> DownloadJsonAsync(
        string url,
        uint appId,
        string description,
        CancellationToken token)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            if (!response.IsSuccessStatusCode)
                return null;
            if (response.Content.Headers.ContentLength
                is > MaximumResponseBytes)
            {
                return null;
            }

            await using var source =
                await response.Content.ReadAsStreamAsync(token);
            using var destination = new MemoryStream();
            var buffer = new byte[81_920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, token);
                if (read == 0)
                    break;
                if (destination.Length + read > MaximumResponseBytes)
                    return null;
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    token);
            }

            destination.Position = 0;
            return await JsonDocument.ParseAsync(
                destination,
                cancellationToken: token);
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or JsonException
            or TaskCanceledException)
        {
            logger.LogDebug(
                exception,
                "Could not download Steam {Description} for AppID {AppId}",
                description,
                appId);
            return null;
        }
    }

    private static async Task<CacheEntry?> ReadCacheAsync(
        string path,
        CancellationToken token)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CacheEntry>(
                stream,
                JsonOptions,
                token);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(
        string path,
        CacheEntry entry,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4_096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    entry,
                    JsonOptions,
                    token);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record CacheEntry
    {
        public required DateTimeOffset CapturedAt { get; init; }
        public string? Publisher { get; init; }
        public long? PositiveCount { get; init; }
        public long? NegativeCount { get; init; }
        public string? RatingSummary { get; init; }

        public SteamStoreMetadata ToMetadata() =>
            new(
                Publisher,
                PositiveCount is { } positive
                && NegativeCount is { } negative
                    ? new()
                    {
                        PositiveCount = positive,
                        NegativeCount = negative,
                        Summary = RatingSummary
                    }
                    : null);
    }
}
