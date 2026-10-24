using System.Text.Json;
using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Services.Hierarchy;

public record SiteCreate(string Name, Point RefPoint, double? BaseHeightM, Polygon? Extent, JsonElement? Attrs);

/// <summary>Partial update; null = not sent (<see cref="ExtentSet"/> distinguishes clearing the extent).</summary>
public record SiteUpdate(string? Name = null, Point? RefPoint = null, double? BaseHeightM = null,
    Polygon? Extent = null, bool ExtentSet = false, JsonElement? Attrs = null);

/// <summary>Sites of the organization (spec 2, 7.1). Organization-filtered: another organization's site is a 404.</summary>
public class SiteService(AppDbContext db)
{
    private static readonly GeometryFactory Wgs84Factory = new(new PrecisionModel(), 4326);

    /// <summary>All sites, or those whose extent (or reference point) intersects the bounding box (overview map).</summary>
    public async Task<List<Site>> ListAsync(double[]? bbox, CancellationToken ct)
    {
        var query = db.Sites.AsNoTracking();
        if (bbox is not null)
        {
            if (bbox.Length != 4 || bbox[0] >= bbox[2] || bbox[1] >= bbox[3])
                throw AppException.BadRequest("site.invalid_bbox", "bbox: minLon,minLat,maxLon,maxLat (EPSG:4326).");
            var box = Wgs84Factory.ToGeometry(new Envelope(bbox[0], bbox[2], bbox[1], bbox[3]));
            query = query.Where(s => s.Extent != null ? s.Extent.Intersects(box) : s.RefPoint.Intersects(box));
        }
        return await query.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<Site> GetAsync(long id, CancellationToken ct) =>
        await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw AppException.NotFound("site.not_found", $"Site {id} not found.");

    public async Task<Site> CreateAsync(SiteCreate request, CancellationToken ct)
    {
        var site = new Site
        {
            Name = ValidName(request.Name),
            RefPoint = NodeService.Wgs84(request.RefPoint) ?? throw AppException.BadRequest("site.ref_point_required", "refPoint is required."),
            BaseHeightM = request.BaseHeightM ?? 0,
            Extent = NodeService.Wgs84(request.Extent),
            Attrs = ObjectDocument(request.Attrs),
        };
        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
        return site;
    }

    public async Task<Site> UpdateAsync(long id, SiteUpdate request, CancellationToken ct)
    {
        var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw AppException.NotFound("site.not_found", $"Site {id} not found.");
        if (request.Name is not null) site.Name = ValidName(request.Name);
        if (request.RefPoint is not null) site.RefPoint = NodeService.Wgs84(request.RefPoint)!;
        if (request.BaseHeightM is { } height) site.BaseHeightM = height;
        if (request.ExtentSet) site.Extent = NodeService.Wgs84(request.Extent);
        if (request.Attrs is not null) site.Attrs = ObjectDocument(request.Attrs);
        await db.SaveChangesAsync(ct);
        return site;
    }

    /// <summary>Deletes the site with its hierarchy; refused (409) while panoramas still reference its nodes.</summary>
    /// <summary>
    /// Deletes the site with everything on it (organization admin). Rows that refer to its nodes without a cascade
    /// (anchor and panorama levels, panorama and model nodes) go first: PostgreSQL checks those references while the
    /// cascade is still running. The files become unreferenced and are removed by the storage cleanup (M9).
    /// </summary>
    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == id, ct)) throw AppException.NotFound("site.not_found", $"Site {id} not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var nodes = db.Nodes.Where(n => n.SiteId == id);
        await db.Anchors.Where(a => nodes.Any(n => n.Id == a.NodeId)).ExecuteDeleteAsync(ct);
        await db.Panoramas.Where(p => nodes.Any(n => n.Id == p.NodeId)).ExecuteDeleteAsync(ct);
        await db.ModelAssets.Where(m => m.SiteId == id).ExecuteDeleteAsync(ct);
        await db.Sites.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string ValidName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 200)
            throw AppException.BadRequest("site.invalid_name", "Name: 1–200 characters.");
        return trimmed;
    }

    internal static JsonDocument ObjectDocument(JsonElement? value)
    {
        if (value is null) return JsonDocument.Parse("{}");
        if (value.Value.ValueKind != JsonValueKind.Object)
            throw AppException.BadRequest("attrs.not_object", "attrs must be a JSON object.");
        return JsonSerializer.SerializeToDocument(value.Value);
    }
}
