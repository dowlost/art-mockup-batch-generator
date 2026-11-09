using System.Collections.Concurrent;

namespace ArtMockups;

public record BatchRequest(List<string> Scenes, List<string> Artworks, bool Overwrite, string? Format);
public record BatchError(string Scene, string Artwork, string Message);

public sealed class BatchJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public int Total { get; init; }
    public int Done;      // rendered + skipped + failed
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

public sealed class BatchService(AppPaths paths, SceneStore scenes, SceneImageCache sceneImages, ArtworkLibrary library, ConfigStore config)
{
    readonly ConcurrentDictionary<string, BatchJob> _jobs = new();

    public BatchJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public BatchJob? Running => _jobs.Values.FirstOrDefault(j => j.Status == "running");

    public BatchJob Start(BatchRequest req)
    {
        if (Running != null) throw new InvalidOperationException("A batch is already running.");
        if (req.Scenes.Count == 0 || req.Artworks.Count == 0) throw new ArgumentException("Select at least one scene and one artwork.");
        var ext = (req.Format ?? "jpg").ToLowerInvariant() == "png" ? "png" : "jpg";

        var sceneList = req.Scenes.Select(scenes.Get).ToList();
        foreach (var s in sceneList)
            if (s.Corners.Count != 4) throw new ArgumentException($"Scene '{s.Name}' has no corners set.");
        var artworks = req.Artworks.Distinct().ToList();

        var job = new BatchJob { Total = sceneList.Count * artworks.Count };
        _jobs[job.Id] = job;
        var quality = config.Current.JpegQuality;
        var maxBytes = config.Current.MaxOutputBytes;

        _ = Task.Run(async () =>
        {
            var prepared = sceneList.ToDictionary(s => s.Name, s => new Lazy<PreparedScene>(
                () => PreparedScene.Create(sceneImages.Get(s), s.Corners, s.Settings), LazyThreadSafetyMode.ExecutionAndPublication));
            var pairs = sceneList.SelectMany(s => artworks.Select(a => (Scene: s, Art: a))).ToList();
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                CancellationToken = job.Cts.Token,
            };
            try
            {
                await Parallel.ForEachAsync(pairs, opts, (pair, ct) =>
                {
                    var (scene, art) = pair;
                    job.Current = $"{art} → {scene.Name}";
                    try
                    {
                        var stem = Names.SafeFileStem(Path.GetFileNameWithoutExtension(art));
                        var outPath = Path.Combine(paths.OutputDir, scene.Name, $"{stem}__{scene.Name}.{ext}");
                        if (!req.Overwrite && File.Exists(outPath))
                        {
                            Interlocked.Increment(ref job.Skipped);
                            return ValueTask.CompletedTask;
                        }
                        using var artMat = ImageIO.Load(library.Resolve(art));
                        using var result = prepared[scene.Name].Value.Render(artMat);
                        ImageIO.Save(outPath, result, quality, maxBytes);
                        Interlocked.Increment(ref job.Rendered);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref job.Failed);
                        job.Errors.Enqueue(new BatchError(scene.Name, art, ex.Message));
                    }
                    finally
                    {
                        Interlocked.Increment(ref job.Done);
                    }
                    return ValueTask.CompletedTask;
                });
                job.Status = "completed";
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
            }
            finally
            {
                job.Current = "";
                foreach (var p in prepared.Values) if (p.IsValueCreated) p.Value.Dispose();
            }
        });
        return job;
    }
}
