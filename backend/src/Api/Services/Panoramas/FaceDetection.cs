using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Api.Services.Panoramas;

/// <summary>A detection in pixel coordinates of the analysed view.</summary>
public record Detection(float X1, float Y1, float X2, float Y2, float Score);

public interface IFaceDetector
{
    /// <summary>Input size the detector wants; views are rendered at this size.</summary>
    (int Width, int Height) InputSize { get; }
    IReadOnlyList<Detection> Detect(SKBitmap view);
}

public class PrivacyOptions
{
    public const string Section = "Privacy";
    /// <summary>The ONNX face model (UltraFace RFB-640, MIT): backend/models/ultraface-rfb-640.onnx.</summary>
    public string FaceModelPath { get; set; } = "models/ultraface-rfb-640.onnx";
    public float FaceThreshold { get; set; } = 0.6f;
    /// <summary>Horizontal field of view of the analysed perspective views (°); narrower = smaller faces found.</summary>
    public double ViewFovDeg { get; set; } = 60;
    /// <summary>Longest side of the display version (px): WebGL texture limit of tablets (spec 4.4 risk).</summary>
    public int MaxDisplayWidth { get; set; } = 8192;
    public int DisplayJpegQuality { get; set; } = 85;
}

/// <summary>
/// Face detector running locally (FR-P8: images never go to an external service): UltraFace RFB-640 ONNX model
/// (Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB, MIT). Input 640×480 RGB, (v − 127) / 128; outputs face
/// scores and normalized corner boxes; non-maximum suppression here.
/// </summary>
public sealed class UltraFaceDetector : IFaceDetector, IDisposable
{
    private readonly InferenceSession _session;
    private readonly float _threshold;

    public UltraFaceDetector(string modelPath, float threshold)
    {
        _session = new InferenceSession(modelPath);
        _threshold = threshold;
    }

    public (int Width, int Height) InputSize => (640, 480);

    public IReadOnlyList<Detection> Detect(SKBitmap view)
    {
        var (w, h) = InputSize;
        if (view.Width != w || view.Height != h) throw new ArgumentException($"View must be {w}×{h}.");
        var input = new DenseTensor<float>([1, 3, h, w]);
        var pixels = view.GetPixelSpan();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = (y * w + x) * 4;
                input[0, 0, y, x] = (pixels[p] - 127f) / 128f;
                input[0, 1, y, x] = (pixels[p + 1] - 127f) / 128f;
                input[0, 2, y, x] = (pixels[p + 2] - 127f) / 128f;
            }

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), input)]);
        var scores = results.First(r => r.Name == "scores").AsTensor<float>();
        var boxes = results.First(r => r.Name == "boxes").AsTensor<float>();
        var candidates = new List<Detection>();
        for (var i = 0; i < scores.Dimensions[1]; i++)
        {
            var score = scores[0, i, 1];
            if (score < _threshold) continue;
            candidates.Add(new Detection(boxes[0, i, 0] * w, boxes[0, i, 1] * h, boxes[0, i, 2] * w, boxes[0, i, 3] * h, score));
        }
        return Suppress(candidates, 0.3f);
    }

    public static List<Detection> Suppress(List<Detection> detections, float iouThreshold)
    {
        var kept = new List<Detection>();
        foreach (var d in detections.OrderByDescending(d => d.Score))
            if (kept.All(k => Iou(k, d) < iouThreshold)) kept.Add(d);
        return kept;
    }

    private static float Iou(Detection a, Detection b)
    {
        var ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        var iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = ix * iy;
        var union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Finds faces on an equirectangular panorama: overlapping perspective views around the horizon (and above /
/// below it), detections mapped back to image angles, enlarged to cover hair and merged across views.
/// Licence plates (only inside detected vehicles, per site) are a later step (spec M3: "may come in round 2").
/// </summary>
public class PrivacyDetector(IFaceDetector detector, Microsoft.Extensions.Options.IOptions<PrivacyOptions> options)
{
    private static readonly double[] Pitches = [-25, 0, 25];
    private const double Margin = 1.4;

    public List<PrivacyRegion> FindFaces(SKBitmap panorama)
    {
        var fov = options.Value.ViewFovDeg;
        var (w, h) = detector.InputSize;
        var found = new List<PrivacyRegion>();
        foreach (var pitch0 in Pitches)
        {
            for (var yaw0 = -180.0; yaw0 < 180; yaw0 += fov * 0.75)
            {
                using var view = Equirect.PerspectiveView(panorama, yaw0, pitch0, fov, w, h);
                foreach (var d in detector.Detect(view))
                    found.Add(ToRegion(d, w, h, yaw0, pitch0, fov));
            }
        }
        return Merge(found);
    }

    private static PrivacyRegion ToRegion(Detection d, int w, int h, double yaw0, double pitch0, double fov)
    {
        // Corners and edge midpoints, yaw unwrapped around the view direction.
        var points = new[] { (d.X1, d.Y1), (d.X2, d.Y1), (d.X1, d.Y2), (d.X2, d.Y2), ((d.X1 + d.X2) / 2, d.Y1), ((d.X1 + d.X2) / 2, d.Y2) }
            .Select(p => Equirect.ViewPixelToAngles(p.Item1, p.Item2, w, h, yaw0, pitch0, fov))
            .Select(a => (Yaw: yaw0 + Equirect.NormalizeYaw(a.Yaw - yaw0), a.Pitch))
            .ToList();
        double minYaw = points.Min(p => p.Yaw), maxYaw = points.Max(p => p.Yaw);
        double minPitch = points.Min(p => p.Pitch), maxPitch = points.Max(p => p.Pitch);
        return new PrivacyRegion(Guid.NewGuid().ToString("N")[..12],
            Math.Round(Equirect.NormalizeYaw((minYaw + maxYaw) / 2), 3), Math.Round((minPitch + maxPitch) / 2, 3),
            Math.Round((maxYaw - minYaw) * Margin, 3), Math.Round((maxPitch - minPitch) * Margin, 3),
            PrivacyRegion.Auto, true, Math.Round(d.Score, 3));
    }

    /// <summary>The same face seen in overlapping views: keep the best-scored, drop overlapping ones.</summary>
    public static List<PrivacyRegion> Merge(List<PrivacyRegion> regions)
    {
        var kept = new List<PrivacyRegion>();
        foreach (var r in regions.OrderByDescending(r => r.Score ?? 0))
            if (kept.All(k => Overlap(k, r) < 0.3)) kept.Add(r);
        return kept;
    }

    private static double Overlap(PrivacyRegion a, PrivacyRegion b)
    {
        var dYaw = Math.Abs(Equirect.NormalizeYaw(a.Yaw - b.Yaw));
        var ix = Math.Max(0, (a.Width + b.Width) / 2 - dYaw);
        var iy = Math.Max(0, Math.Min(a.Pitch + a.Height / 2, b.Pitch + b.Height / 2) - Math.Max(a.Pitch - a.Height / 2, b.Pitch - b.Height / 2));
        var inter = Math.Min(ix, Math.Min(a.Width, b.Width)) * iy;
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : inter / smaller;
    }
}
