using System.Collections.Concurrent;

namespace ArtMockups;

/// <param name="Artworks">Used by one-frame scenes.</param>
/// <param name="Pairs">Used by two-frame scenes.</param>
public record BatchRequest(List<string> Scenes, List<string>? Artworks, bool Overwrite, string? Format, bool FullQuality = false, List<ArtPair>? Pairs = null);
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
        if (req.Scenes is not { Count: > 0 }) throw new ArgumentException("Select at least one scene.");
        var ext = (req.Format ?? "jpg").ToLowerInvariant() == "png" ? "png" : "jpg";

        var sceneList = req.Scenes.Distinct(StringComparer.OrdinalIgnoreCase).Select(scenes.Get).ToList();
        var items = MockupPlan.Build(sceneList, req.Artworks, req.Pairs);
        MockupPlan.RequireSplits(library, items);

        var job = new BatchJob { Total = items.Count };
        _jobs[job.Id] = job;
        // Full quality: no size cap, JPEG at 100 with 4:4:4 colour, PNG is lossless anyway.
        var quality = req.FullQuality ? 100 : config.Current.JpegQuality;
        var maxBytes = req.FullQuality ? 0 : config.Current.MaxOutputBytes;

        _ = Task.Run(async () =>
        {
            var prepared = sceneList.ToDictionary(s => s.Name, s => new Lazy<PreparedMockup>(
                () => PreparedMockup.Create(sceneImages.Get(s), s), LazyThreadSafetyMode.ExecutionAndPublication));
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                CancellationToken = job.Cts.Token,
            };
            try
            {
                await Parallel.ForEachAsync(items, opts, (item, ct) =>
                {
                    var (scene, art) = item;
                    job.Current = $"{art.Label} → {scene.Name}";
                    try
                    {
                        // one flat output folder: 0001__<scene>.jpg
                        var outPath = Path.Combine(paths.OutputDir, $"{MockupPlan.OutputStem(scene, art)}.{ext}");
                        if (!req.Overwrite && File.Exists(outPath))
                        {
                            Interlocked.Increment(ref job.Skipped);
                            return ValueTask.CompletedTask;
                        }
                        using var result = prepared[scene.Name].Value.Render(library, art, requireSplit: true);
                        ImageIO.Save(outPath, result, quality, maxBytes);
                        Interlocked.Increment(ref job.Rendered);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref job.Failed);
                        job.Errors.Enqueue(new BatchError(scene.Name, art.Label, ex.Message));
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
