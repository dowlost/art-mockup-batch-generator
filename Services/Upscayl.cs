using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;

namespace ArtMockups;

/// <summary>
/// Executa o motor local do Upscayl e reaproveita ampliações nos lotes e nas prévias.
/// </summary>
public sealed class Upscayl(AppPaths paths)
{
    public const string Model = "upscayl-standard-4x";
    public const int Factor = 4;
    const long MaxSourcePixels = 40_000_000; // beyond this the 4x result needs many gigabytes of memory


    public sealed record Install(string Exe, string ModelsDir);
    public sealed record ModelChoice(string Id, string Name, string? Alternate = null);
    public static readonly IReadOnlyList<ModelChoice> Models =
    [
        new(Model, "Padrão"),
        new("high-fidelity-4x", "Alta fidelidade", "hfa2k-4x"),
        new("digital-art-4x", "Arte digital"),
        new("remacri-4x", "Remacri"),
        new("ultramix-balanced-4x", "Ultramix equilibrado"),
        new("ultrasharp-4x", "UltraSharp"),
        new("upscayl-lite-4x", "Leve"),
    ];

    readonly SemaphoreSlim _gpu = new(1);
    readonly ConcurrentDictionary<(string Path, long Length, long Modified), string> _fingerprints = new();

    static bool HasModel(string dir, string model) =>
        File.Exists(Path.Combine(dir, model + ".bin")) && File.Exists(Path.Combine(dir, model + ".param"));

    static string? ResolveModel(Install install, ModelChoice model) =>
        HasModel(install.ModelsDir, model.Id) ? model.Id
        : model.Alternate is { } alt && HasModel(install.ModelsDir, alt) ? alt : null;

    public object Capabilities()
    {
        var install = Find(paths.Root);
        return new { models = Models.Where(m => install != null && ResolveModel(install, m) != null)
            .Select(m => new { m.Id, m.Name }).ToArray() };
    }

    public void Validate(UpscaylOptions? options)
    {
        if (options == null) return;
        if (options.Scale is < 1 or > 16 || options.Width is < 1 or > 12000 ||
            options.GpuId is < 0 || options.TileSize is < 0 or (> 0 and < 32))
            throw new ArgumentException("Use uma escala de 1× a 16× e confira a largura, o ID da GPU e o tamanho dos blocos do Upscayl.");
        GetModel(options);
    }

    (Install Install, string Model) GetModel(UpscaylOptions options)
    {
        var install = Find(paths.Root) ?? throw new InvalidOperationException("Motor portátil do Upscayl indisponível. Prepare a pasta tools/upscayl.");
        var choice = Models.FirstOrDefault(m => m.Id == options.Model);
        var model = choice == null ? null : ResolveModel(install, choice);
        return (install, model ?? throw new ArgumentException("O modelo selecionado do Upscayl não está disponível."));
    }

    /// <summary>Localiza o motor configurado, portátil ou instalado.</summary>
    public static Install? Find(string? root = null)
    {
        var exe = OperatingSystem.IsWindows() ? "upscayl-bin.exe" : "upscayl-bin";
        string?[] folders =
        [
            Environment.GetEnvironmentVariable("ARTMOCKUPS_UPSCAYL"),
            Path.Combine(root ?? Directory.GetCurrentDirectory(), "tools", "upscayl"),
            Path.Combine(AppContext.BaseDirectory, "tools", "upscayl"),
            Under(Environment.SpecialFolder.ProgramFiles, "Upscayl", "resources"),
            Under(Environment.SpecialFolder.LocalApplicationData, "Programs", "Upscayl", "resources"),
            "/Applications/Upscayl.app/Contents/Resources",
            "/opt/Upscayl/resources",
        ];
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var found = new Install(Path.Combine(folder, "bin", exe), Path.Combine(folder, "models"));
            if (File.Exists(found.Exe) && HasModel(found.ModelsDir, Model)) return found;
        }
        return null;

        static string? Under(Environment.SpecialFolder root, params string[] parts) =>
            Environment.GetFolderPath(root) is { Length: > 0 } r ? Path.Combine([r, .. parts]) : null;
    }



    public async Task<Mat> RenderAsync(PreparedMockup prepared, ArtworkLibrary library, ArtPair art,
        bool requireSplit, UpscaylOptions options, CancellationToken ct, Action? started = null)
    {
        if (!options.EntireMockup)
            return await prepared.RenderAsync(library, art, requireSplit, path => LoadAsync(path, options, ct, started));
        using var mockup = prepared.Render(library, art, requireSplit);
        return await EnlargeMockupAsync(mockup, options, ct, started);
    }

    public Mat Upscale(Mat source) =>
        UpscaleAsync(source, new UpscaylOptions(), CancellationToken.None).GetAwaiter().GetResult();

    public async Task<Mat> LoadAsync(string path, UpscaylOptions options, CancellationToken ct, Action? started = null)
    {
        var file = new FileInfo(path);
        var key = _fingerprints.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc.Ticks), entry =>
        {
            using var stream = File.OpenRead(entry.Path);
            return Convert.ToHexString(SHA256.HashData(stream));
        });
        return await CachedAsync(key, () => ImageIO.Decode(File.ReadAllBytes(path), file.Name,
            ImreadModes.AnyDepth | ImreadModes.Color), options, ct, started);
    }

    public Task<Mat> EnlargeMockupAsync(Mat mockup, UpscaylOptions options, CancellationToken ct, Action? started = null)
    {
        var bytes = ImageIO.Encode(mockup, "png", 100);
        return CachedAsync(Convert.ToHexString(SHA256.HashData(bytes)), mockup.Clone, options, ct, started);
    }

    async Task<Mat> CachedAsync(string imageHash, Func<Mat> load, UpscaylOptions options, CancellationToken ct, Action? started)
    {
        var (install, model) = GetModel(options);
        var engine = new FileInfo(install.Exe);
        var weights = new FileInfo(Path.Combine(install.ModelsDir, model + ".bin"));
        var parameters = new FileInfo(Path.Combine(install.ModelsDir, model + ".param"));
        var identity = JsonSerializer.Serialize(new { imageHash, options, model, install.Exe, install.ModelsDir,
            engine.LastWriteTimeUtc, weights = weights.LastWriteTimeUtc, parameters = parameters.LastWriteTimeUtc });
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        var target = Path.Combine(paths.CacheDir, "upscayl", key + ".png");

        await _gpu.WaitAsync(ct);
        try
        {
            if (File.Exists(target)) return ImageIO.Load(target);
            started?.Invoke();
            using var source = load();
            var result = await RunUpscaleAsync(source, options, install, model, ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                ImageIO.Save(target, result, 100);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { _gpu.Release(); }
    }

    async Task<Mat> UpscaleAsync(Mat source, UpscaylOptions options, CancellationToken ct)
    {
        var (install, model) = GetModel(options);
        await _gpu.WaitAsync(ct);
        try { return await RunUpscaleAsync(source, options, install, model, ct, limitOutput: false); }
        finally { _gpu.Release(); }
    }

    async Task<Mat> RunUpscaleAsync(Mat source, UpscaylOptions options, Install install, string model, CancellationToken ct, bool limitOutput = true)
    {
        if (source.Type() != MatType.CV_8UC3) throw new ArgumentException("A ampliação por IA exige imagens coloridas de 8 bits.");
        ct.ThrowIfCancellationRequested();
        var dir = Path.Combine(paths.CacheDir, "upscale", Guid.NewGuid().ToString("N"));
        try
        {
            var targetWidth = options.OutputWidth(source.Width);
            var passes = options.DoublePass ? 2 : 1;
            var maxInputPixels = (long)source.Width * source.Height * (options.DoublePass ? options.Scale * options.Scale : 1);
            var outputPixels = targetWidth * (double)targetWidth * source.Height / source.Width;
            if (maxInputPixels > MaxSourcePixels || (limitOutput && outputPixels > 128_000_000))
                throw new ArgumentException("O resultado é grande demais para o Upscayl. Reduza a escala ou desative a ampliação dupla.");

            Directory.CreateDirectory(dir);
            var input = Path.Combine(dir, "in.png");
            File.WriteAllBytes(input, ImageIO.Encode(source, "png", 100));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            for (int pass = 1; pass <= passes; pass++)
            {
                string output = Path.Combine(dir, $"out-{pass}.png");
                var args = new List<string> { "-i", input, "-o", output, "-m", install.ModelsDir, "-n", model, "-f", "png" };
                if (pass == passes && options.Width is int width)
                    args.AddRange(["-w", width.ToString(CultureInfo.InvariantCulture)]);
                else
                    args.AddRange(["-s", options.Scale.ToString(CultureInfo.InvariantCulture)]);
                if (options.GpuId is int gpu) args.AddRange(["-g", gpu.ToString(CultureInfo.InvariantCulture)]);
                if (options.TileSize > 0) args.AddRange(["-t", options.TileSize.ToString(CultureInfo.InvariantCulture)]);
                if (options.Tta) args.Add("-x");
                var (exit, stderr) = await FfmpegRunner.RunAsync(install.Exe, args, timeout.Token);
                if (exit != 0 || !File.Exists(output))
                    throw new InvalidOperationException("Falha na ampliação por IA — " + Reason(stderr));
                input = output;
            }

            var result = ImageIO.Load(input);
            try
            {
                double expectedHeight = targetWidth * (double)source.Height / source.Width;
                if (result.Width != targetWidth || Math.Abs(result.Height - expectedHeight) > 1)
                    throw new InvalidDataException($"A ampliação por IA retornou um tamanho inesperado ({result.Width}×{result.Height}).");
                MatchSourceColour(result, source);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("A ampliação por IA foi interrompida após 10 minutos.");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
            throw new InvalidDataException("A imagem ampliada por IA diverge da arte. Verifique o driver de vídeo.");

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
        return lines.Count == 0 ? "sem detalhes" : string.Join(" ", lines);
    }
}


public record UpscaylOptions(string Model = Upscayl.Model, int Scale = Upscayl.Factor,
    int? Width = null, bool DoublePass = false, int? GpuId = null, int TileSize = 0,
    bool Tta = false, bool EntireMockup = false)
{
    public int OutputWidth(int width) => Width ?? checked(width * Scale * (DoublePass ? Scale : 1));
}
