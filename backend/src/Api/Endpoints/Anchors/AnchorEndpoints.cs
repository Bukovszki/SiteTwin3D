using System.Text.Json;
using Api.Services.Anchors;
using Api.Services.Auth;
using Api.Services.Panoramas;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Api.Endpoints.Anchors;

/// <summary>
/// An anchor of a node with the node's label; <see cref="PositionSource"/>: manual | panorama_fallback. A box
/// (kind region, shape box): centre = position + zRelM above the level floor, sizes in metres, heading from north.
/// </summary>
public record AnchorDto(long Id, long NodeId, string NodeName, string? NodeCode, string TypeCode, string Kind, long? PanoramaId, float? YawDeg,
    float? PitchDeg, Point Position, long? LevelId, float ZRelM, string? PositionSource, string? RegionShape, float? SizeX, float? SizeY,
    float? SizeZ, float? HeadingDeg, long? ModelAssetId, string? FeatureRef)
{
    public static AnchorDto From(AnchorView v) => new(v.Anchor.Id, v.Anchor.NodeId, v.NodeName, v.NodeCode, v.TypeCode, v.Anchor.Kind,
        v.Anchor.PanoramaId, v.Anchor.YawDeg, v.Anchor.PitchDeg, v.Anchor.Position, v.Anchor.LevelId, v.Anchor.ZRelM,
        v.Anchor.Attrs.RootElement.TryGetProperty("position_source", out var s) ? s.GetString() : null,
        v.Anchor.RegionShape, v.Anchor.SizeX, v.Anchor.SizeY, v.Anchor.SizeZ, v.Anchor.HeadingDeg, v.Anchor.ModelAssetId, v.Anchor.FeatureRef);
}

public record LinkRequest(long ToId);

/// <summary>Anchors (FR-A) and the walk-through neighbourhood of panoramas (FR-P4).</summary>
public static class AnchorEndpoints
{
    public static RouteGroupBuilder MapAnchorEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/nodes/{id:long}/anchors", OfNode).WithTags("Anchors").RequireAuthorization(Policies.Member).WithSummary("Anchors of a node");
        api.MapPost("/nodes/{id:long}/anchors", Create).WithTags("Anchors").RequireAuthorization(Policies.Editor)
            .WithSummary("New anchor: point; info point (panorama_point: panoramaId, yawDeg, pitchDeg in image angles) with its approximate place on the floorplan; box (region: sizeX/Y/Z m, headingDeg, centre = position + zRelM); model element (model_feature: modelAssetId, featureRef = stable element id)");
        api.MapGet("/panoramas/{id:long}/anchors", OfPanorama).WithTags("Anchors").RequireAuthorization(Policies.Member)
            .WithSummary("Info points of a panorama");
        api.MapGet("/sites/{siteId:long}/anchors", OfLevel).WithTags("Anchors").RequireAuthorization(Policies.Member)
            .WithSummary("Anchors of a level (levelId=…) or outdoor ones (no levelId), for the floorplan; allLevels=true: the whole site; kind=…: only that kind");
        api.MapPatch("/anchors/{id:long}", Update).WithTags("Anchors").RequireAuthorization(Policies.Editor)
            .WithSummary("Move an anchor (position, levelId, zRelM; info points also yawDeg, pitchDeg; boxes also sizeX/Y/Z, headingDeg)");
        api.MapDelete("/anchors/{id:long}", Delete).WithTags("Anchors").RequireAuthorization(Policies.Editor).WithSummary("Delete an anchor");

        api.MapGet("/panoramas/{id:long}/links", Neighbours).WithTags("Panoramas").RequireAuthorization(Policies.Member)
            .WithSummary("Neighbours for the walk-through, with real distance (m) and bearing (° from north)");
        api.MapPost("/panoramas/{id:long}/links", Link).WithTags("Panoramas").RequireAuthorization(Policies.Editor)
            .WithSummary("Links two panoramas (both ways)");
        api.MapDelete("/panoramas/{id:long}/links/{toId:long}", Unlink).WithTags("Panoramas").RequireAuthorization(Policies.Editor)
            .WithSummary("Removes the link (both ways)");
        api.MapPost("/sites/{siteId:long}/panorama-links/auto", AutoLink).WithTags("Panoramas").RequireAuthorization(Policies.Editor)
            .WithSummary("Suggests the neighbourhood of a level: nearest panoramas within maxDistanceM (default 15 m); existing links stay");
        return api;
    }

    private static async Task<Ok<List<AnchorDto>>> OfNode(long id, AnchorService anchors, CancellationToken ct) =>
        TypedResults.Ok((await anchors.OfNodeAsync(id, ct)).Select(AnchorDto.From).ToList());

    private static async Task<Created<AnchorDto>> Create(long id, AnchorCreate request, AnchorService anchors, CancellationToken ct)
    {
        var anchor = await anchors.CreateAsync(id, request, ct);
        return TypedResults.Created($"/api/v1/anchors/{anchor.Anchor.Id}", AnchorDto.From(anchor));
    }

    private static async Task<Ok<List<AnchorDto>>> OfPanorama(long id, AnchorService anchors, PanoramaService panoramas, CancellationToken ct)
    {
        await panoramas.GetAsync(id, ct); // 404 if not visible
        return TypedResults.Ok((await anchors.OfPanoramaAsync(id, ct)).Select(AnchorDto.From).ToList());
    }

    private static async Task<Ok<List<AnchorDto>>> OfLevel(long siteId, long? levelId, bool? allLevels, string? kind, AnchorService anchors,
        CancellationToken ct) =>
        TypedResults.Ok((await anchors.OfLevelAsync(siteId, levelId, allLevels ?? false, kind, ct)).Select(AnchorDto.From).ToList());

    private static async Task<Ok<AnchorDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, AnchorService anchors, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new AnchorUpdate(patch.Get<Point>("position"), patch.Get<long?>("levelId"), patch.Has("levelId"), patch.Get<float?>("zRelM"),
            patch.Get<float?>("yawDeg"), patch.Get<float?>("pitchDeg"), patch.Get<string>("positionSource"), patch.Get<float?>("sizeX"),
            patch.Get<float?>("sizeY"), patch.Get<float?>("sizeZ"), patch.Get<float?>("headingDeg"));
        return TypedResults.Ok(AnchorDto.From(await anchors.UpdateAsync(id, request, ct)));
    }

    private static async Task<NoContent> Delete(long id, AnchorService anchors, CancellationToken ct)
    {
        await anchors.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<PanoramaNeighbour>>> Neighbours(long id, PanoramaLinkService links, CancellationToken ct) =>
        TypedResults.Ok(await links.NeighboursAsync(id, ct));

    private static async Task<NoContent> Link(long id, LinkRequest request, PanoramaLinkService links, CancellationToken ct)
    {
        await links.LinkAsync(id, request.ToId, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Unlink(long id, long toId, PanoramaLinkService links, CancellationToken ct)
    {
        await links.UnlinkAsync(id, toId, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<int>> AutoLink(long siteId, AutoLinkRequest request, PanoramaLinkService links, CancellationToken ct) =>
        TypedResults.Ok(await links.AutoLinkAsync(siteId, request, ct));
}
