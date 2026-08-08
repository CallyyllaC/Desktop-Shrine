namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal static class AdaptiveArtworkRectangleCompressor
{
    internal const int DefaultMaximumRectangles = 1400;
    internal const int DefaultMinimumBlockDimension = 2;
    internal const int DefaultPaletteColours = 64;
    internal const int DefaultNormalStageCount = 4;
    internal const int DefaultStageCount = 5;
    internal const int DefaultStage5MaximumRectangles = 400;
    internal const int DefaultStage5MinimumBlockDimension = 1;
    internal const long DefaultStage5ErrorPerPixel = 2400;
    internal static readonly IReadOnlyList<int> DefaultStageRectangleTargets =
        Array.AsReadOnly(
            new[] { 150, 400, 900, DefaultMaximumRectangles });
    private const long DefaultErrorPerPixel = 900;

    public static Result Compress(
        int[] rgb565,
        int width,
        int height,
        int maximumRectangles = DefaultMaximumRectangles,
        int minimumBlockDimension = DefaultMinimumBlockDimension)
    {
        Validate(
            rgb565,
            width,
            height,
            maximumRectangles,
            minimumBlockDimension);
        var refinement = Refine(
            rgb565,
            width,
            height,
            maximumRectangles,
            minimumBlockDimension);
        var rectangles = Merge(ToRectangles(refinement.FinalLeaves));
        return new Result(
            rectangles,
            rectangles.Select(rectangle => rectangle.Colour).Distinct().Count());
    }

    public static ArtworkRectanglePlan CreateProgressivePlan(
        int[] rgb565,
        int width,
        int height,
        IReadOnlyList<int>? stageRectangleTargets = null,
        int minimumBlockDimension = DefaultMinimumBlockDimension)
    {
        stageRectangleTargets ??= DefaultStageRectangleTargets;
        ValidateStageTargets(stageRectangleTargets);
        var maximumRectangles =
            stageRectangleTargets[stageRectangleTargets.Count - 1];
        Validate(
            rgb565,
            width,
            height,
            maximumRectangles,
            minimumBlockDimension);
        var refinement = Refine(
            rgb565,
            width,
            height,
            maximumRectangles,
            minimumBlockDimension);

        var cuts = stageRectangleTargets
            .Select(target => refinement.Operations.Count(operation =>
                operation.LeafCountAfterSplit <= target))
            .ToArray();
        // The final stage always consumes every refinement that the quality
        // threshold and minimum block size allowed, even when the resulting
        // leaf count is below its configured ceiling.
        cuts[cuts.Length - 1] = refinement.Operations.Count;

        var stages = new List<ArtworkRectangleStage>(DefaultStageCount);
        var baseLeaves = new List<Node> { refinement.Root };
        for (var index = 0; index < cuts[0]; index++)
            Apply(baseLeaves, refinement.Operations[index]);
        stages.Add(new ArtworkRectangleStage(
            1,
            stageRectangleTargets[0],
            baseLeaves.Count,
            Merge(ToRectangles(baseLeaves))));

        for (var stageIndex = 1; stageIndex < cuts.Length; stageIndex++)
        {
            var incremental = new List<CompressedArtworkRectangle>();
            for (var operationIndex = cuts[stageIndex - 1];
                 operationIndex < cuts[stageIndex];
                 operationIndex++)
            {
                // Keep split operations ordered. A later operation in this
                // same stage may refine one of these children and must draw
                // after its parent approximation.
                incremental.AddRange(Merge(ToRectangles(
                    refinement.Operations[operationIndex].Children)));
            }

            var cumulativeLeaves = cuts[stageIndex] == 0
                ? 1
                : refinement.Operations[cuts[stageIndex] - 1]
                    .LeafCountAfterSplit;
            stages.Add(new ArtworkRectangleStage(
                stageIndex + 1,
                stageRectangleTargets[stageIndex],
                cumulativeLeaves,
                incremental));
        }

        var normalFinalRectangles =
            Merge(ToRectangles(refinement.FinalLeaves));
        var microRefinement = ContinueMicroRefinement(
            refinement.Image,
            refinement.FinalLeaves,
            DefaultStage5MaximumRectangles,
            DefaultStage5MinimumBlockDimension,
            DefaultStage5ErrorPerPixel);
        stages.Add(new ArtworkRectangleStage(
            DefaultStageCount,
            stageRectangleTargets[stageRectangleTargets.Count - 1],
            microRefinement.FinalLeaves.Count,
            microRefinement.Rectangles,
            microRefinement.Rectangles.Count == 0
                ? "remaining-error-below-threshold"
                : null));
        var finalRectangles =
            Merge(ToRectangles(microRefinement.FinalLeaves));
        return new ArtworkRectanglePlan(
            width,
            height,
            stages,
            normalFinalRectangles,
            finalRectangles,
            stages
                .SelectMany(stage => stage.Rectangles)
                .Select(rectangle => rectangle.Colour)
                .Distinct()
                .Count(),
            minimumBlockDimension,
            DefaultStage5MinimumBlockDimension,
            DefaultStage5MaximumRectangles,
            DefaultStage5ErrorPerPixel,
            microRefinement.RemainingErrorBefore,
            microRefinement.RemainingErrorAfter,
            microRefinement.MinimumRefinedErrorPerPixel);
    }

    private static MicroRefinementResult ContinueMicroRefinement(
        QuantizedImage image,
        IReadOnlyList<Node> normalFinalLeaves,
        int maximumAdditionalRectangles,
        int minimumBlockDimension,
        long errorPerPixelThreshold)
    {
        var leaves = new List<Node>(normalFinalLeaves);
        var rectangles = new List<CompressedArtworkRectangle>();
        var remainingErrorBefore = leaves.Sum(node => node.Error);
        long? minimumRefinedErrorPerPixel = null;

        while (rectangles.Count < maximumAdditionalRectangles)
        {
            var worstIndex = -1;
            long worstError = -1;
            IReadOnlyList<Node>? worstChildren = null;
            IReadOnlyList<CompressedArtworkRectangle>? worstRectangles = null;
            for (var index = 0; index < leaves.Count; index++)
            {
                var node = leaves[index];
                if (node.Error <= errorPerPixelThreshold * node.PixelCount)
                    continue;
                var children = Split(image, node, minimumBlockDimension);
                if (children.Count <= 1)
                    continue;
                var childRectangles = Merge(ToRectangles(children));
                if (rectangles.Count + childRectangles.Count
                    > maximumAdditionalRectangles)
                    continue;
                if (node.Error <= worstError)
                    continue;
                worstIndex = index;
                worstError = node.Error;
                worstChildren = children;
                worstRectangles = childRectangles;
            }

            if (worstIndex < 0
                || worstChildren is null
                || worstRectangles is null)
                break;

            var parent = leaves[worstIndex];
            leaves.RemoveAt(worstIndex);
            leaves.InsertRange(worstIndex, worstChildren);
            rectangles.AddRange(worstRectangles);
            var errorPerPixel =
                (parent.Error + parent.PixelCount - 1) / parent.PixelCount;
            minimumRefinedErrorPerPixel = minimumRefinedErrorPerPixel.HasValue
                ? Math.Min(
                    minimumRefinedErrorPerPixel.Value,
                    errorPerPixel)
                : errorPerPixel;
        }

        return new MicroRefinementResult(
            leaves,
            rectangles,
            remainingErrorBefore,
            leaves.Sum(node => node.Error),
            minimumRefinedErrorPerPixel);
    }

    private static RefinementResult Refine(
        int[] rgb565,
        int width,
        int height,
        int maximumRectangles,
        int minimumBlockDimension)
    {
        var image = new QuantizedImage(rgb565, width, height);
        var root = image.Measure(0, 0, width, height);
        var leaves = new List<Node> { root };
        var operations = new List<RefinementOperation>();

        while (true)
        {
            var worstIndex = -1;
            long worstError = -1;
            IReadOnlyList<Node>? worstChildren = null;
            for (var index = 0; index < leaves.Count; index++)
            {
                var node = leaves[index];
                if (node.Error <= DefaultErrorPerPixel * node.PixelCount)
                    continue;
                var children = Split(image, node, minimumBlockDimension);
                if (children.Count <= 1
                    || leaves.Count + children.Count - 1
                        > maximumRectangles)
                    continue;
                if (node.Error <= worstError)
                    continue;
                worstIndex = index;
                worstError = node.Error;
                worstChildren = children;
            }

            if (worstIndex < 0 || worstChildren is null)
                break;

            var parent = leaves[worstIndex];
            leaves.RemoveAt(worstIndex);
            leaves.InsertRange(worstIndex, worstChildren);
            operations.Add(new RefinementOperation(
                parent,
                worstChildren,
                leaves.Count));
        }

        return new RefinementResult(image, root, leaves, operations);
    }

    private static void Apply(
        List<Node> leaves,
        RefinementOperation operation)
    {
        var index = leaves.IndexOf(operation.Parent);
        if (index < 0)
            throw new InvalidOperationException(
                "The adaptive artwork refinement tree is inconsistent.");
        leaves.RemoveAt(index);
        leaves.InsertRange(index, operation.Children);
    }

    private static List<CompressedArtworkRectangle> ToRectangles(
        IEnumerable<Node> nodes) =>
        nodes.Select(node => new CompressedArtworkRectangle(
                node.X,
                node.Y,
                node.Width,
                node.Height,
                node.Colour))
            .ToList();

    private static void Validate(
        int[] rgb565,
        int width,
        int height,
        int maximumRectangles,
        int minimumBlockDimension)
    {
        if (rgb565 is null)
            throw new ArgumentNullException(nameof(rgb565));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (rgb565.Length != checked(width * height))
            throw new ArgumentException(
                "The RGB565 buffer dimensions do not match its length.",
                nameof(rgb565));
        if (maximumRectangles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRectangles));
        if (minimumBlockDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumBlockDimension));
    }

    private static void ValidateStageTargets(
        IReadOnlyList<int> stageRectangleTargets)
    {
        if (stageRectangleTargets.Count == 0)
            throw new ArgumentException(
                "At least one artwork refinement stage is required.",
                nameof(stageRectangleTargets));
        for (var index = 0; index < stageRectangleTargets.Count; index++)
        {
            if (stageRectangleTargets[index] <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(stageRectangleTargets),
                    "Artwork stage targets must be positive.");
            if (index > 0
                && stageRectangleTargets[index]
                    <= stageRectangleTargets[index - 1])
                throw new ArgumentException(
                    "Artwork stage targets must be strictly increasing.",
                    nameof(stageRectangleTargets));
        }
    }

    private static IReadOnlyList<Node> Split(
        QuantizedImage image,
        Node node,
        int minimumBlockDimension)
    {
        var splitWidth = node.Width >= minimumBlockDimension * 2;
        var splitHeight = node.Height >= minimumBlockDimension * 2;
        if (!splitWidth && !splitHeight)
            return Array.Empty<Node>();

        var leftWidth = splitWidth ? node.Width / 2 : node.Width;
        var rightWidth = node.Width - leftWidth;
        var topHeight = splitHeight ? node.Height / 2 : node.Height;
        var bottomHeight = node.Height - topHeight;
        var result = new List<Node>(splitWidth && splitHeight ? 4 : 2);
        Add(node.X, node.Y, leftWidth, topHeight);
        if (splitWidth)
            Add(node.X + leftWidth, node.Y, rightWidth, topHeight);
        if (splitHeight)
        {
            Add(node.X, node.Y + topHeight, leftWidth, bottomHeight);
            if (splitWidth)
            {
                Add(
                    node.X + leftWidth,
                    node.Y + topHeight,
                    rightWidth,
                    bottomHeight);
            }
        }
        return result;

        void Add(int x, int y, int width, int height) =>
            result.Add(image.Measure(x, y, width, height));
    }

    private static List<CompressedArtworkRectangle> Merge(
        List<CompressedArtworkRectangle> rectangles)
    {
        while (true)
        {
            var before = rectangles.Count;
            rectangles = MergeHorizontal(rectangles);
            rectangles = MergeVertical(rectangles);
            if (rectangles.Count == before)
                return rectangles
                    .OrderBy(rectangle => rectangle.Y)
                    .ThenBy(rectangle => rectangle.X)
                    .ThenBy(rectangle => rectangle.Height)
                    .ThenBy(rectangle => rectangle.Width)
                    .ToList();
        }
    }

    private static List<CompressedArtworkRectangle> MergeHorizontal(
        IEnumerable<CompressedArtworkRectangle> source)
    {
        var result = new List<CompressedArtworkRectangle>();
        foreach (var group in source.GroupBy(rectangle => new
                 {
                     rectangle.Y,
                     rectangle.Height,
                     rectangle.Colour
                 }))
        {
            CompressedArtworkRectangle? current = null;
            foreach (var rectangle in group.OrderBy(item => item.X))
            {
                if (current is not null
                    && current.X + current.Width == rectangle.X)
                {
                    current = new(
                        current.X,
                        current.Y,
                        current.Width + rectangle.Width,
                        current.Height,
                        current.Colour);
                    continue;
                }
                if (current is not null)
                    result.Add(current);
                current = rectangle;
            }
            if (current is not null)
                result.Add(current);
        }
        return result;
    }

    private static List<CompressedArtworkRectangle> MergeVertical(
        IEnumerable<CompressedArtworkRectangle> source)
    {
        var result = new List<CompressedArtworkRectangle>();
        foreach (var group in source.GroupBy(rectangle => new
                 {
                     rectangle.X,
                     rectangle.Width,
                     rectangle.Colour
                 }))
        {
            CompressedArtworkRectangle? current = null;
            foreach (var rectangle in group.OrderBy(item => item.Y))
            {
                if (current is not null
                    && current.Y + current.Height == rectangle.Y)
                {
                    current = new(
                        current.X,
                        current.Y,
                        current.Width,
                        current.Height + rectangle.Height,
                        current.Colour);
                    continue;
                }
                if (current is not null)
                    result.Add(current);
                current = rectangle;
            }
            if (current is not null)
                result.Add(current);
        }
        return result;
    }

    internal sealed class Result(
        IReadOnlyList<CompressedArtworkRectangle> rectangles,
        int paletteColourCount)
    {
        public IReadOnlyList<CompressedArtworkRectangle> Rectangles { get; } =
            rectangles;
        public int PaletteColourCount { get; } = paletteColourCount;
    }

    private sealed class RefinementResult(
        QuantizedImage image,
        Node root,
        IReadOnlyList<Node> finalLeaves,
        IReadOnlyList<RefinementOperation> operations)
    {
        public QuantizedImage Image { get; } = image;
        public Node Root { get; } = root;
        public IReadOnlyList<Node> FinalLeaves { get; } = finalLeaves;
        public IReadOnlyList<RefinementOperation> Operations { get; } =
            operations;
    }

    private sealed class MicroRefinementResult(
        IReadOnlyList<Node> finalLeaves,
        IReadOnlyList<CompressedArtworkRectangle> rectangles,
        long remainingErrorBefore,
        long remainingErrorAfter,
        long? minimumRefinedErrorPerPixel)
    {
        public IReadOnlyList<Node> FinalLeaves { get; } = finalLeaves;
        public IReadOnlyList<CompressedArtworkRectangle> Rectangles { get; } =
            rectangles;
        public long RemainingErrorBefore { get; } = remainingErrorBefore;
        public long RemainingErrorAfter { get; } = remainingErrorAfter;
        public long? MinimumRefinedErrorPerPixel { get; } =
            minimumRefinedErrorPerPixel;
    }

    private sealed class RefinementOperation(
        Node parent,
        IReadOnlyList<Node> children,
        int leafCountAfterSplit)
    {
        public Node Parent { get; } = parent;
        public IReadOnlyList<Node> Children { get; } = children;
        public int LeafCountAfterSplit { get; } = leafCountAfterSplit;
    }

    private sealed class Node(
        int x,
        int y,
        int width,
        int height,
        int colour,
        long error)
    {
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Colour { get; } = colour;
        public long Error { get; } = error;
        public long PixelCount => (long)Width * Height;
    }

    private sealed class QuantizedImage
    {
        private readonly int stride;
        private readonly long[] red;
        private readonly long[] green;
        private readonly long[] blue;
        private readonly long[] redSquared;
        private readonly long[] greenSquared;
        private readonly long[] blueSquared;

        public QuantizedImage(int[] source, int width, int height)
        {
            stride = width + 1;
            var length = checked(stride * (height + 1));
            red = new long[length];
            green = new long[length];
            blue = new long[length];
            redSquared = new long[length];
            greenSquared = new long[length];
            blueSquared = new long[length];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var colour = Quantize(source[y * width + x]);
                    var r = ((colour >> 11) & 0x1f) * 255 / 31;
                    var g = ((colour >> 5) & 0x3f) * 255 / 63;
                    var b = (colour & 0x1f) * 255 / 31;
                    SetIntegral(red, x, y, r);
                    SetIntegral(green, x, y, g);
                    SetIntegral(blue, x, y, b);
                    SetIntegral(redSquared, x, y, r * r);
                    SetIntegral(greenSquared, x, y, g * g);
                    SetIntegral(blueSquared, x, y, b * b);
                }
            }
        }

        public Node Measure(int x, int y, int width, int height)
        {
            var count = checked(width * height);
            var redSum = Sum(red, x, y, width, height);
            var greenSum = Sum(green, x, y, width, height);
            var blueSum = Sum(blue, x, y, width, height);
            var representative = QuantizeRgb(
                (int)Math.Round(redSum / (double)count),
                (int)Math.Round(greenSum / (double)count),
                (int)Math.Round(blueSum / (double)count));
            var representativeRed =
                ((representative >> 11) & 0x1f) * 255 / 31;
            var representativeGreen =
                ((representative >> 5) & 0x3f) * 255 / 63;
            var representativeBlue =
                (representative & 0x1f) * 255 / 31;
            var error = ChannelError(
                    redSquared,
                    redSum,
                    representativeRed,
                    x,
                    y,
                    width,
                    height,
                    count) * 2
                + ChannelError(
                    greenSquared,
                    greenSum,
                    representativeGreen,
                    x,
                    y,
                    width,
                    height,
                    count) * 4
                + ChannelError(
                    blueSquared,
                    blueSum,
                    representativeBlue,
                    x,
                    y,
                    width,
                    height,
                    count);
            return new Node(x, y, width, height, representative, error);
        }

        private long ChannelError(
            long[] squared,
            long sum,
            int representative,
            int x,
            int y,
            int width,
            int height,
            int count) =>
            Sum(squared, x, y, width, height)
            - (2L * representative * sum)
            + ((long)count * representative * representative);

        private void SetIntegral(long[] values, int x, int y, long value)
        {
            var index = ((y + 1) * stride) + x + 1;
            values[index] = value
                + values[index - 1]
                + values[index - stride]
                - values[index - stride - 1];
        }

        private long Sum(
            long[] values,
            int x,
            int y,
            int width,
            int height)
        {
            var left = x;
            var top = y;
            var right = x + width;
            var bottom = y + height;
            return values[bottom * stride + right]
                - values[top * stride + right]
                - values[bottom * stride + left]
                + values[top * stride + left];
        }

        private static int Quantize(int rgb565)
        {
            var red = ((rgb565 >> 11) & 0x1f) * 255 / 31;
            var green = ((rgb565 >> 5) & 0x3f) * 255 / 63;
            var blue = (rgb565 & 0x1f) * 255 / 31;
            return QuantizeRgb(red, green, blue);
        }

        private static int QuantizeRgb(int red, int green, int blue)
        {
            var redLevel = Math.Max(0, Math.Min(3, (red * 3 + 127) / 255));
            var greenLevel = Math.Max(
                0,
                Math.Min(3, (green * 3 + 127) / 255));
            var blueLevel = Math.Max(
                0,
                Math.Min(3, (blue * 3 + 127) / 255));
            var red565 = (redLevel * 31 + 1) / 3;
            var green565 = greenLevel * 21;
            var blue565 = (blueLevel * 31 + 1) / 3;
            return (red565 << 11) | (green565 << 5) | blue565;
        }
    }
}

internal sealed class ArtworkRectanglePlan(
    int width,
    int height,
    IReadOnlyList<ArtworkRectangleStage> stages,
    IReadOnlyList<CompressedArtworkRectangle> normalFinalRectangles,
    IReadOnlyList<CompressedArtworkRectangle> finalRectangles,
    int paletteColourCount,
    int minimumBlockDimension,
    int stage5MinimumBlockDimension,
    int stage5MaximumRectangles,
    long stage5ErrorPerPixelThreshold,
    long remainingErrorAfterStage4,
    long remainingErrorAfterStage5,
    long? stage5MinimumRefinedErrorPerPixel)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IReadOnlyList<ArtworkRectangleStage> Stages { get; } = stages;
    public IReadOnlyList<CompressedArtworkRectangle> NormalFinalRectangles
    {
        get;
    } = normalFinalRectangles;
    public IReadOnlyList<CompressedArtworkRectangle> FinalRectangles { get; } =
        finalRectangles;
    public int PaletteColourCount { get; } = paletteColourCount;
    public int MinimumBlockDimension { get; } = minimumBlockDimension;
    public int Stage5MinimumBlockDimension { get; } =
        stage5MinimumBlockDimension;
    public int Stage5MaximumRectangles { get; } = stage5MaximumRectangles;
    public long Stage5ErrorPerPixelThreshold { get; } =
        stage5ErrorPerPixelThreshold;
    public long RemainingErrorAfterStage4 { get; } =
        remainingErrorAfterStage4;
    public long RemainingErrorAfterStage5 { get; } =
        remainingErrorAfterStage5;
    public long? Stage5MinimumRefinedErrorPerPixel { get; } =
        stage5MinimumRefinedErrorPerPixel;
    public bool Stage5Eligible =>
        Stages.Count >= AdaptiveArtworkRectangleCompressor.DefaultStageCount
        && Stages[AdaptiveArtworkRectangleCompressor.DefaultStageCount - 1]
            .Rectangles.Count > 0;
    public int TotalRectangleCommands =>
        Stages.Sum(stage => stage.Rectangles.Count);
}

internal sealed class ArtworkRectangleStage(
    int number,
    int rectangleTarget,
    int cumulativeLeafCount,
    IReadOnlyList<CompressedArtworkRectangle> rectangles,
    string? skipReason = null)
{
    public int Number { get; } = number;
    public int RectangleTarget { get; } = rectangleTarget;
    public int CumulativeLeafCount { get; } = cumulativeLeafCount;
    public IReadOnlyList<CompressedArtworkRectangle> Rectangles { get; } =
        rectangles;
    public string? SkipReason { get; } = skipReason;
}

internal sealed class CompressedArtworkRectangle(
    int x,
    int y,
    int width,
    int height,
    int colour)
{
    public int X { get; } = x;
    public int Y { get; } = y;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Colour { get; } = colour;
}
