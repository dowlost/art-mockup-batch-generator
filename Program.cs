using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using ArtMockups;
using Microsoft.AspNetCore.Http.Json;
using OpenCvSharp;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8000");
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddSingleton(new AppPaths(Directory.GetCurrentDirectory()));
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<SceneStore>();
builder.Services.AddSingleton<SceneImageCache>();
builder.Services.AddSingleton<ArtworkLibrary>();
builder.Services.AddSingleton<BatchService>();
builder.Services.AddSingleton<ZoomVideoService>();
builder.Services.AddSingleton<Upscayl>();
builder.Services.AddSingleton<SplitService>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 201_000_000);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 200_000_000);

var app = builder.Build();

app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => 404,
        ArgumentException or InvalidDataException or JsonException or InvalidOperationException => 400,
        _ => 500,
    };
    await ctx.Response.WriteAsJsonAsync(new { error = ex?.Message ?? "Erro inesperado." });
}));
app.UseDefaultFiles();
// Always revalidate the UI files so a rebuilt app.js/index.html is never served stale from the browser cache.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

var api = app.MapGroup("/api");
api.MapGet("/upscayl", (Upscayl upscayl) => upscayl.Capabilities());

// ---------- config ----------
api.MapGet("/config", (ConfigStore cfg, ArtworkLibrary lib, AppPaths paths) => new
{
    artworkFolder = lib.Folder,
    outputFolder = paths.OutputDir,
    supportsVideoResume = true,
    supportsUpscayl = true,
});

api.MapPut("/config", (ConfigPatch p, ConfigStore cfg, ArtworkLibrary lib, AppPaths paths) =>
{
    var c = cfg.Current;
    if (p.ArtworkFolder != null)
    {
        if (!Directory.Exists(paths.Resolve(p.ArtworkFolder))) throw new DirectoryNotFoundException($"Pasta não encontrada — {p.ArtworkFolder}");
        c.ArtworkFolder = p.ArtworkFolder;
    }
    cfg.Save(c);
    return Results.Ok(new { artworkFolder = lib.Folder });
});

// ---------- scenes ----------
api.MapGet("/scenes", (SceneStore s) => s.List());

api.MapPost("/scenes", async (HttpRequest req, SceneStore s) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? throw new ArgumentException("Selecione a imagem do cenário.");
    var name = form["name"].ToString();
    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(file.FileName);
    await using var stream = file.OpenReadStream();
    return s.Add(name, file.FileName, stream);
});

api.MapPut("/scenes/{name}", (string name, SceneUpdate u, SceneStore s) => s.Update(name, u));
api.MapDelete("/scenes/{name}", (string name, SceneStore s) => { s.Delete(name); return Results.NoContent(); });

api.MapGet("/scenes/{name}/image", (string name, SceneStore s) =>
{
    var scene = s.Get(name);
    var path = s.ImagePath(scene);
    if (!File.Exists(path)) throw new FileNotFoundException("A imagem do cenário está ausente.");
    var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
    return Results.File(path, mime, lastModified: File.GetLastWriteTimeUtc(path));
});

// ---------- artworks ----------
api.MapGet("/artworks", (ArtworkLibrary lib) => lib.List());
api.MapPost("/artworks/import", async (HttpRequest req, ArtworkLibrary lib, CancellationToken cancellation) =>
{
    var form = await req.ReadFormAsync(cancellation);
    var file = form.Files.GetFile("file") ?? throw new ArgumentException("Selecione uma imagem.");
    await using var stream = file.OpenReadStream();
    var name = await lib.ImportAsync(file.FileName, stream, cancellation);
    return Results.Ok(new { file = name });
});

// renames artworks that are not named 0000 to the next free four-digit numbers (dryRun=true only reports the plan)
api.MapPost("/artworks/normalize", (bool? dryRun, ArtworkLibrary lib) =>
{
    var items = lib.NormalizeNames(dryRun ?? false);
    return new { dryRun = dryRun ?? false, count = items.Count, items };
});

api.MapGet("/artworks/{file}/thumb", (string file, ArtworkLibrary lib) =>
{
    var thumb = lib.EnsureThumb(file);
    return thumb == null ? Results.NotFound() : Results.File(thumb, "image/jpeg");
});

// ---------- preview ----------
api.MapPost("/preview", (PreviewRequest r, SceneStore scenes, SceneImageCache cache, ArtworkLibrary lib, ConfigStore cfg) =>
{
    // the scene as currently edited in the UI (not necessarily saved yet)
    var scene = scenes.Get(r.Scene);
    scene.Corners = r.Corners ?? scene.Corners;
    scene.Corners2 = r.Corners2 ?? scene.Corners2;
    scene.FrameCount = r.FrameCount ?? scene.FrameCount;
    scene.Settings = r.Settings ?? scene.Settings;
    using var prepared = PreparedMockup.Create(cache.Get(scene), scene);
    using var result = prepared.Render(lib, new ArtPair(r.Artwork, r.Artwork2));
    using var shown = ImageIO.ResizeToMaxSide(result, r.MaxSide ?? 1600);
    return Results.File(ImageIO.Encode(shown, ".jpg", 90), "image/jpeg");
});

api.MapPost("/contact-sheet", (SheetRequest r, SceneStore scenes, SceneImageCache cache, ArtworkLibrary lib) =>
{
    var scene = scenes.Get(r.Scene);
    var names = r.Artworks.Take(6).ToList();
    if (names.Count == 0) throw new ArgumentException("Selecione pelo menos uma arte.");
    scene.Corners = r.Corners ?? scene.Corners;
    scene.Corners2 = r.Corners2 ?? scene.Corners2;
    scene.FrameCount = r.FrameCount ?? scene.FrameCount;
    scene.Settings = r.Settings ?? scene.Settings;
    using var prepared = PreparedMockup.Create(cache.Get(scene), scene);
    var cells = new List<Mat>();
    try
    {
        foreach (var n in names)
        {
            using var full = prepared.Render(lib, new ArtPair(n)); // two-frame scenes show each artwork split
            var cell = ImageIO.ResizeToMaxSide(full, 640);
            cells.Add(cell);
        }
        int cols = Math.Min(3, cells.Count), rows = (int)Math.Ceiling(cells.Count / (double)cols);
        var size = cells[0].Size();
        var rowMats = new List<Mat>();
        for (int y = 0; y < rows; y++)
        {
            var rowCells = new List<Mat>();
            for (int x = 0; x < cols; x++)
            {
                int i = y * cols + x;
                rowCells.Add(i < cells.Count ? cells[i] : new Mat(size.Height, size.Width, MatType.CV_8UC3, new Scalar(40, 40, 40)));
            }
            var row = new Mat();
            Cv2.HConcat(rowCells.ToArray(), row);
            rowMats.Add(row);
        }
        using var sheet = new Mat();
        Cv2.VConcat(rowMats.ToArray(), sheet);
        foreach (var m in rowMats) m.Dispose();
        return Results.File(ImageIO.Encode(sheet, ".jpg", 88), "image/jpeg");
    }
    finally { foreach (var c in cells) c.Dispose(); }
});

// ---------- batch ----------
api.MapPost("/batch", (BatchRequest r, BatchService b) => Results.Ok(b.Start(r).Snapshot()));
api.MapGet("/batch/{id}", (string id, BatchService b) => b.Get(id) is { } j ? Results.Ok(j.Snapshot()) : Results.NotFound());
api.MapGet("/batch", (BatchService b) => b.Running is { } j ? Results.Ok(j.Snapshot()) : Results.NoContent());
api.MapPost("/batch/{id}/cancel", (string id, BatchService b) =>
{
    b.Get(id)?.Cts.Cancel();
    return Results.Accepted();
});

// ---------- output ----------
static void OpenFolder(string dir)
{
    Directory.CreateDirectory(dir);
    var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new ProcessStartInfo(dir) { UseShellExecute = true }
        : new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open", $"\"{dir}\"");
    Process.Start(psi);
}

api.MapPost("/output/open", (AppPaths paths) => { OpenFolder(paths.OutputDir); return Results.Ok(); });

// ---------- print files, and the plain split used by two-frame scenes ----------
api.MapPost("/split/package", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync(req.HttpContext.RequestAborted);
    var layout = DiptychSplit.ParseLayout(form["layout"]);
    if (layout == SplitLayout.Full) throw new ArgumentException("Escolha Duplo-vertical ou Duplo-horizontal.");

    var files = form.Files.GetFiles("first");
    if (files.Count == 0) throw new ArgumentException("Selecione pelo menos uma imagem.");

    var offset = double.TryParse(form["offset"], System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0.5;
    var stems = form["name"].Select(name => Names.ValidateSceneName(name)).ToArray();
    if (stems.Length != files.Count || stems.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
        throw new ArgumentException("Defina um nome diferente para cada arte.");
    var suffixes = layout == SplitLayout.Side ? new[] { "esquerda", "direita" } : new[] { "cima", "baixo" };
    var part = form["part"].ToString();
    if (part is not ("" or "first" or "second") || (part.Length > 0 && files.Count != 1))
        throw new ArgumentException("Seleção de partes inválida.");
    var ct = req.HttpContext.RequestAborted;

    async Task<Mat[]> Cut(IFormFile file)
    {
        if (!ArtworkLibrary.IsImage(file.FileName) || file.Length == 0)
            throw new ArgumentException($"Selecione um PNG, JPEG ou WebP válido: {Path.GetFileName(file.FileName)}.");
        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes, ct);
        using var art = ImageIO.Decode(bytes.ToArray(), file.FileName, ImreadModes.Color | ImreadModes.AnyDepth);
        return DiptychSplit.Pieces(art, DiptychSplit.Plan(art.Width, art.Height, layout, offset));
    }

    string Name(int file, int piece) => $"{stems[file]}-{suffixes[piece]}.png";
    if (part.Length > 0)
    {
        var pieces = await Cut(files[0]);
        try
        {
            int index = part == "first" ? 0 : 1;
            return Results.File(ImageIO.Encode(pieces[index], "png", 100), "image/png", Name(0, index));
        }
        finally { foreach (var piece in pieces) piece.Dispose(); }
    }

    var zipStream = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    try
    {
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int file = 0; file < files.Count; file++)
            {
                ct.ThrowIfCancellationRequested();
                var pieces = await Cut(files[file]);
                try
                {
                    for (int i = 0; i < pieces.Length; i++)
                    {
                        var entry = zip.CreateEntry(Name(file, i), CompressionLevel.NoCompression);
                        using var entryStream = entry.Open();
                        entryStream.Write(ImageIO.Encode(pieces[i], "png", 100));
                    }
                }
                finally { foreach (var piece in pieces) piece.Dispose(); }
            }
        }
        zipStream.Position = 0;
        return Results.File(zipStream, "application/zip", files.Count == 1 ? $"{stems[0]}.zip" : "artes-cortadas.zip");
    }
    catch { zipStream.Dispose(); throw; }
});

api.MapPost("/split/cut", (SplitRequest r, SplitService s) => s.Cut(r));
api.MapGet("/split/cut-preview", (string artwork, string layout, double? offset, SplitService s) =>
    Results.File(s.CutPreview(artwork, layout, offset ?? 0.5), "image/jpeg"));
api.MapPost("/split/info", (SplitRequest r, SplitService s) => s.Info(r));
api.MapPost("/split/preview", (SplitRequest r, SplitService s) => Results.File(s.Preview(r), "image/jpeg"));
api.MapPost("/split/export", (SplitRequest r, SplitService s) => s.Export(r));
api.MapPost("/split/open", (ArtworkLibrary lib) => { OpenFolder(lib.Folder); return Results.Ok(); });

// ---------- zoom videos (virtual camera over the composited scene) ----------
api.MapPost("/videos/synthetic", (SyntheticRequest req, ZoomVideoService z) => Results.Ok(z.StartSynthetic(req).Snapshot()));
api.MapPost("/videos/synthetic/info", (SyntheticRequest req, ZoomVideoService z) => Results.Ok(z.SyntheticInfo(req)));
api.MapPost("/videos/synthetic/preview", async (SyntheticPreview pv, ZoomVideoService z, CancellationToken ct) =>
    Results.File(await z.PreviewSyntheticAsync(pv.Options, new ArtPair(pv.Artwork, pv.Artwork2), pv.T, ct), "image/jpeg"));
api.MapGet("/videos/jobs", (ZoomVideoService z) => z.Running is { } j ? Results.Ok(j.Snapshot()) : Results.NoContent());
api.MapGet("/videos/jobs/{id}", (string id, ZoomVideoService z) => z.Get(id) is { } j ? Results.Ok(j.Snapshot()) : Results.NotFound());
api.MapPost("/videos/jobs/{id}/cancel", (string id, ZoomVideoService z) => { z.Get(id)?.Cts.Cancel(); return Results.Accepted(); });

api.MapGet("/output/zip", (AppPaths paths) =>
{
    var tmp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    using (var zip = new ZipArchive(tmp, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var f in Directory.EnumerateFiles(paths.OutputDir, "*", SearchOption.AllDirectories)
                     .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            zip.CreateEntryFromFile(f, Path.GetRelativePath(paths.OutputDir, f).Replace('\\', '/'), CompressionLevel.NoCompression);
    }
    tmp.Position = 0;
    return Results.File(tmp, "application/zip", "mockups.zip");
});

app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine("Art Mockup Batch Generator running at " + string.Join(", ", app.Urls)));
app.Run();

record ConfigPatch(string? ArtworkFolder);
record PreviewRequest(string Scene, string Artwork, List<PointD>? Corners, SceneSettings? Settings, int? MaxSide,
    string? Artwork2 = null, List<PointD>? Corners2 = null, int? FrameCount = null);
record SheetRequest(string Scene, List<string> Artworks, List<PointD>? Corners, SceneSettings? Settings,
    List<PointD>? Corners2 = null, int? FrameCount = null);
record SyntheticPreview(SyntheticRequest Options, string Artwork, double T, string? Artwork2 = null);

public partial class Program { }
