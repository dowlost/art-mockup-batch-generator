using System.Text.RegularExpressions;

namespace ArtMockups;

public record PointD(double X, double Y);

public class SceneSettings
{
    public string FitMode { get; set; } = "cover";        // cover | contain | stretch
    public string MatteColor { get; set; } = "#ffffff";   // used by contain
    public double LightingStrength { get; set; } = 0.8;   // 0..1
    public double GlareStrength { get; set; } = 0;        // 0..1
    public double EdgeFeatherPx { get; set; } = 1;        // 0..10

    public SceneSettings Normalized()
    {
        var fit = (FitMode ?? "cover").ToLowerInvariant();
        if (fit is not ("cover" or "contain" or "stretch"))
            throw new ArgumentException($"Invalid fit mode '{FitMode}'. Use cover, contain or stretch.");
        if (!Regex.IsMatch(MatteColor ?? "", "^#[0-9a-fA-F]{6}$"))
            throw new ArgumentException($"Invalid matte color '{MatteColor}'. Use #rrggbb.");
        return new SceneSettings
        {
            FitMode = fit,
            MatteColor = MatteColor!,
            LightingStrength = Math.Clamp(LightingStrength, 0, 1),
            GlareStrength = Math.Clamp(GlareStrength, 0, 1),
            EdgeFeatherPx = Math.Clamp(EdgeFeatherPx, 0, 10),
        };
    }
}

public class Scene
{
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    /// <summary>4 points (TL, TR, BR, BL) in original image pixels. Empty until set.</summary>
    public List<PointD> Corners { get; set; } = new();
    public SceneSettings Settings { get; set; } = new();
    /// <summary>Optional expected artwork aspect ratio (width / height), used only for warnings.</summary>
    public double? ExpectedAspectRatio { get; set; }
}

public record SceneUpdate(string? Name, List<PointD>? Corners, SceneSettings? Settings, double? ExpectedAspectRatio, bool? ClearExpectedAspectRatio);

public class AppConfig
{
    /// <summary>Empty = <c>data/artworks</c> inside the data directory.</summary>
    public string ArtworkFolder { get; set; } = "";
    public string AiModel { get; set; } = "claude-sonnet-5-5";
    public int JpegQuality { get; set; } = 92;
    /// <summary>Maximum size of each generated image in bytes (0 = unlimited). Default 2 MB.</summary>
    public long MaxOutputBytes { get; set; } = 2_000_000;
    public string CaptionPrompt { get; set; } =
        "You write product copy for a small online store selling decorative framed art prints. " +
        "Store name: <YOUR STORE NAME>. Tone: warm, modern and inviting. Write in Brazilian Portuguese. " +
        "Title: a short product title (max 60 characters). " +
        "Caption: an Instagram caption of 2-3 short sentences with a call to action, followed by 8-12 relevant hashtags.";
}

public static class Names
{
    static readonly Regex SafeScene = new(@"^[\w\- ]{1,60}$", RegexOptions.Compiled);

    public static string ValidateSceneName(string? name)
    {
        name = name?.Trim() ?? "";
        if (!SafeScene.IsMatch(name))
            throw new ArgumentException("Scene name must be 1-60 characters: letters, digits, spaces, '-' or '_'.");
        return name;
    }

    public static string SafeFileStem(string stem)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        return stem;
    }
}
