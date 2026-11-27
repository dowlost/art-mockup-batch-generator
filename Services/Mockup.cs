using OpenCvSharp;

namespace ArtMockups;

/// <summary>
/// The artwork(s) for one mockup. One-frame scenes use only <see cref="First"/>. On a two-frame scene
/// <see cref="First"/> goes in frame 1 and <see cref="Second"/> in frame 2; without a second artwork the first one is
/// split across both frames, using its part files (0001_part1, 0001_part2).
/// </summary>
public record ArtPair(string First, string? Second = null)
{
    public string Label => Second == null ? First : $"{First} + {Second}";
}

public static class MockupPlan
{
    /// <summary>One item per output file: one-frame scenes × artworks, two-frame scenes × pairs.</summary>
    public static List<(Scene Scene, ArtPair Art)> Build(IReadOnlyList<Scene> scenes, IEnumerable<string>? artworks, IEnumerable<ArtPair>? pairs)
    {
        var singles = (artworks ?? []).Distinct().Select(a => new ArtPair(a)).ToList();
        var doubles = (pairs ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p?.First))
            .Select(p => new ArtPair(p.First, string.IsNullOrWhiteSpace(p.Second) ? null : p.Second))
            .Distinct().ToList();

        var items = new List<(Scene, ArtPair)>();
        foreach (var scene in scenes)
        {
            if (!scene.IsReady)
                throw new ArgumentException(scene.FrameCount == 2
                    ? $"Scene '{scene.Name}' needs the 4 corners of both frames."
                    : $"Scene '{scene.Name}' has no corners set.");
            var list = scene.FrameCount == 2 ? doubles : singles;
            if (list.Count == 0)
                throw new ArgumentException(scene.FrameCount == 2
                    ? $"Add at least one two-frame pair for scene '{scene.Name}'."
                    : "Select at least one artwork.");
            items.AddRange(list.Select(a => (scene, a)));
        }
        return items;
    }

    /// <summary>
    /// Fails, naming every artwork concerned, if a two-frame scene would show an artwork that has not been split the
    /// way its frames are arranged.
    /// </summary>
    public static void RequireSplits(ArtworkLibrary library, IEnumerable<(Scene Scene, ArtPair Art)> items)
    {
        var problems = items
            .Where(i => i.Scene.FrameCount == 2 && i.Art.Second == null)
            .Where(i => library.SceneSplit(i.Art.First)?.Layout != i.Scene.Arrangement)
            .Select(i => $"{i.Art.First} ({(i.Scene.Arrangement == SplitLayout.Side ? "side by side" : "top and bottom")})")
            .Distinct().ToList();
        if (problems.Count > 0) throw new ArgumentException($"Split these artworks first: {string.Join(", ", problems)}.");
    }

    /// <summary>Output file name without extension: 0001__scene, 0001+0002__scene or 0001-split__scene.</summary>
    public static string OutputStem(Scene scene, ArtPair art)
    {
        static string Stem(string file) => Names.SafeFileStem(Path.GetFileNameWithoutExtension(file));
        var stem = Stem(art.First);
        if (scene.FrameCount == 2) stem += art.Second == null ? "-split" : "+" + Stem(art.Second);
        return stem + "__" + Names.SafeFileStem(scene.Name);
    }
}

/// <summary>A scene with all its frames prepared. Thread-safe for concurrent rendering.</summary>
public sealed class PreparedMockup : IDisposable
{
    readonly Mat _scene; // borrowed, not owned
    readonly PreparedScene[] _frames;
    readonly SplitLayout _layout;
    readonly int[] _readingOrder; // frame indexes from left to right (side) or top to bottom (stacked)

    PreparedMockup(Mat scene, PreparedScene[] frames, SplitLayout layout, int[] readingOrder)
    {
        _scene = scene; _frames = frames; _layout = layout; _readingOrder = readingOrder;
    }

    public static PreparedMockup Create(Mat sceneImage, Scene scene)
    {
        var frames = new List<PreparedScene>();
        try
        {
            foreach (var corners in scene.Frames) frames.Add(PreparedScene.Create(sceneImage, corners, scene.Settings));
        }
        catch
        {
            foreach (var f in frames) f.Dispose();
            throw;
        }

        var layout = scene.Arrangement;
        var order = new[] { 0, 1 };
        if (frames.Count == 2)
            order = order.OrderBy(i => scene.Frames[i].Average(p => layout == SplitLayout.Side ? p.X : p.Y)).ToArray();
        return new PreparedMockup(sceneImage, frames.ToArray(), layout, order);
    }

    /// <summary>
    /// Loads the artwork(s) and returns a new full-size mockup. For a split on a two-frame scene the part files are
    /// used; if they are missing, <paramref name="requireSplit"/> makes that an error (generation), otherwise the
    /// artwork is cut in memory (previews).
    /// </summary>
    public Mat Render(ArtworkLibrary library, ArtPair art, bool requireSplit = false)
    {
        var path = library.Resolve(art.First);
        if (_frames.Length == 2 && string.IsNullOrWhiteSpace(art.Second))
        {
            if (library.SceneSplit(art.First) is { } split && split.Layout == _layout)
            {
                using var part1 = ImageIO.Load(split.Part1);
                using var part2 = ImageIO.Load(split.Part2);
                // part 1 is the left or top half, whichever frame was marked first
                return Compose((_readingOrder[0], part1), (_readingOrder[1], part2));
            }
            if (requireSplit)
                throw new ArgumentException($"'{art.First}' is not split for this scene yet. Confirm the split first.");
        }
        using var first = ImageIO.Load(path);
        using var second = _frames.Length == 2 && !string.IsNullOrWhiteSpace(art.Second) ? ImageIO.Load(library.Resolve(art.Second)) : null;
        return Render(first, second);
    }

    /// <summary>On a two-frame scene, a null <paramref name="second"/> splits <paramref name="first"/> across both frames.</summary>
    public Mat Render(Mat first, Mat? second = null)
    {
        if (_frames.Length == 1) return _frames[0].Render(first);
        if (second != null) return Compose((0, first), (1, second));

        // The same centred cut as a default split.
        var halves = DiptychSplit.Pieces(first, DiptychSplit.Plan(first.Width, first.Height, _layout));
        using var a = halves[0];
        using var b = halves[1];
        return Compose((_readingOrder[0], a), (_readingOrder[1], b));
    }

    Mat Compose(params (int Frame, Mat Art)[] placements)
    {
        var output = _scene.Clone();
        try
        {
            foreach (var (frame, art) in placements) _frames[frame].RenderInto(output, art);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var f in _frames) f.Dispose();
    }
}
