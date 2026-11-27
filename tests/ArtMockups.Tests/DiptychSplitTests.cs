using System.Buffers.Binary;
using ArtMockups;
using OpenCvSharp;
using Xunit;

public sealed class DiptychSplitTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("split-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    string OutDir => Path.Combine(_dir, "prints");

    string Source(string name, int width, int height, MatType? type = null)
    {
        var t = type ?? MatType.CV_8UC3;
        using var img = new Mat(height, width, t);
        Cv2.Randu(img, Scalar.All(0), Scalar.All(t.Depth == MatType.CV_16U ? 65535 : 255));
        var path = Path.Combine(_dir, name);
        Cv2.ImEncode(Path.GetExtension(name), img, out var bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    Mat Read(SplitFile f) => Cv2.ImDecode(File.ReadAllBytes(Path.Combine(OutDir, f.Name)), ImreadModes.Unchanged);

    /// <summary>The art inside a print file, after checking that the border around it is completely white.</summary>
    Mat Art(SplitFile f, SplitPlan plan)
    {
        using var file = Read(f);
        Assert.Equal((plan.FileWidth, plan.FileHeight), (file.Width, file.Height));
        Assert.True(plan.Border > 0);
        var inner = new Rect(plan.Border, plan.Border, plan.PieceWidth, plan.PieceHeight);
        using var painted = file.Clone();
        using (var hole = new Mat(painted, inner)) hole.SetTo(Scalar.All(file.Depth() == MatType.CV_16U ? 65535 : 255));
        using var white = new Mat(file.Size(), file.Type(), Scalar.All(file.Depth() == MatType.CV_16U ? 65535 : 255));
        Assert.Equal(0, Cv2.Norm(painted, white, NormTypes.INF));
        using var view = new Mat(file, inner);
        return view.Clone();
    }

    static void AssertSamePixels(Mat expected, Mat actual)
    {
        Assert.Equal(expected.Size(), actual.Size());
        Assert.Equal(expected.Type(), actual.Type());
        Assert.Equal(0, Cv2.Norm(expected, actual, NormTypes.INF));
    }

    static byte[] FakeProfile(int length, string colourSpace = "RGB ")
    {
        var icc = new byte[length];
        new Random(7).NextBytes(icc);
        BinaryPrimitives.WriteInt32BigEndian(icc, length);
        System.Text.Encoding.ASCII.GetBytes(colourSpace).CopyTo(icc, 16);
        return icc;
    }

    [Theory]
    [InlineData(2688, 1792, SplitLayout.Side, 2388, 1791, 1194, 1791)]
    [InlineData(2688, 1792, SplitLayout.Stacked, 1344, 1792, 1344, 896)]
    [InlineData(1792, 2688, SplitLayout.Stacked, 1791, 2388, 1791, 1194)]
    [InlineData(1792, 2688, SplitLayout.Side, 1792, 1344, 896, 1344)]
    public void OriginalPixels_PiecesHaveExactPrintShape(int w, int h, SplitLayout layout, int cw, int ch, int pw, int ph)
    {
        var plan = DiptychSplit.Plan(w, h, layout);
        Assert.Equal((cw, ch), (plan.Crop.Width, plan.Crop.Height));
        Assert.Equal((pw, ph), (plan.PieceWidth, plan.PieceHeight));
        Assert.Equal(plan.PieceCmWidth * ph, plan.PieceCmHeight * pw); // 2:3 or 3:2 exactly
        Assert.True(plan.Crop.Right <= w && plan.Crop.Bottom <= h);
    }

    [Fact]
    public void ReportsRealDetailAndHowMuchIsCropped()
    {
        var plan = DiptychSplit.Plan(2688, 1792, SplitLayout.Side);
        Assert.InRange(plan.SourceDpi, 50, 51);
        Assert.InRange(plan.CroppedPercent, 11, 12);
        // upscaling changes the file size, not the real detail
        Assert.Equal(plan.SourceDpi, DiptychSplit.Plan(2688, 1792, SplitLayout.Side, dpi: 300).SourceDpi);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 150)]
    [InlineData(1, 300)]
    [InlineData(7, 300)]
    [InlineData(-1, 0)]
    public void Offset_SlidesTheCropWithoutLeavingTheImage(double offset, int x) =>
        Assert.Equal(x, DiptychSplit.Plan(2688, 1792, SplitLayout.Side, offset).Crop.X);

    [Theory]
    [InlineData(SplitLayout.Side, 150, 3543, 5315)]
    [InlineData(SplitLayout.Side, 300, 7087, 10630)]
    [InlineData(SplitLayout.Stacked, 150, 5315, 3543)]
    [InlineData(SplitLayout.Stacked, 300, 10630, 7087)]
    public void PrintResolutions_Give60By90cmPieces(SplitLayout layout, int dpi, int pw, int ph)
    {
        var plan = DiptychSplit.Plan(2688, 1792, layout, dpi: dpi);
        Assert.Equal((pw, ph), (plan.PieceWidth, plan.PieceHeight));
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => DiptychSplit.Plan(2688, 1792, SplitLayout.Side, dpi: 72));
        Assert.Throws<ArgumentException>(() => DiptychSplit.Plan(3, 3, SplitLayout.Side));
        Assert.Throws<ArgumentException>(() => DiptychSplit.Plan(2688, 1792, SplitLayout.Side, double.NaN));
        Assert.Throws<ArgumentException>(() => DiptychSplit.ParseLayout("diagonal"));
        Assert.Throws<ArgumentException>(() => DiptychSplit.Export(Source("a.png", 40, 30), OutDir, SplitLayout.Side, 0.5, 0, "gif"));
    }

    [Theory]
    [InlineData(SplitLayout.Side)]
    [InlineData(SplitLayout.Stacked)]
    public void OriginalExport_IsPixelIdenticalToTheSource(SplitLayout layout)
    {
        var source = Source("0001.png", 1000, 700);
        var before = File.ReadAllBytes(source);
        var files = DiptychSplit.Export(source, OutDir, layout, 0.5, 0, "png");

        Assert.Equal(["0001_print_part1.png", "0001_print_part2.png"], files.Select(f => f.Name));
        using var art = ImageIO.Load(source);
        var plan = DiptychSplit.Plan(1000, 700, layout);
        using var expected = new Mat(art, plan.Crop);
        using var a = Art(files[0], plan);
        using var b = Art(files[1], plan);
        using var joined = new Mat();
        if (layout == SplitLayout.Side) Cv2.HConcat(new[] { a, b }, joined);
        else Cv2.VConcat(new[] { a, b }, joined);
        AssertSamePixels(expected, joined);
        Assert.Equal(before, File.ReadAllBytes(source)); // the artwork is never modified
    }

    [Fact]
    public void Parts_GoNextToTheArtwork_AndAreOnlyReplacedWhenConfirmed()
    {
        var source = Source("0007.png", 400, 300);
        var original = File.ReadAllBytes(source);
        var unrelated = Source("00070_print_part1.png", 40, 30); // another artwork's half must survive
        DiptychSplit.Export(source, _dir, SplitLayout.Side, 0.5, 0, "png");
        var part1 = Path.Combine(_dir, "0007_print_part1.png");
        var first = File.ReadAllBytes(part1);

        var refused = Assert.Throws<InvalidOperationException>(() => DiptychSplit.Export(source, _dir, SplitLayout.Stacked, 0.5, 0, "png"));
        Assert.Contains("0007_print_part1.png", refused.Message);
        Assert.Equal(first, File.ReadAllBytes(part1));

        DiptychSplit.Export(source, _dir, SplitLayout.Stacked, 0.5, 0, "jpg", overwrite: true);
        Assert.Equal(["0007.png", "00070_print_part1.png", "0007_print_part1.jpg", "0007_print_part2.jpg"],
            Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.True(File.Exists(unrelated));
    }

    [Theory]
    [InlineData("0007_part2.png", SplitLayout.Side)]
    [InlineData("0007_print.png", SplitLayout.Full)]
    [InlineData("0007_print_part1.png", SplitLayout.Side)]
    public void APrintFile_IsNotProcessedAgain(string name, SplitLayout layout) =>
        Assert.Throws<ArgumentException>(() => DiptychSplit.Export(Source(name, 400, 300), OutDir, layout, 0.5, 0, "png"));

    [Theory]
    [InlineData(2688, 1792, 2688, 1792, 90, 60)] // 3:2 fits without cropping
    [InlineData(1792, 2688, 1792, 2688, 60, 90)]
    [InlineData(2464, 1856, 2463, 1642, 90, 60)] // 4:3 art is trimmed to 3:2
    public void FullArt_IsOnePrint_FollowingTheArtworksOrientation(int w, int h, int cw, int ch, int cmW, int cmH)
    {
        var plan = DiptychSplit.Plan(w, h, SplitLayout.Full);
        Assert.Equal(1, plan.Pieces);
        Assert.Equal((cw, ch), (plan.Crop.Width, plan.Crop.Height));
        Assert.Equal((cw, ch), (plan.PieceWidth, plan.PieceHeight));
        Assert.Equal((cmW, cmH), (plan.PieceCmWidth, plan.PieceCmHeight));
        Assert.Equal((cmW + 1, cmH + 1), (plan.FileCmWidth, plan.FileCmHeight));
    }

    [Theory]
    [InlineData(SplitLayout.Full, 0)]
    [InlineData(SplitLayout.Full, 200)]
    [InlineData(SplitLayout.Side, 200)]
    public void EveryPrint_HasHalfACentimetreOfWhiteOnEachSide(SplitLayout layout, int dpi)
    {
        var plan = DiptychSplit.Plan(600, 400, layout, dpi: dpi);
        Assert.Equal(plan.PieceWidth * 0.5 / plan.PieceCmWidth, plan.Border, 0.5);
        var files = DiptychSplit.Export(Source("0005.png", 600, 400), OutDir, layout, 0.5, dpi, "png");
        foreach (var f in files) using (Art(f, plan)) { }

        // the file, border included, opens at exactly 1 cm more than the art in each direction
        var bytes = File.ReadAllBytes(Path.Combine(OutDir, files[0].Name));
        int at = bytes.AsSpan().IndexOf("pHYs"u8);
        Assert.Equal(plan.FileCmWidth, 100 * files[0].Width / (double)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4)), 0.05);
        Assert.Equal(plan.FileCmHeight, 100 * files[0].Height / (double)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8)), 0.05);
    }

    [Fact]
    public void FullArtExport_IsOneFile_IndependentOfTheSplitParts()
    {
        var source = Source("0008.png", 600, 400);
        DiptychSplit.Export(source, _dir, SplitLayout.Side, 0.5, 0, "png");
        var files = DiptychSplit.Export(source, _dir, SplitLayout.Full, 0.5, 0, "png"); // no confirmation needed

        Assert.Equal(["0008_print.png"], files.Select(f => f.Name));
        Assert.Equal(3, Directory.GetFiles(_dir, "0008_*").Length);
        Assert.Throws<InvalidOperationException>(() => DiptychSplit.Export(source, _dir, SplitLayout.Full, 0.5, 0, "png"));
        using var art = ImageIO.Load(source);
        using var print = ImageIO.Load(Path.Combine(_dir, "0008_print.png"));
        var border = DiptychSplit.Plan(600, 400, SplitLayout.Full).Border;
        using var inner = new Mat(print, new Rect(border, border, 600, 400));
        AssertSamePixels(art, inner.Clone());
    }

    [Fact]
    public void SixteenBitArtwork_StaysSixteenBit_AndJpegIsConverted()
    {
        var source = Source("deep.png", 400, 300, MatType.CV_16UC3);
        var png = DiptychSplit.Export(source, OutDir, SplitLayout.Side, 0.5, 0, "png");
        using var art = Cv2.ImDecode(File.ReadAllBytes(source), ImreadModes.Unchanged);
        using var expected = new Mat(art, new Rect(0, 0, 200, 300));
        using var left = Art(png[0], DiptychSplit.Plan(400, 300, SplitLayout.Side));
        AssertSamePixels(expected, left);

        var jpg = DiptychSplit.Export(source, OutDir, SplitLayout.Side, 0.5, 0, "jpg", overwrite: true);
        using var small = Read(jpg[0]);
        Assert.Equal(MatType.CV_8UC3, small.Type());
    }

    [Fact]
    public void DpiExport_ResizesTheWholeCropOnce_SoTheSeamMatches()
    {
        var source = Source("0002.png", 400, 300);
        var files = DiptychSplit.Export(source, OutDir, SplitLayout.Side, 0.5, 150, "png");

        Assert.Equal("0002_print_part1.png", files[0].Name);
        using var art = ImageIO.Load(source);
        using var expected = new Mat();
        Cv2.Resize(art, expected, new Size(3543 * 2, 5315), 0, 0, InterpolationFlags.Lanczos4);
        var plan = DiptychSplit.Plan(400, 300, SplitLayout.Side, dpi: 150);
        using var a = Art(files[0], plan);
        using var b = Art(files[1], plan);
        using var joined = new Mat();
        Cv2.HConcat(new[] { a, b }, joined);
        AssertSamePixels(expected, joined);
    }

    [Fact]
    public void Png_IsTaggedWithThePrintSize()
    {
        var files = DiptychSplit.Export(Source("0003.png", 1000, 700), OutDir, SplitLayout.Side, 0.5, 0, "png");
        var bytes = File.ReadAllBytes(Path.Combine(OutDir, files[0].Name));
        int at = bytes.AsSpan().IndexOf("pHYs"u8);
        Assert.True(at > 0 && at < bytes.AsSpan().IndexOf("IDAT"u8));
        // pixels per metre: file pixels over 0.61 m wide and 0.91 m high
        Assert.Equal((uint)Math.Round(files[0].Width / 0.61), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4)));
        Assert.Equal((uint)Math.Round(files[0].Height / 0.91), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8)));
        Assert.Equal(1, bytes[at + 12]);
    }

    [Fact]
    public void Jpeg_IsTaggedWithDpi()
    {
        var files = DiptychSplit.Export(Source("0004.png", 400, 300), OutDir, SplitLayout.Stacked, 0.5, 150, "jpg");
        var bytes = File.ReadAllBytes(Path.Combine(OutDir, files[1].Name));
        Assert.True(bytes.AsSpan(6, 5).SequenceEqual("JFIF\0"u8));
        Assert.Equal(1, bytes[13]); // dots per inch
        Assert.Equal(150, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(14)));
        Assert.Equal(150, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16)));
        using var img = Read(files[1]);
        Assert.Equal((5315 + 60, 3543 + 60), (img.Width, img.Height)); // 30 px of border per side
    }

    [Theory]
    [InlineData("png", "png", 3000)]
    [InlineData("png", "jpg", 3000)]
    [InlineData("jpg", "png", 3000)]
    [InlineData("jpg", "jpg", 150_000)] // needs several JPEG segments
    public void ColourProfile_IsCopiedToBothPieces(string from, string to, int size)
    {
        var source = Source("icc." + from, 400, 300);
        var icc = FakeProfile(size);
        File.WriteAllBytes(source, PrintMetadata.Apply(File.ReadAllBytes(source), 96, 96, icc));
        Assert.Equal(icc, PrintMetadata.ReadRgbProfile(File.ReadAllBytes(source)));

        var files = DiptychSplit.Export(source, OutDir, SplitLayout.Side, 0.5, 0, to);
        foreach (var f in files)
        {
            Assert.Equal(icc, PrintMetadata.ReadRgbProfile(File.ReadAllBytes(Path.Combine(OutDir, f.Name))));
            using var img = Read(f);
            Assert.Equal((204, 304), (img.Width, img.Height));
        }
    }

    [Fact]
    public void NonRgbProfile_IsNotCopied()
    {
        var source = Source("cmyk.jpg", 40, 30);
        var tagged = PrintMetadata.Apply(File.ReadAllBytes(source), 96, 96, FakeProfile(500, "CMYK"));
        Assert.Null(PrintMetadata.ReadRgbProfile(tagged));
    }
}
