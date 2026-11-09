using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using ArtMockups;
using Microsoft.AspNetCore.Http.Json;
using OpenCvSharp;

LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8000");
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddSingleton(new AppPaths(Directory.GetCurrentDirectory()));
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<SceneStore>();
builder.Services.AddSingleton<SceneImageCache>();
builder.Services.AddSingleton<ArtworkLibrary>();
builder.Services.AddSingleton<BatchService>();
builder.Services.AddSingleton(sp => new AiService(sp.GetRequiredService<ConfigStore>(), new HttpClient { Timeout = TimeSpan.FromSeconds(90) }));
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
    await ctx.Response.WriteAsJsonAsync(new { error = ex?.Message ?? "Unexpected error." });
}));
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------- config ----------
api.MapGet("/config", (ConfigStore cfg, AiService ai, ArtworkLibrary lib, AppPaths paths) => new
{
    aiEnabled = ai.Enabled,
    aiModel = cfg.Current.AiModel,
    artworkFolder = lib.Folder,
    outputFolder = paths.OutputDir,
    captionPrompt = cfg.Current.CaptionPrompt,
});

api.MapPut("/config", (ConfigPatch p, ConfigStore cfg, ArtworkLibrary lib, AppPaths paths) =>
{
    var c = cfg.Current;
    if (p.ArtworkFolder != null)
    {
        if (!Directory.Exists(paths.Resolve(p.ArtworkFolder))) throw new DirectoryNotFoundException($"Folder not found: {p.ArtworkFolder}");
        c.ArtworkFolder = p.ArtworkFolder;
    }
    if (p.CaptionPrompt != null) c.CaptionPrompt = p.CaptionPrompt;
    cfg.Save(c);
    return Results.Ok(new { artworkFolder = lib.Folder });
});

// ---------- scenes ----------
api.MapGet("/scenes", (SceneStore s) => s.List());

api.MapPost("/scenes", async (HttpRequest req, SceneStore s) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? throw new ArgumentException("Missing scene image file.");
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
    if (!File.Exists(path)) throw new FileNotFoundException("Scene image is missing.");
    var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
    return Results.File(path, mime, lastModified: File.GetLastWriteTimeUtc(path));
});

// ---------- artworks ----------
api.MapGet("/artworks", (ArtworkLibrary lib) => lib.List());

api.MapGet("/artworks/{file}/thumb", (string file, ArtworkLibrary lib) =>
{
    var thumb = lib.EnsureThumb(file);
    return thumb == null ? Results.NotFound() : Results.File(thumb, "image/jpeg");
});

// ---------- preview ----------
api.MapPost("/preview", (PreviewRequest r, SceneStore scenes, SceneImageCache cache, ArtworkLibrary lib, ConfigStore cfg) =>
{
    var scene = scenes.Get(r.Scene);
    var corners = r.Corners ?? scene.Corners;
    var settings = r.Settings ?? scene.Settings;
    using var prepared = PreparedScene.Create(cache.Get(scene), corners, settings);
    using var art = ImageIO.Load(lib.Resolve(r.Artwork));
    using var result = prepared.Render(art);
    using var shown = ImageIO.ResizeToMaxSide(result, r.MaxSide ?? 1600);
    return Results.File(ImageIO.Encode(shown, ".jpg", 90), "image/jpeg");
});

api.MapPost("/contact-sheet", (SheetRequest r, SceneStore scenes, SceneImageCache cache, ArtworkLibrary lib) =>
{
    var scene = scenes.Get(r.Scene);
    var names = r.Artworks.Take(6).ToList();
    if (names.Count == 0) throw new ArgumentException("Select at least one artwork.");
    using var prepared = PreparedScene.Create(cache.Get(scene), r.Corners ?? scene.Corners, r.Settings ?? scene.Settings);
    var cells = new List<Mat>();
    try
    {
        foreach (var n in names)
        {
            using var art = ImageIO.Load(lib.Resolve(n));
            using var full = prepared.Render(art);
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
api.MapPost("/output/open", (AppPaths paths) =>
{
    Directory.CreateDirectory(paths.OutputDir);
    var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new ProcessStartInfo(paths.OutputDir) { UseShellExecute = true }
        : new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open", $"\"{paths.OutputDir}\"");
    Process.Start(psi);
    return Results.Ok();
});

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

// ---------- AI (optional) ----------
api.MapPost("/ai/detect-corners", async (DetectRequest r, AiService ai, SceneStore scenes, SceneImageCache cache, CancellationToken ct) =>
{
    var scene = scenes.Get(r.Scene);
    return new { corners = await ai.DetectCornersAsync(cache.Get(scene), ct) };
});

api.MapPost("/ai/captions", async (CaptionRequest r, AiService ai, ArtworkLibrary lib, AppPaths paths, CancellationToken ct) =>
{
    if (!ai.Enabled) throw new ArgumentException("AI features are disabled: set ANTHROPIC_API_KEY in the .env file and restart.");
    var rows = new List<AiService.Caption>();
    var errors = new List<BatchError>();
    foreach (var file in r.Artworks.Distinct())
    {
        try
        {
            using var art = ImageIO.Load(lib.Resolve(file));
            rows.Add(await ai.CaptionAsync(file, art, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(new BatchError("", file, ex.Message));
        }
    }
    string? written = null;
    if (rows.Count > 0)
    {
        written = Path.Combine(paths.OutputDir, "captions.csv");
        await File.WriteAllTextAsync(written, AiService.ToCsv(rows), ct);
    }
    return new { count = rows.Count, file = written, errors };
});

app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine("Art Mockup Batch Generator running at " + string.Join(", ", app.Urls)));
app.Run();

static void LoadDotEnv(string path)
{
    if (!File.Exists(path)) return;
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var i = line.IndexOf('=');
        if (i <= 0) continue;
        var key = line[..i].Trim();
        var val = line[(i + 1)..].Trim().Trim('"', '\'');
        if (Environment.GetEnvironmentVariable(key) == null) Environment.SetEnvironmentVariable(key, val);
    }
}

record ConfigPatch(string? ArtworkFolder, string? CaptionPrompt);
record PreviewRequest(string Scene, string Artwork, List<PointD>? Corners, SceneSettings? Settings, int? MaxSide);
record SheetRequest(string Scene, List<string> Artworks, List<PointD>? Corners, SceneSettings? Settings);
record DetectRequest(string Scene);
record CaptionRequest(List<string> Artworks);

public partial class Program { }
