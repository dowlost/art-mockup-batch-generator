using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace ArtMockups;

public sealed class AppPaths
{
    public string Root { get; }
    public string DataDir { get; }
    public string ScenesDir => Path.Combine(DataDir, "scenes");
    public string OutputDir => Path.Combine(DataDir, "output");
    public string CacheDir => Path.Combine(DataDir, ".cache");
    public string ScenesJson => Path.Combine(ScenesDir, "scenes.json");
    public string ConfigJson => Path.Combine(DataDir, "config.json");

    public AppPaths(string root)
    {
        Root = root;
        DataDir = Environment.GetEnvironmentVariable("ARTMOCKUPS_DATA") is { Length: > 0 } d
            ? Path.GetFullPath(d) : Path.Combine(root, "data");
        Directory.CreateDirectory(ScenesDir);
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "artworks"));
    }

    public string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(Root, p));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed class ConfigStore(AppPaths paths)
{
    readonly object _lock = new();
    AppConfig? _cur;

    public AppConfig Current
    {
        get
        {
            lock (_lock)
            {
                if (_cur != null) return _cur;
                try
                {
                    if (File.Exists(paths.ConfigJson))
                        _cur = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(paths.ConfigJson), Json.Options);
                }
                catch (JsonException) { /* fall back to defaults */ }
                return _cur ??= new AppConfig();
            }
        }
    }

    public void Save(AppConfig cfg)
    {
        lock (_lock)
        {
            File.WriteAllText(paths.ConfigJson, JsonSerializer.Serialize(cfg, Json.Options));
            _cur = cfg;
        }
    }
}

public sealed class SceneStore(AppPaths paths)
{
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
    readonly object _lock = new();

    record Doc(List<Scene> Scenes);

    List<Scene> ReadUnlocked()
    {
        var list = new List<Scene>();
        if (File.Exists(paths.ScenesJson))
        {
            try { list = JsonSerializer.Deserialize<Doc>(File.ReadAllText(paths.ScenesJson), Json.Options)?.Scenes ?? new(); }
            catch (JsonException ex) { throw new InvalidDataException($"scenes.json is invalid: {ex.Message}"); }
        }
        // Pick up images dropped into data/scenes/ by hand.
        bool changed = false;
        foreach (var f in Directory.EnumerateFiles(paths.ScenesDir).Where(f => ImageExt.Contains(Path.GetExtension(f))))
        {
            var file = Path.GetFileName(f);
            if (list.Any(s => s.Image.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            var stem = Path.GetFileNameWithoutExtension(f);
            try { stem = Names.ValidateSceneName(stem); } catch { continue; }
            if (list.Any(s => s.Name.Equals(stem, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new Scene { Name = stem, Image = file });
            changed = true;
        }
        if (changed) WriteUnlocked(list);
        return list;
    }

    void WriteUnlocked(List<Scene> list)
    {
        var tmp = paths.ScenesJson + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Doc(list), Json.Options));
        File.Move(tmp, paths.ScenesJson, overwrite: true);
    }

    public List<Scene> List() { lock (_lock) return ReadUnlocked(); }

    public Scene Get(string name)
    {
        lock (_lock)
            return ReadUnlocked().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new FileNotFoundException($"Scene '{name}' not found.");
    }

    public string ImagePath(Scene s) => Path.Combine(paths.ScenesDir, s.Image);

    public Scene Add(string name, string originalFileName, Stream content)
    {
        name = Names.ValidateSceneName(name);
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!ImageExt.Contains(ext)) throw new ArgumentException("Scene image must be PNG, JPG or WebP.");
        lock (_lock)
        {
            var list = ReadUnlocked();
            if (list.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"A scene named '{name}' already exists.");
            var file = name + ext;
            using (var fs = File.Create(Path.Combine(paths.ScenesDir, file))) content.CopyTo(fs);
            var scene = new Scene { Name = name, Image = file };
            list.Add(scene);
            WriteUnlocked(list);
            return scene;
        }
    }

    public Scene Update(string name, SceneUpdate u)
    {
        lock (_lock)
        {
            var list = ReadUnlocked();
            var scene = list.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException($"Scene '{name}' not found.");
            if (u.Corners != null)
            {
                if (u.Corners.Count != 4 && u.Corners.Count != 0) throw new ArgumentException("A scene needs exactly 4 corners.");
                if (u.Corners.Count == 4)
                {
                    using var img = ImageIO.Load(ImagePath(scene));
                    PreparedScene.ValidateCorners(u.Corners, img.Width, img.Height);
                }
                scene.Corners = u.Corners;
            }
            if (u.Settings != null) scene.Settings = u.Settings.Normalized();
            if (u.ClearExpectedAspectRatio == true) scene.ExpectedAspectRatio = null;
            else if (u.ExpectedAspectRatio is > 0) scene.ExpectedAspectRatio = u.ExpectedAspectRatio;
            if (u.Name != null && !u.Name.Equals(scene.Name, StringComparison.Ordinal))
            {
                var newName = Names.ValidateSceneName(u.Name);
                if (list.Any(s => s != scene && s.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"A scene named '{newName}' already exists.");
                scene.Name = newName;
            }
            WriteUnlocked(list);
            return scene;
        }
    }

    public void Delete(string name)
    {
        lock (_lock)
        {
            var list = ReadUnlocked();
            var scene = list.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException($"Scene '{name}' not found.");
            list.Remove(scene);
            WriteUnlocked(list);
            var img = ImagePath(scene);
            if (File.Exists(img)) File.Delete(img);
        }
    }
}

/// <summary>Decoded scene photos, cached until the file changes. Mats are treated as read-only.</summary>
public sealed class SceneImageCache(SceneStore scenes)
{
    readonly ConcurrentDictionary<string, (DateTime Stamp, long Len, Lazy<Mat> Mat)> _cache = new();

    public Mat Get(Scene scene)
    {
        var path = scenes.ImagePath(scene);
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException($"Scene image '{scene.Image}' is missing.");
        var entry = _cache.AddOrUpdate(path,
            _ => (fi.LastWriteTimeUtc, fi.Length, new Lazy<Mat>(() => ImageIO.Load(path))),
            (_, old) => old.Stamp == fi.LastWriteTimeUtc && old.Len == fi.Length
                ? old : (fi.LastWriteTimeUtc, fi.Length, new Lazy<Mat>(() => ImageIO.Load(path))));
        return entry.Mat.Value;
    }
}

public record ArtworkInfo(string File, string Stem, int Width, int Height, long Bytes, string? Error);

public sealed class ArtworkLibrary(AppPaths paths, ConfigStore config)
{
    static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
    readonly ConcurrentDictionary<string, ArtworkInfo> _info = new();

    public string Folder => string.IsNullOrWhiteSpace(config.Current.ArtworkFolder) ? Path.Combine(paths.DataDir, "artworks") : paths.Resolve(config.Current.ArtworkFolder);

    public IEnumerable<string> Files() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder).Where(f => Ext.Contains(Path.GetExtension(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    public List<ArtworkInfo> List()
    {
        if (!Directory.Exists(Folder)) throw new DirectoryNotFoundException($"Artwork folder not found: {Folder}");
        var files = Files().ToList();
        var result = new ArtworkInfo[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            i => result[i] = GetInfo(files[i]));
        return result.ToList();
    }

    /// <summary>Resolves an artwork file name to a full path inside the artwork folder (no path traversal).</summary>
    public string Resolve(string file)
    {
        if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file)
            throw new ArgumentException($"Invalid artwork name '{file}'.");
        var full = Path.Combine(Folder, file);
        if (!File.Exists(full)) throw new FileNotFoundException($"Artwork '{file}' not found.");
        return full;
    }

    public string ThumbPath(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{fullPath}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}")));
        return Path.Combine(paths.CacheDir, "thumbs", key + ".jpg");
    }

    ArtworkInfo GetInfo(string full)
    {
        var fi = new FileInfo(full);
        var key = $"{full}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        return _info.GetOrAdd(key, _ =>
        {
            try
            {
                using var img = ImageIO.Load(full);
                var thumb = ThumbPath(full);
                if (!File.Exists(thumb))
                {
                    using var small = ImageIO.ResizeToMaxSide(img, 360);
                    ImageIO.Save(thumb, small, 80);
                }
                return new ArtworkInfo(fi.Name, Path.GetFileNameWithoutExtension(fi.Name), img.Width, img.Height, fi.Length, null);
            }
            catch (Exception ex)
            {
                return new ArtworkInfo(fi.Name, Path.GetFileNameWithoutExtension(fi.Name), 0, 0, fi.Length, ex.Message);
            }
        });
    }

    public string? EnsureThumb(string file)
    {
        var full = Resolve(file);
        var info = GetInfo(full);
        return info.Error == null ? ThumbPath(full) : null;
    }
}
