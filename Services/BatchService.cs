using System.Collections.Concurrent;

namespace ArtMockups;

/// <param name="Artworks">Used by one-frame scenes.</param>
/// <param name="Pairs">Used by two-frame scenes.</param>
public record BatchRequest(List<string> Scenes, List<string>? Artworks, bool Overwrite, string? Format, bool FullQuality = false, List<ArtPair>? Pairs = null, bool AllowAutomaticSplit = false, UpscaylOptions? Upscayl = null, int? Quality = null, List<string>? Originals = null);
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

public sealed class BatchService(AppPaths paths, SceneStore scenes, SceneImageCache sceneImages, ArtworkLibrary library, ConfigStore config, Upscayl? upscayl = null)
{
    readonly ConcurrentDictionary<string, BatchJob> _jobs = new();

    public BatchJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public BatchJob? Running => _jobs.Values.FirstOrDefault(j => j.Status == "running");

    public BatchJob Start(BatchRequest req)
    {
        if (Running != null) throw new InvalidOperationException("Já existe um lote em andamento.");
        var originals = (req.Originals ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (req.Scenes is not { Count: > 0 } && originals.Count == 0)
            throw new ArgumentException("Selecione cenários ou artes originais para exportar.");
        var ext = req.Format?.ToLowerInvariant() switch { "webp" => "webp", "png" => "png", _ => "jpg" };
        if (req.Quality is < 1 or > 100) throw new ArgumentException("A qualidade deve estar entre 1 e 100.");

        var sceneList = (req.Scenes ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select(scenes.Get).ToList();
        var items = MockupPlan.Build(sceneList, req.Artworks, req.Pairs);
        if (!req.AllowAutomaticSplit) MockupPlan.RequireSplits(library, items);

        var enhancer = upscayl ?? new Upscayl(paths);
        if (items.Count > 0) enhancer.Validate(req.Upscayl);
        var outputs = items.Select(item => (Scene: (Scene?)item.Scene, item.Art))
            .Concat(originals.Select(file => (Scene: (Scene?)null, Art: new ArtPair(file))));

        var job = new BatchJob { Total = items.Count + originals.Count };
        _jobs[job.Id] = job;
        // Full quality: no size cap, JPEG at 100 with 4:4:4 colour, PNG is lossless anyway.
        var quality = req.FullQuality ? 100 : req.Quality ?? config.Current.JpegQuality;
        var maxBytes = req.FullQuality ? 0 : config.Current.MaxOutputBytes;

        _ = Task.Run(async () =>
        {
            var prepared = sceneList.ToDictionary(s => s.Name, s => new Lazy<PreparedMockup>(
                () => PreparedMockup.Create(sceneImages.Get(s), s), LazyThreadSafetyMode.ExecutionAndPublication));
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = req.Upscayl == null ? Math.Max(1, Environment.ProcessorCount - 1) : 1,
                CancellationToken = job.Cts.Token,
            };
            try
            {
                await Parallel.ForEachAsync(outputs, opts, async (item, ct) =>
                {
                    var (scene, art) = item;
                    job.Current = scene == null ? $"{art.First} · original" : $"{art.Label} → {scene.Name}";
                    try
                    {
                        var name = scene == null
                            ? Path.GetFileNameWithoutExtension(art.First) + "_original" + Path.GetExtension(art.First)
                            : $"{MockupPlan.OutputStem(scene, art)}.{ext}";
                        var outPath = Path.Combine(paths.OutputDir, name);
                        if (!req.Overwrite && File.Exists(outPath))
                        {
                            Interlocked.Increment(ref job.Skipped);
                            return;
                        }

                        if (scene == null)
                        {
                            if (!ArtworkLibrary.IsImage(art.First))
                                throw new ArgumentException("O arquivo original precisa ser uma imagem.");
                            await CopyOriginalAsync(library.Resolve(art.First), outPath, req.Overwrite, ct);
                            Interlocked.Increment(ref job.Rendered);
                            return;
                        }

                        var compositor = prepared[scene.Name].Value;
                        using var result = req.Upscayl is { } options
                            ? await enhancer.RenderAsync(compositor, library, art, !req.AllowAutomaticSplit, options, ct,
                                () => job.Current = $"Upscayl · {art.Label}")
                            : compositor.Render(library, art, requireSplit: !req.AllowAutomaticSplit);
                        ct.ThrowIfCancellationRequested();
                        job.Current = $"{art.Label} → {scene.Name}";
                        ImageIO.Save(outPath, result, quality, maxBytes);
                        Interlocked.Increment(ref job.Rendered);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref job.Failed);
                        job.Errors.Enqueue(new BatchError(scene?.Name ?? "Original", art.Label, ex.Message));
                    }
                    finally
                    {
                        Interlocked.Increment(ref job.Done);
                    }
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

    static async Task CopyOriginalAsync(string sourcePath, string targetPath, bool overwrite, CancellationToken ct)
    {
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(destination, ct);

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, targetPath, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
