using System.Text.Json;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Api.Endpoints.Models;

/// <summary>
/// A 3D model; <see cref="TilesetUrl"/> is the API address of its tileset.json, whose tile contents are short-lived
/// signed URLs of the object store. <see cref="Placement"/>: only for a GLB (a 3D Tiles package is georeferenced).
/// </summary>
public record ModelDto(long Id, long SiteId, long? NodeId, string Name, string Kind, bool Segmented, ModelPlacement? Placement,
    string? ElementIdProperty, string TilesetUrl)
{
    public static ModelDto From(ModelAsset m) => new(m.Id, m.SiteId, m.NodeId, ModelService.AttrString(m, "name") ?? $"#{m.Id}", m.Kind,
        m.Segmented, ModelService.PlacementOf(m), ModelService.AttrString(m, "element_id_property"), $"/api/v1/models/{m.Id}/tileset.json");
}

/// <summary>3D models (spec 9, FR-I1): signed upload of a GLB or a 3D Tiles package, placement, tileset with signed URLs.</summary>
public static class ModelEndpoints
{
    public static RouteGroupBuilder MapModelEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/uploads/model", Upload).WithTags("Models").RequireAuthorization(Policies.Editor)
            .WithSummary("Signed PUT URLs for the files of a model: one .glb, or a 3D Tiles package with tileset.json at its root (paths relative to the package)");
        api.MapGet("/sites/{siteId:long}/models", List).WithTags("Models").RequireAuthorization(Policies.Member)
            .WithSummary("3D models of a site; each tileset.json comes with signed tile URLs");
        api.MapPost("/sites/{siteId:long}/models", Create).WithTags("Models").RequireAuthorization(Policies.Editor)
            .WithSummary("Registers an uploaded model (prefix, name, nodeId, placement for a GLB: lon, lat, heightM above the site base, headingDeg)");

        var group = api.MapGroup("/models").WithTags("Models");
        group.MapGet("/{id:long}", Get).RequireAuthorization(Policies.Member);
        group.MapGet("/{id:long}/tileset.json", Tileset).RequireAuthorization(Policies.Member)
            .WithSummary("tileset.json (path=…: a nested one) with short-lived signed tile URLs; a GLB as a one-tile tileset with its placement");
        group.MapPatch("/{id:long}", Update).RequireAuthorization(Policies.Editor)
            .WithSummary("Name, nodeId (null: none), placement (GLB only), elementIdProperty (null: not segmented)");
        group.MapPost("/{id:long}/element-check", CheckElements).RequireAuthorization(Policies.Editor)
            .WithSummary("Import check of the stable element ids: missing ids and ids repeated inside one property table");
        group.MapDelete("/{id:long}", Delete).RequireAuthorization(Policies.Editor)
            .WithSummary("Deletes the model and its files; the boxes stay (they belong to the records, not to the model)");
        return api;
    }

    private static async Task<Ok<ModelUploadTicket>> Upload(ModelUploadRequest request, ModelService models, CancellationToken ct) =>
        TypedResults.Ok(await models.CreateUploadAsync(request, ct));

    private static async Task<Ok<List<ModelDto>>> List(long siteId, ModelService models, CancellationToken ct) =>
        TypedResults.Ok((await models.ListAsync(siteId, ct)).Select(ModelDto.From).ToList());

    private static async Task<Created<ModelDto>> Create(long siteId, ModelCreate request, ModelService models, CancellationToken ct)
    {
        var model = await models.CreateAsync(siteId, request, ct);
        return TypedResults.Created($"/api/v1/models/{model.Id}", ModelDto.From(model));
    }

    private static async Task<Ok<ModelDto>> Get(long id, ModelService models, CancellationToken ct) =>
        TypedResults.Ok(ModelDto.From(await models.GetAsync(id, ct)));

    private static async Task<IResult> Tileset(long id, string? path, ModelService models, HttpContext http, CancellationToken ct)
    {
        var tileset = await models.TilesetAsync(id, path, "/api/v1", ct);
        // The signed URLs inside expire: the browser must not keep the document longer than they live.
        http.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Text(tileset.ToJsonString(), "application/json");
    }

    private static async Task<Ok<ModelDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, ModelService models, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new ModelUpdate(patch.Get<string>("name"), patch.Get<long?>("nodeId"), patch.Has("nodeId"), patch.Get<ModelPlacement>("placement"),
            patch.Get<string>("elementIdProperty"), patch.Has("elementIdProperty"));
        return TypedResults.Ok(ModelDto.From(await models.UpdateAsync(id, request, ct)));
    }

    private static async Task<Ok<ElementCheck>> CheckElements(long id, ModelService models, CancellationToken ct) =>
        TypedResults.Ok(await models.CheckElementsAsync(id, ct));

    private static async Task<NoContent> Delete(long id, ModelService models, CancellationToken ct)
    {
        await models.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }
}
