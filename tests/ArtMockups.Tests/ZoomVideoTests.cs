using ArtMockups;
using Xunit;

public class ZoomVideoTests
{
    [Fact]
    public void ParsesFfmpegProgressFrameLines_AndIgnoresTheRest()
    {
        Assert.Equal(48, FfmpegRunner.ParseProgressFrame("frame=48"));
        Assert.Equal(0, FfmpegRunner.ParseProgressFrame("frame=0"));
        Assert.Null(FfmpegRunner.ParseProgressFrame("progress=continue"));
        Assert.Null(FfmpegRunner.ParseProgressFrame("fps=24.0"));
        Assert.Null(FfmpegRunner.ParseProgressFrame(null));
    }

    [Fact]
    public void EncodeArgs_AskForProgress_AndOnlyLoopAddsReverseConcat()
    {
        var plain = ZoomVideoService.BuildEncodeArgs("in/%05d.png", 24, "o.mp4", false);
        var loop = ZoomVideoService.BuildEncodeArgs("in/%05d.png", 24, "o.mp4", true);
        Assert.Contains("pipe:1", plain);
        Assert.DoesNotContain("-filter_complex", plain);
        var filter = loop[loop.IndexOf("-filter_complex") + 1];
        Assert.Contains("reverse", filter);
        Assert.Contains("concat=n=2:v=1:a=0", filter);
        Assert.Contains("-an", loop);
        Assert.Equal("o.mp4", loop[^1]);
    }

    [Fact]
    public void OutputName_MatchesTheStillImageName()
    {
        Assert.Equal("0001__cenario1.mp4", ZoomVideoService.SyntheticName("cenario1", "0001.png", false));
        Assert.Equal("0001__cenario1_loop.mp4", ZoomVideoService.SyntheticName("cenario1", "0001.png", true));
    }
}
