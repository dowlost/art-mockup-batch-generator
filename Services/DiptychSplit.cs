using OpenCvSharp;

namespace ArtMockups;

/// <summary>How an artwork is turned into print files.</summary>
public enum SplitLayout
{
    /// <summary>Two portrait prints (60×90 cm) next to each other; the whole artwork is 4:3.</summary>
    Side,
    /// <summary>Two landscape prints (90×60 cm) one above the other; the whole artwork is 3:4.</summary>
    Stacked,
    /// <summary>Not split: one print, 60×90 cm for a portrait artwork and 90×60 cm otherwise.</summary>
    Full,
}

/// <param name="Crop">Area of the source artwork used by the print(s), in source pixels.</param>
/// <param name="PieceWidth">Width of the art on each print in pixels, without the border.</param>
/// <param name="PieceHeight">Height of the art on each print in pixels, without the border.</param>
/// <param name="Dpi">Requested print resolution; 0 = keep the original pixels.</param>
public sealed record SplitPlan(
    SplitLayout Layout, int SourceWidth, int SourceHeight, Rect Crop, int PieceWidth, int PieceHeight, int Dpi)
{
    bool Portrait => Layout == SplitLayout.Side || (Layout == SplitLayout.Full && SourceHeight > SourceWidth);

    public int Pieces => Layout == SplitLayout.Full ? 1 : 2;
    /// <summary>Size of the art on each print.</summary>
    public double PieceCmWidth => Portrait ? DiptychSplit.ShortCm : DiptychSplit.LongCm;
    public double PieceCmHeight => Portrait ? DiptychSplit.LongCm : DiptychSplit.ShortCm;
    /// <summary>White border on each side, in pixels.</summary>
    public int Border => (int)Math.Round(PieceWidth * DiptychSplit.BorderCm / PieceCmWidth);
    /// <summary>Size of the print file, border included.</summary>
    public int FileWidth => PieceWidth + 2 * Border;
    public int FileHeight => PieceHeight + 2 * Border;
    public double FileCmWidth => PieceCmWidth + 2 * DiptychSplit.BorderCm;
    public double FileCmHeight => PieceCmHeight + 2 * DiptychSplit.BorderCm;
    /// <summary>Source pixels across one piece (before any resampling).</summary>
    public int SourcePieceWidth => Layout == SplitLayout.Side ? Crop.Width / 2 : Crop.Width;
    /// <summary>Real detail of the print: source pixels per inch at the final size.</summary>
    public double SourceDpi => SourcePieceWidth / (PieceCmWidth / 2.54);
    public double CroppedPercent => 100 * (1 - (double)Crop.Width * Crop.Height / ((double)SourceWidth * SourceHeight));
    /// <summary>The print needs more pixels than the artwork has.</summary>
    public bool NeedsEnlarging => Dpi != 0 && PieceWidth > SourcePieceWidth;
}

public static class DiptychSplit
{
    public const double ShortCm = 60, LongCm = 90;
    /// <summary>White margin added on every side of a print, as room for cutting and framing (60×90 prints at 61×91).</summary>
    public const double BorderCm = 0.5;

    public static SplitLayout ParseLayout(string? layout) => (layout ?? "").ToLowerInvariant() switch
    {
        "side" => SplitLayout.Side,
        "stacked" => SplitLayout.Stacked,
        "full" => SplitLayout.Full,
        _ => throw new ArgumentException($"Invalid layout '{layout}'. Use full, side or stacked."),
    };

    /// <summary>
    /// Pure geometry. The crop has exactly the shape of the art on all prints together (4:3 side, 3:4 stacked, 2:3 or
    /// 3:2 full), so pieces are exactly 2:3 / 3:2. <paramref name="offset"/> (0..1, 0.5 = centred) slides the crop
    /// along the trimmed axis.
    /// </summary>
    public static SplitPlan Plan(int sourceWidth, int sourceHeight, SplitLayout layout, double offset = 0.5, int dpi = 0)
    {
        if (dpi is not (0 or 150 or 200 or 300)) throw new ArgumentException("Resolution must be original, 150, 200 or 300 DPI.");
        if (!double.IsFinite(offset)) throw new ArgumentException("Invalid crop position.");
        offset = Math.Clamp(offset, 0, 1);

        var (a, b) = layout switch
        {
            SplitLayout.Side => (4, 3),
            SplitLayout.Stacked => (3, 4),
            _ => sourceHeight > sourceWidth ? (2, 3) : (3, 2),
        };
        int k = Math.Min(sourceWidth / a, sourceHeight / b);
        if (k < 1) throw new ArgumentException($"The artwork is too small to print ({sourceWidth}×{sourceHeight}).");
        int cw = a * k, ch = b * k;
        var crop = new Rect((int)Math.Round((sourceWidth - cw) * offset), (int)Math.Round((sourceHeight - ch) * offset), cw, ch);

        var plan = new SplitPlan(layout, sourceWidth, sourceHeight, crop,
            layout == SplitLayout.Side ? cw / 2 : cw, layout == SplitLayout.Stacked ? ch / 2 : ch, dpi);
        if (dpi == 0) return plan;
        static int Pixels(double cm, int dpi) => (int)Math.Round(cm / 2.54 * dpi);
        return plan with { PieceWidth = Pixels(plan.PieceCmWidth, dpi), PieceHeight = Pixels(plan.PieceCmHeight, dpi) };
    }

    /// <summary>Views (no copy) of the pieces of <paramref name="whole"/>: itself, left/right or top/bottom.</summary>
    static Mat[] Cut(Mat whole, SplitLayout layout)
    {
        int w = layout == SplitLayout.Side ? whole.Width / 2 : whole.Width;
        int h = layout == SplitLayout.Stacked ? whole.Height / 2 : whole.Height;
        var first = new Mat(whole, new Rect(0, 0, w, h));
        return layout == SplitLayout.Full ? [first] : [first, new Mat(whole, new Rect(whole.Width - w, whole.Height - h, w, h))];
    }

    /// <summary>The pieces in original pixels, without border (left/top first). The caller disposes them.</summary>
    public static Mat[] Pieces(Mat art, SplitPlan plan)
    {
        using var crop = new Mat(art, plan.Crop);
        return Cut(crop, plan.Layout).Select(view => { using (view) return view.Clone(); }).ToArray();
    }

    /// <summary>A new image: the piece with a white border of <paramref name="border"/> pixels on every side.</summary>
    public static Mat AddBorder(Mat piece, int border)
    {
        var result = new Mat();
        var white = Scalar.All(piece.Depth() == MatType.CV_16U ? ushort.MaxValue : byte.MaxValue);
        Cv2.CopyMakeBorder(piece, result, border, border, border, border, BorderTypes.Constant | BorderTypes.Isolated, white);
        return result;
    }

    public static string[] PrintSuffixes(SplitLayout layout) =>
        layout == SplitLayout.Full ? DerivedFiles.PrintFull : DerivedFiles.PrintParts;

    /// <summary>
    /// Writes the print file(s): &lt;name&gt;_print, or &lt;name&gt;_print_part1 (left or top) and
    /// &lt;name&gt;_print_part2. Each gets a white border. With dpi 0 no pixel of the art is resampled. Otherwise the
    /// whole crop is resized once and then cut, so the two sides of a seam match exactly. Bit depth and the embedded
    /// colour profile are kept.
    /// </summary>
    /// <param name="upscale">
    /// Optional AI upscaler. Used only when the print needs more pixels than the crop has: it enlarges the whole crop,
    /// and the result is then resized to the print size. The caller's function returns a new image.
    /// </param>
    /// <param name="overwrite">
    /// Required when these files already exist. They are replaced, including ones in another format.
    /// </param>
    public static List<SplitFile> Export(string sourcePath, string outDir, SplitLayout layout, double offset, int dpi, string format,
        Func<Mat, Mat>? upscale = null, bool overwrite = false)
    {
        var ext = (format ?? "").ToLowerInvariant();
        if (ext is not ("png" or "jpg")) throw new ArgumentException("Format must be png or jpg.");
        return Write(sourcePath, outDir, layout, offset, dpi, ext, upscale, overwrite, PrintSuffixes(layout), border: true);
    }

    /// <summary>
    /// Cuts an artwork for a two-frame scene into &lt;name&gt;_part1 and &lt;name&gt;_part2: the original pixels as PNG,
    /// with no upscaling and no border.
    /// </summary>
    public static List<SplitFile> SplitForScenes(string sourcePath, string outDir, SplitLayout layout, double offset = 0.5, bool overwrite = false)
    {
        if (layout == SplitLayout.Full) throw new ArgumentException("A scene split is side by side or top and bottom.");
        return Write(sourcePath, outDir, layout, offset, 0, "png", null, overwrite, DerivedFiles.SceneParts, border: false);
    }

    static List<SplitFile> Write(string sourcePath, string outDir, SplitLayout layout, double offset, int dpi, string ext,
        Func<Mat, Mat>? upscale, bool overwrite, string[] suffixes, bool border)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (DerivedFiles.IsDerived(stem))
            throw new ArgumentException($"'{Path.GetFileName(sourcePath)}' is a part or print file. Pick the original artwork.");
        var old = DerivedFiles.Existing(outDir, stem, suffixes);
        if (old.Count > 0 && !overwrite)
            throw new InvalidOperationException($"Already there: {string.Join(", ", old.Select(Path.GetFileName))}. Confirm replacing them.");

        var source = File.ReadAllBytes(sourcePath);
        using var art = ImageIO.Decode(source, Path.GetFileName(sourcePath), ImreadModes.Color | ImreadModes.AnyDepth);
        var plan = Plan(art.Width, art.Height, layout, offset, dpi);
        var icc = PrintMetadata.ReadRgbProfile(source);

        using var whole = new Mat();
        bool ai = upscale != null && plan.NeedsEnlarging;
        using (var crop = new Mat(art, plan.Crop))
        {
            var size = new Size(
                layout == SplitLayout.Side ? plan.PieceWidth * 2 : plan.PieceWidth,
                layout == SplitLayout.Stacked ? plan.PieceHeight * 2 : plan.PieceHeight);
            void Resize(Mat from) =>
                Cv2.Resize(from, whole, size, 0, 0, size.Width < from.Width ? InterpolationFlags.Area : InterpolationFlags.Lanczos4);

            if (dpi == 0) crop.CopyTo(whole);
            else if (ai)
            {
                if (art.Depth() != MatType.CV_8U)
                    throw new ArgumentException("AI upscaling would reduce this 16-bit artwork to 8-bit. Untick AI upscale to keep its depth.");
                using var own = crop.Clone();
                using var enlarged = upscale!(own);
                Resize(enlarged);
            }
            else Resize(crop);
        }
        // JPEG is 8-bit only.
        if (ext == "jpg" && whole.Depth() == MatType.CV_16U) whole.ConvertTo(whole, MatType.CV_8UC3, 1 / 257.0);

        // Tagged so that the file opens at exactly the print size, border included (JPEG can only store whole numbers).
        double dpiX = border ? plan.FileWidth / (plan.FileCmWidth / 2.54) : plan.PieceWidth / (plan.PieceCmWidth / 2.54);
        double dpiY = border ? plan.FileHeight / (plan.FileCmHeight / 2.54) : plan.PieceHeight / (plan.PieceCmHeight / 2.54);

        // Every file is encoded before anything on disk changes.
        var prints = new List<(SplitFile File, byte[] Bytes)>();
        var views = Cut(whole, layout);
        for (int i = 0; i < views.Length; i++)
        {
            using var view = views[i];
            using var piece = AddBorder(view, border ? plan.Border : 0);
            var bytes = PrintMetadata.Apply(ImageIO.Encode(piece, ext, 100), dpiX, dpiY, icc);
            var name = DerivedFiles.FileName(stem, suffixes[i], ext);
            prints.Add((new SplitFile(name, piece.Width, piece.Height, bytes.LongLength), bytes));
        }
        foreach (var (file, bytes) in prints) ImageIO.WriteAtomic(Path.Combine(outDir, file.Name), bytes);
        // Files left over from an earlier export in another format would be duplicates of the new ones.
        foreach (var path in old)
            if (!prints.Any(p => p.File.Name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                File.Delete(path);
        return prints.Select(p => p.File).ToList();
    }
}

public record SplitFile(string Name, int Width, int Height, long Bytes);

/// <param name="Ai">Enlarge with Upscayl. Null = yes when it is installed.</param>
public record SplitRequest(string Artwork, string Layout, double? Offset, int? Dpi, string? Format, bool? Ai = null, bool Overwrite = false);

public sealed class SplitService(ArtworkLibrary library, Upscayl upscayl)
{
    // A 300 DPI pair is about 150 megapixels, so only one export runs at a time.
    readonly object _exportLock = new();

    public object Info(SplitRequest r)
    {
        var art = library.Info(r.Artwork);
        var layout = DiptychSplit.ParseLayout(r.Layout);
        var plan = DiptychSplit.Plan(art.Width, art.Height, layout, r.Offset ?? 0.5, r.Dpi ?? 0);
        bool aiAvailable = Upscayl.Find() != null;
        return new
        {
            existing = DerivedFiles.Existing(library.Folder, art.Stem, DiptychSplit.PrintSuffixes(layout)).Select(Path.GetFileName), // would be replaced
            aiAvailable, usesAi = aiAvailable && (r.Ai ?? true) && plan.NeedsEnlarging,
            aiDpi = Math.Min(plan.SourceDpi * Upscayl.Factor, plan.Dpi),
            sourceWidth = art.Width, sourceHeight = art.Height,
            croppedPercent = plan.CroppedPercent, pieces = plan.Pieces,
            artCmWidth = plan.PieceCmWidth, artCmHeight = plan.PieceCmHeight, borderCm = DiptychSplit.BorderCm,
            fileWidth = plan.FileWidth, fileHeight = plan.FileHeight,
            fileCmWidth = plan.FileCmWidth, fileCmHeight = plan.FileCmHeight,
            sourceDpi = plan.SourceDpi,
        };
    }

    /// <summary>JPEG showing the print(s) with their white border, two pieces with a gap between them.</summary>
    public byte[] Preview(SplitRequest r) =>
        Picture(r.Artwork, DiptychSplit.ParseLayout(r.Layout), r.Offset ?? 0.5, maxSide: 700, border: true);

    /// <summary>JPEG showing how a scene split would cut an artwork (no border).</summary>
    public byte[] CutPreview(string artwork, string layout, double offset) =>
        Picture(artwork, DiptychSplit.ParseLayout(layout), offset, maxSide: 260, border: false);

    /// <summary>Writes the part files used by two-frame scenes.</summary>
    public object Cut(SplitRequest r)
    {
        var source = library.Resolve(r.Artwork);
        lock (_exportLock)
        {
            var files = DiptychSplit.SplitForScenes(source, library.Folder, DiptychSplit.ParseLayout(r.Layout), r.Offset ?? 0.5, r.Overwrite);
            return new { files };
        }
    }

    byte[] Picture(string artwork, SplitLayout layout, double offset, int maxSide, bool border)
    {
        using var art = ImageIO.Load(library.Resolve(artwork));
        var plan = DiptychSplit.Plan(art.Width, art.Height, layout, offset);
        var shown = DiptychSplit.Pieces(art, plan).Select(piece =>
        {
            using (piece)
            {
                using var small = ImageIO.ResizeToMaxSide(piece, layout == SplitLayout.Full ? maxSide * 12 / 7 : maxSide);
                return DiptychSplit.AddBorder(small, border ? (int)Math.Round(small.Width * DiptychSplit.BorderCm / plan.PieceCmWidth) : 0);
            }
        }).ToList();
        try
        {
            if (shown.Count == 1) return ImageIO.Encode(shown[0], ".jpg", 90);
            int gap = Math.Max(6, maxSide / 30);
            bool side = layout == SplitLayout.Side;
            using var spacer = new Mat(side ? shown[0].Height : gap, side ? gap : shown[0].Width, MatType.CV_8UC3, new Scalar(17, 17, 17));
            using var both = new Mat();
            if (side) Cv2.HConcat(new[] { shown[0], spacer, shown[1] }, both);
            else Cv2.VConcat(new[] { shown[0], spacer, shown[1] }, both);
            return ImageIO.Encode(both, ".jpg", 90);
        }
        finally { foreach (var m in shown) m.Dispose(); }
    }

    public object Export(SplitRequest r)
    {
        var source = library.Resolve(r.Artwork);
        // An explicit request for AI without Upscayl installed fails with a message instead of quietly resizing.
        bool ai = r.Ai ?? Upscayl.Find() != null;
        lock (_exportLock)
        {
            var files = DiptychSplit.Export(source, library.Folder, DiptychSplit.ParseLayout(r.Layout), r.Offset ?? 0.5, r.Dpi ?? 0, r.Format ?? "png",
                ai ? upscayl.Upscale : null, r.Overwrite);
            return new { folder = library.Folder, files };
        }
    }
}
