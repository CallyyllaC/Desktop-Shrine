using DesktopShrine.Plugin.GOverlay.LegacySdk;
using Xunit;

namespace DesktopShrine.GOverlay.Legacy.Tests;

public sealed class AdaptiveArtworkRectangleCompressorTests
{
    [Fact]
    public void SolidColourBecomesOneRectangle()
    {
        var result = AdaptiveArtworkRectangleCompressor.Compress(
            Enumerable.Repeat(0xF81F, 200 * 193).ToArray(),
            200,
            193);

        var rectangle = Assert.Single(result.Rectangles);
        Assert.Equal(0, rectangle.X);
        Assert.Equal(0, rectangle.Y);
        Assert.Equal(200, rectangle.Width);
        Assert.Equal(193, rectangle.Height);
        Assert.Equal(1, result.PaletteColourCount);
    }

    [Fact]
    public void TwoColourImageUsesVeryFewRectangles()
    {
        const int width = 200;
        const int height = 193;
        var pixels = new int[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = x < width / 2
                    ? 0xF800
                    : 0x001F;

        var result = AdaptiveArtworkRectangleCompressor.Compress(
            pixels,
            width,
            height);

        Assert.InRange(result.Rectangles.Count, 2, 4);
        AssertCompleteCoverage(result.Rectangles, width, height);

        var plan = AdaptiveArtworkRectangleCompressor
            .CreateProgressivePlan(pixels, width, height);
        Assert.Empty(plan.Stages[4].Rectangles);
        Assert.False(plan.Stage5Eligible);
    }

    [Fact]
    public void HighDetailQuadrantsSubdivideToTwoByTwoBlocks()
    {
        var pixels = new[]
        {
            0xF800, 0xF800, 0x07E0, 0x07E0,
            0xF800, 0xF800, 0x07E0, 0x07E0,
            0x001F, 0x001F, 0xFFFF, 0xFFFF,
            0x001F, 0x001F, 0xFFFF, 0xFFFF
        };

        var result = AdaptiveArtworkRectangleCompressor.Compress(
            pixels,
            4,
            4);

        Assert.Equal(4, result.Rectangles.Count);
        Assert.All(result.Rectangles, rectangle =>
        {
            Assert.Equal(2, rectangle.Width);
            Assert.Equal(2, rectangle.Height);
        });
        AssertCompleteCoverage(result.Rectangles, 4, 4);
    }

    [Fact]
    public void DetailedOddSizedImageIsDeterministicAndBounded()
    {
        const int width = 199;
        const int height = 187;
        var random = new Random(42199);
        var pixels = Enumerable.Range(0, width * height)
            .Select(_ => random.Next(0, 65536))
            .ToArray();

        var first = AdaptiveArtworkRectangleCompressor.Compress(
            pixels,
            width,
            height);
        var second = AdaptiveArtworkRectangleCompressor.Compress(
            pixels,
            width,
            height);

        Assert.InRange(
            first.Rectangles.Count,
            1,
            AdaptiveArtworkRectangleCompressor.DefaultMaximumRectangles);
        Assert.Equal(
            first.Rectangles.Select(RectangleIdentity),
            second.Rectangles.Select(RectangleIdentity));
        Assert.InRange(
            first.PaletteColourCount,
            1,
            AdaptiveArtworkRectangleCompressor.DefaultPaletteColours);
        Assert.All(first.Rectangles, rectangle =>
        {
            Assert.InRange(rectangle.Colour, 0, ushort.MaxValue);
            Assert.True(rectangle.Width > 0);
            Assert.True(rectangle.Height > 0);
        });
        AssertCompleteCoverage(first.Rectangles, width, height);
    }

    [Fact]
    public void ProgressivePlanUsesFiveDeterministicIncrementalStages()
    {
        const int width = 199;
        const int height = 187;
        var random = new Random(42199);
        var pixels = Enumerable.Range(0, width * height)
            .Select(_ => random.Next(0, 65536))
            .ToArray();

        var first = AdaptiveArtworkRectangleCompressor
            .CreateProgressivePlan(pixels, width, height);
        var second = AdaptiveArtworkRectangleCompressor
            .CreateProgressivePlan(pixels, width, height);

        Assert.Equal(
            AdaptiveArtworkRectangleCompressor.DefaultStageCount,
            first.Stages.Count);
        Assert.Equal(
            AdaptiveArtworkRectangleCompressor.DefaultStageRectangleTargets,
            first.Stages
                .Take(AdaptiveArtworkRectangleCompressor.DefaultNormalStageCount)
                .Select(stage => stage.RectangleTarget));
        Assert.All(first.Stages, stage => Assert.NotEmpty(stage.Rectangles));
        Assert.True(first.Stages
            .Zip(first.Stages.Skip(1), (left, right) =>
                left.CumulativeLeafCount < right.CumulativeLeafCount)
            .All(increases => increases));
        Assert.Equal(
            first.Stages.Select(StageIdentity),
            second.Stages.Select(StageIdentity));

        AssertCompleteCoverage(first.Stages[0].Rectangles, width, height);
        AssertProgressivePlanMatchesFinal(first);
        Assert.All(first.Stages.SelectMany(stage => stage.Rectangles),
            rectangle => AssertInsideBounds(rectangle, width, height));
        Assert.InRange(
            first.Stages[4].Rectangles.Count,
            1,
            AdaptiveArtworkRectangleCompressor
                .DefaultStage5MaximumRectangles);
        Assert.True(first.Stage5Eligible);
        Assert.True(
            first.RemainingErrorAfterStage5
                < first.RemainingErrorAfterStage4);
        Assert.True(
            first.Stage5MinimumRefinedErrorPerPixel
                > AdaptiveArtworkRectangleCompressor
                    .DefaultStage5ErrorPerPixel);

        var normal = AdaptiveArtworkRectangleCompressor.Compress(
            pixels,
            width,
            height);
        Assert.Equal(
            normal.Rectangles.Select(RectangleIdentity),
            first.NormalFinalRectangles.Select(RectangleIdentity));
        AssertFirstFourStagesMatchNormalResult(first);
    }

    [Fact]
    public void FlatProgressivePlanKeepsEmptyLaterStages()
    {
        var plan = AdaptiveArtworkRectangleCompressor.CreateProgressivePlan(
            Enumerable.Repeat(0x07E0, 17 * 11).ToArray(),
            17,
            11);

        Assert.Equal(5, plan.Stages.Count);
        Assert.Single(plan.Stages[0].Rectangles);
        Assert.All(plan.Stages.Skip(1), stage =>
            Assert.Empty(stage.Rectangles));
        Assert.False(plan.Stage5Eligible);
        Assert.Equal(
            "remaining-error-below-threshold",
            plan.Stages[4].SkipReason);
        Assert.Equal(
            plan.RemainingErrorAfterStage4,
            plan.RemainingErrorAfterStage5);
        AssertCompleteCoverage(plan.Stages[0].Rectangles, 17, 11);
    }

    [Fact]
    public void StageFiveCanMicroRefineToSinglePixels()
    {
        const int width = 4;
        const int height = 4;
        var pixels = Enumerable.Range(0, width * height)
            .Select(index => ((index % width) + (index / width)) % 2 == 0
                ? 0xF800
                : 0x001F)
            .ToArray();

        var plan = AdaptiveArtworkRectangleCompressor
            .CreateProgressivePlan(pixels, width, height);
        var stage5 = plan.Stages[4];

        Assert.True(plan.Stage5Eligible);
        Assert.NotEmpty(stage5.Rectangles);
        Assert.Contains(
            stage5.Rectangles,
            rectangle => rectangle.Width == 1 && rectangle.Height == 1);
        Assert.InRange(
            stage5.Rectangles.Count,
            1,
            AdaptiveArtworkRectangleCompressor
                .DefaultStage5MaximumRectangles);
        Assert.All(stage5.Rectangles,
            rectangle => AssertInsideBounds(rectangle, width, height));
        Assert.True(
            plan.Stage5MinimumRefinedErrorPerPixel
                > plan.Stage5ErrorPerPixelThreshold);
        Assert.Equal(1, plan.Stage5MinimumBlockDimension);
        AssertProgressivePlanMatchesFinal(plan);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(7, 2)]
    public void VerySmallImagesRemainValid(int width, int height)
    {
        var pixels = Enumerable.Range(0, width * height)
            .Select(index => index % 2 == 0 ? 0xFFFF : 0)
            .ToArray();

        var result = AdaptiveArtworkRectangleCompressor.Compress(
            pixels,
            width,
            height);

        Assert.InRange(result.Rectangles.Count, 1, width * height);
        AssertCompleteCoverage(result.Rectangles, width, height);
    }

    private static string RectangleIdentity(
        CompressedArtworkRectangle rectangle) =>
        $"{rectangle.X},{rectangle.Y},{rectangle.Width},{rectangle.Height},{rectangle.Colour}";

    private static string StageIdentity(ArtworkRectangleStage stage) =>
        $"{stage.Number}:{stage.RectangleTarget}:{stage.CumulativeLeafCount}:"
        + string.Join(";", stage.Rectangles.Select(RectangleIdentity));

    private static void AssertProgressivePlanMatchesFinal(
        ArtworkRectanglePlan plan)
    {
        var progressive = Enumerable.Repeat(-1, plan.Width * plan.Height)
            .ToArray();
        foreach (var stage in plan.Stages)
            Paint(progressive, plan.Width, stage.Rectangles);

        var final = Enumerable.Repeat(-1, plan.Width * plan.Height).ToArray();
        Paint(final, plan.Width, plan.FinalRectangles);
        Assert.Equal(final, progressive);
    }

    private static void AssertFirstFourStagesMatchNormalResult(
        ArtworkRectanglePlan plan)
    {
        var progressive = Enumerable.Repeat(-1, plan.Width * plan.Height)
            .ToArray();
        foreach (var stage in plan.Stages.Take(
                     AdaptiveArtworkRectangleCompressor
                         .DefaultNormalStageCount))
            Paint(progressive, plan.Width, stage.Rectangles);

        var normal = Enumerable.Repeat(-1, plan.Width * plan.Height).ToArray();
        Paint(normal, plan.Width, plan.NormalFinalRectangles);
        Assert.Equal(normal, progressive);
    }

    private static void Paint(
        int[] pixels,
        int width,
        IEnumerable<CompressedArtworkRectangle> rectangles)
    {
        foreach (var rectangle in rectangles)
            for (var y = rectangle.Y;
                 y < rectangle.Y + rectangle.Height;
                 y++)
                for (var x = rectangle.X;
                     x < rectangle.X + rectangle.Width;
                     x++)
                    pixels[y * width + x] = rectangle.Colour;
    }

    private static void AssertInsideBounds(
        CompressedArtworkRectangle rectangle,
        int width,
        int height)
    {
        Assert.InRange(rectangle.X, 0, width - 1);
        Assert.InRange(rectangle.Y, 0, height - 1);
        Assert.InRange(rectangle.X + rectangle.Width, 1, width);
        Assert.InRange(rectangle.Y + rectangle.Height, 1, height);
        Assert.True(rectangle.Width > 0);
        Assert.True(rectangle.Height > 0);
    }

    private static void AssertCompleteCoverage(
        IReadOnlyList<CompressedArtworkRectangle> rectangles,
        int width,
        int height)
    {
        var coverage = new int[width * height];
        foreach (var rectangle in rectangles)
        {
            AssertInsideBounds(rectangle, width, height);
            for (var y = rectangle.Y;
                 y < rectangle.Y + rectangle.Height;
                 y++)
                for (var x = rectangle.X;
                     x < rectangle.X + rectangle.Width;
                     x++)
                    coverage[y * width + x]++;
        }

        Assert.All(coverage, count => Assert.Equal(1, count));
    }
}
