using OpenCvSharp;

namespace ArtMockups;

public record CameraBox(double CenterX, double CenterY, double Width, double Height)
{
    public double Left => CenterX - Width / 2;
    public double Top => CenterY - Height / 2;
}

/// <summary>
/// A virtual camera over a still (the composited mockup): a crop box of the output aspect that glides from the full view
/// to the artwork opening. Every frame is one affine warp of the still, so motion is exactly smooth (no tracking, no wobble).
/// </summary>
public sealed class ZoomCamera
{
    public const int LongSide = 1920, ShortSide = 1080;

    public int OutWidth { get; }
    public int OutHeight { get; }
    public double SceneWidth { get; }
    public double SceneHeight { get; }
    public bool Ease { get; }
    readonly double _aspect;
    readonly double _cx0, _cy0, _w0, _cx1, _cy1, _w1;

    public CameraBox StartBox => Box(0);
    public CameraBox EndBox => Box(1);
    /// <summary>How many times the end-of-zoom crop is enlarged to reach the output size (1 = native detail; &gt; 1 = soft).</summary>
    public double EndUpscale => OutWidth / _w1;

    public ZoomCamera(int sceneWidth, int sceneHeight, IReadOnlyList<PointD> corners, bool portrait, double endFill, bool ease)
    {
        if (corners.Count != 4) throw new ArgumentException("Marque os quatro cantos do quadro no cenário.");
        if (endFill is < 0.3 or > 1.0) throw new ArgumentException("O preenchimento final deve ficar entre 30% e 100%.");
        SceneWidth = sceneWidth; SceneHeight = sceneHeight; Ease = ease;
        OutWidth = portrait ? ShortSide : LongSide;
        OutHeight = portrait ? LongSide : ShortSide;
        _aspect = (double)OutWidth / OutHeight;

        // Start: the largest box of the output aspect that fits the scene, centred.
        if (sceneWidth / (double)sceneHeight >= _aspect) { _w0 = sceneHeight * _aspect; }
        else { _w0 = sceneWidth; }
        // Centred, unless that would cut the artwork off: then slide the box just enough to keep the whole opening in view.
        double h0 = _w0 / _aspect, margin = 0.06 * _w0;
        double minX = corners.Min(p => p.X), maxX = corners.Max(p => p.X), minY = corners.Min(p => p.Y), maxY = corners.Max(p => p.Y);
        static double Keep(double centre, double size, double lo, double hi, double margin, double sceneSize)
        {
            double a = hi + margin - size / 2, b = lo - margin + size / 2; // centre range that keeps [lo,hi] inside the box
            if (a <= b) centre = Math.Clamp(centre, a, b);
            return Math.Clamp(centre, size / 2, Math.Max(size / 2, sceneSize - size / 2));
        }
        _cx0 = Keep(sceneWidth / 2.0, _w0, minX, maxX, margin, sceneWidth);
        _cy0 = Keep(sceneHeight / 2.0, h0, minY, maxY, margin, sceneHeight);

        // End: centred on the opening, sized so the whole opening fits and fills `endFill` of the output.
        double Dist(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        double openW = (Dist(corners[0], corners[1]) + Dist(corners[3], corners[2])) / 2;
        double openH = (Dist(corners[0], corners[3]) + Dist(corners[1], corners[2])) / 2;
        _cx1 = corners.Average(p => p.X); _cy1 = corners.Average(p => p.Y);
        _w1 = Math.Clamp(Math.Max(openW / endFill, openH * _aspect / endFill), Math.Min(32, _w0), _w0);
    }

    static double Smooth(double t) => t * t * (3 - 2 * t);

    /// <summary>Camera box at t in [0,1] (0 = start, 1 = end), always inside the scene.</summary>
    public CameraBox Box(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double e = Ease ? Smooth(t) : t;
        double w = _w0 * Math.Pow(_w1 / _w0, e); // geometric: constant zoom speed on screen
        double h = w / _aspect;
        double cx = _cx0 + (_cx1 - _cx0) * e, cy = _cy0 + (_cy1 - _cy0) * e;
        cx = Math.Clamp(cx, w / 2, Math.Max(w / 2, SceneWidth - w / 2));
        cy = Math.Clamp(cy, h / 2, Math.Max(h / 2, SceneHeight - h / 2));
        return new CameraBox(cx, cy, w, h);
    }

    /// <summary>Renders the frame at t from the full-resolution still.</summary>
    public Mat Render(Mat still, double t)
    {
        var b = Box(t);
        double s = OutWidth / b.Width;
        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.SetArray(new[] { s, 0, -b.Left * s, 0, s, -b.Top * s });
        var dst = new Mat();
        Cv2.WarpAffine(still, dst, m, new Size(OutWidth, OutHeight), InterpolationFlags.Lanczos4, BorderTypes.Replicate);
        return dst;
    }
}
