using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenCvSharp;

namespace ArtMockups;

public sealed class VideoJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public int Total { get; set; }
    public int Done;
    public int Rendered;
    public int Skipped;
    public int Failed;
    public volatile string Current = "";
    public volatile string Status = "running"; // running | completed | cancelled
    public ConcurrentQueue<BatchError> Errors { get; } = new();
    public DateTime Started { get; } = DateTime.UtcNow;
    public CancellationTokenSource Cts { get; } = new();

    public object Snapshot() => new
    {
        id = Id, status = Status, total = Total, done = Volatile.Read(ref Done), rendered = Volatile.Read(ref Rendered),
        skipped = Volatile.Read(ref Skipped), failed = Volatile.Read(ref Failed), current = Current,
        errors = Errors.ToArray(), seconds = (DateTime.UtcNow - Started).TotalSeconds,
    };
}

/// <summary>Runs ffmpeg (or another command-line tool) and returns its exit code and stderr.</summary>
public static class FfmpegRunner
{
    /// <summary>Parses the "frame=N" lines ffmpeg prints with <c>-progress pipe:1</c>.</summary>
    public static int? ParseProgressFrame(string? line) =>
        line != null && line.StartsWith("frame=") && int.TryParse(line.AsSpan(6).Trim(), out var n) ? n : null;

    public static async Task<(int Exit, string Stderr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, Action<int>? onFrame = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");
        var err = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null && err.Length < 20000) err.AppendLine(e.Data); };
        proc.OutputDataReceived += (_, e) => { if (onFrame != null && ParseProgressFrame(e.Data) is { } n) onFrame(n); };
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (proc.ExitCode, err.ToString());
    }

    public static string Brief(string stderr)
    {
        var s = stderr.Trim();
        return s.Length == 0 ? "no details" : s[..Math.Min(s.Length, 400)];
    }
}

/// <summary>
/// Zoom videos: the artwork is placed in a scene, then a virtual camera glides from the full view into the artwork.
/// One video per (scene x artwork), written next to the still images in the output folder. Needs ffmpeg.
/// </summary>
public sealed class ZoomVideoService(AppPaths paths, ArtworkLibrary library, SceneStore scenes, SceneImageCache sceneImages)
{
    readonly ConcurrentDictionary<string, VideoJob> _jobs = new();
    public VideoJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public VideoJob? Running => _jobs.Values.FirstOrDefault(j => j.Status == "running");

    /// <summary>ARTMOCKUPS_FFMPEG env var, then ffmpeg(.exe) next to the app / working dir, then PATH.</summary>
    public string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var env = Environment.GetEnvironmentVariable("ARTMOCKUPS_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        foreach (var dir in new[] { AppContext.BaseDirectory, paths.Root })
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static List<string> BuildEncodeArgs(string pattern, double fps, string output, bool loop)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-y", "-framerate", fps.ToString("0.####", CultureInfo.InvariantCulture), "-i", pattern };
        if (loop)
        {
            args.AddRange(["-filter_complex", "[0:v]split[a][b];[b]reverse[r];[a][r]concat=n=2:v=1:a=0[v]", "-map", "[v]"]);
        }
        args.AddRange(["-an", "-c:v", "libx264", "-crf", "14", "-preset", "slow", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output]);
        return args;
    }

    // ---------- synthetic zoom: we move the camera ourselves over the composited mockup ----------

    /// <summary>Same base name as the still image (&lt;art&gt;__&lt;scene&gt;), so image and video sit side by side in the output folder.</summary>
    public static string SyntheticName(string scene, string artwork, bool loop) =>
        Names.SafeFileStem(Path.GetFileNameWithoutExtension(artwork)) + "__" + Names.SafeFileStem(scene) + (loop ? "_loop" : "") + ".mp4";

    public static string SyntheticName(Scene scene, ArtPair art, bool loop) =>
        MockupPlan.OutputStem(scene, art) + (loop ? "_loop" : "") + ".mp4";

    ZoomCamera CameraFor(Scene scene, Mat sceneImage, SyntheticRequest r)
    {
        var endFill = r.EndFill ?? 0.9;
        return new ZoomCamera(sceneImage.Width, sceneImage.Height, scene.CameraTarget(),string.Equals(r.Orientation, "portrait", StringComparison.OrdinalIgnoreCase), endFill, r.Ease ?? true);
    }

    static string FirstScene(SyntheticRequest r) =>
        r.Scene ?? r.Scenes?.FirstOrDefault() ?? throw new ArgumentException("Pick a scene.");

    public object SyntheticInfo(SyntheticRequest r)
    {
        var scene = scenes.Get(FirstScene(r));
        var cam = CameraFor(scene, sceneImages.Get(scene), r);
        return new { outWidth = cam.OutWidth, outHeight = cam.OutHeight, endUpscale = cam.EndUpscale, startWidth = cam.StartBox.Width, endWidth = cam.EndBox.Width };
    }

    public Task<byte[]> PreviewSyntheticAsync(SyntheticRequest r, ArtPair art, double t) => Task.Run(() =>
    {
        var scene = scenes.Get(FirstScene(r));
        var img = sceneImages.Get(scene);
        var cam = CameraFor(scene, img, r);
        using var prepared = PreparedMockup.Create(img, scene);
        using var composite = prepared.Render(library, art);
        using var frame = cam.Render(composite, t);
        return ImageIO.Encode(frame, ".jpg", 90);
    });

    /// <summary>One video per (scene x artwork or pair), written next to the still images in the output folder.</summary>
    public VideoJob StartSynthetic(SyntheticRequest req)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg was not found. Put ffmpeg.exe next to the app and restart.");
        if (Running != null) throw new InvalidOperationException("A video job is already running.");
        var names = (req.Scenes is { Count: > 0 } ? req.Scenes : req.Scene != null ? [req.Scene] : []).Distinct().ToList();
        if (names.Count == 0) throw new ArgumentException("Select at least one scene for the videos.");
        var sceneList = names.Select(scenes.Get).ToList();
        var items = MockupPlan.Build(sceneList, req.Artworks, req.Pairs);
        MockupPlan.RequireSplits(library, items);
        double seconds = req.Seconds ?? 5; int fps = req.Fps ?? 24;
        if (seconds is < 1 or > 15) throw new ArgumentException("Duration must be between 1 and 15 seconds.");
        if (fps is < 12 or > 60) throw new ArgumentException("Frame rate must be between 12 and 60.");
        int frameCount = Math.Max(2, (int)Math.Round(seconds * fps));
        foreach (var s in sceneList) CameraFor(s, sceneImages.Get(s), req); // validates the options before the job starts

        // each video = frameCount units of rendering + frameCount units of encoding, so the bar keeps moving while ffmpeg works
        var job = new VideoJob { Total = items.Count * frameCount * 2 };
        _jobs[job.Id] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var scene in sceneList)
                {
                    job.Cts.Token.ThrowIfCancellationRequested();
                    var sceneImage = sceneImages.Get(scene);
                    var cam = CameraFor(scene, sceneImage, req);
                    using var prepared = PreparedMockup.Create(sceneImage, scene);
                    foreach (var art in items.Where(i => i.Scene == scene).Select(i => i.Art))
                    {
                        job.Cts.Token.ThrowIfCancellationRequested();
                        job.Current = $"{art.Label} -> {scene.Name}";
                        var output = Path.Combine(paths.OutputDir, SyntheticName(scene, art, req.Loop));
                        if (!req.Overwrite && File.Exists(output))
                        {
                            Interlocked.Increment(ref job.Skipped);
                            Interlocked.Add(ref job.Done, frameCount * 2);
                            continue;
                        }
                        int baseDone = Volatile.Read(ref job.Done);
                        var tmpDir = Path.Combine(paths.CacheDir, "render", job.Id + "_" + MockupPlan.OutputStem(scene, art));
                        try
                        {
                            Directory.CreateDirectory(tmpDir);
                            using var composite = prepared.Render(library, art, requireSplit: true);
                            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = job.Cts.Token };
                            await Parallel.ForAsync(0, frameCount, po, (i, ct) =>
                            {
                                using var frame = cam.Render(composite, i / (double)(frameCount - 1));
                                Cv2.ImEncode(".png", frame, out var buf, [new ImageEncodingParam(ImwriteFlags.PngCompression, 1)]);
                                File.WriteAllBytes(Path.Combine(tmpDir, $"{i + 1:00000}.png"), buf);
                                Interlocked.Increment(ref job.Done);
                                return ValueTask.CompletedTask;
                            });

                            Directory.CreateDirectory(paths.OutputDir);
                            var tmpOut = output + ".tmp.mp4";
                            int outFrames = req.Loop ? frameCount * 2 : frameCount;
                            var (exit, err) = await FfmpegRunner.RunAsync(ffmpeg, BuildEncodeArgs(Path.Combine(tmpDir, "%05d.png"), fps, tmpOut, req.Loop), job.Cts.Token,
                                n => Volatile.Write(ref job.Done, baseDone + frameCount + (int)(frameCount * Math.Min(1.0, n / (double)outFrames))));
                            if (exit != 0)
                            {
                                try { File.Delete(tmpOut); } catch { }
                                throw new InvalidDataException("ffmpeg failed: " + FfmpegRunner.Brief(err));
                            }
                            File.Move(tmpOut, output, overwrite: true);
                            Interlocked.Increment(ref job.Rendered);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref job.Failed);
                            job.Errors.Enqueue(new BatchError(scene.Name, art.Label, ex.Message));
                        }
                        finally
                        {
                            Volatile.Write(ref job.Done, baseDone + frameCount * 2); // this video is finished (or failed): its whole share is counted
                            try { Directory.Delete(tmpDir, true); } catch { }
                        }
                    }
                }
                job.Status = "completed";
            }
            catch (OperationCanceledException) { job.Status = "cancelled"; }
            catch (Exception ex)
            {
                job.Errors.Enqueue(new BatchError("", "", ex.Message));
                job.Status = "completed";
            }
            finally { job.Current = ""; }
        });
        return job;
    }
}

/// <param name="Scene">Used by preview/info (one scene).</param>
/// <param name="Scenes">Used by generate (one or more scenes).</param>
/// <param name="Pairs">Used by two-frame scenes.</param>
public record SyntheticRequest(string? Scene, List<string>? Scenes, List<string>? Artworks, double? Seconds, int? Fps, string? Orientation, double? EndFill, bool? Ease, bool Loop, bool Overwrite, List<ArtPair>? Pairs = null);
