using ArtMockups;
using OpenCvSharp;
using Xunit;

public class CompositorTests
{
    static readonly PointD[] Quad = { new(200, 150), new(560, 170), new(540, 450), new(210, 430) };

    // Synthetic scene: mid-gray wall, bright blank frame area with a left-to-right shadow gradient.
    static Mat MakeScene()
    {
        var scene = new Mat(600, 800, MatType.CV_8UC3, new Scalar(120, 130, 140));
        var poly = Quad.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
        Cv2.FillConvexPoly(scene, poly, new Scalar(245, 245, 245));
        for (int x = 200; x < 560; x++)
        {
            double f = 1.0 - 0.5 * (x - 200) / 360.0; // darker towards the right
            for (int y = 150; y < 450; y++)
            {
                var px = scene.At<Vec3b>(y, x);
                if (px.Item0 == 245)
                    scene.Set(y, x, new Vec3b((byte)(245 * f), (byte)(245 * f), (byte)(245 * f)));
            }
        }
        return scene;
    }

    static Mat Red() => new(300, 400, MatType.CV_8UC3, new Scalar(0, 0, 255));

    [Fact]
    public void OutputHasSceneSize_AndPixelsOutsideMaskUnchanged()
    {
        using var scene = MakeScene();
        using var art = Red();
        using var prepared = PreparedScene.Create(scene, Quad, new SceneSettings());
        using var result = prepared.Render(art);

        Assert.Equal(scene.Size(), result.Size());
        // Far from the frame (top-left corner block and bottom-right block) must be identical.
        foreach (var rect in new[] { new Rect(0, 0, 100, 100), new Rect(650, 500, 150, 100) })
        {
            using var a = new Mat(scene, rect);
            using var b = new Mat(result, rect);
            using var diff = new Mat();
            Cv2.Absdiff(a, b, diff);
            Assert.Equal(0, Cv2.CountNonZero(diff.Reshape(1)));
        }
    }

    [Fact]
    public void InsideFrame_ShowsArtwork_WithShadowGradient()
    {
        using var scene = MakeScene();
        using var art = Red();
        using var prepared = PreparedScene.Create(scene, Quad, new SceneSettings { LightingStrength = 1 });
        using var result = prepared.Render(art);

        var left = result.At<Vec3b>(300, 260);
        var right = result.At<Vec3b>(300, 500);
        Assert.True(left.Item2 > 150 && left.Item0 < 40, "left side should be red");
        Assert.True(right.Item2 > 100 && right.Item0 < 40, "right side should be red");
        Assert.True(left.Item2 > right.Item2 + 20, "shadow on the right should darken the art");
    }

    [Fact]
    public void LightingStrength_ChangesResult()
    {
        using var scene = MakeScene();
        using var art = Red();
        using var none = PreparedScene.Create(scene, Quad, new SceneSettings { LightingStrength = 0 });
        using var full = PreparedScene.Create(scene, Quad, new SceneSettings { LightingStrength = 1 });
        using var r0 = none.Render(art);
        using var r1 = full.Render(art);
        Assert.True(r0.At<Vec3b>(300, 500).Item2 > r1.At<Vec3b>(300, 500).Item2 + 20);
    }

    [Theory]
    [InlineData("cover")]
    [InlineData("contain")]
    [InlineData("stretch")]
    public void AllFitModes_Render(string fit)
    {
        using var scene = MakeScene();
        using var art = new Mat(500, 200, MatType.CV_8UC3, new Scalar(0, 255, 0));
        using var prepared = PreparedScene.Create(scene, Quad, new SceneSettings { FitMode = fit, MatteColor = "#0000ff" });
        using var result = prepared.Render(art);
        Assert.Equal(scene.Size(), result.Size());
    }

    [Fact]
    public void InvalidCorners_AreRejected()
    {
        using var scene = MakeScene();
        Assert.Throws<ArgumentException>(() => PreparedScene.Create(scene, new PointD[] { new(1, 1) }, new SceneSettings()));
        Assert.Throws<ArgumentException>(() => PreparedScene.Create(scene, new PointD[] { new(0, 0), new(9999, 0), new(9999, 10), new(0, 10) }, new SceneSettings()));
        // bow-tie (self-intersecting) order
        Assert.Throws<ArgumentException>(() => PreparedScene.Create(scene, new PointD[] { new(200, 150), new(540, 450), new(560, 170), new(210, 430) }, new SceneSettings()));
    }
}
