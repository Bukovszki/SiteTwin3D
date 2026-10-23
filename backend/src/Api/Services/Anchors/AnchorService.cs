using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Services.Anchors;

public static class PositionSources
{
    /// <summary>The user marked the approximate place on the floorplan (spec FR-A2, decided).</summary>
    public const string Manual = "manual";
    /// <summary>No floorplan on the level: temporarily the panorama's own position.</summary>
    public const string PanoramaFallback = "panorama_fallback";
}

/// <summary>
/// A box (region / box) is centred on <c>Position</c> + <c>ZRelM</c> (metres above the level floor), sizes in real
/// metres, heading clockwise from north (spec 8.2).
/// </summary>
public record AnchorCreate(string Kind, Point Position, long? LevelId, float? ZRelM, long? PanoramaId, float? YawDeg, float? PitchDeg,
    string? PositionSource, string? RegionShape = null, float? SizeX = null, float? SizeY = null, float? SizeZ = null, float? HeadingDeg = null);

/// <summary>Partial update; null = not sent (<see cref="LevelIdSet"/> distinguishes "outdoor").</summary>
public record AnchorUpdate(Point? Position = null, long? LevelId = null, bool LevelIdSet = false, float? ZRelM = null, float? YawDeg = null,
    float? PitchDeg = null, string? PositionSource = null, float? SizeX = null, float? SizeY = null, float? SizeZ = null, float? HeadingDeg = null);

/// <summary>An anchor with what the views need of its node (label, type, code).</summary>
public record AnchorView(Anchor Anchor, string NodeName, string? NodeCode, string TypeCode);

/// <summary>
/// Anchors: the spatial appearances of a node (spec 8.2, FR-A). The anchor always belongs to the node; display-side ids
/// (panorama, yaw / pitch) live only in the anchor. Every anchor has a WGS84 position, level and height above the
/// floor, so the node shows on the floorplan even without own geometry (FR-A4). Point, info point (panorama_point)
/// and box (region / box, spec 9.1); the prism shape and model_feature come later (M7).
/// </summary>
public class AnchorService(AppDbContext db, CurrentUser currentUser)
{
    private bool CanSeeUnapproved => Roles.EditorRoles.Contains(currentUser.Role);

    public async Task<List<AnchorView>> OfNodeAsync(long nodeId, CancellationToken ct)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, ct))
            throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
        return await Views(Visible().Where(a => a.NodeId == nodeId)).ToListAsync(ct);
    }

    /// <summary>Info points of a panorama (with the node's label).</summary>
    public async Task<List<AnchorView>> OfPanoramaAsync(long panoramaId, CancellationToken ct) =>
        await Views(Visible().Where(a => a.PanoramaId == panoramaId)).ToListAsync(ct);

    /// <summary>Anchors of a level (or outdoor ones) for the floorplan (FR-AR1b); <paramref name="allLevels"/>: of the whole site (3D).</summary>
    public async Task<List<AnchorView>> OfLevelAsync(long siteId, long? levelId, bool allLevels, string? kind, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
        var query = Visible().Where(a => db.Nodes.Any(n => n.Id == a.NodeId && n.SiteId == siteId));
        if (!allLevels) query = query.Where(a => a.LevelId == levelId);
        if (kind is not null) query = query.Where(a => a.Kind == kind);
        return await Views(query).ToListAsync(ct);
    }

    public async Task<AnchorView> GetAsync(long id, CancellationToken ct) =>
        await Views(Visible().Where(a => a.Id == id)).FirstOrDefaultAsync(ct)
        ?? throw AppException.NotFound("anchor.not_found", $"Anchor {id} not found.");

    public async Task<AnchorView> CreateAsync(long nodeId, AnchorCreate request, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
        if (request.Kind is not (AnchorKinds.Point or AnchorKinds.PanoramaPoint or AnchorKinds.Region)
            || (request.Kind == AnchorKinds.Region && (request.RegionShape ?? RegionShapes.Box) != RegionShapes.Box))
            throw AppException.BadRequest("anchor.kind_not_supported", "Supported: point, panorama_point and region (box); prisms and model elements come later.");
        if (request.LevelId is { } levelId) await RequireLevelAsync(levelId, node.SiteId, ct);

        var anchor = new Anchor
        {
            NodeId = nodeId,
            Kind = request.Kind,
            Position = NodeService.Wgs84(request.Position) ?? throw AppException.BadRequest("anchor.position_required", "position is required."),
            LevelId = request.LevelId,
            ZRelM = request.ZRelM ?? 0,
            Attrs = AttrsWithSource(null, request.PositionSource ?? PositionSources.Manual),
        };
        if (request.Kind == AnchorKinds.PanoramaPoint)
        {
            if (request.PanoramaId is not { } panoramaId || request.YawDeg is not { } yaw || request.PitchDeg is not { } pitch)
                throw AppException.BadRequest("anchor.panorama_fields", "An info point needs panoramaId, yawDeg and pitchDeg.");
            var panoramaSite = await db.Panoramas.Where(p => p.Id == panoramaId)
                .Select(p => (long?)db.Nodes.Where(n => n.Id == p.NodeId).Select(n => n.SiteId).First()).FirstOrDefaultAsync(ct)
                ?? throw AppException.NotFound("panorama.not_found", $"Panorama {panoramaId} not found.");
            if (panoramaSite != node.SiteId) throw AppException.BadRequest("anchor.other_site", "The panorama is on another site.");
            anchor.PanoramaId = panoramaId;
            anchor.YawDeg = ValidYaw(yaw);
            anchor.PitchDeg = ValidPitch(pitch);
        }
        if (request.Kind == AnchorKinds.Region)
        {
            if (request.SizeX is not { } x || request.SizeY is not { } y || request.SizeZ is not { } z)
                throw AppException.BadRequest("anchor.box_fields", "A box needs sizeX, sizeY and sizeZ (metres).");
            anchor.RegionShape = RegionShapes.Box;
            anchor.SizeX = ValidSize(x);
            anchor.SizeY = ValidSize(y);
            anchor.SizeZ = ValidSize(z);
            anchor.HeadingDeg = Normalize(request.HeadingDeg ?? 0);
        }
        db.Anchors.Add(anchor);
        await db.SaveChangesAsync(ct);
        return await GetAsync(anchor.Id, ct);
    }

    /// <summary>Moving an anchor (e.g. dragging its marker on the floorplan, FR-AR1b, FR-A5).</summary>
    public async Task<AnchorView> UpdateAsync(long id, AnchorUpdate request, CancellationToken ct)
    {
        var anchor = await db.Anchors.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw AppException.NotFound("anchor.not_found", $"Anchor {id} not found.");
        if (request.Position is not null)
        {
            anchor.Position = NodeService.Wgs84(request.Position)!;
            // A position marked by hand is a manual one, even if it started as the panorama fallback.
            anchor.Attrs = AttrsWithSource(anchor.Attrs, request.PositionSource ?? PositionSources.Manual);
        }
        if (request.LevelIdSet)
        {
            var siteId = await db.Nodes.Where(n => n.Id == anchor.NodeId).Select(n => n.SiteId).FirstAsync(ct);
            if (request.LevelId is { } levelId) await RequireLevelAsync(levelId, siteId, ct);
            anchor.LevelId = request.LevelId;
        }
        if (request.ZRelM is { } z) anchor.ZRelM = z;
        if (anchor.Kind == AnchorKinds.PanoramaPoint)
        {
            if (request.YawDeg is { } yaw) anchor.YawDeg = ValidYaw(yaw);
            if (request.PitchDeg is { } pitch) anchor.PitchDeg = ValidPitch(pitch);
        }
        if (anchor.Kind == AnchorKinds.Region && anchor.RegionShape == RegionShapes.Box)
        {
            if (request.SizeX is { } sx) anchor.SizeX = ValidSize(sx);
            if (request.SizeY is { } sy) anchor.SizeY = ValidSize(sy);
            if (request.SizeZ is { } sz) anchor.SizeZ = ValidSize(sz);
            if (request.HeadingDeg is { } h) anchor.HeadingDeg = Normalize(h);
        }
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var nodeId = await db.Anchors.Where(a => a.Id == id).Select(a => (long?)a.NodeId).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("anchor.not_found", $"Anchor {id} not found.");
        currentUser.RequireEditableSite(await db.Nodes.Where(n => n.Id == nodeId).Select(n => n.SiteId).FirstAsync(ct));
        if (await db.Anchors.Where(a => a.Id == id).ExecuteDeleteAsync(ct) == 0)
            throw AppException.NotFound("anchor.not_found", $"Anchor {id} not found.");
    }

    /// <summary>Viewers do not see info points of panoramas they cannot see (not yet approved, FR-P8).</summary>
    private IQueryable<Anchor> Visible() => CanSeeUnapproved
        ? db.Anchors.AsNoTracking()
        : db.Anchors.AsNoTracking().Where(a => a.PanoramaId == null || db.Panoramas.Any(p => p.Id == a.PanoramaId && p.PrivacyStatus == PrivacyStatus.Approved));

    private IQueryable<AnchorView> Views(IQueryable<Anchor> anchors) =>
        anchors.OrderBy(a => a.Id).Join(db.Nodes.Include(n => n.Type), a => a.NodeId, n => n.Id, (a, n) => new AnchorView(a, n.Name, n.Code, n.Type.Code));

    private async Task RequireLevelAsync(long levelId, long siteId, CancellationToken ct)
    {
        var level = await db.Nodes.AsNoTracking().Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == levelId, ct);
        if (level is null || !level.Type.IsLevel || level.SiteId != siteId)
            throw AppException.BadRequest("level.invalid", "levelId must be a level of the same site.");
    }

    private static JsonDocument AttrsWithSource(JsonDocument? attrs, string source)
    {
        if (source is not (PositionSources.Manual or PositionSources.PanoramaFallback))
            throw AppException.BadRequest("anchor.invalid_position_source", "positionSource: manual or panorama_fallback.");
        var obj = attrs is null ? new JsonObject() : JsonNode.Parse(attrs.RootElement.GetRawText())!.AsObject();
        obj["position_source"] = source;
        return JsonSerializer.SerializeToDocument(obj);
    }

    private static float ValidYaw(float yaw) =>
        yaw is >= -180 and <= 180 ? yaw : throw AppException.BadRequest("anchor.invalid_angle", "yawDeg: −180…180 (image angle).");

    /// <summary>0.05 m … 500 m per side: a box is around equipment or a room, not a whole site.</summary>
    private static float ValidSize(float size) =>
        size is >= 0.05f and <= 500 ? size : throw AppException.BadRequest("anchor.invalid_size", "Box sizes: 0.05…500 m.");

    private static float Normalize(float heading) => ((heading % 360) + 360) % 360;

    private static float ValidPitch(float pitch) =>
        pitch is >= -90 and <= 90 ? pitch : throw AppException.BadRequest("anchor.invalid_angle", "pitchDeg: −90…90.");
}
