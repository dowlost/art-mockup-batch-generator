using ArtMockups;
using OpenCvSharp;
using Xunit;

public class ZoomCameraTests
{
    static readonly List<PointD> Opening = [new(608, 244), new(1413, 244), new(1413, 692), new(608, 692)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Box_KeepsAspect_StaysInsideScene_AndZoomsMonotonically(bool portrait)
    {
        var cam = new ZoomCamera(2048, 2048, Opening, portrait, 0.9, ease: true);
        double aspect = portrait ? 9.0 / 16 : 16.0 / 9, prevW = double.MaxValue;
        for (int i = 0; i <= 100; i++)
        {
            var b = cam.Box(i / 100.0);
            Assert.Equal(aspect, b.Width / b.Height, 6);
            Assert.True(b.Left >= -1e-6 && b.Top >= -1e-6 && b.Left + b.Width <= 2048 + 1e-6 && b.Top + b.Height <= 2048 + 1e-6, $"t={i / 100.0} box outside scene");
            Assert.True(b.Width <= prevW + 1e-9, "zoom must never reverse");
            prevW = b.Width;
        }
    }

    [Fact]
    public void Start_IsFullView_End_FitsOpeningAtEndFill()
    {
        var cam = new ZoomCamera(2048, 2048, Opening, portrait: false, endFill: 0.9, ease: true);
        Assert.Equal(2048, cam.StartBox.Width, 6);            // widest 16:9 box in a square scene
        Assert.Equal(805 / 0.9, cam.EndBox.Width, 3);          // opening width / endFill
        Assert.Equal(1010.5, cam.EndBox.CenterX, 3);           // centred on the opening
        Assert.Equal(1920 / cam.EndBox.Width, cam.EndUpscale, 6);
    }

    [Fact]
    public void Start_KeepsTheWholeOpeningInView()
    {
        // opening near the top of a square scene: a centred 16:9 box (rows 448..1600) would cut it off
        var high = new List<PointD> { new(600, 150), new(1400, 150), new(1400, 600), new(600, 600) };
        var cam = new ZoomCamera(2048, 2048, high, portrait: false, endFill: 0.9, ease: true);
        var s = cam.StartBox;
        Assert.True(s.Top <= 150 && s.Top + s.Height >= 600, $"start box {s.Top:0}..{s.Top + s.Height:0} must contain the opening 150..600");
        Assert.True(s.Top >= 0 && s.Top + s.Height <= 2048);
    }

    [Fact]
    public void Ease_HasZeroSpeedAtBothEnds_AndLinearDoesNot()
    {
        var eased = new ZoomCamera(2048, 2048, Opening, false, 0.9, ease: true);
        var linear = new ZoomCamera(2048, 2048, Opening, false, 0.9, ease: false);
        double Speed(ZoomCamera c, double t) => Math.Abs(c.Box(t + 0.001).Width - c.Box(t).Width);
        Assert.True(Speed(eased, 0) < Speed(linear, 0) * 0.05);
        Assert.True(Speed(eased, 0.999) < Speed(linear, 0.999) * 0.05);
    }

    [Fact]
    public void TallOpening_IsStillFullyVisibleAtTheEnd()
    {
        // a square-ish opening in a landscape frame is limited by height, not width
        var tall = new List<PointD> { new(900, 300), new(1150, 300), new(1150, 800), new(900, 800) };
        var cam = new ZoomCamera(2048, 2048, tall, portrait: false, endFill: 0.9, ease: true);
        var end = cam.EndBox;
        Assert.True(end.Height * 0.9 >= 500 - 1e-6, $"opening height 500 must fit in {end.Height * 0.9}");
    }

    [Fact]
    public void Render_HasExactOutputSize_AndEndFrameCentreIsTheArtwork()
    {
        using var scene = new Mat(2048, 2048, MatType.CV_8UC3, new Scalar(200, 210, 220));
        using var red = new Mat(300, 400, MatType.CV_8UC3, new Scalar(0, 0, 255));
        using var prepared = PreparedScene.Create(scene, Opening, new SceneSettings { LightingStrength = 0, EdgeFeatherPx = 0 });
        using var composite = prepared.Render(red);

        var land = new ZoomCamera(2048, 2048, Opening, false, 0.9, true);
        using var first = land.Render(composite, 0);
        using var last = land.Render(composite, 1);
        Assert.Equal(new Size(1920, 1080), first.Size());
        var c = last.At<Vec3b>(540, 960);
        Assert.True(c.Item2 > 240 && c.Item0 < 15, $"end frame centre BGR {c.Item0},{c.Item1},{c.Item2}");
        var edge = first.At<Vec3b>(5, 5);                   // wall colour, not art
        Assert.True(edge.Item2 < 230 && edge.Item0 > 190);

        var port = new ZoomCamera(2048, 2048, Opening, true, 0.9, true);
        using var p = port.Render(composite, 0.5);
        Assert.Equal(new Size(1080, 1920), p.Size());
    }

    [Fact]
    public void Rejects_BadOptions()
    {
        Assert.Throws<ArgumentException>(() => new ZoomCamera(2048, 2048, Opening, false, 1.5, true));
        Assert.Throws<ArgumentException>(() => new ZoomCamera(2048, 2048, Opening.Take(3).ToList(), false, 0.9, true));
    }

    [Fact]
    public void SyntheticName_IsFileSafe()
        => Assert.Equal("0001__cenario1_loop.mp4", ZoomVideoService.SyntheticName("cenario1", "0001.png", true));
}
