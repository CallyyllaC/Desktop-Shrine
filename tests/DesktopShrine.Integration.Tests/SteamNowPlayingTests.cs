using DesktopShrine.Abstractions;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.SteamNowPlaying;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class SteamNowPlayingTests
{
    [Fact]
    public async Task PublishesActiveFallbackWhenManifestIsUnknown()
    {
        var publisher = new CapturingPublisher();
        var context = new TestPluginContext(publisher);
        await using var plugin = new SteamNowPlayingPlugin(
            new FakeSteamRuntime(99_999, null));

        var token = TestContext.Current.CancellationToken;
        await plugin.InitialiseAsync(context, token);
        await plugin.StartAsync(token);
        var state = await publisher.Next.WaitAsync(
            TimeSpan.FromSeconds(5),
            token);

        Assert.True(state.IsAvailable);
        Assert.Equal("Unknown Steam game", state.Title);
        Assert.Equal("Steam App 99999", state.Subtitle);
        Assert.Equal("Unknown publisher", state.Artist);
        Assert.Equal("Steam", state.AlbumTitle);
        Assert.Equal("steam:99999", state.SourceAppUserModelId);
        Assert.Equal(PlaybackStatus.Playing, state.Status);
        Assert.Equal(InputActivityState.Active, plugin.ActivityState);

        await plugin.StopAsync(token);
    }

    [Fact]
    public async Task PublishesUnavailableStateWhenNoGameIsRunning()
    {
        var publisher = new CapturingPublisher();
        var context = new TestPluginContext(publisher);
        await using var plugin = new SteamNowPlayingPlugin(
            new FakeSteamRuntime(null, null));

        var token = TestContext.Current.CancellationToken;
        await plugin.InitialiseAsync(context, token);
        await plugin.StartAsync(token);
        var state = await publisher.Next.WaitAsync(
            TimeSpan.FromSeconds(5),
            token);

        Assert.False(state.IsAvailable);
        Assert.Equal(InputActivityState.Inactive, plugin.ActivityState);

        await plugin.StopAsync(token);
    }

    [Fact]
    public void DiscoversModernAndLegacyLibraryFolders()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var modern = Path.Combine(root, "Modern");
            var legacy = Path.Combine(root, "Legacy");
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            Directory.CreateDirectory(Path.Combine(modern, "steamapps"));
            Directory.CreateDirectory(Path.Combine(legacy, "steamapps"));
            File.WriteAllText(
                Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                $$"""
                "libraryfolders"
                {
                    "0"
                    {
                        "path" "{{Escape(root)}}"
                        "apps" { "413150" "1" }
                    }
                    "1"
                    {
                        "path" "{{Escape(modern)}}"
                    }
                    "2" "{{Escape(legacy)}}"
                }
                """);

            var libraries = SteamLibraryCatalog.DiscoverLibraries(root);

            Assert.Equal(3, libraries.Count);
            Assert.Contains(root, libraries, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(modern, libraries, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(legacy, libraries, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolvesManifestNameAndInstallDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var steamApps = Path.Combine(root, "steamapps");
            var installDirectory = Path.Combine(
                steamApps,
                "common",
                "Stardew Valley");
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(
                Path.Combine(steamApps, "appmanifest_413150.acf"),
                """
                "AppState"
                {
                    "appid" "413150"
                    "name" "Stardew Valley"
                    "installdir" "Stardew Valley"
                }
                """);

            var game = SteamLibraryCatalog.FindGame(root, 413150);

            Assert.NotNull(game);
            Assert.Equal("Stardew Valley", game.Name);
            Assert.Equal(installDirectory, game.InstallDirectory);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void VdfParserSupportsCommentsAndEscapedPaths()
    {
        var document = SteamVdf.Parse(
            """
            // Steam uses Valve KeyValues
            "root"
            {
                "path" "D:\\SteamLibrary"
                "quote" "A \"game\""
            }
            """);

        var root = document.GetObject("root");
        Assert.NotNull(root);
        Assert.Equal(@"D:\SteamLibrary", root.GetString("path"));
        Assert.Equal("A \"game\"", root.GetString("quote"));
    }

    [Fact]
    public async Task StoreMetadataLoadsPublisherAndRatingsFromCache()
    {
        var cache = CreateTemporaryDirectory();
        try
        {
            var handler = new SteamStoreHandler();
            using var httpClient = new HttpClient(handler);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StoreMetadataCacheDirectory"] = cache
                })
                .Build();
            var settings =
                SteamNowPlayingSettings.FromConfiguration(configuration);
            var provider = new SteamStoreMetadataProvider(
                httpClient,
                NullLogger.Instance,
                settings);
            var token = TestContext.Current.CancellationToken;

            var downloaded = await provider.GetAsync(413150, token);
            var cached = await provider.GetAsync(413150, token);

            Assert.NotNull(downloaded);
            Assert.Equal("ConcernedApe", downloaded.Publisher);
            Assert.NotNull(downloaded.Rating);
            Assert.Equal(741_234, downloaded.Rating.PositiveCount);
            Assert.Equal(18_765, downloaded.Rating.NegativeCount);
            Assert.Equal(
                "Overwhelmingly Positive",
                downloaded.Rating.Summary);
            Assert.Equal(downloaded, cached);
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    [Fact]
    public async Task ArtworkFindsNestedSteamLibraryCapsule()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const uint appId = 293760;
            var steamDirectory = Path.Combine(root, "Steam");
            var artworkCache = Path.Combine(root, "ArtworkCache");
            var libraryCapsule = Path.Combine(
                steamDirectory,
                "appcache",
                "librarycache",
                appId.ToString(),
                "82a64882635949020b8f88591b43cb3e",
                "library_capsule.png");
            Directory.CreateDirectory(
                Path.GetDirectoryName(libraryCapsule)!);
            byte[] sourceArtwork =
            [
                0x89, 0x50, 0x4E, 0x47,
                0x0D, 0x0A, 0x1A, 0x0A,
                0x01
            ];
            await File.WriteAllBytesAsync(
                libraryCapsule,
                sourceArtwork,
                TestContext.Current.CancellationToken);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ArtworkCacheDirectory"] = artworkCache,
                    ["DownloadArtwork"] = "false"
                })
                .Build();
            var settings =
                SteamNowPlayingSettings.FromConfiguration(configuration);
            using var httpClient = new HttpClient(
                new UnexpectedHttpHandler());
            var provider = new SteamArtworkProvider(
                httpClient,
                NullLogger.Instance,
                settings);

            var artwork = await provider.GetAsync(
                appId,
                steamDirectory,
                installDirectory: null,
                TestContext.Current.CancellationToken);

            Assert.NotNull(artwork);
            Assert.Equal("image/png", artwork.ContentType);
            Assert.Equal(sourceArtwork, artwork.Data);
            Assert.True(File.Exists(Path.Combine(
                artworkCache,
                appId.ToString(),
                "cover.png")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "DesktopShrineTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Escape(string path) =>
        path.Replace(@"\", @"\\", StringComparison.Ordinal);

    private sealed class FakeSteamRuntime(
        uint? appId,
        string? steamDirectory) : ISteamRuntime
    {
        public uint? GetRunningAppId() => appId;
        public string? GetSteamDirectory() => steamDirectory;
    }

    private sealed class SteamStoreHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var json = request.RequestUri!.AbsolutePath.Contains(
                "appdetails",
                StringComparison.Ordinal)
                ? """
                  {
                    "413150": {
                      "success": true,
                      "data": {
                        "publishers": [ "ConcernedApe" ]
                      }
                    }
                  }
                  """
                : """
                  {
                    "success": 1,
                    "query_summary": {
                      "review_score_desc": "Overwhelmingly Positive",
                      "total_positive": 741234,
                      "total_negative": 18765
                    }
                  }
                  """;
            return Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException(
                $"Unexpected request to {request.RequestUri}");
    }

    private sealed class CapturingPublisher : IPluginPublisher
    {
        private readonly TaskCompletionSource<NowPlayingState> next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NowPlayingState> Next => next.Task;

        public ValueTask PublishAsync<T>(
            string providedPortId,
            T payload,
            CancellationToken cancellationToken = default)
            where T : IShrineMessage
        {
            if (payload is NowPlayingState state)
                next.TrySetResult(state);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPluginContext(
        IPluginPublisher publisher) : IPluginContext
    {
        public string PluginId => "steam-now-playing";
        public IConfiguration Configuration { get; } =
            new ConfigurationBuilder().Build();
        public ILoggerFactory LoggerFactory =>
            NullLoggerFactory.Instance;
        public IPluginPublisher Publisher => publisher;
        public IPluginSubscriber Subscriber =>
            throw new NotSupportedException();
        public ILiveConfiguration<OutputInputProfile>? InputProfile => null;

        public ILiveConfiguration<TConfig> ObserveConfiguration<TConfig>(
            Func<IConfiguration, TConfig> snapshotFactory,
            Func<TConfig, ConfigurationValidationResult>? validator = null)
            where TConfig : notnull =>
            throw new NotSupportedException();
    }
}
