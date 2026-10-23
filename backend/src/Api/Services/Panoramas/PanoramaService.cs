using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Api.Services.Panoramas;

public record UploadRequest(long SiteId, string FileName, long Size);

public record UploadTicket(string Key, string Url, string ContentType, DateTime ExpiresAt);

public record PanoramaCreate(string UploadKey, string Sha256, long NodeId, Point Position, long? LevelId, float? ZRelM, float? HeadingDeg,
    DateTime? CapturedAt);

/// <summary>Partial update; null = not sent (the *Set flags distinguish clearing a value).</summary>
public record PanoramaUpdate(long? NodeId = null, Point? Position = null, long? LevelId = null, bool LevelIdSet = false, float? ZRelM = null,
    float? HeadingDeg = null, bool HeadingDegSet = false, DateTime? CapturedAt = null);

/// <summary>A panorama with what the viewer needs: absolute height (spec 7.1) and a short-lived image URL.</summary>
public record PanoramaView(Panorama Panorama, long SiteId, double AbsoluteHeightM, string? ImageUrl, bool ImageIsOriginal);

/// <summary>
/// Panoramas (spec 4.4): upload with a signed URL straight to the object store, metadata here, background
/// processing (PanoramaProcessor). Viewers see approved (blurred) panoramas only; editors see the original during the
/// review through a short-lived signed URL.
/// </summary>
public partial class PanoramaService(AppDbContext db, IObjectStorage storage, PanoramaQueue queue, CurrentUser currentUser,
    IOptions<StorageOptions> storageOptions)
{
    public const string ContentType = "image/jpeg";

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    private bool CanSeeUnapproved => Roles.EditorRoles.Contains(currentUser.Role);

    private string UploadPrefix => $"org/{currentUser.OrganizationId}/uploads/";

    public async Task<UploadTicket> CreateUploadAsync(UploadRequest request, CancellationToken ct)
    {
        await RequireSiteAsync(request.SiteId, ct);
        var maxBytes = storageOptions.Value.MaxPanoramaMb * 1024L * 1024L;
        if (request.Size <= 0 || request.Size > maxBytes)
            throw AppException.BadRequest("panorama.too_large", $"A panorama may be at most {storageOptions.Value.MaxPanoramaMb} MB.");
        if (!Regex.IsMatch(request.FileName ?? "", @"\.jpe?g$", RegexOptions.IgnoreCase))
            throw AppException.BadRequest("panorama.not_jpeg", "Panoramas are uploaded as equirectangular JPEG.");
        var key = $"{UploadPrefix}{Guid.NewGuid():N}.jpg";
        return new UploadTicket(key, storage.PresignPut(key, ContentType), ContentType,
            DateTime.UtcNow.AddMinutes(storageOptions.Value.UploadUrlMinutes));
    }

    public async Task<Panorama> CreateAsync(PanoramaCreate request, CancellationToken ct)
    {
        // The key must be one of this organization's upload keys, never an arbitrary object.
        if (!(request.UploadKey ?? "").StartsWith(UploadPrefix, StringComparison.Ordinal) || request.UploadKey!.Contains(".."))
            throw AppException.BadRequest("panorama.invalid_upload", "Unknown upload.");
        if (!Sha256Regex().IsMatch(request.Sha256 ?? ""))
            throw AppException.BadRequest("panorama.invalid_sha256", "sha256 must be 64 hex digits.");
        if (await db.Panoramas.AnyAsync(p => p.StorageKey == request.UploadKey, ct))
            throw AppException.Conflict("panorama.upload_used", "This upload is already registered.");
        if (await storage.SizeAsync(request.UploadKey, ct) is null)
            throw AppException.BadRequest("panorama.upload_missing", "The file has not been uploaded (or the upload expired).");

        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == request.NodeId, ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {request.NodeId} not found.");
        if (request.LevelId is { } levelId) await RequireLevelAsync(levelId, node.SiteId, ct);

        var panorama = new Panorama
        {
            NodeId = node.Id,
            StorageKey = request.UploadKey,
            Sha256 = request.Sha256!.ToLowerInvariant(),
            Position = NodeService.Wgs84(request.Position) ?? throw AppException.BadRequest("panorama.position_required", "position is required."),
            LevelId = request.LevelId,
            ZRelM = request.ZRelM ?? 1.6f,
            HeadingDeg = request.HeadingDeg is { } h ? NormalizeHeading(h) : null,
            CapturedAt = request.CapturedAt,
            PrivacyStatus = PrivacyStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.Panoramas.Add(panorama);
        await db.SaveChangesAsync(ct);
        queue.Wake();
        return panorama;
    }

    /// <summary>Panoramas of a site; a level, or outdoor ones (levelId missing and outdoor=true), or all.</summary>
    public async Task<List<Panorama>> ListAsync(long siteId, long? levelId, bool outdoor, CancellationToken ct)
    {
        await RequireSiteAsync(siteId, ct);
        var query = Visible().Where(p => db.Nodes.Any(n => n.Id == p.NodeId && n.SiteId == siteId));
        if (levelId is { } level) query = query.Where(p => p.LevelId == level);
        else if (outdoor) query = query.Where(p => p.LevelId == null);
        return await query.OrderBy(p => p.Id).ToListAsync(ct);
    }

    /// <summary>Panoramas linked to a node or anywhere below it (FR-P3).</summary>
    public async Task<List<Panorama>> SubtreeAsync(long nodeId, bool recursive, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
        var path = node.Path;
        var query = recursive
            ? Visible().Where(p => db.Nodes.Any(n => n.Id == p.NodeId && n.Path.IsDescendantOf(path)))
            : Visible().Where(p => p.NodeId == nodeId);
        return await query.OrderBy(p => p.Id).ToListAsync(ct);
    }

    public async Task<PanoramaView> GetAsync(long id, CancellationToken ct)
    {
        var panorama = await Visible().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw AppException.NotFound("panorama.not_found", $"Panorama {id} not found.");
        var node = await db.Nodes.AsNoTracking().FirstAsync(n => n.Id == panorama.NodeId, ct);
        var site = await db.Sites.AsNoTracking().FirstAsync(s => s.Id == node.SiteId, ct);

        // Absolute height for Cesium (spec 7.1): site base + level floor + height above the floor.
        var elevation = 0.0;
        if (panorama.LevelId is { } levelId)
        {
            var level = await db.Nodes.AsNoTracking().FirstAsync(n => n.Id == levelId, ct);
            if (level.Attrs.RootElement.TryGetProperty("elevation_m", out var e) && e.ValueKind == JsonValueKind.Number)
                elevation = e.GetDouble();
        }

        string? url = null;
        var original = false;
        if (panorama.DisplayKey is not null) url = storage.PresignGet(panorama.DisplayKey);
        else if (CanSeeUnapproved && panorama.PrivacyStatus is PrivacyStatus.Review or PrivacyStatus.Pending or PrivacyStatus.Rendering)
        {
            url = storage.PresignGet(panorama.StorageKey);
            original = true;
        }
        return new PanoramaView(panorama, node.SiteId, Math.Round(site.BaseHeightM + elevation + (double)(decimal)panorama.ZRelM, 3), url, original);
    }

    public async Task<Panorama> UpdateAsync(long id, PanoramaUpdate request, CancellationToken ct)
    {
        var panorama = await db.Panoramas.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw AppException.NotFound("panorama.not_found", $"Panorama {id} not found.");
        var siteId = await db.Nodes.Where(n => n.Id == panorama.NodeId).Select(n => n.SiteId).FirstAsync(ct);
        if (request.NodeId is { } nodeId && nodeId != panorama.NodeId)
        {
            var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
                ?? throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
            if (node.SiteId != siteId) throw AppException.BadRequest("panorama.other_site", "The node is on another site.");
            panorama.NodeId = nodeId;
        }
        if (request.Position is not null) panorama.Position = NodeService.Wgs84(request.Position)!;
        if (request.LevelIdSet)
        {
            if (request.LevelId is { } levelId) await RequireLevelAsync(levelId, siteId, ct);
            panorama.LevelId = request.LevelId;
        }
        if (request.ZRelM is { } z) panorama.ZRelM = z;
        if (request.HeadingDegSet) panorama.HeadingDeg = request.HeadingDeg is { } h ? NormalizeHeading(h) : null;
        if (request.CapturedAt is { } captured) panorama.CapturedAt = captured;
        await db.SaveChangesAsync(ct);
        return panorama;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var panorama = await db.Panoramas.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw AppException.NotFound("panorama.not_found", $"Panorama {id} not found.");
        db.Panoramas.Remove(panorama);
        await db.SaveChangesAsync(ct);
        // Objects after the row: an orphan file is cleaned up later (spec 6.2), a row without its file would be worse.
        foreach (var key in new[] { panorama.StorageKey, panorama.DisplayKey }.OfType<string>())
            try { await storage.DeleteAsync(key, ct); } catch (Amazon.S3.AmazonS3Exception) { }
    }

    /// <summary>The editor's decision on the suggested regions plus manual ones (only while in review).</summary>
    public async Task<Panorama> SetRegionsAsync(long id, List<PrivacyRegion> regions, CancellationToken ct)
    {
        var panorama = await ReviewableAsync(id, ct);
        foreach (var r in regions)
        {
            if (r.Width <= 0 || r.Width > 180 || r.Height <= 0 || r.Height > 180 || r.Pitch < -90 || r.Pitch > 90 || r.Yaw < -180 || r.Yaw > 180
                || r.Source is not (PrivacyRegion.Auto or PrivacyRegion.Manual) || string.IsNullOrWhiteSpace(r.Id))
                throw AppException.BadRequest("panorama.invalid_region", "Invalid region (angles in degrees, source auto|manual).");
        }
        panorama.PrivacyRegions = JsonSerializer.SerializeToDocument(regions, PanoramaProcessor.Json);
        await db.SaveChangesAsync(ct);
        return panorama;
    }

    /// <summary>Approval: the blurred display version is rendered in the background, then viewers see it.</summary>
    public async Task<Panorama> ApproveAsync(long id, CancellationToken ct)
    {
        var panorama = await ReviewableAsync(id, ct);
        panorama.PrivacyStatus = PrivacyStatus.Rendering;
        await db.SaveChangesAsync(ct);
        queue.Wake();
        return panorama;
    }

    private async Task<Panorama> ReviewableAsync(long id, CancellationToken ct)
    {
        var panorama = await db.Panoramas.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw AppException.NotFound("panorama.not_found", $"Panorama {id} not found.");
        if (panorama.PrivacyStatus != PrivacyStatus.Review)
            throw AppException.Conflict("panorama.not_in_review", "Only a panorama waiting for review can be changed or approved.");
        return panorama;
    }

    /// <summary>Viewers: approved only (FR-P8).</summary>
    private IQueryable<Panorama> Visible() =>
        CanSeeUnapproved ? db.Panoramas.AsNoTracking() : db.Panoramas.AsNoTracking().Where(p => p.PrivacyStatus == PrivacyStatus.Approved);

    private async Task RequireSiteAsync(long siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
    }

    internal async Task RequireLevelAsync(long levelId, long siteId, CancellationToken ct)
    {
        var level = await db.Nodes.AsNoTracking().Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == levelId, ct);
        if (level is null || !level.Type.IsLevel || level.SiteId != siteId)
            throw AppException.BadRequest("level.invalid", "levelId must be a level of the same site.");
    }

    /// <summary>Headings are degrees from geographic north, clockwise, in [0, 360).</summary>
    public static float NormalizeHeading(float heading) => ((heading % 360) + 360) % 360;
}
