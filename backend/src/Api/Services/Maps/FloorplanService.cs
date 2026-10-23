using System.Text.Json;
using Api.Data;
using Api.Model;
using Api.Services.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Maps;

public record FloorplanCreate(string Source, string Name, string Layer, string? CqlFilter, JsonElement? WmsParams, string? ExternalUrl,
    int? SortOrder, float? Opacity);

/// <summary>Partial update; null = not sent (<see cref="CqlFilterSet"/> distinguishes clearing the filter).</summary>
public record FloorplanUpdate(string? Name = null, string? Layer = null, string? CqlFilter = null, bool CqlFilterSet = false,
    JsonElement? WmsParams = null, string? ExternalUrl = null, int? SortOrder = null, float? Opacity = null);

public record LevelInfo(long Id, string Name, string? Code, long? ParentId, double ElevationM, double? HeightM, int FloorplanCount, int PanoramaCount);

/// <summary>
/// Floorplans of a level (FR-AR1): only REFERENCES to WMS layers – a MapManager publication ("workspace:layer",
/// optionally with a CQL filter selecting the level on a shared layer) or an external WMS. No files are stored or
/// processed here (spec 6.3); a level may have several (architecture, mechanical…).
/// </summary>
public class FloorplanService(AppDbContext db, Auth.CurrentUser currentUser)
{
    public async Task<List<LevelInfo>> LevelsAsync(long siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
        var levels = await db.Nodes.AsNoTracking()
            .Where(n => n.SiteId == siteId && n.Type.IsLevel)
            .Select(n => new
            {
                n.Id, n.Name, n.Code, n.ParentId, n.Attrs,
                Floorplans = db.Floorplans.Count(f => f.LevelId == n.Id),
                Panoramas = db.Panoramas.Count(p => p.LevelId == n.Id),
            })
            .ToListAsync(ct);
        return levels
            .Select(l => new LevelInfo(l.Id, l.Name, l.Code, l.ParentId, Number(l.Attrs, "elevation_m") ?? 0, Number(l.Attrs, "height_m"),
                l.Floorplans, l.Panoramas))
            .OrderBy(l => l.ElevationM).ThenBy(l => l.Name)
            .ToList();
    }

    public async Task<List<Floorplan>> ListAsync(long levelId, CancellationToken ct)
    {
        await RequireLevelAsync(levelId, ct);
        return await db.Floorplans.AsNoTracking().Where(f => f.LevelId == levelId).OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(ct);
    }

    public async Task<Floorplan> GetAsync(long id, CancellationToken ct) =>
        await db.Floorplans.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct)
        ?? throw AppException.NotFound("floorplan.not_found", $"Floorplan {id} not found.");

    public async Task<Floorplan> CreateAsync(long levelId, FloorplanCreate request, CancellationToken ct)
    {
        await RequireLevelAsync(levelId, ct);
        if (request.Source is not (LayerSources.MapManager or LayerSources.ExternalWms))
            throw AppException.BadRequest("map_layer.invalid_source", "Source: mapmanager or external_wms.");
        var floorplan = new Floorplan
        {
            LevelId = levelId,
            Source = request.Source,
            Name = Required(request.Name, "floorplan.name_required"),
            Layer = Required(request.Layer, "floorplan.layer_required"),
            CqlFilter = string.IsNullOrWhiteSpace(request.CqlFilter) ? null : request.CqlFilter.Trim(),
            WmsParams = SiteService.ObjectDocument(request.WmsParams),
            ExternalUrl = request.Source == LayerSources.ExternalWms ? MapLayerService.ValidUrl(request.ExternalUrl) : null,
            SortOrder = request.SortOrder ?? 0,
            Opacity = ValidOpacity(request.Opacity ?? 1),
        };
        db.Floorplans.Add(floorplan);
        await db.SaveChangesAsync(ct);
        return floorplan;
    }

    public async Task<Floorplan> UpdateAsync(long id, FloorplanUpdate request, CancellationToken ct)
    {
        var floorplan = await db.Floorplans.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw AppException.NotFound("floorplan.not_found", $"Floorplan {id} not found.");
        if (request.Name is not null) floorplan.Name = Required(request.Name, "floorplan.name_required");
        if (request.Layer is not null) floorplan.Layer = Required(request.Layer, "floorplan.layer_required");
        if (request.CqlFilterSet) floorplan.CqlFilter = string.IsNullOrWhiteSpace(request.CqlFilter) ? null : request.CqlFilter.Trim();
        if (request.WmsParams is not null) floorplan.WmsParams = SiteService.ObjectDocument(request.WmsParams);
        if (request.ExternalUrl is not null && floorplan.Source == LayerSources.ExternalWms) floorplan.ExternalUrl = MapLayerService.ValidUrl(request.ExternalUrl);
        if (request.SortOrder is { } sortOrder) floorplan.SortOrder = sortOrder;
        if (request.Opacity is { } opacity) floorplan.Opacity = ValidOpacity(opacity);
        await db.SaveChangesAsync(ct);
        return floorplan;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var floorplan = await GetAsync(id, ct);
        currentUser.RequireEditableSite(await db.Nodes.Where(n => n.Id == floorplan.LevelId).Select(n => n.SiteId).FirstAsync(ct));
        if (await db.Floorplans.Where(f => f.Id == id).ExecuteDeleteAsync(ct) == 0)
            throw AppException.NotFound("floorplan.not_found", $"Floorplan {id} not found.");
    }

    private async Task RequireLevelAsync(long levelId, CancellationToken ct)
    {
        var isLevel = await db.Nodes.Where(n => n.Id == levelId).Select(n => (bool?)n.Type.IsLevel).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {levelId} not found.");
        if (!isLevel) throw AppException.BadRequest("level.invalid", "Floorplans belong to levels.");
    }

    private static double? Number(JsonDocument attrs, string name) =>
        attrs.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Required(string? value, string code)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length is 0 or > 500) throw AppException.BadRequest(code, "A value of 1–500 characters is required.");
        return trimmed;
    }

    private static float ValidOpacity(float opacity) =>
        opacity is >= 0 and <= 1 ? opacity : throw AppException.BadRequest("floorplan.invalid_opacity", "Opacity: 0–1.");
}
