using OpenCvSharp;

namespace ArtMockups;

/// <summary>
/// AI upscaling with the "Standard" model of a separately installed Upscayl (https://github.com/upscayl/upscayl),
/// run as a command-line tool. Nothing of Upscayl is shipped with this app.
/// </summary>
public sealed class Upscayl(AppPaths paths)
{
    public const string Model = "upscayl-standard-4x";
    public const int Factor = 4;
    const long MaxSourcePixels = 40_000_000; // beyond this the 4x result needs many gigabytes of memory

    public sealed record Install(string Exe, string ModelsDir);

    /// <summary>ARTMOCKUPS_UPSCAYL (the install's "resources" folder), then the usual install folders.</summary>
    public static Install? Find()
    {
        var exe = OperatingSystem.IsWindows() ? "upscayl-bin.exe" : "upscayl-bin";
        string?[] folders =
        [
            Environment.GetEnvironmentVariable("ARTMOCKUPS_UPSCAYL"),
            Under(Environment.SpecialFolder.ProgramFiles, "Upscayl", "resources"),
            Under(Environment.SpecialFolder.LocalApplicationData, "Programs", "Upscayl", "resources"),
            "/Applications/Upscayl.app/Contents/Resources",
            "/opt/Upscayl/resources",
        ];
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var found = new Install(Path.Combine(folder, "bin", exe), Path.Combine(folder, "models"));
            if (File.Exists(found.Exe) && File.Exists(Path.Combine(found.ModelsDir, Model + ".bin"))) return found;
        }
        return null;

        static string? Under(Environment.SpecialFolder root, params string[] parts) =>
            Environment.GetFolderPath(root) is { Length: > 0 } r ? Path.Combine([r, .. parts]) : null;
    }

    /// <summary>Returns the image enlarged exactly 4x, with the brightness and colour of the source.</summary>
    public Mat Upscale(Mat source)
    {
        var install = Find() ?? throw new InvalidOperationException("Upscayl was not found. Install it, or untick AI upscale.");
        if (source.Type() != MatType.CV_8UC3) throw new ArgumentException("AI upscaling works with 8-bit colour images only.");
        if ((long)source.Width * source.Height > MaxSourcePixels)
            throw new ArgumentException("This artwork is too large for AI upscaling. Untick AI upscale.");

        var dir = Path.Combine(paths.CacheDir, "upscale", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string input = Path.Combine(dir, "in.png"), output = Path.Combine(dir, "out.png");
            File.WriteAllBytes(input, ImageIO.Encode(source, "png", 100));

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            (int Exit, string Stderr) run;
            try
            {
                run = FfmpegRunner.RunAsync(install.Exe,
                    ["-i", input, "-o", output, "-m", install.ModelsDir, "-n", Model, "-s", Factor.ToString(), "-f", "png"],
                    timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("AI upscaling took longer than 10 minutes and was stopped.");
            }
            // The tool can exit with 0 without writing anything, so check for the file too.
            if (run.Exit != 0 || !File.Exists(output))
                throw new InvalidOperationException("AI upscaling failed: " + Reason(run.Stderr));

            var result = ImageIO.Load(output);
            try
            {
                if (result.Width != source.Width * Factor || result.Height != source.Height * Factor)
                    throw new InvalidDataException($"AI upscaling returned an unexpected size ({result.Width}×{result.Height}).");
                MatchSourceColour(result, source);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* the cache folder can be cleared by hand */ }
        }
    }

    /// <summary>
    /// The model shifts brightness slightly (and turns some off-whites pure white). This adds back the smooth, large
    /// scale difference to the source, leaving the fine detail alone. It also rejects a result that does not resemble
    /// the source at all (a failing graphics driver can produce black or scrambled images).
    /// </summary>
    public static void MatchSourceColour(Mat enlarged, Mat source)
    {
        using var small = new Mat();
        Cv2.Resize(enlarged, small, source.Size(), 0, 0, InterpolationFlags.Area);
        if (Cv2.Norm(small, source, NormTypes.L1) / ((double)source.Width * source.Height * source.Channels()) > 40)
            throw new InvalidDataException("AI upscaling produced an image that does not match the artwork. Check the graphics driver.");

        using var wanted = new Mat();
        using var got = new Mat();
        source.ConvertTo(wanted, MatType.CV_32FC3);
        small.ConvertTo(got, MatType.CV_32FC3);
        using var shift = new Mat();
        Cv2.Subtract(wanted, got, shift);
        Cv2.GaussianBlur(shift, shift, new Size(0, 0), 8);
        using var shift16 = new Mat(); // whole levels, to keep the full-size copy small
        shift.ConvertTo(shift16, MatType.CV_16SC3);
        using var full = new Mat();
        Cv2.Resize(shift16, full, enlarged.Size(), 0, 0, InterpolationFlags.Linear);
        Cv2.Add(enlarged, full, enlarged, null, MatType.CV_8U); // saturates to 0..255
    }

    /// <summary>The last messages of the tool, without its progress percentages.</summary>
    static string Reason(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.EndsWith('%')).TakeLast(3).ToList();
        return lines.Count == 0 ? "no details" : string.Join(" ", lines);
    }
}
