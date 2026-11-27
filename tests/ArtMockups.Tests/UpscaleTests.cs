using ArtMockups;
using OpenCvSharp;
using Xunit;

[Collection("env")]
public sealed class UpscaleTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("upscale-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    string OutDir => Path.Combine(_dir, "prints");

    string Source(string name, int width, int height, MatType? type = null)
    {
        using var img = new Mat(height, width, type ?? MatType.CV_8UC3);
        Cv2.Randu(img, Scalar.All(0), Scalar.All(255));
        var path = Path.Combine(_dir, name);
        Cv2.ImEncode(".png", img, out var bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Stands in for the AI model: a recognisable 4x enlargement.</summary>
    static Mat Fake(Mat source)
    {
        var big = new Mat();
        Cv2.Resize(source, big, new Size(source.Width * 4, source.Height * 4), 0, 0, InterpolationFlags.Nearest);
        return big;
    }

    [Theory]
    [InlineData(SplitLayout.Side, 4724, 7087)]
    [InlineData(SplitLayout.Stacked, 7087, 4724)]
    public void TwoHundredDpi_IsSupported(SplitLayout layout, int w, int h)
    {
        var plan = DiptychSplit.Plan(2688, 1792, layout, dpi: 200);
        Assert.Equal((w, h), (plan.PieceWidth, plan.PieceHeight));
        Assert.True(plan.NeedsEnlarging);
    }

    [Fact]
    public void AiExport_UpscalesTheWholeCropOnce_ThenResizesAndCuts()
    {
        var calls = new List<Size>();
        var source = Source("0001.png", 400, 300);
        var files = DiptychSplit.Export(source, OutDir, SplitLayout.Side, 0.5, 150, "png", m => { calls.Add(m.Size()); return Fake(m); });

        Assert.Equal([new Size(400, 300)], calls);
        Assert.Equal("0001_part1.png", files[0].Name);
        using var art = ImageIO.Load(source);
        using var big = Fake(art);
        using var expected = new Mat();
        Cv2.Resize(big, expected, new Size(3543 * 2, 5315), 0, 0, InterpolationFlags.Lanczos4);
        var plan = DiptychSplit.Plan(400, 300, SplitLayout.Side, dpi: 150);
        var inner = new Rect(plan.Border, plan.Border, plan.PieceWidth, plan.PieceHeight);
        using var left = ImageIO.Load(Path.Combine(OutDir, files[0].Name));
        using var right = ImageIO.Load(Path.Combine(OutDir, files[1].Name));
        using var a = new Mat(left, inner);
        using var b = new Mat(right, inner);
        using var joined = new Mat();
        Cv2.HConcat(new[] { a, b }, joined);
        Assert.Equal(0, Cv2.Norm(expected, joined, NormTypes.INF));
    }

    [Theory]
    [InlineData(400, 300, 0)]      // original pixels are never resampled
    [InlineData(7200, 5400, 150)]  // already more pixels than the print needs
    public void AiIsNotUsed_WhenNothingHasToBeEnlarged(int w, int h, int dpi)
    {
        var files = DiptychSplit.Export(Source("big.png", w, h), OutDir, SplitLayout.Side, 0.5, dpi, "jpg",
            _ => throw new InvalidOperationException("must not run"));
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void AiRefusesToReduceSixteenBitArtwork()
    {
        var source = Source("deep.png", 400, 300, MatType.CV_16UC3);
        var error = Assert.Throws<ArgumentException>(() => DiptychSplit.Export(source, OutDir, SplitLayout.Side, 0.5, 150, "png", Fake));
        Assert.Contains("16-bit", error.Message);
    }

    [Fact]
    public void ColourMatch_RemovesBrightnessShift_AndKeepsFineDetail()
    {
        using var source = new Mat(60, 80, MatType.CV_8UC3, new Scalar(100, 150, 245));
        using var enlarged = new Mat(240, 320, MatType.CV_8UC3, new Scalar(104, 153, 255)); // lifted, red clipped
        for (int y = 0; y < 240; y++) // fine detail the model "added": alternating pixels, zero on average
            for (int x = 0; x < 320; x++)
            {
                var p = enlarged.At<Vec3b>(y, x);
                enlarged.Set(y, x, new Vec3b((byte)(p.Item0 + ((x + y) % 2 == 0 ? 10 : -10)), p.Item1, p.Item2));
            }

        Upscayl.MatchSourceColour(enlarged, source);

        var a = enlarged.At<Vec3b>(100, 100);
        var b = enlarged.At<Vec3b>(100, 101);
        Assert.Equal(20, a.Item0 - b.Item0);                     // detail unchanged
        Assert.InRange((a.Item0 + b.Item0) / 2, 99, 101);         // brightness back to the source
        Assert.InRange(a.Item1, 149, 151);
        Assert.InRange(a.Item2, 244, 246);
    }

    [Fact]
    public void ColourMatch_RejectsABrokenResult()
    {
        using var source = new Mat(60, 80, MatType.CV_8UC3, new Scalar(200, 200, 200));
        using var black = new Mat(240, 320, MatType.CV_8UC3, Scalar.All(0));
        Assert.Throws<InvalidDataException>(() => Upscayl.MatchSourceColour(black, source));
    }

    [Fact]
    public void Find_UsesTheConfiguredFolder()
    {
        var old = Environment.GetEnvironmentVariable("ARTMOCKUPS_UPSCAYL");
        var exe = OperatingSystem.IsWindows() ? "upscayl-bin.exe" : "upscayl-bin";
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        Directory.CreateDirectory(Path.Combine(_dir, "models"));
        File.WriteAllText(Path.Combine(_dir, "bin", exe), "");
        File.WriteAllText(Path.Combine(_dir, "models", Upscayl.Model + ".bin"), "");
        try
        {
            Environment.SetEnvironmentVariable("ARTMOCKUPS_UPSCAYL", _dir);
            Assert.Equal(Path.Combine(_dir, "bin", exe), Upscayl.Find()?.Exe);
        }
        finally { Environment.SetEnvironmentVariable("ARTMOCKUPS_UPSCAYL", old); }
    }
}
