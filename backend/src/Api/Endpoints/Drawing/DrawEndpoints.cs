using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Model;
using Api.Services;
using Api.Services.Auth;
using Api.Services.Drawing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Api.Endpoints.Drawing;

public record DrawLayerDto(long Id, long SiteId, long? LevelId, string Name, string GeometryType, List<DrawField> Fields, JsonElement Style, int SortOrder)
{
    public static DrawLayerDto From(DrawLayer l) =>
        new(l.Id, l.SiteId, l.LevelId, l.Name, l.GeometryType, DrawService.Fields(l), l.Style.RootElement.Clone(), l.SortOrder);
}

/// <summary>Drawing layers and their elements (spec 8.6, 10).</summary>
public static class DrawEndpoints
{
    public static RouteGroupBuilder MapDrawEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/draw-layers").WithTags("Drawing");
        group.MapGet("/", List).RequireAuthorization(Policies.Member)
            .WithSummary("Drawing layers of a site: levelId=… for a level, outdoor=true for site-level ones");
        group.MapPost("/", Create).RequireAuthorization(Policies.Editor)
            .WithSummary("New layer: one geometry type (Point, MultiLineString, MultiPolygon) and its fields (name, type, length, decimals, alias)");
        group.MapPatch("/{id:long}", Update).RequireAuthorization(Policies.Editor).WithSummary("Name, fields, style, order");
        group.MapDelete("/{id:long}", Delete).RequireAuthorization(Policies.Editor).WithSummary("Delete with all its elements");
        group.MapGet("/{id:long}/features", Features).RequireAuthorization(Policies.Member)
            .WithSummary("Elements as GeoJSON in EPSG:3857 with record version; bbox=minX,minY,maxX,maxY (EPSG:3857)");
        group.MapPost("/{id:long}/transaction", Transaction).RequireAuthorization(Policies.Editor)
            .WithSummary("Inserts, updates, deletes (GeoJSON in EPSG:3857) in one transaction: 400 with every error, 409 on stale versions; nothing saved then");
        return api;
    }

    private static async Task<Ok<List<DrawLayerDto>>> List(long siteId, long? levelId, bool? outdoor, DrawService draw, CancellationToken ct) =>
        TypedResults.Ok((await draw.LayersAsync(siteId, levelId, outdoor ?? false, ct)).Select(DrawLayerDto.From).ToList());

    private static async Task<Created<DrawLayerDto>> Create(DrawLayerCreate request, DrawService draw, CancellationToken ct)
    {
        var layer = await draw.CreateLayerAsync(request, ct);
        return TypedResults.Created($"/api/v1/draw-layers/{layer.Id}", DrawLayerDto.From(layer));
    }

    private static async Task<Ok<DrawLayerDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, DrawService draw, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new DrawLayerUpdate(patch.Get<string>("name"), patch.Get<List<DrawField>>("fields"), patch.Json("style"), patch.Get<int?>("sortOrder"));
        return TypedResults.Ok(DrawLayerDto.From(await draw.UpdateLayerAsync(id, request, ct)));
    }

    private static async Task<NoContent> Delete(long id, DrawService draw, CancellationToken ct)
    {
        await draw.DeleteLayerAsync(id, ct);
        return TypedResults.NoContent();
    }

    /// <summary>A GeoJSON FeatureCollection; each feature carries its record id, version and optional node link.</summary>
    private static async Task<IResult> Features(long id, string? bbox, DrawService draw, IOptions<JsonOptions> json, CancellationToken ct)
    {
        double[]? box = null;
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parts = bbox.Split(',');
            box = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out box[i]))
                    throw AppException.BadRequest("draw.invalid_bbox", "bbox: minX,minY,maxX,maxY (EPSG:3857).");
            if (box.Length != 4) throw AppException.BadRequest("draw.invalid_bbox", "bbox: minX,minY,maxX,maxY (EPSG:3857).");
        }
        var features = await draw.FeaturesAsync(id, box, ct);
        var options = json.Value.SerializerOptions;
        var collection = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["crs"] = new JsonObject { ["type"] = "name", ["properties"] = new JsonObject { ["name"] = "EPSG:3857" } },
            ["features"] = new JsonArray(features.Select(f => (JsonNode)new JsonObject
            {
                ["type"] = "Feature",
                ["id"] = f.Id,
                ["geometry"] = JsonSerializer.SerializeToNode(f.Geometry, options),
                ["properties"] = JsonNode.Parse(f.Attrs.GetRawText()),
                ["seq"] = f.Seq,
                ["version"] = f.Version,
                ["nodeId"] = f.NodeId,
            }).ToArray()),
        };
        return TypedResults.Text(collection.ToJsonString(), "application/geo+json");
    }

    private static async Task<Ok<DrawTransactionResult>> Transaction(long id, DrawTransaction tx, DrawService draw, CancellationToken ct) =>
        TypedResults.Ok(await draw.ApplyAsync(id, tx, ct));
}
