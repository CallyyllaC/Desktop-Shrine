using DesktopShrine.Contracts.Audio;
using DesktopShrine.Contracts.Hardware;
using DesktopShrine.Contracts.Media;
using DesktopShrine.Plugin.GOverlay;
using DesktopShrine.Plugin.GOverlay.Layout;
using Xunit;

namespace DesktopShrine.Integration.Tests;

public sealed class GOverlayLayoutTests
{
    [Fact]
    public void LayoutUsesTheRequestedThreeIndependentRegions()
    {
        var scene = new GOverlayDashboardLayout().Compose(
            SampleState(),
            new DateTime(2026, 7, 28, 9, 42, 0));

        Assert.Equal(new(0, 0, 480, 28), scene.Header.Bounds);
        Assert.Equal(new(0, 28, 480, 220), scene.Content.Bounds);
        Assert.Equal(new(0, 248, 480, 72), scene.Footer.Bounds);
        Assert.Equal(320, scene.Footer.Bounds.Bottom);

        var artwork = Assert.IsType<GOverlayArtworkCommand>(
            scene.Content.Commands.Single(command =>
                command.Key == "content.artwork"));
        Assert.Equal(new(10, 38, 200, 200), artwork.Bounds);
        var waterfall = Assert.IsType<GOverlayWaterfallCommand>(
            scene.Content.Commands.Single(command =>
                command.Key == "content.waterfall"));
        Assert.Equal(GOverlayWaterfallGeometry.Bounds, waterfall.Bounds);
        Assert.Equal(4, waterfall.ColumnWidth);
        Assert.Equal(59, waterfall.ColumnCount);
        Assert.Equal(new(258, 148, 1, 80), waterfall.WriteCursorBounds);
        Assert.Equal(new(224, 232, 240), waterfall.WriteCursorColour);
        Assert.DoesNotContain(
            scene.Content.Commands,
            command => command is GOverlayMeterBarCommand);

        var headerLabel = Assert.IsType<GOverlayTextCommand>(
            scene.Header.Commands.Single(command =>
                command.Key == "header.label"));
        Assert.Equal(new(9, 4, 462, 20), headerLabel.Bounds);
        Assert.DoesNotContain(
            scene.Header.Commands,
            command => command.Key is
                "header.time" or
                "header.status-dot" or
                "header.status");
    }

    [Theory]
    [InlineData(102, "1:42")]
    [InlineData(2602, "43:22")]
    [InlineData(7322, "2:02")]
    [InlineData(90061, "25:01")]
    public void FooterUsesTwoSignificantTimeUnits(
        double seconds,
        string expected)
    {
        var state = SampleState();
        state.PositionSeconds = seconds;
        state.DurationSeconds = seconds;
        var scene = new GOverlayDashboardLayout().Compose(
            state,
            DateTime.UnixEpoch);

        var elapsed = Assert.IsType<GOverlayTextCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "footer.elapsed"));
        var duration = Assert.IsType<GOverlayTextCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "footer.duration"));

        Assert.Equal(expected, elapsed.Text);
        Assert.Equal(expected, duration.Text);
        Assert.Equal(57, elapsed.Bounds.Width);
        Assert.Equal(57, duration.Bounds.Width);
        Assert.Equal(new GOverlayColour(9, 12, 17), elapsed.Background);
    }

    [Fact]
    public void GameFooterUsesCentreOutPositiveAndNegativeRatings()
    {
        var state = SampleState();
        state.HasRating = true;
        state.PositiveRatingCount = 900;
        state.NegativeRatingCount = 100;
        state.RatingSummary = "Very Positive";
        state.PositionSeconds = 0;
        state.DurationSeconds = 0;

        var scene = new GOverlayDashboardLayout().Compose(
            state,
            DateTime.UnixEpoch);

        var positive = Assert.IsType<GOverlayProgressCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "footer.rating-positive"));
        var negative = Assert.IsType<GOverlayProgressCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "footer.rating-negative"));
        var positiveCount = Assert.IsType<GOverlayTextCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "footer.rating-positive-count"));
        var negativeCount = Assert.IsType<GOverlayTextCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "footer.rating-negative-count"));

        Assert.Equal(0.9, positive.Value, 3);
        Assert.Equal(0.1, negative.Value, 3);
        Assert.Equal(
            GOverlayProgressDirection.RightToLeft,
            positive.Direction);
        Assert.Equal(
            GOverlayProgressDirection.LeftToRight,
            negative.Direction);
        Assert.Equal("+900", positiveCount.Text);
        Assert.Equal("-100", negativeCount.Text);
        Assert.DoesNotContain(
            scene.Footer.Commands,
            command => command.Key is
                "footer.elapsed" or "footer.progress" or "footer.duration");
    }

    [Fact]
    public void RatingSvgRendersPositiveOutwardFromTheCentre()
    {
        var state = SampleState();
        state.HasRating = true;
        state.PositiveRatingCount = 900;
        state.NegativeRatingCount = 100;
        state.PositionSeconds = 0;
        state.DurationSeconds = 0;
        var scene = new GOverlayDashboardLayout().Compose(
            state,
            DateTime.UnixEpoch);

        var svg = GOverlaySvgRenderer.Render(scene);

        Assert.Contains(
            "<rect x=\"90\" y=\"264\" width=\"148\" height=\"5\"",
            svg);
        Assert.Contains(
            "<rect x=\"242\" y=\"264\" width=\"16\" height=\"5\"",
            svg);
    }

    [Fact]
    public void FooterHidesTimelineWhenNoDurationOrRatingExists()
    {
        var state = SampleState();
        state.PositionSeconds = 0;
        state.DurationSeconds = 0;

        var scene = new GOverlayDashboardLayout().Compose(
            state,
            DateTime.UnixEpoch);

        Assert.DoesNotContain(
            scene.Footer.Commands,
            command => command.Key is
                "footer.elapsed" or "footer.progress" or "footer.duration");
        Assert.DoesNotContain(
            scene.Footer.Commands,
            command => command.Key.StartsWith(
                "footer.rating",
                StringComparison.Ordinal));
    }

    [Fact]
    public void HeaderCanBeReplacedWithoutReplacingContentOrFooter()
    {
        var layout = new GOverlayDashboardLayout(
            header: new TestHeaderRegion());

        var scene = layout.Compose(SampleState(), DateTime.UnixEpoch);

        Assert.Equal("replacement-header", scene.Header.Id);
        Assert.Equal("content", scene.Content.Id);
        Assert.Equal("footer", scene.Footer.Id);
    }

    [Fact]
    public void BridgeCodecRoundTripsAndCanReuseUnchangedArtwork()
    {
        var original = SampleState();
        original.HasRating = true;
        original.PositiveRatingCount = 12_345;
        original.NegativeRatingCount = 678;
        original.RatingSummary = "Very Positive";
        original.DontUseDrawPixels = true;
        using var firstFrame = new MemoryStream();
        GOverlayBridgeCodec.WriteFrame(
            firstFrame,
            original,
            includeArtwork: true);
        firstFrame.Position = 0;
        var first = GOverlayBridgeCodec.ReadFrame(firstFrame);

        var next = SampleState();
        next.Revision = 2;
        next.Title = "A different title";
        using var secondFrame = new MemoryStream();
        GOverlayBridgeCodec.WriteFrame(
            secondFrame,
            next,
            includeArtwork: false);
        secondFrame.Position = 0;
        var second = GOverlayBridgeCodec.ReadFrame(secondFrame, first);

        Assert.Equal(original.ArtworkData, first.ArtworkData);
        Assert.Equal(original.ArtworkData, second.ArtworkData);
        Assert.Equal(original.RenderGeneration, first.RenderGeneration);
        Assert.Equal(next.RenderGeneration, second.RenderGeneration);
        Assert.Equal("A different title", second.Title);
        Assert.Equal("Oxanium-Bold_20px.bin", second.FontName);
        Assert.Equal(original.WaterfallBands, first.WaterfallBands);
        Assert.Equal(original.WaterfallBands, second.WaterfallBands);
        Assert.Equal(
            original.WaterfallColumnSequence,
            second.WaterfallColumnSequence);
        Assert.Equal(
            original.WaterfallColumnWidth,
            second.WaterfallColumnWidth);
        Assert.Equal(original.HasRating, first.HasRating);
        Assert.Equal(
            original.PositiveRatingCount,
            first.PositiveRatingCount);
        Assert.Equal(
            original.NegativeRatingCount,
            first.NegativeRatingCount);
        Assert.True(first.DontUseDrawPixels);
    }

    [Fact]
    public void ArtworkTransferFlagDoesNotChangeLayoutMediaHardwareOrAudio()
    {
        var layout = new GOverlayDashboardLayout();
        var firstState = SampleState();
        firstState.DontUseDrawPixels = false;
        var secondState = SampleState();
        secondState.DontUseDrawPixels = true;

        var first = layout.Compose(firstState, DateTime.UnixEpoch);
        var second = layout.Compose(secondState, DateTime.UnixEpoch);

        Assert.Equal(
            first.Regions.SelectMany(region => region.Commands)
                .Select(command => command.Fingerprint),
            second.Regions.SelectMany(region => region.Commands)
                .Select(command => command.Fingerprint));
    }

    [Fact]
    public void HardwareStateTakesOverOnlyWhenTheMediaRouteIsInactive()
    {
        var builder = new GOverlayStateBuilder();
        builder.Update(Media([1, 2, 3], TimeSpan.Zero));
        builder.Update(Hardware());

        Assert.Equal(GOverlayDashboardMode.Media, builder.Current.Mode);

        var result = builder.UpdateMediaRoute(isActive: false);

        Assert.Equal(GOverlayDashboardMode.Hardware, result.Mode);
        Assert.Equal("AMD Radeon RX 5700 XT", result.GpuName);
        Assert.Equal("AMD Ryzen 9 5950X", result.CpuName);
        Assert.Equal(
            ["LOAD", "VRAM", "HOT", "PWR", "CLK"],
            result.GpuMetrics.Select(metric => metric.Label));
        Assert.Equal("46%", result.GpuMetrics[1].DisplayValue);
        Assert.Equal("84°", result.GpuMetrics[2].DisplayValue);
        Assert.Equal("176W", result.GpuMetrics[3].DisplayValue);
        Assert.Equal("1.9G", result.GpuMetrics[4].DisplayValue);
        Assert.Equal(
            ["LOAD", "CMOS", "TEMP", "PWR", "CLK"],
            result.CpuMetrics.Select(metric => metric.Label));
        Assert.Equal("3.12V", result.CpuMetrics[1].DisplayValue);
        Assert.Equal("63°", result.CpuMetrics[2].DisplayValue);
        Assert.Equal("74W", result.CpuMetrics[3].DisplayValue);
        Assert.Equal("4.5G", result.CpuMetrics[4].DisplayValue);
        Assert.All(result.CpuMetrics, metric => Assert.True(metric.IsAvailable));
        Assert.Equal("RAM  32.0 / 64.0 GB", result.PhysicalMemorySummary);
        Assert.Equal("VIRT  40.0 / 96.0 GB", result.VirtualMemorySummary);
        Assert.Equal(0.5, result.PhysicalMemoryLevel, 3);
        Assert.Equal(40d / 96d, result.VirtualMemoryLevel, 3);

        Assert.Equal(
            GOverlayDashboardMode.Media,
            builder.UpdateMediaRoute(isActive: true).Mode);
    }

    [Fact]
    public void HardwareMetersExpandOutwardFromTheCentreDivider()
    {
        var builder = new GOverlayStateBuilder();
        builder.Update(Hardware());
        var state = builder.UpdateMediaRoute(isActive: false);

        var scene = new GOverlayDashboardLayout().Compose(
            state,
            DateTime.UnixEpoch);
        var gpu = Assert.IsType<GOverlayProgressCommand>(
            scene.Content.Commands.Single(command =>
                command.Key == "hardware.gpu.0.meter"));
        var cpu = Assert.IsType<GOverlayProgressCommand>(
            scene.Content.Commands.Single(command =>
                command.Key == "hardware.cpu.0.meter"));

        Assert.Equal(new(10, 87, 218, 6), gpu.Bounds);
        Assert.Equal(new(252, 87, 218, 6), cpu.Bounds);
        Assert.Equal(
            GOverlayProgressDirection.RightToLeft,
            gpu.Direction);
        Assert.Equal(
            GOverlayProgressDirection.LeftToRight,
            cpu.Direction);
        Assert.Equal(0.92, gpu.Value, 3);
        Assert.Equal(0.34, cpu.Value, 3);
        Assert.Contains(
            scene.Header.Commands,
            command => command is GOverlayTextCommand text
                && text.Text == "HARDWARE MONITOR");
        Assert.DoesNotContain(
            scene.Content.Commands,
            command => command.Key == "content.artwork");

        var physicalMemory = Assert.IsType<GOverlayProgressCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "hardware.footer.physical-memory.meter"));
        var virtualMemory = Assert.IsType<GOverlayProgressCommand>(
            scene.Footer.Commands.Single(command =>
                command.Key == "hardware.footer.virtual-memory.meter"));
        Assert.Equal(
            GOverlayProgressDirection.RightToLeft,
            physicalMemory.Direction);
        Assert.Equal(
            GOverlayProgressDirection.LeftToRight,
            virtualMemory.Direction);
        Assert.Equal(0.5, physicalMemory.Value, 3);
        Assert.Equal(40d / 96d, virtualMemory.Value, 3);
    }

    [Fact]
    public void RouteProviderChangesAdvanceTheRenderGenerationOnce()
    {
        var builder = new GOverlayStateBuilder();

        var hardware = builder.UpdateMediaRoute(
            selectedProviderId: null);
        var sameHardware = builder.UpdateMediaRoute(
            selectedProviderId: null);
        var windows = builder.UpdateMediaRoute(
            "windows-now-playing");
        var sameWindows = builder.UpdateMediaRoute(
            "windows-now-playing");
        var steam = builder.UpdateMediaRoute(
            "steam-now-playing");
        var backToHardware = builder.UpdateMediaRoute(
            selectedProviderId: null);

        Assert.Equal(
            hardware.RenderGeneration,
            sameHardware.RenderGeneration);
        Assert.True(
            windows.RenderGeneration > hardware.RenderGeneration);
        Assert.Equal(
            windows.RenderGeneration,
            sameWindows.RenderGeneration);
        Assert.True(
            steam.RenderGeneration > windows.RenderGeneration);
        Assert.True(
            backToHardware.RenderGeneration
                > steam.RenderGeneration);
    }

    [Fact]
    public void BridgeCodecRoundTripsHardwareDashboardState()
    {
        var builder = new GOverlayStateBuilder();
        builder.Update(Hardware());
        var original = builder.UpdateMediaRoute(isActive: false);
        using var frame = new MemoryStream();

        GOverlayBridgeCodec.WriteFrame(
            frame,
            original,
            includeArtwork: false);
        frame.Position = 0;
        var result = GOverlayBridgeCodec.ReadFrame(frame);

        Assert.Equal(GOverlayDashboardMode.Hardware, result.Mode);
        Assert.Equal(original.RenderGeneration, result.RenderGeneration);
        Assert.Equal(original.HardwareProvider, result.HardwareProvider);
        Assert.Equal(
            original.HardwareProviderVersion,
            result.HardwareProviderVersion);
        Assert.Equal(original.GpuName, result.GpuName);
        Assert.Equal(original.CpuName, result.CpuName);
        Assert.Equal(5, result.GpuMetrics.Length);
        Assert.Equal("176W", result.GpuMetrics[3].DisplayValue);
        Assert.Equal(5, result.CpuMetrics.Length);
        Assert.True(result.CpuMetrics[2].IsAvailable);
        Assert.Equal(
            original.PhysicalMemorySummary,
            result.PhysicalMemorySummary);
        Assert.Equal(
            original.VirtualMemorySummary,
            result.VirtualMemorySummary);
        Assert.Equal(
            original.PhysicalMemoryLevel,
            result.PhysicalMemoryLevel);
        Assert.Equal(
            original.VirtualMemoryLevel,
            result.VirtualMemoryLevel);
        Assert.Equal("63°", result.CpuMetrics[2].DisplayValue);
    }

    [Fact]
    public void CurrentContractsBuildACompleteDashboardState()
    {
        var builder = new GOverlayStateBuilder();
        var artwork = new byte[] { 1, 2, 3, 4 };
        builder.Update(new NowPlayingState
        {
            IsAvailable = true,
            CapturedAt = DateTimeOffset.UtcNow,
            Title = "Song Title",
            Artist = "Artist Name",
            AlbumTitle = "Album Name",
            Status = PlaybackStatus.Playing,
            Position = TimeSpan.FromSeconds(102),
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(238),
            Artwork = new() { ContentType = "image/png", Data = artwork }
        });
        builder.Update(new MediaColourPalette(
            new(new(10, 20, 30)),
            new(new(40, 50, 60)),
            new(new(1, 2, 3)),
            new(new(220, 230, 240)),
            new()));
        var capturedAt = DateTimeOffset.UtcNow;
        builder.Update(Frame(capturedAt, 1, [0.25f, 0, 0, 0, 0, 0, 0, 0]));
        var result = builder.PrepareDisplayState(
            capturedAt + TimeSpan.FromMilliseconds(100));

        Assert.Equal("Song Title", result.Title);
        Assert.Equal("Artist Name", result.Artist);
        Assert.Equal("Album Name", result.Context);
        Assert.Equal(102, result.PositionSeconds);
        Assert.Equal(238, result.DurationSeconds);
        Assert.Equal(artwork, result.ArtworkData);
        Assert.True(result.HasWaterfallColumn);
        Assert.Equal(8, result.WaterfallBands.Length);
        Assert.True(result.WaterfallBands[0] > 0);
        Assert.Equal("ACTIVE", result.PlaybackStatus);
    }

    [Fact]
    public void SteamPublisherAndRatingBuildTheGameDashboardState()
    {
        var builder = new GOverlayStateBuilder();
        builder.Update(new NowPlayingState
        {
            IsAvailable = true,
            CapturedAt = DateTimeOffset.UtcNow,
            SourceAppUserModelId = "steam:413150",
            Title = "Stardew Valley",
            Artist = "ConcernedApe",
            AlbumTitle = "Steam",
            Status = PlaybackStatus.Playing,
            Rating = new()
            {
                PositiveCount = 741_234,
                NegativeCount = 18_765,
                Summary = "Overwhelmingly Positive"
            }
        });

        var result = builder.Current;

        Assert.Equal("ConcernedApe", result.Artist);
        Assert.Equal("Overwhelmingly Positive", result.Context);
        Assert.True(result.HasRating);
        Assert.Equal(741_234, result.PositiveRatingCount);
        Assert.Equal(18_765, result.NegativeRatingCount);
        Assert.Equal(0, result.DurationSeconds);
    }

    [Fact]
    public void WaterfallAggregationRetainsShortTransientPeaks()
    {
        var aggregator = new GOverlayWaterfallAggregator(
            new GOverlayWaterfallOptions
            {
                NoiseFloorDb = -60,
                VisualCeilingDb = 0
            });
        var now = DateTimeOffset.UtcNow;
        aggregator.Add(Frame(
            now,
            1,
            [0.001f, 0, 0, 0, 0, 0, 0, 0]));
        aggregator.Add(Frame(
            now + TimeSpan.FromMilliseconds(20),
            2,
            [0.5f, 0, 0, 0, 0, 0, 0, 0]));

        var interval = aggregator.Consume(
            now + TimeSpan.FromMilliseconds(100));

        Assert.True(interval.HasColumn);
        Assert.False(interval.BecameInactive);
        Assert.Equal(8, interval.Values.Length);
        Assert.True(interval.Values[0] > 0.8f);
        Assert.All(interval.Values.Skip(1), value =>
            Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void WaterfallRoutesEachLogarithmicFrequencyBand(int activeBand)
    {
        var aggregator = new GOverlayWaterfallAggregator(
            new GOverlayWaterfallOptions());
        var amplitudes = new float[8];
        amplitudes[activeBand] = 0.1f;
        var now = DateTimeOffset.UtcNow;
        aggregator.Add(Frame(now, 1, amplitudes));

        var interval = aggregator.Consume(now);

        Assert.True(interval.Values[activeBand] > 0);
        for (var band = 0; band < interval.Values.Length; band++)
            if (band != activeBand)
                Assert.Equal(0, interval.Values[band]);
    }

    [Fact]
    public void WaterfallUsesImmediateAttackAndConfiguredRelease()
    {
        var aggregator = new GOverlayWaterfallAggregator(
            new GOverlayWaterfallOptions
            {
                NoiseFloorDb = -60,
                VisualCeilingDb = 0,
                Release = TimeSpan.FromMilliseconds(500),
                ColumnInterval = TimeSpan.FromMilliseconds(100)
            });
        var now = DateTimeOffset.UtcNow;
        aggregator.Add(Frame(
            now,
            1,
            [1, 0, 0, 0, 0, 0, 0, 0]));
        var attack = aggregator.Consume(now);

        aggregator.Add(Frame(
            now + TimeSpan.FromMilliseconds(100),
            2,
            [0, 0, 0, 0, 0, 0, 0, 0]));
        var firstRelease = aggregator.Consume(
            now + TimeSpan.FromMilliseconds(100));
        var withoutAudio = aggregator.Consume(
            now + TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, attack.Values[0], 3);
        Assert.InRange(firstRelease.Values[0], 0.8f, 0.83f);
        Assert.False(withoutAudio.HasColumn);
    }

    [Fact]
    public void WaterfallRetainsFramesUntilTheFiveHundredMillisecondColumn()
    {
        var aggregator = new GOverlayWaterfallAggregator(
            new GOverlayWaterfallOptions
            {
                NoiseFloorDb = -60,
                VisualCeilingDb = 0,
                ColumnInterval = TimeSpan.FromMilliseconds(500)
            });
        var now = DateTimeOffset.UtcNow;
        aggregator.Add(Frame(
            now,
            1,
            [0.01f, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(aggregator.Consume(now).HasColumn);

        aggregator.Add(Frame(
            now + TimeSpan.FromMilliseconds(100),
            2,
            [0.8f, 0, 0, 0, 0, 0, 0, 0]));
        aggregator.Add(Frame(
            now + TimeSpan.FromMilliseconds(300),
            3,
            [0.01f, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(aggregator.Consume(
            now + TimeSpan.FromMilliseconds(499)).HasColumn);

        var interval = aggregator.Consume(
            now + TimeSpan.FromMilliseconds(500));

        Assert.True(interval.HasColumn);
        Assert.True(interval.Values[0] > 0.85f);
    }

    [Fact]
    public void WaterfallResetsAfterAudioInputBecomesInactive()
    {
        var aggregator = new GOverlayWaterfallAggregator(
            new GOverlayWaterfallOptions
            {
                InactiveAfter = TimeSpan.FromMilliseconds(700)
            });
        var now = DateTimeOffset.UtcNow;
        aggregator.Add(Frame(
            now,
            1,
            [0.5f, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(aggregator.Consume(now).HasColumn);

        var inactive = aggregator.Consume(
            now + TimeSpan.FromMilliseconds(700));

        Assert.True(inactive.BecameInactive);
        Assert.False(inactive.HasColumn);
        Assert.False(aggregator.Consume(
            now + TimeSpan.FromSeconds(2)).BecameInactive);
    }

    [Fact]
    public void WaterfallWritePositionWrapsWithoutResettingHistory()
    {
        var builder = new GOverlayStateBuilder(
            new GOverlayWaterfallOptions
            {
                ColumnInterval = TimeSpan.FromMilliseconds(100)
            });
        var now = DateTimeOffset.UtcNow;
        GOverlayDashboardState? result = null;
        for (var index = 0;
             index <= GOverlayWaterfallGeometry.ColumnCount;
             index++)
        {
            builder.Update(Frame(
                now + TimeSpan.FromMilliseconds(index * 100),
                index,
                [0.2f, 0, 0, 0, 0, 0, 0, 0]));
            result = builder.PrepareDisplayState(
                now + TimeSpan.FromMilliseconds(index * 100));
        }

        Assert.NotNull(result);
        Assert.Equal(0, result.WaterfallWritePosition);
        Assert.Equal(
            GOverlayWaterfallGeometry.ColumnCount + 1,
            result.WaterfallColumnSequence);
    }

    [Fact]
    public void SvgWaterfallShowsTheMovingWriteCursor()
    {
        var state = SampleState();
        var waterfall = Assert.IsType<GOverlayWaterfallCommand>(
            new GOverlayDashboardLayout()
                .Compose(state, DateTime.UtcNow)
                .Content.Commands.Single(command =>
                    command.Key == "content.waterfall"));

        var svg = GOverlaySvgRenderer.Render(
            new GOverlayDashboardLayout().Compose(
                state,
                DateTime.UtcNow));

        Assert.Contains(
            $"<rect x=\"{waterfall.WriteCursorBounds.X}\" "
            + $"y=\"{waterfall.WriteCursorBounds.Y}\" "
            + $"width=\"{waterfall.WriteCursorBounds.Width}\" "
            + $"height=\"{waterfall.WriteCursorBounds.Height}\" "
            + $"fill=\"{waterfall.WriteCursorColour.Hex}\"/>",
            svg,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WaterfallColumnWidthControlsCircularHistoryLength()
    {
        const int columnWidth = 6;
        var builder = new GOverlayStateBuilder(
            new GOverlayWaterfallOptions
            {
                ColumnInterval = TimeSpan.FromMilliseconds(500)
            },
            columnWidth);
        var columnCount =
            GOverlayWaterfallGeometry.ColumnCountFor(columnWidth);
        var now = DateTimeOffset.UtcNow;
        GOverlayDashboardState? result = null;
        for (var index = 0; index <= columnCount; index++)
        {
            builder.Update(Frame(
                now + TimeSpan.FromMilliseconds(index * 500),
                index,
                [0.2f, 0, 0, 0, 0, 0, 0, 0]));
            result = builder.PrepareDisplayState(
                now + TimeSpan.FromMilliseconds(index * 500));
        }

        Assert.NotNull(result);
        Assert.Equal(columnWidth, result.WaterfallColumnWidth);
        Assert.Equal(0, result.WaterfallWritePosition);
        Assert.Equal(columnCount + 1, result.WaterfallColumnSequence);
    }

    [Fact]
    public void WaterfallResetsForPaletteButNotProgressChanges()
    {
        var builder = new GOverlayStateBuilder();
        var artwork = new byte[] { 1, 2, 3 };
        var media = Media(artwork, TimeSpan.FromSeconds(10));
        builder.Update(media);
        builder.Update(Palette(new(10, 20, 30)));
        var initialReset = builder.Current.WaterfallResetSequence;

        builder.Update(media with
        {
            Position = TimeSpan.FromSeconds(20)
        });
        Assert.Equal(
            initialReset,
            builder.Current.WaterfallResetSequence);

        builder.Update(Palette(new(11, 20, 30)));
        Assert.True(
            builder.Current.WaterfallResetSequence > initialReset);
        Assert.Equal(0, builder.Current.WaterfallWritePosition);
        Assert.False(builder.Current.HasWaterfallColumn);
    }

    [Fact]
    public void PlayingProgressAdvancesBetweenMediaEventsAndRestartResetsIt()
    {
        var anchor = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);
        var media = new NowPlayingState
        {
            IsAvailable = true,
            CapturedAt = anchor,
            TimelineLastUpdatedAt = anchor,
            Status = PlaybackStatus.Playing,
            PlaybackRate = 1,
            Position = TimeSpan.FromSeconds(10),
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromMinutes(4)
        };
        var builder = new GOverlayStateBuilder();
        builder.Update(media);

        var advanced = builder.PrepareDisplayState(
            anchor + TimeSpan.FromSeconds(3.8));
        builder.Update(media with
        {
            CapturedAt = anchor + TimeSpan.FromSeconds(4),
            TimelineLastUpdatedAt = anchor + TimeSpan.FromSeconds(4),
            Position = TimeSpan.Zero
        });

        Assert.Equal(13, advanced.PositionSeconds);
        Assert.Equal(0, builder.Current.PositionSeconds);
    }

    [Fact]
    public void AudioFramesAreAggregatedWithoutRebuildingProgressState()
    {
        var anchor = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);
        var builder = new GOverlayStateBuilder();
        builder.Update(new NowPlayingState
        {
            IsAvailable = true,
            CapturedAt = anchor,
            TimelineLastUpdatedAt = anchor,
            Status = PlaybackStatus.Playing,
            Position = TimeSpan.FromSeconds(10),
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromMinutes(4),
            Artwork = new()
            {
                ContentType = "image/png",
                Data = new byte[1024]
            }
        });
        var revision = builder.Current.Revision;

        for (var index = 0; index < 50; index++)
        {
            builder.Update(Frame(
                anchor + TimeSpan.FromMilliseconds(index * 20),
                index,
                [0.2f, 0, 0, 0, 0, 0, 0, 0]));
        }

        Assert.Equal(revision, builder.Current.Revision);

        var displayed = builder.PrepareDisplayState(
            anchor + TimeSpan.FromSeconds(1));

        Assert.Equal(11, displayed.PositionSeconds);
        Assert.True(displayed.HasWaterfallColumn);
        Assert.True(displayed.Revision > revision);
    }

    [Fact]
    public void PausedProgressDoesNotAdvanceBetweenMediaEvents()
    {
        var anchor = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);
        var builder = new GOverlayStateBuilder();
        builder.Update(new NowPlayingState
        {
            IsAvailable = true,
            CapturedAt = anchor,
            TimelineLastUpdatedAt = anchor,
            Status = PlaybackStatus.Paused,
            Position = TimeSpan.FromSeconds(42),
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromMinutes(4)
        });

        var displayed = builder.PrepareDisplayState(
            anchor + TimeSpan.FromSeconds(30));

        Assert.Equal(42, displayed.PositionSeconds);
    }

    [Fact]
    public void WaterfallResetsForArtworkAndAudioInactivity()
    {
        var builder = new GOverlayStateBuilder(
            new GOverlayWaterfallOptions
            {
                InactiveAfter = TimeSpan.FromMilliseconds(700)
            });
        var now = DateTimeOffset.UtcNow;
        builder.Update(Media([1, 2, 3], TimeSpan.Zero));
        var mediaReset = builder.Current.WaterfallResetSequence;
        builder.Update(Frame(
            now,
            1,
            [0.2f, 0, 0, 0, 0, 0, 0, 0]));
        var active = builder.PrepareDisplayState(now);

        Assert.True(active.AudioActive);
        Assert.True(active.HasWaterfallColumn);

        builder.Update(Media([4, 5, 6], TimeSpan.Zero));
        var artworkReset = builder.Current.WaterfallResetSequence;
        Assert.True(artworkReset > mediaReset);
        Assert.False(builder.Current.HasWaterfallColumn);

        builder.Update(Frame(
            now + TimeSpan.FromMilliseconds(10),
            2,
            [0.2f, 0, 0, 0, 0, 0, 0, 0]));
        builder.PrepareDisplayState(
            now + TimeSpan.FromMilliseconds(10));
        var inactive = builder.PrepareDisplayState(
            now + TimeSpan.FromMilliseconds(710));

        Assert.False(inactive.AudioActive);
        Assert.False(inactive.HasWaterfallColumn);
        Assert.Equal(0, inactive.WaterfallWritePosition);
        Assert.True(
            inactive.WaterfallResetSequence > artworkReset);
    }

    [Fact]
    public void WaterfallPaletteInterpolatesThroughArtworkColours()
    {
        var background = new GOverlayColour(9, 12, 17);
        var dark = new GOverlayColour(0, 0, 0);
        var dominant = new GOverlayColour(30, 60, 90);
        var accent = new GOverlayColour(90, 150, 210);
        var light = new GOverlayColour(255, 255, 255);

        Assert.Equal(
            background,
            GOverlayWaterfallPalette.Map(
                0,
                true,
                background,
                dark,
                dominant,
                accent,
                light));
        Assert.Equal(
            dark,
            GOverlayWaterfallPalette.Map(
                0.08,
                true,
                background,
                dark,
                dominant,
                accent,
                light));
        Assert.Equal(
            dominant,
            GOverlayWaterfallPalette.Map(
                0.40,
                true,
                background,
                dark,
                dominant,
                accent,
                light));
        Assert.Equal(
            accent,
            GOverlayWaterfallPalette.Map(
                0.70,
                true,
                background,
                dark,
                dominant,
                accent,
                light));
        Assert.Equal(
            light,
            GOverlayWaterfallPalette.Map(
                1,
                true,
                background,
                dark,
                dominant,
                accent,
                light));
    }

    [Fact]
    public void EachHostStateBuilderStartsWithNewRenderAndWaterfallGenerations()
    {
        var first = new GOverlayStateBuilder();
        var second = new GOverlayStateBuilder();

        Assert.NotEqual(
            first.Current.RenderGeneration,
            second.Current.RenderGeneration);
        Assert.NotEqual(
            first.Current.WaterfallResetSequence,
            second.Current.WaterfallResetSequence);
    }

    [Theory]
    [InlineData(10, 1, new int[0])]
    [InlineData(10, 3, new[] { 11, 12 })]
    [InlineData(58, 3, new[] { 0, 1 })]
    public void WaterfallIdentifiesSkippedCircularSlots(
        int previousPosition,
        long sequenceDelta,
        int[] expected)
    {
        Assert.Equal(
            expected,
            GOverlayWaterfallGeometry.SkippedPositions(
                previousPosition,
                sequenceDelta));
    }

    [Fact]
    public void SvgRendererProducesA480By320Preview()
    {
        var svg = GOverlaySvgRenderer.Render(
            new GOverlayDashboardLayout().Compose(
                SampleState(),
                new DateTime(2026, 7, 28, 9, 42, 0)));

        Assert.Contains("width=\"480\" height=\"320\"", svg);
        Assert.Contains("MEDIA SESSION", svg);
        Assert.Contains("Song Title", svg);
        Assert.Contains("dominant", svg);
    }

    [Theory]
    [InlineData(1920, 1080, 10, 82, 200, 112)]
    [InlineData(1000, 1500, 43, 38, 133, 200)]
    [InlineData(800, 800, 10, 38, 200, 200)]
    public void ArtworkIsContainedInsideTheVisibleArea(
        int sourceWidth,
        int sourceHeight,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var result = GOverlayArtworkSizing.Contain(
            sourceWidth,
            sourceHeight,
            new(10, 38, 200, 200));

        Assert.Equal(
            new(
                expectedX,
                expectedY,
                expectedWidth,
                expectedHeight),
            result);
    }

    private static GOverlayDashboardState SampleState() => new()
    {
        Revision = 1,
        RenderGeneration = 42,
        IsAvailable = true,
        PlaybackStatus = "ACTIVE",
        Title = "Song Title",
        Artist = "Artist Name",
        Context = "Album / Source",
        PositionSeconds = 102,
        DurationSeconds = 238,
        Dominant = new(92, 76, 180),
        Accent = new(74, 210, 226),
        Dark = new(12, 16, 25),
        Light = new(224, 232, 240),
        HasPalette = true,
        AudioActive = true,
        WaterfallResetSequence = 2,
        WaterfallColumnSequence = 7,
        WaterfallWritePosition = 6,
        HasWaterfallColumn = true,
        WaterfallBands =
        [
            0.04f,
            0.12f,
            0.38f,
            0.91f,
            0.54f,
            0.28f,
            0.73f,
            0.16f
        ],
        ArtworkKey = "sample",
        ArtworkContentType = "image/png",
        ArtworkData = [1, 2, 3]
    };

    private static AudioSpectrumFrame Frame(
        DateTimeOffset capturedAt,
        int sequence,
        IReadOnlyList<float> bandAmplitudes)
    {
        const int sampleRate = 48_000;
        const int fftSize = 4_800;
        var spectrum = new float[(fftSize / 2) + 1];
        for (var bandIndex = 0;
             bandIndex < GOverlayWaterfallOptions.DefaultBands.Count;
             bandIndex++)
        {
            var band = GOverlayWaterfallOptions.DefaultBands[bandIndex];
            var firstBin = (int)Math.Ceiling(
                band.MinimumHz * fftSize / sampleRate);
            var finalBin = (int)Math.Ceiling(
                band.MaximumHz * fftSize / sampleRate);
            var fftMagnitude = bandAmplitudes[bandIndex]
                * fftSize / 4;
            for (var bin = firstBin; bin < finalBin; bin++)
                spectrum[bin] = fftMagnitude;
        }

        return new()
        {
            CapturedAt = capturedAt,
            Sequence = sequence,
            SampleRate = sampleRate,
            FftSize = fftSize,
            Spectrum = spectrum,
            Waveform = []
        };
    }

    private static NowPlayingState Media(
        byte[] artwork,
        TimeSpan position) =>
        new()
        {
            IsAvailable = true,
            CapturedAt = DateTimeOffset.UtcNow,
            Title = "Song Title",
            Artist = "Artist Name",
            AlbumTitle = "Album Name",
            Status = PlaybackStatus.Playing,
            Position = position,
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromMinutes(4),
            Artwork = new()
            {
                ContentType = "image/png",
                Data = artwork
            }
        };

    private static MediaColourPalette Palette(BaseColour dominant) =>
        new(
            new(dominant),
            new(new(40, 50, 60)),
            new(new(1, 2, 3)),
            new(new(220, 230, 240)),
            new());

    private static HardwareMonitorState Hardware() =>
        new()
        {
            IsAvailable = true,
            CapturedAt = new DateTimeOffset(
                2026,
                7,
                30,
                13,
                32,
                10,
                TimeSpan.Zero),
            Provider = "LibreHardwareMonitor",
            ProviderVersion = "0.9.6.0",
            System = new()
            {
                ProcessorName = "AMD Ryzen 9 5950X",
                CpuUsagePercent = 34,
                CpuTemperatureCelsius = 63,
                CpuPowerWatts = 74,
                CpuPowerLimitWatts = 142,
                CpuClockMegahertz = 4_500,
                CpuClockLimitMegahertz = 5_050,
                UsedMemoryBytes = 32L * 1024 * 1024 * 1024,
                TotalMemoryBytes = 64L * 1024 * 1024 * 1024,
                UsedVirtualMemoryBytes = 40L * 1024 * 1024 * 1024,
                TotalVirtualMemoryBytes = 96L * 1024 * 1024 * 1024,
                CmosBatteryVoltageVolts = 3.12
            },
            GraphicsProcessors =
            [
                new()
                {
                    Id = 1,
                    Name = "AMD Radeon RX 5700 XT",
                    Type = GraphicsProcessorType.Discrete,
                    UsagePercent = 92,
                    UsedMemoryBytes = 4_600,
                    TotalMemoryBytes = 10_000,
                    HotspotTemperatureCelsius = 84,
                    PowerWatts = 176,
                    ClockMegahertz = 1_900,
                    FanSpeedRpm = 1_700
                }
            ]
        };

    private sealed class TestHeaderRegion : IGOverlayHeaderRegion
    {
        public GOverlayRegionScene Compose(
            GOverlayDashboardState state,
            GOverlayRectangle bounds,
            DateTime localTime)
        {
            _ = state;
            _ = localTime;
            return new(
                "replacement-header",
                bounds,
                [
                    new GOverlayFillRectangleCommand(
                        "replacement",
                        bounds,
                        new(1, 2, 3))
                ]);
        }
    }
}
