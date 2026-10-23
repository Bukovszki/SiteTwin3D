using System.Globalization;
using System.Text.Json;
using Api.Model;
using Api.Services;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Api.Endpoints.Sites;

/// <summary>Site with reference point and base height; geometries as GeoJSON in EPSG:4326.</summary>
public record SiteDto(long Id, string Name, Point RefPoint, double BaseHeightM, Polygon? Extent, JsonElement Attrs)
{
    public static SiteDto From(Site s) => new(s.Id, s.Name, s.RefPoint, s.BaseHeightM, s.Extent, s.Attrs.RootElement.Clone());
}

public static class SiteEndpoints
{
    public static RouteGroupBuilder MapSiteEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/sites").WithTags("Sites");

        group.MapGet("/", List).RequireAuthorization(Policies.Member)
            .WithSummary("Sites of the organization; bbox=minLon,minLat,maxLon,maxLat (EPSG:4326) limits to a map area");
        group.MapGet("/{id:long}", Get).RequireAuthorization(Policies.Member).WithSummary("Site with reference point and base height");
        group.MapPost("/", Create).RequireAuthorization(Policies.OrgAdmin).WithSummary("New site");
        group.MapPatch("/{id:long}", Update).RequireAuthorization(Policies.OrgAdmin)
            .WithSummary("Partial update (name, refPoint, baseHeightM, extent – null clears it –, attrs)");
        group.MapDelete("/{id:long}", Delete).RequireAuthorization(Policies.OrgAdmin)
            .WithSummary("Delete with the whole hierarchy (409 while panoramas reference its nodes)");
        return group;
    }

    private static async Task<Ok<List<SiteDto>>> List(SiteService sites, string? bbox, CancellationToken ct) =>
        TypedResults.Ok((await sites.ListAsync(ParseBbox(bbox), ct)).Select(SiteDto.From).ToList());

    private static async Task<Ok<SiteDto>> Get(long id, SiteService sites, CancellationToken ct) =>
        TypedResults.Ok(SiteDto.From(await sites.GetAsync(id, ct)));

    private static async Task<Created<SiteDto>> Create(SiteCreate request, SiteService sites, CancellationToken ct)
    {
        var site = await sites.CreateAsync(request, ct);
        return TypedResults.Created($"/api/v1/sites/{site.Id}", SiteDto.From(site));
    }

    private static async Task<Ok<SiteDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, SiteService sites, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new SiteUpdate(
            Name: patch.Get<string>("name"),
            RefPoint: patch.Get<Point>("refPoint"),
            BaseHeightM: patch.Get<double?>("baseHeightM"),
            Extent: patch.Get<Polygon>("extent"),
            ExtentSet: patch.Has("extent"),
            Attrs: patch.Json("attrs"));
        return TypedResults.Ok(SiteDto.From(await sites.UpdateAsync(id, request, ct)));
    }

    private static async Task<NoContent> Delete(long id, SiteService sites, CancellationToken ct)
    {
        await sites.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static double[]? ParseBbox(string? bbox)
    {
        if (string.IsNullOrWhiteSpace(bbox)) return null;
        var parts = bbox.Split(',');
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                throw AppException.BadRequest("site.invalid_bbox", "bbox: minLon,minLat,maxLon,maxLat (EPSG:4326).");
        return values;
    }
}
