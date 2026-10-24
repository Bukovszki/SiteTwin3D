using System.Text.Json;
using Api.Endpoints.Nodes;
using Api.Model;
using Api.Services;
using Api.Services.Auth;
using Api.Services.Maps;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Api.Endpoints.Maps;

public record MapLayerDto(long Id, long? SiteId, string Source, string Name, string Layer, JsonElement WmsParams, string? ExternalUrl,
    int SortOrder, bool VisibleDefault)
{
    public static MapLayerDto From(MapLayer l) =>
        new(l.Id, l.SiteId, l.Source, l.Name, l.Layer, l.WmsParams.RootElement.Clone(), l.ExternalUrl, l.SortOrder, l.VisibleDefault);
}

public record SyncRequest(string Layer);

public record FloorplanDto(long Id, long LevelId, string Source, string Name, string Layer, string? CqlFilter, JsonElement WmsParams,
    string? ExternalUrl, int SortOrder, float Opacity)
{
    public static FloorplanDto From(Floorplan f) =>
        new(f.Id, f.LevelId, f.Source, f.Name, f.Layer, f.CqlFilter, f.WmsParams.RootElement.Clone(), f.ExternalUrl, f.SortOrder, f.Opacity);
}

/// <summary>Background layers, the WMS proxy and the MapManager link (spec 4.3, 6.3).</summary>
public static class MapEndpoints
{
    public static RouteGroupBuilder MapMapEndpoints(this RouteGroupBuilder api)
    {
        var layers = api.MapGroup("/map-layers").WithTags("Maps");
        layers.MapGet("/", ListLayers).RequireAuthorization(Policies.Member)
            .WithSummary("Background layers of the organization, plus the site's own ones with siteId");
        layers.MapPost("/", CreateLayer).RequireAuthorization(Policies.OrgAdmin)
            .WithSummary("New background layer: reference to a MapManager layer or an external WMS (no files)");
        layers.MapPatch("/{id:long}", UpdateLayer).RequireAuthorization(Policies.OrgAdmin).WithSummary("Partial update");
        layers.MapDelete("/{id:long}", DeleteLayer).RequireAuthorization(Policies.OrgAdmin).WithSummary("Delete");

        api.MapGet("/maps/wms", Wms).WithTags("Maps").RequireAuthorization(Policies.Member)
            .WithSummary("WMS proxy (mapLayerId=… or floorplanId=…): GetMap, GetFeatureInfo, GetLegendGraphic of a configured layer only");

        api.MapGet("/sites/{siteId:long}/floors/{levelId:long}/geometry", FloorGeometry).WithTags("Floorplans").RequireAuthorization(Policies.Member)
            .WithSummary("Vector geometry of a level (own drawing layers + MapManager floorplan features, WGS84 GeoJSON): snap targets; partial=true if MapManager is unreachable");
        api.MapGet("/sites/{siteId:long}/levels", Levels).WithTags("Floorplans").RequireAuthorization(Policies.Member)
            .WithSummary("Levels of the site by elevation, with floorplan and panorama counts (level selector)");
        var floorplans = api.MapGroup("/levels/{levelId:long}/floorplans").WithTags("Floorplans");
        floorplans.MapGet("/", ListFloorplans).RequireAuthorization(Policies.Member).WithSummary("Floorplan layers of a level");
        floorplans.MapPost("/", CreateFloorplan).RequireAuthorization(Policies.Editor)
            .WithSummary("New floorplan: reference to a MapManager publication (layer, optional CQL filter) or an external WMS; no files");
        api.MapPatch("/floorplans/{id:long}", UpdateFloorplan).WithTags("Floorplans").RequireAuthorization(Policies.Editor)
            .WithSummary("Partial update (cqlFilter: null clears it)");
        api.MapDelete("/floorplans/{id:long}", DeleteFloorplan).WithTags("Floorplans").RequireAuthorization(Policies.Editor).WithSummary("Delete");

        api.MapGet("/maps/mapmanager/fields", MapManagerFields).WithTags("MapManager link").RequireAuthorization(Policies.Editor)
            .WithSummary("Fields of a MapManager layer (layer=workspace:layer) before linking; MapManager decides what the token may see");

        var links = api.MapGroup("/sites/{siteId:long}").WithTags("MapManager link");
        links.MapPost("/link-layer", LinkLayer).RequireAuthorization(Policies.Editor)
            .WithSummary("Skeleton records from a MapManager layer (layer, typeId, nameField, parentId); attributes are not copied");
        links.MapPost("/sync-links", Sync).RequireAuthorization(Policies.Editor)
            .WithSummary("Compares a linked layer with the records: new features and orphan records");
        links.MapGet("/linked-layers", LinkedLayers).RequireAuthorization(Policies.Member)
            .WithSummary("MapManager layers referenced by the site's records");
        links.MapGet("/linked-layers/features", LayerGrid).RequireAuthorization(Policies.Member)
            .WithSummary("Grid page of a linked layer (layer=…, filter, sort, dir, first, rows); rows from MapManager with their record");

        api.MapGet("/nodes/{id:long}/linked-attributes", LinkedAttributes).WithTags("MapManager link").RequireAuthorization(Policies.Member)
            .WithSummary("Live attributes of a linked record from MapManager (204 if not linked)");
        return api;
    }

    private static async Task<Ok<List<MapLayerDto>>> ListLayers(long? siteId, MapLayerService layers, CancellationToken ct) =>
        TypedResults.Ok((await layers.ListAsync(siteId, ct)).Select(MapLayerDto.From).ToList());

    private static async Task<Created<MapLayerDto>> CreateLayer(MapLayerCreate request, MapLayerService layers, CancellationToken ct)
    {
        var layer = await layers.CreateAsync(request, ct);
        return TypedResults.Created($"/api/v1/map-layers/{layer.Id}", MapLayerDto.From(layer));
    }

    private static async Task<Ok<MapLayerDto>> UpdateLayer(long id, JsonElement body, IOptions<JsonOptions> json, MapLayerService layers, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new MapLayerUpdate(patch.Get<string>("name"), patch.Get<string>("layer"), patch.Json("wmsParams"),
            patch.Get<string>("externalUrl"), patch.Get<int?>("sortOrder"), patch.Get<bool?>("visibleDefault"));
        return TypedResults.Ok(MapLayerDto.From(await layers.UpdateAsync(id, request, ct)));
    }

    private static async Task<NoContent> DeleteLayer(long id, MapLayerService layers, CancellationToken ct)
    {
        await layers.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Wms(HttpContext context, long? mapLayerId, long? floorplanId, MapLayerService layers,
        FloorplanService floorplans, WmsProxy proxy, CancellationToken ct)
    {
        // Organization-filtered: another organization's layer or floorplan is not found.
        if (floorplanId is { } fid)
        {
            var f = await floorplans.GetAsync(fid, ct);
            return await proxy.ForwardAsync(context, new WmsProxy.WmsTarget(f.Source, f.Layer, f.ExternalUrl, f.WmsParams, f.CqlFilter), ct);
        }
        if (mapLayerId is not { } id)
            throw AppException.BadRequest("maps.layer_required", "mapLayerId or floorplanId is required.");
        var layer = await layers.GetAsync(id, ct);
        return await proxy.ForwardAsync(context, new WmsProxy.WmsTarget(layer.Source, layer.Layer, layer.ExternalUrl, layer.WmsParams, null), ct);
    }

    private static async Task<Ok<FloorGeometryDto>> FloorGeometry(long siteId, long levelId, FloorGeometryService geometry, CancellationToken ct)
    {
        var result = await geometry.GetAsync(siteId, levelId, ct);
        return TypedResults.Ok(new FloorGeometryDto(
            result.Elements.Select(e => new FloorFeatureDto(e.Geometry, new FloorFeatureProperties(e.Source, e.Layer))).ToList(), result.Partial));
    }

    private static async Task<Ok<List<LevelInfo>>> Levels(long siteId, FloorplanService floorplans, CancellationToken ct) =>
        TypedResults.Ok(await floorplans.LevelsAsync(siteId, ct));

    private static async Task<Ok<List<FloorplanDto>>> ListFloorplans(long levelId, FloorplanService floorplans, CancellationToken ct) =>
        TypedResults.Ok((await floorplans.ListAsync(levelId, ct)).Select(FloorplanDto.From).ToList());

    private static async Task<Created<FloorplanDto>> CreateFloorplan(long levelId, FloorplanCreate request, FloorplanService floorplans, CancellationToken ct)
    {
        var f = await floorplans.CreateAsync(levelId, request, ct);
        return TypedResults.Created($"/api/v1/floorplans/{f.Id}", FloorplanDto.From(f));
    }

    private static async Task<Ok<FloorplanDto>> UpdateFloorplan(long id, JsonElement body, IOptions<JsonOptions> json, FloorplanService floorplans, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new FloorplanUpdate(patch.Get<string>("name"), patch.Get<string>("layer"), patch.Get<string>("cqlFilter"), patch.Has("cqlFilter"),
            patch.Json("wmsParams"), patch.Get<string>("externalUrl"), patch.Get<int?>("sortOrder"), patch.Get<float?>("opacity"));
        return TypedResults.Ok(FloorplanDto.From(await floorplans.UpdateAsync(id, request, ct)));
    }

    private static async Task<NoContent> DeleteFloorplan(long id, FloorplanService floorplans, CancellationToken ct)
    {
        await floorplans.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<LayerField>>> MapManagerFields(string layer, LinkService links, CancellationToken ct) =>
        TypedResults.Ok(await links.LayerFieldsAsync(layer, ct));

    private static async Task<Ok<LinkLayerResult>> LinkLayer(long siteId, LinkLayerRequest request, LinkService links, CancellationToken ct) =>
        TypedResults.Ok(await links.LinkLayerAsync(siteId, request, ct));

    private static async Task<Ok<SyncResult>> Sync(long siteId, SyncRequest request, LinkService links, CancellationToken ct) =>
        TypedResults.Ok(await links.SyncAsync(siteId, request.Layer, ct));

    private static async Task<Ok<List<LinkedLayer>>> LinkedLayers(long siteId, LinkService links, CancellationToken ct) =>
        TypedResults.Ok(await links.LinkedLayersAsync(siteId, ct));

    private static async Task<Ok<LayerGridPage>> LayerGrid(long siteId, string layer, string? filter, string? sort, string? dir,
        int? first, int? rows, LinkService links, IOptions<JsonOptions> json, CancellationToken ct) =>
        TypedResults.Ok(await links.GridAsync(siteId, layer, NodeEndpoints.ParseFilters(filter, json), sort, dir == "desc",
            first ?? 0, rows ?? 50, ct));

    private static async Task<Results<Ok<JsonElement>, NoContent>> LinkedAttributes(long id, LinkService links, CancellationToken ct) =>
        await links.LinkedAttributesAsync(id, ct) is { } attributes ? TypedResults.Ok(attributes) : TypedResults.NoContent();
}

public record FloorFeatureProperties(string Source, string Layer);

public record FloorFeatureDto(Geometry Geometry, FloorFeatureProperties Properties)
{
    public string Type => "Feature";
}

/// <summary>GeoJSON FeatureCollection (WGS84) with a <see cref="Partial"/> flag.</summary>
public record FloorGeometryDto(List<FloorFeatureDto> Features, bool Partial)
{
    public string Type => "FeatureCollection";
}
