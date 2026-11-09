using OpenCvSharp;

namespace ArtMockups;

public static class ImageIO
{
    /// <summary>Decodes an image (EXIF orientation is applied by OpenCV). Unicode-safe paths.</summary>
    public static Mat Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {Path.GetFileName(path)}");
        var bytes = File.ReadAllBytes(path);
        var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidDataException($"Cannot decode image '{Path.GetFileName(path)}' (corrupt or unsupported).");
        }
        return mat;
    }

    public static byte[] Encode(Mat img, string ext, int jpegQuality)
    {
        ext = ext.StartsWith('.') ? ext : "." + ext;
        var prms = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            ? new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality) }
            : Array.Empty<ImageEncodingParam>();
        Cv2.ImEncode(ext, img, out var buf, prms);
        return buf;
    }

    /// <summary>
    /// Encodes the image so the result is at most <paramref name="maxBytes"/> (0 = no limit).
    /// JPEG: lowers quality step by step, then downscales slightly if still too large.
    /// PNG: downscales step by step (PNG has no quality setting).
    /// </summary>
    public static byte[] EncodeUnderLimit(Mat img, string ext, int jpegQuality, long maxBytes)
    {
        var bytes = Encode(img, ext, jpegQuality);
        if (maxBytes <= 0 || bytes.Length <= maxBytes) return bytes;

        bool jpeg = ext.TrimStart('.').Equals("jpg", StringComparison.OrdinalIgnoreCase);
        if (jpeg)
        {
            for (int q = jpegQuality - 4; q >= 60; q -= 4)
            {
                bytes = Encode(img, ext, q);
                if (bytes.Length <= maxBytes) return bytes;
            }
        }

        // Still too big: shrink the image 8% at a time (quality floor 80 for JPEG).
        Mat current = img.Clone();
        try
        {
            for (int i = 0; i < 30; i++)
            {
                var next = new Mat();
                Cv2.Resize(current, next, new Size(Math.Max(1, (int)(current.Width * 0.92)), Math.Max(1, (int)(current.Height * 0.92))),
                    0, 0, InterpolationFlags.Area);
                current.Dispose(); current = next;
                bytes = Encode(current, ext, jpeg ? Math.Min(jpegQuality, 80) : jpegQuality);
                if (bytes.Length <= maxBytes) return bytes;
            }
        }
        finally { current.Dispose(); }
        throw new InvalidDataException($"Could not get the image under {maxBytes / 1_000_000.0:0.#} MB.");
    }

    /// <summary>Atomic save: write to a temp file and move into place. maxBytes 0 = no size limit.</summary>
    public static void Save(string path, Mat img, int jpegQuality, long maxBytes = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = EncodeUnderLimit(img, Path.GetExtension(path), jpegQuality, maxBytes);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public static Mat ResizeToMaxSide(Mat img, int maxSide)
    {
        var longSide = Math.Max(img.Width, img.Height);
        if (maxSide <= 0 || longSide <= maxSide) return img.Clone();
        var s = (double)maxSide / longSide;
        var dst = new Mat();
        Cv2.Resize(img, dst, new Size(Math.Max(1, (int)Math.Round(img.Width * s)), Math.Max(1, (int)Math.Round(img.Height * s))),
            0, 0, InterpolationFlags.Area);
        return dst;
    }
}

/// <summary>
/// Everything about a scene that does not depend on the artwork (mask, shading map, glare map),
/// computed once and reused for every artwork. Thread-safe for concurrent <see cref="Render"/> calls.
/// The scene Mat is borrowed, not owned.
/// </summary>
public sealed class PreparedScene : IDisposable
{
    readonly Mat _scene;
    readonly Rect _roi;
    readonly Point2f[] _local;
    readonly Mat _alpha3;
    readonly Mat? _shade3;
    readonly Mat? _invGlare3;
    readonly int _frameW, _frameH;
    readonly SceneSettings _s;

    PreparedScene(Mat scene, Rect roi, Point2f[] local, Mat alpha3, Mat? shade3, Mat? invGlare3, int fw, int fh, SceneSettings s)
    {
        _scene = scene; _roi = roi; _local = local; _alpha3 = alpha3; _shade3 = shade3; _invGlare3 = invGlare3;
        _frameW = fw; _frameH = fh; _s = s;
    }

    /// <summary>Estimated aspect ratio (w/h) of the frame area.</summary>
    public double FrameAspect => (double)_frameW / _frameH;

    public static PreparedScene Create(Mat scene, IReadOnlyList<PointD> corners, SceneSettings settings)
    {
        settings = settings.Normalized();
        var pts = ValidateCorners(corners, scene.Width, scene.Height);

        int pad = (int)Math.Ceiling(settings.EdgeFeatherPx * 3) + 2;
        int x0 = Math.Max(0, (int)Math.Floor(pts.Min(p => p.X)) - pad);
        int y0 = Math.Max(0, (int)Math.Floor(pts.Min(p => p.Y)) - pad);
        int x1 = Math.Min(scene.Width, (int)Math.Ceiling(pts.Max(p => p.X)) + pad);
        int y1 = Math.Min(scene.Height, (int)Math.Ceiling(pts.Max(p => p.Y)) + pad);
        var roi = new Rect(x0, y0, x1 - x0, y1 - y0);
        var local = pts.Select(p => new Point2f(p.X - x0, p.Y - y0)).ToArray();

        double top = Dist(pts[0], pts[1]), bottom = Dist(pts[3], pts[2]);
        double left = Dist(pts[0], pts[3]), right = Dist(pts[1], pts[2]);
        int fw = Math.Max(8, (int)Math.Round((top + bottom) / 2));
        int fh = Math.Max(8, (int)Math.Round((left + right) / 2));

        // --- Anti-aliased mask (2x supersampled, then area-downscaled) ---
        const int SS = 2;
        using var big = new Mat(roi.Height * SS, roi.Width * SS, MatType.CV_8UC1, Scalar.All(0));
        var poly = local.Select(p => new Point((int)Math.Round(p.X * SS), (int)Math.Round(p.Y * SS))).ToArray();
        Cv2.FillConvexPoly(big, poly, Scalar.All(255), LineTypes.AntiAlias);
        using var alpha8 = new Mat();
        Cv2.Resize(big, alpha8, new Size(roi.Width, roi.Height), 0, 0, InterpolationFlags.Area);
        using var alphaF = new Mat();
        alpha8.ConvertTo(alphaF, MatType.CV_32FC1, 1 / 255.0);
        if (settings.EdgeFeatherPx > 0)
            Cv2.GaussianBlur(alphaF, alphaF, new Size(0, 0), settings.EdgeFeatherPx * 0.5);

        // --- Shading + glare maps from the blank area of the scene photo ---
        Mat? shade3 = null, invGlare3 = null;
        if (settings.LightingStrength > 0 || settings.GlareStrength > 0)
            BuildLightMaps(scene, roi, alpha8, settings, out shade3, out invGlare3);

        var alpha3 = new Mat();
        Cv2.Merge(new[] { alphaF, alphaF, alphaF }, alpha3);

        return new PreparedScene(scene, roi, local, alpha3, shade3, invGlare3, fw, fh, settings);
    }

    static void BuildLightMaps(Mat scene, Rect roi, Mat alpha8, SceneSettings s, out Mat? shade3, out Mat? invGlare3)
    {
        using var sceneRoi = new Mat(scene, roi);
        using var gray8 = new Mat();
        Cv2.CvtColor(sceneRoi, gray8, ColorConversionCodes.BGR2GRAY);
        using var gray = new Mat();
        gray8.ConvertTo(gray, MatType.CV_32FC1, 1 / 255.0);

        // Interior of the frame area only (avoid the frame edge / mat border pixels).
        using var inner8 = new Mat();
        Cv2.Threshold(alpha8, inner8, 250, 255, ThresholdTypes.Binary);
        int erode = Math.Max(2, (int)Math.Round(Math.Min(roi.Width, roi.Height) * 0.01));
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * erode + 1, 2 * erode + 1)))
            Cv2.Erode(inner8, inner8, kernel);
        if (Cv2.CountNonZero(inner8) == 0)
            Cv2.Threshold(alpha8, inner8, 128, 255, ThresholdTypes.Binary);

        using var innerF = new Mat();
        inner8.ConvertTo(innerF, MatType.CV_32FC1, 1 / 255.0);

        // Normalized convolution: blur luminance only over valid pixels so the frame doesn't bleed in.
        double sigma = Math.Max(2.0, 0.004 * Math.Max(roi.Width, roi.Height));
        using var num = new Mat();
        using var den = new Mat();
        Cv2.Multiply(gray, innerF, num);
        Cv2.GaussianBlur(num, num, new Size(0, 0), sigma);
        Cv2.GaussianBlur(innerF, den, new Size(0, 0), sigma);

        num.GetArray(out float[] n);
        den.GetArray(out float[] d);
        innerF.GetArray(out float[] m);

        var smooth = new float[n.Length];
        var sample = new List<float>(200_000);
        int validCount = 0;
        foreach (var v in m) if (v > 0.5f) validCount++;
        int stride = Math.Max(1, validCount / 200_000);
        int seen = 0;
        for (int i = 0; i < n.Length; i++)
        {
            smooth[i] = d[i] > 1e-4f ? n[i] / d[i] : -1f;
            if (m[i] > 0.5f && (seen++ % stride) == 0) sample.Add(smooth[i]);
        }
        sample.Sort();
        float Pct(double p) => sample.Count == 0 ? 1f : sample[Math.Min(sample.Count - 1, (int)(p * (sample.Count - 1)))];
        float reference = Math.Max(0.05f, Pct(0.95));
        float p85 = Pct(0.85);

        var shade = new float[n.Length];
        var invGlare = new float[n.Length];
        float L = (float)s.LightingStrength, G = (float)s.GlareStrength;
        float glareRange = Math.Max(1e-3f, reference * 1.05f - p85);
        for (int i = 0; i < n.Length; i++)
        {
            float lum = smooth[i] < 0 ? reference : smooth[i];
            float ratio = lum / reference;
            shade[i] = Math.Clamp(1f + L * (ratio - 1f), 0.4f, 1.25f);
            float g = Math.Clamp((lum - p85) / glareRange, 0f, 1f) * G * 0.5f;
            invGlare[i] = 1f - g;
        }

        shade3 = s.LightingStrength > 0 ? ToThreeChannel(shade, roi.Height, roi.Width) : null;
        invGlare3 = s.GlareStrength > 0 ? ToThreeChannel(invGlare, roi.Height, roi.Width) : null;
    }

    static Mat ToThreeChannel(float[] data, int rows, int cols)
    {
        using var one = new Mat(rows, cols, MatType.CV_32FC1);
        one.SetArray(data);
        var three = new Mat();
        Cv2.Merge(new[] { one, one, one }, three);
        return three;
    }

    /// <summary>Composites <paramref name="artwork"/> into the scene and returns a new full-size BGR image.</summary>
    public Mat Render(Mat artwork)
    {
        using var canvas = FitArtwork(artwork);
        var src = new[] { new Point2f(0, 0), new Point2f(_frameW, 0), new Point2f(_frameW, _frameH), new Point2f(0, _frameH) };
        using var M = Cv2.GetPerspectiveTransform(src, _local);

        using var warped = new Mat();
        Cv2.WarpPerspective(canvas, warped, M, new Size(_roi.Width, _roi.Height),
            InterpolationFlags.Cubic, BorderTypes.Replicate);

        using var art = new Mat();
        warped.ConvertTo(art, MatType.CV_32FC3);
        if (_shade3 != null)
        {
            Cv2.Multiply(art, _shade3, art);
            Cv2.Threshold(art, art, 255, 255, ThresholdTypes.Trunc);
        }
        if (_invGlare3 != null)
        {
            // screen blend with a white highlight: out = 255 - (255 - out) * (1 - g)
            using var tmp = new Mat();
            Cv2.Subtract(Scalar.All(255), art, tmp);
            Cv2.Multiply(tmp, _invGlare3, tmp);
            Cv2.Subtract(Scalar.All(255), tmp, art);
        }

        using var sceneRoi = new Mat(_scene, _roi);
        using var sceneF = new Mat();
        sceneRoi.ConvertTo(sceneF, MatType.CV_32FC3);
        using var diff = new Mat();
        Cv2.Subtract(art, sceneF, diff);
        Cv2.Multiply(diff, _alpha3, diff);
        Cv2.Add(sceneF, diff, diff);
        using var res8 = new Mat();
        diff.ConvertTo(res8, MatType.CV_8UC3);

        var output = _scene.Clone();
        using (var view = new Mat(output, _roi)) res8.CopyTo(view);
        return output;
    }

    /// <summary>Prepares the artwork to exactly the frame's aspect ratio according to fit mode.</summary>
    Mat FitArtwork(Mat art)
    {
        int w = _frameW, h = _frameH;
        static InterpolationFlags Interp(double scale) => scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic;

        if (_s.FitMode == "stretch")
        {
            var dst = new Mat();
            Cv2.Resize(art, dst, new Size(w, h), 0, 0, Interp(Math.Min((double)w / art.Width, (double)h / art.Height)));
            return dst;
        }

        if (_s.FitMode == "cover")
        {
            double sc = Math.Max((double)w / art.Width, (double)h / art.Height);
            int nw = Math.Max(w, (int)Math.Ceiling(art.Width * sc)), nh = Math.Max(h, (int)Math.Ceiling(art.Height * sc));
            using var resized = new Mat();
            Cv2.Resize(art, resized, new Size(nw, nh), 0, 0, Interp(sc));
            var rect = new Rect((nw - w) / 2, (nh - h) / 2, w, h);
            using var crop = new Mat(resized, rect);
            return crop.Clone();
        }

        // contain
        {
            double sc = Math.Min((double)w / art.Width, (double)h / art.Height);
            int nw = Math.Clamp((int)Math.Round(art.Width * sc), 1, w), nh = Math.Clamp((int)Math.Round(art.Height * sc), 1, h);
            using var resized = new Mat();
            Cv2.Resize(art, resized, new Size(nw, nh), 0, 0, Interp(sc));
            var c = _s.MatteColor;
            var matte = new Scalar(Convert.ToInt32(c.Substring(5, 2), 16), Convert.ToInt32(c.Substring(3, 2), 16), Convert.ToInt32(c.Substring(1, 2), 16));
            var canvas = new Mat(h, w, MatType.CV_8UC3, matte);
            using var view = new Mat(canvas, new Rect((w - nw) / 2, (h - nh) / 2, nw, nh));
            resized.CopyTo(view);
            return canvas;
        }
    }

    static double Dist(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static Point2f[] ValidateCorners(IReadOnlyList<PointD>? corners, int width, int height)
    {
        if (corners == null || corners.Count != 4)
            throw new ArgumentException("This scene has no corners yet. Click the 4 corners of the blank frame area first.");
        var pts = new Point2f[4];
        for (int i = 0; i < 4; i++)
        {
            var c = corners[i];
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || c.X < 0 || c.Y < 0 || c.X > width || c.Y > height)
                throw new ArgumentException($"Corner {i + 1} is outside the image bounds ({width}x{height}).");
            pts[i] = new Point2f((float)c.X, (float)c.Y);
        }
        double sign = 0, area = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % 4]; var c = pts[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-6) throw new ArgumentException("Corners are degenerate (three points in a line).");
            if (sign == 0) sign = Math.Sign(cross);
            else if (Math.Sign(cross) != sign)
                throw new ArgumentException("Corners must form a convex quadrilateral in order: top-left, top-right, bottom-right, bottom-left.");
            area += a.X * b.Y - b.X * a.Y;
        }
        if (Math.Abs(area) / 2 < 100) throw new ArgumentException("The frame area is too small.");
        return pts;
    }

    public void Dispose()
    {
        _alpha3.Dispose();
        _shade3?.Dispose();
        _invGlare3?.Dispose();
    }
}
