using Api.Services.Panoramas;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Api.Tests.Panoramas;

/// <summary>Image-angle geometry, region mapping, blurring and the real ONNX face model (FR-P8).</summary>
public sealed class EquirectTests
{
    [Theory]
    [InlineData(0, 0, 2048, 512)]       // image centre
    [InlineData(-180, 90, 0, 0)]        // left edge, zenith
    [InlineData(90, -45, 3072, 768)]    // right quarter, below the horizon
    public void Angles_And_Pixels_RoundTrip(double yaw, double pitch, double x, double y)
    {
        var (px, py) = Equirect.AnglesToPixel(yaw, pitch, 4096, 2048);
        var (yaw2, pitch2) = Equirect.PixelToAngles(px, py, 4096, 2048);

        Assert.Equal(x, px, 6);
        Assert.Equal(y * 2, py, 6);
        Assert.Equal(yaw, yaw2, 6);
        Assert.Equal(pitch, pitch2, 6);
    }

    [Fact]
    public void PerspectiveView_CentreLooksAtItsDirection()
    {
        var (yaw, pitch) = Equirect.ViewPixelToAngles(320, 240, 640, 480, 100, 20, 60);
        var (edgeYaw, _) = Equirect.ViewPixelToAngles(640, 240, 640, 480, 0, 0, 60);

        Assert.Equal(100, yaw, 6);
        Assert.Equal(20, pitch, 6);
        Assert.Equal(30, edgeYaw, 6); // half the horizontal FOV
    }

    [Fact]
    public void Region_AcrossTheSeam_IsSplitInTwo()
    {
        var region = new PrivacyRegion("r", 178, 0, 10, 10, PrivacyRegion.Manual, true);

        var rects = Equirect.RegionRects(region, 3600, 1800).ToList();

        Assert.Equal(2, rects.Count);
        Assert.Contains(rects, r => r.Right == 3600);
        Assert.Contains(rects, r => r.Left == 0 && r.Right == 30);
    }

    [Fact]
    public void FacesFound_InViews_MapBackToImageAngles()
    {
        // A red "face" at yaw 60°, pitch 10°, about 6°×8° (2:1 panorama, 10 px per degree).
        using var panorama = TestImages.Panorama(3600, 1800);
        TestImages.PaintPatch(panorama, yaw: 60, pitch: 10, width: 6, height: 8);
        var detector = new PrivacyDetector(new RedPatchDetector(), Options.Create(new PrivacyOptions()));

        var regions = detector.FindFaces(panorama);

        var face = Assert.Single(regions);
        Assert.Equal(60, face.Yaw, 0);
        Assert.Equal(10, face.Pitch, 0);
        Assert.InRange(face.Width, 6, 6 * 1.6);   // enlarged by the margin, not more
        Assert.InRange(face.Height, 8, 8 * 1.6);
        Assert.True(face.Accepted);
        Assert.Equal(PrivacyRegion.Auto, face.Source);
    }

    [Fact]
    public void Blur_RemovesDetail_InsideTheRegionOnly()
    {
        using var panorama = TestImages.Panorama(3600, 1800);
        TestImages.PaintCheckerboard(panorama, yaw: 0, pitch: 0, size: 10);
        TestImages.PaintCheckerboard(panorama, yaw: 90, pitch: 0, size: 10);
        var before = TestImages.Contrast(panorama, yaw: 0, pitch: 0, size: 8);

        PanoramaProcessor.Blur(panorama, [new PrivacyRegion("r", 0, 0, 14, 14, PrivacyRegion.Auto, true)]);

        Assert.True(TestImages.Contrast(panorama, 0, 0, 8) < before / 10, "the checkerboard in the region is gone");
        Assert.Equal(before, TestImages.Contrast(panorama, 90, 0, 8)); // untouched outside
    }

    [Fact]
    public void UltraFace_Model_LoadsAndFindsNoFaceOnAnEmptyScene()
    {
        using var detector = new UltraFaceDetector(Path.Combine(AppContext.BaseDirectory, "models", "ultraface-rfb-640.onnx"), 0.6f);
        using var panorama = TestImages.Panorama(3600, 1800);
        var privacy = new PrivacyDetector(detector, Options.Create(new PrivacyOptions()));

        Assert.Empty(privacy.FindFaces(panorama));
    }
}

/// <summary>Stand-in face detector for geometry tests: reports the bounding box of pure red pixels in a view.</summary>
public sealed class RedPatchDetector : IFaceDetector
{
    public (int Width, int Height) InputSize => (640, 480);

    public IReadOnlyList<Detection> Detect(SKBitmap view)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var pixels = view.GetPixelSpan();
        for (var y = 0; y < view.Height; y++)
            for (var x = 0; x < view.Width; x++)
            {
                var p = (y * view.Width + x) * 4;
                if (pixels[p] > 240 && pixels[p + 1] < 15 && pixels[p + 2] < 15)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
        // Faces cut by the view edge are found in a neighbouring view.
        if (maxX < 0 || minX == 0 || minY == 0 || maxX == view.Width - 1 || maxY == view.Height - 1) return [];
        return [new Detection(minX, minY, maxX + 1, maxY + 1, 0.9f)];
    }
}

public static class TestImages
{
    /// <summary>A grey-blue gradient panorama (no faces), RGBA.</summary>
    public static SKBitmap Panorama(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height),
                [new SKColor(150, 180, 210), new SKColor(110, 110, 100)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, width, height, paint);
        return bitmap;
    }

    public static void PaintPatch(SKBitmap image, double yaw, double pitch, double width, double height)
    {
        using var canvas = new SKCanvas(image);
        var (x, y) = Equirect.AnglesToPixel(yaw, pitch, image.Width, image.Height);
        var w = width / 360 * image.Width;
        var h = height / 180 * image.Height;
        canvas.DrawRect((float)(x - w / 2), (float)(y - h / 2), (float)w, (float)h, new SKPaint { Color = SKColors.Red });
    }

    public static void PaintCheckerboard(SKBitmap image, double yaw, double pitch, double size)
    {
        using var canvas = new SKCanvas(image);
        var (cx, cy) = Equirect.AnglesToPixel(yaw, pitch, image.Width, image.Height);
        var half = size / 360 * image.Width / 2;
        for (var i = 0; i < 20; i++)
            for (var j = 0; j < 20; j++)
                canvas.DrawRect((float)(cx - half + i * half / 10), (float)(cy - half + j * half / 10), (float)(half / 10), (float)(half / 10),
                    new SKPaint { Color = (i + j) % 2 == 0 ? SKColors.Black : SKColors.White });
    }

    /// <summary>Standard deviation of the brightness in a square around an angle: the "detail" there.</summary>
    public static double Contrast(SKBitmap image, double yaw, double pitch, double size)
    {
        var (cx, cy) = Equirect.AnglesToPixel(yaw, pitch, image.Width, image.Height);
        var half = (int)(size / 360 * image.Width / 2);
        var values = new List<double>();
        for (var y = (int)cy - half; y < cy + half; y++)
            for (var x = (int)cx - half; x < cx + half; x++)
            {
                var c = image.GetPixel(x, y);
                values.Add((c.Red + c.Green + c.Blue) / 3.0);
            }
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
    }

    public static byte[] Jpeg(SKBitmap image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }
}

/// <summary>
/// Manual check of the real model on a real photo (not in CI, no photo in the repository): set FACE_SAMPLE to a JPEG
/// with faces; the photo is placed into a panorama (60° wide) and the regions are drawn into FACE_SAMPLE.out.jpg.
/// </summary>
public sealed class RealFaceCheck
{
    [Fact]
    public void UltraFace_FindsFacesOfASamplePhoto_InAPanorama()
    {
        var sample = Environment.GetEnvironmentVariable("FACE_SAMPLE");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;

        using var photo = SKBitmap.Decode(sample);
        using var panorama = TestImages.Panorama(8192, 4096);
        var width = 8192 * 60 / 360;
        var height = width * photo.Height / photo.Width;
        using (var canvas = new SKCanvas(panorama))
            canvas.DrawBitmap(photo, new SKRect((8192 - width) / 2f, (4096 - height) / 2f, (8192 + width) / 2f, (4096 + height) / 2f));
        using var detector = new UltraFaceDetector(Path.Combine(AppContext.BaseDirectory, "models", "ultraface-rfb-640.onnx"), 0.6f);

        var started = DateTime.UtcNow;
        var regions = new PrivacyDetector(detector, Options.Create(new PrivacyOptions())).FindFaces(panorama);
        var seconds = (DateTime.UtcNow - started).TotalSeconds;

        using (var canvas = new SKCanvas(panorama))
            foreach (var r in regions)
                foreach (var rect in Equirect.RegionRects(r, 8192, 4096))
                    canvas.DrawRect(rect, new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 3 });
        File.WriteAllBytes(sample + ".out.jpg", TestImages.Jpeg(panorama));
        File.WriteAllText(sample + ".out.txt", $"{regions.Count} regions in {seconds:0.0} s");
        Assert.NotEmpty(regions);
    }
}
