using System.Text.Json;
using ArtMockups;
using OpenCvSharp;
using Xunit;

[Collection("env")]
public class TwoFrameTests
{
    static List<PointD> Box(double x0, double y0, double x1, double y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    static readonly Vec3b Wall = new(120, 130, 140), Red = new(0, 0, 255), Green = new(0, 255, 0), Blue = new(255, 0, 0), Yellow = new(0, 255, 255);

    static Mat MakeScene() => new(600, 1000, MatType.CV_8UC3, new Scalar(Wall.Item0, Wall.Item1, Wall.Item2));
    static Mat Solid(Vec3b c, int w = 300, int h = 450) => new(h, w, MatType.CV_8UC3, new Scalar(c.Item0, c.Item1, c.Item2));

    static Scene TwoFrames(List<PointD> a, List<PointD> b, double feather = 1) => new()
    {
        Name = "duo", Image = "duo.png", FrameCount = 2, Corners = a, Corners2 = b,
        Settings = new SceneSettings { LightingStrength = 0, EdgeFeatherPx = feather },
    };

    static void AssertColour(Vec3b expected, Mat img, int x, int y)
    {
        var p = img.At<Vec3b>(y, x);
        for (int i = 0; i < 3; i++) Assert.InRange(p[i], expected[i] - 3, expected[i] + 3);
    }

    [Fact]
    public void Pair_PutsEachArtworkInItsFrame_AndLeavesTheRestUnchanged()
    {
        using var image = MakeScene();
        using var first = Solid(Red);
        using var second = Solid(Green);
        using var mockup = PreparedMockup.Create(image, TwoFrames(Box(100, 100, 350, 475), Box(600, 100, 850, 475)));
        using var result = mockup.Render(first, second);

        Assert.Equal(image.Size(), result.Size());
        AssertColour(Red, result, 225, 290);
        AssertColour(Green, result, 725, 290);
        foreach (var (x, y) in new[] { (10, 10), (475, 290), (990, 590), (225, 550) }) Assert.Equal(Wall, result.At<Vec3b>(y, x));
        Assert.Equal(Wall, image.At<Vec3b>(290, 225)); // the cached scene photo is not drawn on
    }

    [Fact]
    public void FramesCloseTogether_DoNotEraseEachOther()
    {
        using var image = MakeScene();
        using var first = Solid(Red);
        using var second = Solid(Green);
        // 6 px apart with a wide feather: each frame's working area overlaps its neighbour.
        using var mockup = PreparedMockup.Create(image, TwoFrames(Box(100, 100, 397, 545), Box(403, 100, 700, 545), feather: 6));
        using var result = mockup.Render(first, second);

        AssertColour(Red, result, 385, 300);
        AssertColour(Green, result, 415, 300);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // frame 1 clicked on the right-hand side
    public void Split_SideBySide_SendsTheLeftHalfToTheLeftFrame(bool reversed)
    {
        using var image = MakeScene();
        using var art = Solid(Blue, 400, 300);
        using (var right = new Mat(art, new Rect(200, 0, 200, 300))) right.SetTo(new Scalar(0, 255, 255));
        List<PointD> leftFrame = Box(100, 100, 350, 475), rightFrame = Box(600, 100, 850, 475);
        using var mockup = PreparedMockup.Create(image, reversed ? TwoFrames(rightFrame, leftFrame) : TwoFrames(leftFrame, rightFrame));
        using var result = mockup.Render(art);

        AssertColour(Blue, result, 225, 290);
        AssertColour(Yellow, result, 725, 290);
    }

    [Fact]
    public void Split_Stacked_SendsTheTopHalfToTheTopFrame()
    {
        using var image = MakeScene();
        using var art = Solid(Blue, 300, 400);
        using (var bottom = new Mat(art, new Rect(0, 200, 300, 200))) bottom.SetTo(new Scalar(0, 255, 255));
        using var mockup = PreparedMockup.Create(image, TwoFrames(Box(300, 320, 600, 520), Box(300, 60, 600, 260)));
        using var result = mockup.Render(art);

        AssertColour(Blue, result, 450, 160);
        AssertColour(Yellow, result, 450, 420);
    }

    [Fact]
    public void SceneSavedBeforeTwoFrames_LoadsWithOneFrame()
    {
        const string json = """{"name":"a","image":"a.png","corners":[{"x":0,"y":0},{"x":9,"y":0},{"x":9,"y":9},{"x":0,"y":9}]}""";
        var scene = JsonSerializer.Deserialize<Scene>(json, Json.Options)!;
        Assert.Equal(1, scene.FrameCount);
        Assert.True(scene.IsReady);
        Assert.Equal(scene.Corners, scene.CameraTarget());
    }

    [Fact]
    public void CameraTarget_CoversBothFrames_OnlyWhenBothAreSet()
    {
        var scene = TwoFrames(Box(100, 120, 350, 475), Box(600, 100, 850, 460));
        Assert.Equal(Box(100, 100, 850, 475), scene.CameraTarget());
        scene.Corners2 = [];
        Assert.False(scene.IsReady);
        Assert.Empty(scene.CameraTarget());
    }

    [Fact]
    public void Plan_CombinesOneFrameScenesWithArtworks_AndTwoFrameScenesWithPairs()
    {
        var single = new Scene { Name = "solo", Corners = Box(0, 0, 9, 9) };
        var duo = TwoFrames(Box(0, 0, 9, 9), Box(20, 0, 29, 9));
        List<ArtPair> pairs = [new("0001.png", "0002.png"), new("0003.png", ""), new("0001.png", "0002.png")];
        var items = MockupPlan.Build([single, duo], ["0001.png", "0002.png", "0001.png"], pairs);

        Assert.Equal(["0001__solo", "0002__solo", "0001+0002__duo", "0003-split__duo"],
            items.Select(i => MockupPlan.OutputStem(i.Scene, i.Art)));
        Assert.Equal("0001+0002__duo_loop.mp4", ZoomVideoService.SyntheticName(duo, pairs[0], true));
        Assert.Single(MockupPlan.Build([duo], null, [pairs[0]])); // two-frame scenes need no ticked artworks
    }

    [Fact]
    public void Plan_ExplainsWhatIsMissing()
    {
        var single = new Scene { Name = "solo", Corners = Box(0, 0, 9, 9) };
        var duo = TwoFrames(Box(0, 0, 9, 9), Box(20, 0, 29, 9));
        Assert.Contains("arte ou par", Assert.Throws<ArgumentException>(() => MockupPlan.Build([duo], ["0001.png"], null)).Message);
        Assert.Contains("uma arte", Assert.Throws<ArgumentException>(() => MockupPlan.Build([single], null, [new("0001.png")])).Message);
        duo.Corners2 = [];
        Assert.Contains("dois quadros", Assert.Throws<ArgumentException>(() => MockupPlan.Build([duo], null, [new("0001.png")])).Message);
    }

    [Fact]
    public async Task SavedTwoFrameScene_GeneratesPairAndSplitImages()
    {
        var root = Directory.CreateTempSubdirectory("duo-").FullName;
        var old = Environment.GetEnvironmentVariable("ARTMOCKUPS_DATA");
        Environment.SetEnvironmentVariable("ARTMOCKUPS_DATA", root);
        try
        {
            var paths = new AppPaths(root);
            var config = new ConfigStore(paths);
            var scenes = new SceneStore(paths);
            var library = new ArtworkLibrary(paths, config);
            foreach (var (name, colour) in new[] { ("0001.png", Red), ("0002.png", Green) })
            {
                using var art = Solid(colour);
                ImageIO.Save(Path.Combine(library.Folder, name), art, 90);
            }
            using (var image = MakeScene())
                scenes.Add("duo", "duo.png", new MemoryStream(ImageIO.Encode(image, ".png", 90)));

            Assert.Throws<ArgumentException>(() => scenes.Update("duo", new SceneUpdate(null, null, null, null, null, FrameCount: 3)));
            Assert.Throws<ArgumentException>(() => scenes.Update("duo", new SceneUpdate(null, null, null, null, null, Corners2: Box(0, 0, 5000, 9))));
            scenes.Update("duo", new SceneUpdate(null, Box(100, 100, 350, 475), null, null, null, Box(600, 100, 850, 475), 2));

            var saved = new SceneStore(paths).Get("duo");
            Assert.Equal(2, saved.FrameCount);
            Assert.True(saved.IsReady);
            var json = File.ReadAllText(paths.ScenesJson);
            Assert.DoesNotContain("isReady", json);
            Assert.DoesNotContain("\"frames\"", json);

            var batch = new BatchService(paths, scenes, new SceneImageCache(scenes), library, config);
            var request = new BatchRequest(["duo"], null, false, "png", true, [new("0001.png", "0002.png"), new("0003.png")]);
            // 0003: left half blue, right half yellow
            using (var art = Solid(Blue, 400, 300))
            {
                using (var right = new Mat(art, new Rect(200, 0, 200, 300))) right.SetTo(new Scalar(0, 255, 255));
                ImageIO.Save(Path.Combine(library.Folder, "0003.png"), art, 90);
            }
            var source = Path.Combine(library.Folder, "0003.png");

            // generation needs the split to exist, cut the way the frames are placed
            Assert.Contains("0003.png (lado a lado)", Assert.Throws<ArgumentException>(() => batch.Start(request)).Message);
            DiptychSplit.SplitForScenes(source, library.Folder, SplitLayout.Stacked);
            Assert.Equal(SplitLayout.Stacked, library.SceneSplit("0003.png")!.Value.Layout);
            Assert.Throws<ArgumentException>(() => batch.Start(request));
            var parts = DiptychSplit.SplitForScenes(source, library.Folder, SplitLayout.Side, overwrite: true);
            Assert.Equal(["0003_part1.png", "0003_part2.png"], parts.Select(p => p.Name));
            Assert.All(parts, p => Assert.Equal((200, 300), (p.Width, p.Height))); // original pixels, no border
            Assert.Equal(SplitLayout.Side, library.SceneSplit("0003.png")!.Value.Layout);

            var job = batch.Start(request);
            for (int i = 0; i < 200 && job.Status == "running"; i++) await Task.Delay(50);

            Assert.Equal("completed", job.Status);
            Assert.Empty(job.Errors);
            using var pair = ImageIO.Load(Path.Combine(paths.OutputDir, "0001+0002__duo.png"));
            AssertColour(Red, pair, 225, 290);
            AssertColour(Green, pair, 725, 290);
            using var split = ImageIO.Load(Path.Combine(paths.OutputDir, "0003-split__duo.png"));
            AssertColour(Blue, split, 225, 290);
            AssertColour(Yellow, split, 725, 290);

            // the part files are what gets shown, with part 1 on the left even if frame 1 was marked on the right
            using (var green = Solid(Green, 200, 300)) ImageIO.Save(Path.Combine(library.Folder, "0003_part1.png"), green, 90);
            var swapped = new SceneStore(paths).Get("duo");
            (swapped.Corners, swapped.Corners2) = (swapped.Corners2, swapped.Corners);
            using var photo = ImageIO.Load(scenes.ImagePath(swapped));
            using var mockup = PreparedMockup.Create(photo, swapped);
            using var shown = mockup.Render(library, new ArtPair("0003.png"), requireSplit: true);
            AssertColour(Green, shown, 225, 290);
            AssertColour(Yellow, shown, 725, 290);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARTMOCKUPS_DATA", old);
            Directory.Delete(root, true);
        }
    }
}
