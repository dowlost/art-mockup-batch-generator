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
    /// <summary>Highest artwork number used so far. Tracked in git, unlike the artworks themselves.</summary>
    public string CounterJson => Path.Combine(DataDir, "artworks-counter.json");

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
            if (u.FrameCount is { } frames)
            {
                if (frames is not (1 or 2)) throw new ArgumentException("A scene has 1 or 2 frames.");
                scene.FrameCount = frames;
            }
            foreach (var corners in new[] { u.Corners, u.Corners2 })
            {
                if (corners == null) continue;
                if (corners.Count != 4 && corners.Count != 0) throw new ArgumentException("Each frame needs exactly 4 corners.");
                if (corners.Count == 4)
                {
                    using var img = ImageIO.Load(ImagePath(scene));
                    PreparedScene.ValidateCorners(corners, img.Width, img.Height);
                }
            }
            if (u.Corners != null) scene.Corners = u.Corners;
            if (u.Corners2 != null) scene.Corners2 = u.Corners2;
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

public record RenameItem(string From, string To);

public record ArtworkInfo(string File, string Stem, int Width, int Height, long Bytes, string? Error);

/// <summary>
/// Files the app writes next to an artwork. For the scenes: 0001_part1 (left or top) and 0001_part2, a plain cut.
/// For printing: 0001_print, or 0001_print_part1 and 0001_print_part2, with a white border.
/// </summary>
public static class DerivedFiles
{
    public static readonly string[] SceneParts = ["part1", "part2"];
    public static readonly string[] PrintFull = ["print"];
    public static readonly string[] PrintParts = ["print_part1", "print_part2"];

    static readonly System.Text.RegularExpressions.Regex Pattern =
        new(@"^(.+?)_(print_part[12]|print|part[12])$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string FileName(string stem, string suffix, string ext) => $"{stem}_{suffix}.{ext}";

    public static bool IsDerived(string stem) => Pattern.IsMatch(stem);

    /// <summary>"0001_part2" and "0001_print_part1" give "0001"; other names are returned unchanged.</summary>
    public static string BaseStem(string stem) => Pattern.Match(stem) is { Success: true } m ? m.Groups[1].Value : stem;

    /// <summary>Full paths of these files of an artwork that are already in the folder, in any image format.</summary>
    public static List<string> Existing(string folder, string stem, IEnumerable<string> suffixes)
    {
        if (!Directory.Exists(folder)) return [];
        var wanted = suffixes.Select(s => $"{stem}_{s}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(folder)
            .Where(f => ArtworkLibrary.IsImage(f) && wanted.Contains(Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class ArtworkLibrary(AppPaths paths, ConfigStore config)
{
    static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
    readonly ConcurrentDictionary<string, ArtworkInfo> _info = new();

    public string Folder => string.IsNullOrWhiteSpace(config.Current.ArtworkFolder) ? Path.Combine(paths.DataDir, "artworks") : paths.Resolve(config.Current.ArtworkFolder);

    public static bool IsImage(string path) => Ext.Contains(Path.GetExtension(path));

    public IEnumerable<string> Files() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder).Where(IsImage).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    public List<ArtworkInfo> List()
    {
        if (!Directory.Exists(Folder)) throw new DirectoryNotFoundException($"Artwork folder not found: {Folder}");
        var files = Files().ToList();
        var result = new ArtworkInfo[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            i => result[i] = GetInfo(files[i]));
        RecordNumber(HighestNumber(files));
        return result.ToList();
    }

    record Counter(int LastNumber);
    readonly object _counterLock = new();

    /// <summary>The highest number in the counter file: 0 if there is none, null if the file cannot be read.</summary>
    public int? LastNumber()
    {
        lock (_counterLock)
        {
            if (!File.Exists(paths.CounterJson)) return 0;
            try { return JsonSerializer.Deserialize<Counter>(File.ReadAllText(paths.CounterJson), Json.Options) is { LastNumber: >= 0 } c ? c.LastNumber : null; }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Raises the counter to <paramref name="number"/>; it never goes down. An unreadable file is left alone.</summary>
    void RecordNumber(int number)
    {
        lock (_counterLock)
        {
            if (LastNumber() is not { } last || number <= last) return;
            File.WriteAllText(paths.CounterJson, JsonSerializer.Serialize(new Counter(number), Json.Options) + "\n");
        }
    }

    /// <summary>
    /// The split of an artwork used by two-frame scenes (0001_part1 and 0001_part2) and how it was cut, or null if the
    /// artwork is not split. Portrait halves were cut side by side, landscape halves top and bottom.
    /// </summary>
    public (string Part1, string Part2, SplitLayout Layout)? SceneSplit(string artworkFile)
    {
        var found = DerivedFiles.Existing(Folder, Path.GetFileNameWithoutExtension(artworkFile), DerivedFiles.SceneParts);
        string? Pick(string suffix) => found
            .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (Pick("part1") is not { } part1 || Pick("part2") is not { } part2) return null;
        var info = GetInfo(part1);
        if (info.Error != null) return null;
        return (part1, part2, info.Width > info.Height ? SplitLayout.Stacked : SplitLayout.Side);
    }

    static string NumberPart(string file) => DerivedFiles.BaseStem(Path.GetFileNameWithoutExtension(file));

    static int HighestNumber(IEnumerable<string> files) =>
        files.Select(NumberPart).Where(s => FourDigits.IsMatch(s)).Select(int.Parse).DefaultIfEmpty(0).Max();

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

    static readonly System.Text.RegularExpressions.Regex FourDigits = new(@"^\d{4}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Renames every artwork whose name is not exactly four digits (0001.png) to the next numbers after the highest one
    /// ever used (in the folder or in the counter file, so numbers are not reused), oldest file first. Already-numbered
    /// files and the files derived from them (0001_part1, 0001_print) are never touched; the derived files of a renamed
    /// artwork get its new number. With <paramref name="dryRun"/> nothing is renamed and the plan is returned. Renames
    /// are appended to artworks-rename-map.csv.
    /// </summary>
    public List<RenameItem> NormalizeNames(bool dryRun)
    {
        var files = Files().ToList();
        static string Stem(string f) => Path.GetFileNameWithoutExtension(f);
        bool Ok(string f) => FourDigits.IsMatch(NumberPart(f));
        int last = LastNumber() ?? throw new InvalidDataException(
            $"{Path.GetFileName(paths.CounterJson)} cannot be read. Fix it (for example after a merge conflict) before renaming.");
        int next = Math.Max(last, HighestNumber(files));
        var todo = files.Where(f => !Ok(f))
            .OrderBy(f => DerivedFiles.IsDerived(Stem(f))) // artworks first, so that their parts and prints can follow them
            .ThenBy(f => File.GetCreationTimeUtc(f)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var plan = new List<RenameItem>();
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // old name -> new number
        foreach (var f in todo)
        {
            var stem = Stem(f);
            var baseStem = DerivedFiles.BaseStem(stem);
            if (!DerivedFiles.IsDerived(stem) || !numbers.TryGetValue(baseStem, out var number))
            {
                do { next++; } while (next <= 9999 && Directory.EnumerateFiles(Folder, $"{next:0000}.*").Any());
                if (next > 9999) throw new InvalidOperationException("No free four-digit numbers left (0001-9999).");
                number = next;
                numbers.TryAdd(baseStem, number);
            }
            var suffix = stem[baseStem.Length..].ToLowerInvariant(); // "" or "_part1", "_print", "_print_part2", ...
            plan.Add(new RenameItem(Path.GetFileName(f), $"{number:0000}{suffix}{Path.GetExtension(f).ToLowerInvariant()}"));
        }
        if (dryRun || plan.Count == 0) return plan;

        foreach (var item in plan) File.Move(Path.Combine(Folder, item.From), Path.Combine(Folder, item.To));
        RecordNumber(next);

        var map = Path.Combine(paths.DataDir, "artworks-rename-map.csv");
        var sb = new StringBuilder();
        if (!File.Exists(map)) sb.AppendLine("\"New\",\"Old\"");
        foreach (var item in plan) sb.AppendLine($"\"{item.To}\",\"{item.From.Replace("\"", "\"\"")}\"");
        File.AppendAllText(map, sb.ToString(), new UTF8Encoding(true));
        return plan;
    }

    /// <summary>Size and other details of one artwork; throws if it cannot be decoded.</summary>
    public ArtworkInfo Info(string file)
    {
        var info = GetInfo(Resolve(file));
        return info.Error == null ? info : throw new InvalidDataException(info.Error);
    }

    public string? EnsureThumb(string file)
    {
        var full = Resolve(file);
        var info = GetInfo(full);
        return info.Error == null ? ThumbPath(full) : null;
    }
}
