using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Services.Panoramas;

/// <summary>A neighbour of a panorama for the walk-through; distance and bearing are real (geography), not EPSG:3857.</summary>
public record PanoramaNeighbour(long Id, Point Position, long? LevelId, double DistanceM, double BearingDeg, string PrivacyStatus);

public record AutoLinkRequest(long? LevelId, double? MaxDistanceM, int? MaxNeighbours);

/// <summary>
/// Walk-through neighbourhood (FR-P4). Spec 14.2/5 is open (automatic or manual): links are stored as manual links
/// (panorama_link, both directions), and an automatic suggestion links the nearest panoramas of a level within a
/// distance; the result can be corrected by hand. Distances and bearings are computed on the ellipsoid (PostGIS
/// geography), never from EPSG:3857 coordinates (spec 7.3).
/// </summary>
public class PanoramaLinkService(AppDbContext db, CurrentUser currentUser)
{
    private bool CanSeeUnapproved => Roles.EditorRoles.Contains(currentUser.Role);

    private long OrganizationId => currentUser.OrganizationId ?? throw new InvalidOperationException("No organization in the request.");

    public async Task<List<PanoramaNeighbour>> NeighboursAsync(long panoramaId, CancellationToken ct)
    {
        await RequirePanoramaAsync(panoramaId, ct);
        // Raw SQL for geography; the organization condition is explicit because the global filter does not apply here.
        var rows = await db.Database.SqlQuery<NeighbourRow>($"""
            SELECT p2.id AS id, ST_X(p2.position) AS lon, ST_Y(p2.position) AS lat, p2.level_id AS level_id,
                   ST_Distance(p1.position::geography, p2.position::geography) AS distance_m,
                   degrees(ST_Azimuth(p1.position::geography, p2.position::geography)) AS bearing_deg,
                   p2.privacy_status AS privacy_status
              FROM panorama_link l
              JOIN panorama p1 ON p1.id = l.from_id
              JOIN panorama p2 ON p2.id = l.to_id
             WHERE l.from_id = {panoramaId} AND l.organization_id = {OrganizationId}
             ORDER BY 6
            """).ToListAsync(ct);
        return rows
            .Where(r => CanSeeUnapproved || r.PrivacyStatus == PrivacyStatus.Approved)
            .Select(r => new PanoramaNeighbour(r.Id, new Point(r.Lon, r.Lat) { SRID = 4326 }, r.LevelId, Math.Round(r.DistanceM, 2),
                Math.Round(r.BearingDeg, 2), r.PrivacyStatus))
            .ToList();
    }

    /// <summary>Links two panoramas both ways (same site).</summary>
    public async Task LinkAsync(long fromId, long toId, CancellationToken ct)
    {
        if (fromId == toId) throw AppException.BadRequest("panorama_link.self", "A panorama cannot be linked to itself.");
        var a = await RequirePanoramaAsync(fromId, ct);
        var b = await RequirePanoramaAsync(toId, ct);
        if (a != b) throw AppException.BadRequest("panorama_link.other_site", "Only panoramas of the same site can be linked.");
        foreach (var (f, t) in new[] { (fromId, toId), (toId, fromId) })
            if (!await db.PanoramaLinks.AnyAsync(l => l.FromId == f && l.ToId == t, ct))
                db.PanoramaLinks.Add(new PanoramaLink { FromId = f, ToId = t });
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlinkAsync(long fromId, long toId, CancellationToken ct)
    {
        currentUser.RequireEditableSite(await RequirePanoramaAsync(fromId, ct));
        await db.PanoramaLinks.Where(l => (l.FromId == fromId && l.ToId == toId) || (l.FromId == toId && l.ToId == fromId)).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Suggests the neighbourhood of a level (or of the outdoor panoramas): each panorama is linked to its nearest
    /// ones within the distance. Existing links stay (manual corrections are not lost). Returns the number of new links.
    /// </summary>
    public async Task<int> AutoLinkAsync(long siteId, AutoLinkRequest request, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
        var maxDistance = Math.Clamp(request.MaxDistanceM ?? 15, 1, 200);
        var maxNeighbours = Math.Clamp(request.MaxNeighbours ?? 4, 1, 12);
        var levelId = request.LevelId;
        var pairs = await db.Database.SqlQuery<PairRow>($"""
            SELECT a.id AS from_id, b.id AS to_id,
                   row_number() OVER (PARTITION BY a.id ORDER BY ST_Distance(a.position::geography, b.position::geography)) AS rank
              FROM panorama a
              JOIN node na ON na.id = a.node_id
              JOIN panorama b ON b.id <> a.id AND b.organization_id = a.organization_id
              JOIN node nb ON nb.id = b.node_id AND nb.site_id = na.site_id
             WHERE a.organization_id = {OrganizationId} AND na.site_id = {siteId}
               AND a.level_id IS NOT DISTINCT FROM {levelId} AND b.level_id IS NOT DISTINCT FROM {levelId}
               AND ST_DWithin(a.position::geography, b.position::geography, {maxDistance})
            """).ToListAsync(ct);

        var existing = (await db.PanoramaLinks.Select(l => new { l.FromId, l.ToId }).ToListAsync(ct)).Select(l => (l.FromId, l.ToId)).ToHashSet();
        var created = 0;
        foreach (var p in pairs.Where(p => p.Rank <= maxNeighbours))
            foreach (var (f, t) in new[] { (p.FromId, p.ToId), (p.ToId, p.FromId) })
                if (existing.Add((f, t)))
                {
                    db.PanoramaLinks.Add(new PanoramaLink { FromId = f, ToId = t });
                    created++;
                }
        await db.SaveChangesAsync(ct);
        return created / 2;
    }

    /// <summary>The panorama's site; 404 if it is not visible (other organization, or unapproved for viewers).</summary>
    private async Task<long> RequirePanoramaAsync(long id, CancellationToken ct)
    {
        var query = db.Panoramas.Where(p => p.Id == id);
        if (!CanSeeUnapproved) query = query.Where(p => p.PrivacyStatus == PrivacyStatus.Approved);
        return await query.Select(p => (long?)db.Nodes.Where(n => n.Id == p.NodeId).Select(n => n.SiteId).First()).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("panorama.not_found", $"Panorama {id} not found.");
    }

    private record NeighbourRow(long Id, double Lon, double Lat, long? LevelId, double DistanceM, double BearingDeg, string PrivacyStatus);

    private record PairRow(long FromId, long ToId, long Rank);
}
