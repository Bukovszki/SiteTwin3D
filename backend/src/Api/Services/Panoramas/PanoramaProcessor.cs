using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Api.Data;
using Api.Model;
using Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Api.Services.Panoramas;

/// <summary>
/// The two background steps of a panorama (FR-P8). Runs without a signed-in user, so it reads across organizations
/// deliberately (IgnoreQueryFilters) and only touches the panorama it was given.
/// <list type="number">
/// <item>Analyse (pending → review): checks the SHA-256 and the 2:1 format, suggests face regions.</item>
/// <item>Render (rendering → approved): burns the accepted regions into a display version (downscaled for WebGL),
/// then deletes the unblurred original unless the organization keeps it for a limited time.</item>
/// </list>
/// </summary>
public class PanoramaProcessor(AppDbContext db, IObjectStorage storage, PrivacyDetector detector, IOptions<PrivacyOptions> options,
    ILogger<PanoramaProcessor> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task AnalyzeAsync(long panoramaId, CancellationToken ct)
    {
        var panorama = await db.Panoramas.IgnoreQueryFilters().FirstAsync(p => p.Id == panoramaId, ct);
        try
        {
            var bytes = await ReadAllAsync(panorama.StorageKey, ct);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(sha, panorama.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PanoramaException("panorama.checksum_mismatch", "The uploaded file does not match its SHA-256.");

            using var image = Decode(bytes);
            var ratio = (double)image.Width / image.Height;
            if (Math.Abs(ratio - 2) > 0.04)
                throw new PanoramaException("panorama.not_equirectangular", $"Not a 2:1 equirectangular image ({image.Width}×{image.Height}).");

            var started = DateTime.UtcNow;
            var regions = detector.FindFaces(image);
            panorama.PrivacyRegions = JsonSerializer.SerializeToDocument(regions, Json);
            panorama.Width = image.Width;
            panorama.Height = image.Height;
            panorama.PrivacyStatus = PrivacyStatus.Review;
            panorama.ProcessingError = null;
            logger.LogInformation("Panorama {Id} analysed: {Count} face region(s) in {Seconds:0.0} s", panorama.Id, regions.Count,
                (DateTime.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (ex is PanoramaException or Amazon.S3.AmazonS3Exception)
        {
            Fail(panorama, ex);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RenderAsync(long panoramaId, CancellationToken ct)
    {
        var panorama = await db.Panoramas.IgnoreQueryFilters().FirstAsync(p => p.Id == panoramaId, ct);
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == panorama.OrganizationId, ct);
        try
        {
            using var image = Decode(await ReadAllAsync(panorama.StorageKey, ct));
            var regions = panorama.PrivacyRegions.Deserialize<List<PrivacyRegion>>(Json) ?? [];
            Blur(image, regions.Where(r => r.Accepted));

            using var display = Downscale(image, options.Value.MaxDisplayWidth);
            using var data = display.Encode(SKEncodedImageFormat.Jpeg, options.Value.DisplayJpegQuality);
            var key = DisplayKey(panorama);
            await using (var stream = data.AsStream())
                await storage.PutAsync(key, stream, "image/jpeg", ct);

            panorama.DisplayKey = key;
            panorama.Width = display.Width;
            panorama.Height = display.Height;
            panorama.PrivacyStatus = PrivacyStatus.Approved;
            panorama.ProcessingError = null;
            if (organization.PanoramaOriginalRetentionDays is { } days)
                panorama.OriginalRetainedUntil = DateTime.UtcNow.AddDays(days);
            else
            {
                await storage.DeleteAsync(panorama.StorageKey, ct);
                panorama.OriginalRetainedUntil = null;
            }
            logger.LogInformation("Panorama {Id} approved: display version {Width}×{Height}, {Count} region(s) blurred", panorama.Id,
                display.Width, display.Height, regions.Count(r => r.Accepted));
        }
        catch (Exception ex) when (ex is PanoramaException or Amazon.S3.AmazonS3Exception)
        {
            Fail(panorama, ex);
        }
        await db.SaveChangesAsync(ct);
    }

    public static string DisplayKey(Panorama p) => $"org/{p.OrganizationId}/panoramas/{p.Id}/display.jpg";

    /// <summary>
    /// Irreversible blur: the region is shrunk to a few pixels and enlarged again, so the detail is gone (not just
    /// hidden); edges stay soft.
    /// </summary>
    public static void Blur(SKBitmap image, IEnumerable<PrivacyRegion> regions)
    {
        using var canvas = new SKCanvas(image);
        foreach (var region in regions)
            foreach (var rect in Equirect.RegionRects(region, image.Width, image.Height))
            {
                var r = SKRectI.Intersect(rect, new SKRectI(0, 0, image.Width, image.Height));
                if (r.Width < 2 || r.Height < 2) continue;
                using var patch = new SKBitmap(r.Width, r.Height, image.ColorType, image.AlphaType);
                image.ExtractSubset(patch, r);
                var tinyW = Math.Clamp(r.Width / 16, 2, 8);
                var tinyH = Math.Clamp(r.Height / 16, 2, 8);
                using var tiny = patch.Resize(new SKImageInfo(tinyW, tinyH, image.ColorType, image.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var smooth = tiny.Resize(new SKImageInfo(r.Width, r.Height, image.ColorType, image.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
                canvas.DrawBitmap(smooth, r.Left, r.Top);
            }
        canvas.Flush();
    }

    private static SKBitmap Downscale(SKBitmap image, int maxWidth)
    {
        if (image.Width <= maxWidth) return image.Copy();
        var info = new SKImageInfo(maxWidth, maxWidth / 2, image.ColorType, image.AlphaType);
        return image.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static SKBitmap Decode(byte[] bytes)
    {
        using var decoded = SKBitmap.Decode(bytes) ?? throw new PanoramaException("panorama.unreadable", "The image cannot be decoded.");
        // Work in RGBA so pixel access is uniform.
        return decoded.Copy(SKColorType.Rgba8888) ?? throw new PanoramaException("panorama.unreadable", "The image cannot be converted.");
    }

    private async Task<byte[]> ReadAllAsync(string key, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(key, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private void Fail(Panorama panorama, Exception ex)
    {
        logger.LogWarning("Panorama {Id} failed: {Message}", panorama.Id, ex.Message);
        panorama.PrivacyStatus = PrivacyStatus.Failed;
        panorama.ProcessingError = ex is PanoramaException p ? p.Code : "panorama.storage_error";
    }
}

public class PanoramaException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Wakes the worker when a panorama is registered or approved (it also polls, so nothing is lost on restart).</summary>
public class PanoramaQueue
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => _signal.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _signal.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Background worker: takes pending / rendering panoramas one by one (the queue is the panorama table itself, so
/// work survives a restart).
/// </summary>
public class PanoramaWorker(IServiceScopeFactory scopes, PanoramaQueue queue, ILogger<PanoramaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await ProcessNextAsync(stoppingToken)) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Panorama processing error");
            }
            await queue.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    /// <summary>Processes one panorama; false if there was none.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var next = await db.Panoramas.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.PrivacyStatus == PrivacyStatus.Pending || p.PrivacyStatus == PrivacyStatus.Rendering)
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.PrivacyStatus })
            .FirstOrDefaultAsync(ct);
        if (next is null) return false;

        var processor = scope.ServiceProvider.GetRequiredService<PanoramaProcessor>();
        if (next.PrivacyStatus == PrivacyStatus.Pending) await processor.AnalyzeAsync(next.Id, ct);
        else await processor.RenderAsync(next.Id, ct);
        return true;
    }
}
