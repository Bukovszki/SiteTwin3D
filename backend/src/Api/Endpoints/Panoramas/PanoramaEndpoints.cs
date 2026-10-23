using System.Text.Json;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Panoramas;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Api.Endpoints.Panoramas;

/// <summary>A panorama; <see cref="ImageUrl"/> (detail only) is a short-lived signed URL of the display version, or of the original during review (editors).</summary>
public record PanoramaDto(long Id, long NodeId, Point Position, long? LevelId, float ZRelM, float? HeadingDeg, DateTime? CapturedAt,
    string PrivacyStatus, string? ProcessingError, List<PrivacyRegion> PrivacyRegions, int? Width, int? Height,
    long? SiteId = null, double? AbsoluteHeightM = null, string? ImageUrl = null, bool ImageIsOriginal = false)
{
    public static PanoramaDto From(Panorama p) => new(p.Id, p.NodeId, p.Position, p.LevelId, p.ZRelM, p.HeadingDeg, p.CapturedAt,
        p.PrivacyStatus, p.ProcessingError, p.PrivacyRegions.Deserialize<List<PrivacyRegion>>(PanoramaProcessor.Json) ?? [], p.Width, p.Height);

    public static PanoramaDto From(PanoramaView v) => From(v.Panorama) with
    {
        SiteId = v.SiteId, AbsoluteHeightM = v.AbsoluteHeightM, ImageUrl = v.ImageUrl, ImageIsOriginal = v.ImageIsOriginal,
    };
}

public record RegionsRequest(List<PrivacyRegion> Regions);

/// <summary>Panoramas (spec 4.4, 10): signed upload, metadata, placement, privacy review (FR-P8).</summary>
public static class PanoramaEndpoints
{
    public static RouteGroupBuilder MapPanoramaEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/uploads/panorama", Upload).WithTags("Panoramas").RequireAuthorization(Policies.Editor)
            .WithSummary("Signed PUT URL for an equirectangular JPEG (straight to the object store; the API never sees the bytes)");
        api.MapGet("/sites/{siteId:long}/panoramas", List).WithTags("Panoramas").RequireAuthorization(Policies.Member)
            .WithSummary("Panoramas of a site: levelId=… for a level, outdoor=true for outdoor ones; viewers see approved ones only");
        api.MapGet("/nodes/{id:long}/panoramas", Subtree).WithTags("Panoramas").RequireAuthorization(Policies.Member)
            .WithSummary("Panoramas of a node; recursive=true: of its whole subtree (FR-P3)");

        var group = api.MapGroup("/panoramas").WithTags("Panoramas");
        group.MapPost("/", Create).RequireAuthorization(Policies.Editor)
            .WithSummary("Registers an uploaded panorama (uploadKey, sha256, nodeId, position, levelId, zRelM, headingDeg); analysis starts in the background");
        group.MapGet("/{id:long}", Get).RequireAuthorization(Policies.Member)
            .WithSummary("Panorama with absolute height and a short-lived image URL");
        group.MapPatch("/{id:long}", Update).RequireAuthorization(Policies.Editor)
            .WithSummary("Placement: position, levelId (null: outdoor), zRelM, headingDeg (null: not calibrated), nodeId, capturedAt");
        group.MapDelete("/{id:long}", Delete).RequireAuthorization(Policies.Editor).WithSummary("Deletes the panorama and its files");
        group.MapPut("/{id:long}/privacy-regions", SetRegions).RequireAuthorization(Policies.Editor)
            .WithSummary("Reviewed regions to blur (suggested ones accepted / rejected, manual ones added); only in review");
        group.MapPost("/{id:long}/approve", Approve).RequireAuthorization(Policies.Editor)
            .WithSummary("Approves the review: the blurred display version is rendered, then viewers see the panorama");
        return api;
    }

    private static async Task<Ok<UploadTicket>> Upload(UploadRequest request, PanoramaService panoramas, CancellationToken ct) =>
        TypedResults.Ok(await panoramas.CreateUploadAsync(request, ct));

    private static async Task<Ok<List<PanoramaDto>>> List(long siteId, long? levelId, bool? outdoor, PanoramaService panoramas, CancellationToken ct) =>
        TypedResults.Ok((await panoramas.ListAsync(siteId, levelId, outdoor ?? false, ct)).Select(PanoramaDto.From).ToList());

    private static async Task<Ok<List<PanoramaDto>>> Subtree(long id, bool? recursive, PanoramaService panoramas, CancellationToken ct) =>
        TypedResults.Ok((await panoramas.SubtreeAsync(id, recursive ?? false, ct)).Select(PanoramaDto.From).ToList());

    private static async Task<Created<PanoramaDto>> Create(PanoramaCreate request, PanoramaService panoramas, CancellationToken ct)
    {
        var panorama = await panoramas.CreateAsync(request, ct);
        return TypedResults.Created($"/api/v1/panoramas/{panorama.Id}", PanoramaDto.From(panorama));
    }

    private static async Task<Ok<PanoramaDto>> Get(long id, PanoramaService panoramas, CancellationToken ct) =>
        TypedResults.Ok(PanoramaDto.From(await panoramas.GetAsync(id, ct)));

    private static async Task<Ok<PanoramaDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, PanoramaService panoramas, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new PanoramaUpdate(
            NodeId: patch.Get<long?>("nodeId"),
            Position: patch.Get<Point>("position"),
            LevelId: patch.Get<long?>("levelId"),
            LevelIdSet: patch.Has("levelId"),
            ZRelM: patch.Get<float?>("zRelM"),
            HeadingDeg: patch.Get<float?>("headingDeg"),
            HeadingDegSet: patch.Has("headingDeg"),
            CapturedAt: patch.Get<DateTime?>("capturedAt"));
        return TypedResults.Ok(PanoramaDto.From(await panoramas.UpdateAsync(id, request, ct)));
    }

    private static async Task<NoContent> Delete(long id, PanoramaService panoramas, CancellationToken ct)
    {
        await panoramas.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<PanoramaDto>> SetRegions(long id, RegionsRequest request, PanoramaService panoramas, CancellationToken ct) =>
        TypedResults.Ok(PanoramaDto.From(await panoramas.SetRegionsAsync(id, request.Regions ?? [], ct)));

    private static async Task<Ok<PanoramaDto>> Approve(long id, PanoramaService panoramas, CancellationToken ct) =>
        TypedResults.Ok(PanoramaDto.From(await panoramas.ApproveAsync(id, ct)));
}
