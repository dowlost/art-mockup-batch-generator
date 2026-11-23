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
    public void StartsAtOne_WhenNothingIsNumbered()
    {
        var (lib, _, cleanup) = Setup("b.png", "a.png");
        try { Assert.Equal(["0001.png", "0002.png"], lib.NormalizeNames(true).Select(p => p.To)); }
        finally { cleanup(); }
    }
}
