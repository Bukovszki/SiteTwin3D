using SkiaSharp;

namespace Api.Services.Panoramas;

/// <summary>
/// Geometry of equirectangular (2:1, 360°×180°) panoramas, in IMAGE angles (independent of the panorama's
/// heading, so a later orientation calibration does not move privacy regions):
/// <list type="bullet">
/// <item>yaw: 0° at the image centre, positive to the right, [-180, 180);</item>
/// <item>pitch: 0° at the horizon, positive up, [-90, 90].</item>
/// </list>
/// Direction vectors: x right, y up, z towards the image centre.
/// </summary>
public static class Equirect
{
    private const double Deg = Math.PI / 180;

    public static (double Yaw, double Pitch) PixelToAngles(double x, double y, int width, int height) =>
        ((x / width - 0.5) * 360, (0.5 - y / height) * 180);

    public static (double X, double Y) AnglesToPixel(double yaw, double pitch, int width, int height) =>
        ((yaw / 360 + 0.5) * width, (0.5 - pitch / 180) * height);

    public static double NormalizeYaw(double yaw)
    {
        yaw %= 360;
        if (yaw >= 180) yaw -= 360;
        if (yaw < -180) yaw += 360;
        return yaw;
    }

    /// <summary>Direction of a pixel of a perspective view looking at (yaw0, pitch0) with the given horizontal FOV.</summary>
    public static (double Yaw, double Pitch) ViewPixelToAngles(double i, double j, int viewWidth, int viewHeight,
        double yaw0, double pitch0, double fovDeg)
    {
        var t = Math.Tan(fovDeg * Deg / 2);
        var u = (2 * i / viewWidth - 1) * t;
        var v = (1 - 2 * j / viewHeight) * t * viewHeight / viewWidth;
        var n = Math.Sqrt(u * u + v * v + 1);
        double x = u / n, y = v / n, z = 1 / n;

        // Look up by pitch0 (around x), then turn right by yaw0 (around y).
        double cp = Math.Cos(pitch0 * Deg), sp = Math.Sin(pitch0 * Deg);
        var y1 = y * cp + z * sp;
        var z1 = -y * sp + z * cp;
        double cy = Math.Cos(yaw0 * Deg), sy = Math.Sin(yaw0 * Deg);
        var x2 = x * cy + z1 * sy;
        var z2 = -x * sy + z1 * cy;

        return (Math.Atan2(x2, z2) / Deg, Math.Asin(Math.Clamp(y1, -1, 1)) / Deg);
    }

    /// <summary>Renders a perspective view (RGBA) of the panorama: the input of the face detector.</summary>
    public static SKBitmap PerspectiveView(SKBitmap source, double yaw0, double pitch0, double fovDeg, int width, int height)
    {
        var view = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var src = source.GetPixelSpan();
        var dst = view.GetPixelSpan();
        int sw = source.Width, sh = source.Height, stride = source.RowBytes;
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var (yaw, pitch) = ViewPixelToAngles(i + 0.5, j + 0.5, width, height, yaw0, pitch0, fovDeg);
                var (x, y) = AnglesToPixel(yaw, pitch, sw, sh);
                var sx = Math.Clamp((int)x, 0, sw - 1);
                var sy = Math.Clamp((int)y, 0, sh - 1);
                var s = sy * stride + sx * 4;
                var d = (j * width + i) * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = 255;
            }
        }
        return view;
    }

    /// <summary>
    /// Pixel rectangles of a region in the equirectangular image; two when it crosses the ±180° seam.
    /// </summary>
    public static IEnumerable<SKRectI> RegionRects(PrivacyRegion region, int width, int height)
    {
        var x0 = ((region.Yaw - region.Width / 2) / 360 + 0.5) * width;
        var x1 = ((region.Yaw + region.Width / 2) / 360 + 0.5) * width;
        var y0 = Math.Clamp((0.5 - (region.Pitch + region.Height / 2) / 180) * height, 0, height);
        var y1 = Math.Clamp((0.5 - (region.Pitch - region.Height / 2) / 180) * height, 0, height);
        int top = (int)Math.Floor(y0), bottom = (int)Math.Ceiling(y1);
        if (x0 < 0)
        {
            yield return new SKRectI(0, top, (int)Math.Ceiling(x1), bottom);
            yield return new SKRectI((int)Math.Floor(x0 + width), top, width, bottom);
        }
        else if (x1 > width)
        {
            yield return new SKRectI((int)Math.Floor(x0), top, width, bottom);
            yield return new SKRectI(0, top, (int)Math.Ceiling(x1 - width), bottom);
        }
        else yield return new SKRectI((int)Math.Floor(x0), top, (int)Math.Ceiling(x1), bottom);
    }
}

/// <summary>
/// A region to blur (FR-P8), in image angles (degrees). Automatic suggestions start accepted; the editor may reject
/// them or add manual ones; only accepted regions are burnt into the display version.
/// </summary>
public record PrivacyRegion(string Id, double Yaw, double Pitch, double Width, double Height, string Source, bool Accepted, double? Score = null)
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}
