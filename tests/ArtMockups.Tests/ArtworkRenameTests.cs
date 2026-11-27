using ArtMockups;
using Xunit;

[Collection("env")]
public class ArtworkRenameTests
{
    static (ArtworkLibrary Lib, string Dir, Action Cleanup) Setup(params string[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var old = Environment.GetEnvironmentVariable("ARTMOCKUPS_DATA");
        Environment.SetEnvironmentVariable("ARTMOCKUPS_DATA", root);
        var paths = new AppPaths(root);
        var dir = Path.Combine(root, "artworks");
        var t = DateTime.UtcNow.AddHours(-10);
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
            File.SetCreationTimeUtc(Path.Combine(dir, f), t); // later files in the list are newer
            t = t.AddMinutes(1);
        }
        return (new ArtworkLibrary(paths, new ConfigStore(paths)), dir, () =>
        {
            Environment.SetEnvironmentVariable("ARTMOCKUPS_DATA", old);
            try { Directory.Delete(root, true); } catch { }
        });
    }

    [Fact]
    public void RenamesOnlyNonConforming_ContinuingAfterHighestNumber_OldestFirst()
    {
        var (lib, dir, cleanup) = Setup("0001.png", "0002.png", "0047.png", "zeta random.png", "alpha.JPG", "12.png", "00123.jpeg");
        try
        {
            var plan = lib.NormalizeNames(dryRun: true);
            Assert.Equal(["zeta random.png", "alpha.JPG", "12.png", "00123.jpeg"], plan.Select(p => p.From));
            Assert.Equal(["0048.png", "0049.jpg", "0050.png", "0051.jpeg"], plan.Select(p => p.To));
            Assert.True(File.Exists(Path.Combine(dir, "zeta random.png")), "dry run must not rename");

            var done = lib.NormalizeNames(dryRun: false);
            Assert.Equal(4, done.Count);
            Assert.True(File.Exists(Path.Combine(dir, "0048.png")) && File.Exists(Path.Combine(dir, "0051.jpeg")));
            Assert.True(File.Exists(Path.Combine(dir, "0047.png")), "conforming files are untouched");
            Assert.False(File.Exists(Path.Combine(dir, "zeta random.png")));
            Assert.Empty(lib.NormalizeNames(dryRun: false)); // idempotent
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SplitHalves_KeepTheirNumber_AndFollowARenamedArtwork()
    {
        var (lib, _, cleanup) = Setup("0001.png", "0001_part1.png", "0001_part2.png", "0009_part1.jpg",
            "sea_part2.png", "sea_part1.png", "sea.png", "lost_part1.png", "lost_part2.png");
        try
        {
            var plan = lib.NormalizeNames(dryRun: false).ToDictionary(p => p.From, p => p.To);

            // numbering continues after 0009, which only exists as a half
            Assert.Equal(new Dictionary<string, string>
            {
                ["sea.png"] = "0010.png", ["sea_part1.png"] = "0010_part1.png", ["sea_part2.png"] = "0010_part2.png",
                ["lost_part1.png"] = "0011_part1.png", ["lost_part2.png"] = "0011_part2.png",
            }, plan);
            Assert.Empty(lib.NormalizeNames(dryRun: false));
        }
        finally { cleanup(); }
    }

    [Fact]
    public void PrintFile_FollowsItsArtwork()
    {
        var (lib, _, cleanup) = Setup("0003_print.png", "sea_print.jpg", "sea.png");
        try
        {
            Assert.Equal([("sea.png", "0004.png"), ("sea_print.jpg", "0004_print.jpg")],
                lib.NormalizeNames(true).Select(p => (p.From, p.To)));
        }
        finally { cleanup(); }
    }

    [Fact]
    public void Counter_KeepsNumbersFromBeingReused()
    {
        var (lib, dir, cleanup) = Setup("0001.png", "0012.png", "0012_part1.png");
        try
        {
            Assert.Equal(0, lib.LastNumber());
            lib.List();
            Assert.Equal(12, lib.LastNumber());

            // the files are gone (or never reached this computer): the counter still remembers
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            lib.List();
            Assert.Equal(12, lib.LastNumber());
            File.WriteAllText(Path.Combine(dir, "new.png"), "x");
            Assert.Equal(["0013.png"], lib.NormalizeNames(false).Select(p => p.To));
            Assert.Equal(13, lib.LastNumber());
        }
        finally { cleanup(); }
    }

    [Fact]
    public void UnreadableCounter_StopsRenaming_AndIsLeftAlone()
    {
        var (lib, dir, cleanup) = Setup("0001.png", "new.png");
        try
        {
            var counter = Path.Combine(dir, "..", "artworks-counter.json");
            File.WriteAllText(counter, "<<<<<<< HEAD");
            lib.List(); // listing still works
            Assert.Throws<InvalidDataException>(() => lib.NormalizeNames(false));
            Assert.Equal("<<<<<<< HEAD", File.ReadAllText(counter));
            Assert.True(File.Exists(Path.Combine(dir, "new.png")));
        }
        finally { cleanup(); }
    }

    [Fact]
    public void StartsAtOne_WhenNothingIsNumbered()
    {
        var (lib, _, cleanup) = Setup("b.png", "a.png");
        try { Assert.Equal(["0001.png", "0002.png"], lib.NormalizeNames(true).Select(p => p.To)); }
        finally { cleanup(); }
    }
}
