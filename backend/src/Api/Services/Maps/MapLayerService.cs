using System.Text.Json;
using Api.Data;
using Api.Model;
using Api.Services.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Maps;

public record MapLayerCreate(long? SiteId, string Source, string Name, string Layer, JsonElement? WmsParams, string? ExternalUrl,
    int? SortOrder, bool? VisibleDefault);

/// <summary>Partial update; null = not sent.</summary>
public record MapLayerUpdate(string? Name = null, string? Layer = null, JsonElement? WmsParams = null, string? ExternalUrl = null,
    int? SortOrder = null, bool? VisibleDefault = null);

/// <summary>
/// Background layers (orthophoto, utilities) of a site or of the whole organization: only REFERENCES to WMS layers
/// of MapManager or of an external WMS (spec 4.3, 6.3); no files.
/// </summary>
public class MapLayerService(AppDbContext db)
{
    /// <summary>The organization-wide layers plus, if given, the site's own ones.</summary>
    public async Task<List<MapLayer>> ListAsync(long? siteId, CancellationToken ct)
    {
        if (siteId is { } id && !await db.Sites.AnyAsync(s => s.Id == id, ct))
            throw AppException.NotFound("site.not_found", $"Site {id} not found.");
        return await db.MapLayers.AsNoTracking()
            .Where(l => l.SiteId == null || l.SiteId == siteId)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .ToListAsync(ct);
    }

    public async Task<MapLayer> GetAsync(long id, CancellationToken ct) =>
        await db.MapLayers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
        ?? throw AppException.NotFound("map_layer.not_found", $"Map layer {id} not found.");

    public async Task<MapLayer> CreateAsync(MapLayerCreate request, CancellationToken ct)
    {
        if (request.SiteId is { } siteId && !await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
        if (request.Source is not (LayerSources.MapManager or LayerSources.ExternalWms))
            throw AppException.BadRequest("map_layer.invalid_source", "Source: mapmanager or external_wms.");

        var layer = new MapLayer
        {
            SiteId = request.SiteId,
            Source = request.Source,
            Name = Required(request.Name, "map_layer.name_required"),
            Layer = Required(request.Layer, "map_layer.layer_required"),
            WmsParams = SiteService.ObjectDocument(request.WmsParams),
            ExternalUrl = request.Source == LayerSources.ExternalWms ? ValidUrl(request.ExternalUrl) : null,
            SortOrder = request.SortOrder ?? 0,
            VisibleDefault = request.VisibleDefault ?? true,
        };
        db.MapLayers.Add(layer);
        await db.SaveChangesAsync(ct);
        return layer;
    }

    public async Task<MapLayer> UpdateAsync(long id, MapLayerUpdate request, CancellationToken ct)
    {
        var layer = await db.MapLayers.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw AppException.NotFound("map_layer.not_found", $"Map layer {id} not found.");
        if (request.Name is not null) layer.Name = Required(request.Name, "map_layer.name_required");
        if (request.Layer is not null) layer.Layer = Required(request.Layer, "map_layer.layer_required");
        if (request.WmsParams is not null) layer.WmsParams = SiteService.ObjectDocument(request.WmsParams);
        if (request.ExternalUrl is not null && layer.Source == LayerSources.ExternalWms) layer.ExternalUrl = ValidUrl(request.ExternalUrl);
        if (request.SortOrder is { } sortOrder) layer.SortOrder = sortOrder;
        if (request.VisibleDefault is { } visible) layer.VisibleDefault = visible;
        await db.SaveChangesAsync(ct);
        return layer;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        if (await db.MapLayers.Where(l => l.Id == id).ExecuteDeleteAsync(ct) == 0)
            throw AppException.NotFound("map_layer.not_found", $"Map layer {id} not found.");
    }

    private static string Required(string? value, string code)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length is 0 or > 500) throw AppException.BadRequest(code, "A value of 1–500 characters is required.");
        return trimmed;
    }

    /// <summary>Absolute http(s) URL without query (the proxy builds the query); internal addresses are refused at connect time.</summary>
    internal static string ValidUrl(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw AppException.BadRequest("map_layer.invalid_url", "externalUrl must be an absolute http(s) URL.");
        return uri.GetLeftPart(UriPartial.Path);
    }
}
