using ArtMockups;
using OpenCvSharp;
using Xunit;

public class SizeLimitTests
{
    static Mat Noise()
    {
        var m = new Mat(1500, 2000, MatType.CV_8UC3);
        Cv2.Randu(m, Scalar.All(0), Scalar.All(255));
        return m;
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    public void Output_NeverExceedsLimit(string ext)
    {
        using var img = Noise();
        const long limit = 2_000_000;
        var bytes = ImageIO.EncodeUnderLimit(img, ext, 92, limit);
        Assert.True(bytes.Length <= limit, $"{bytes.Length} > {limit}");
    }

    [Fact]
    public void FullQuality_NoLimit_KeepsResolutionAndIsLarger()
    {
        using var img = Noise();
        var full = ImageIO.EncodeUnderLimit(img, "jpg", 100, 0);
        Assert.True(full.Length > 2_000_000);
        using var back = Cv2.ImDecode(full, ImreadModes.Color);
        Assert.Equal(img.Width, back.Width);
        Assert.Equal(img.Height, back.Height);
        Assert.True(full.Length > ImageIO.Encode(img, "jpg", 92).Length);
    }

    [Fact]
    public void SmallImage_KeepsRequestedQuality()
    {
        using var img = new Mat(200, 200, MatType.CV_8UC3, new Scalar(10, 20, 30));
        var limited = ImageIO.EncodeUnderLimit(img, "jpg", 92, 2_000_000);
        Assert.Equal(ImageIO.Encode(img, "jpg", 92).Length, limited.Length);
    }
}
