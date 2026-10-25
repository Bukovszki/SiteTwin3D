using System.Text.Json;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Api.Endpoints.NodeTypes;

/// <summary>
/// Node type; <see cref="Shared"/> base types are read only. <see cref="AttrSchema"/>: the fields that apply in the
/// organization (form, grid, import) – for a shared type its own plus <see cref="ExtensionSchema"/>, the organization's
/// added fields; <see cref="BaseSchema"/>: the type's own fields only.
/// </summary>
public record NodeTypeDto(long Id, string Code, string Name, string Aspect, bool IsLevel, bool Shared, JsonElement? AttrSchema,
    JsonElement? BaseSchema, JsonElement? ExtensionSchema)
{
    public static NodeTypeDto From(NodeTypeView v) =>
        new(v.Type.Id, v.Type.Code, v.Type.Name, v.Type.Aspect, v.Type.IsLevel, v.Type.OrganizationId is null,
            v.Schema?.RootElement.Clone(), v.Type.AttrSchema?.RootElement.Clone(), v.Extension?.RootElement.Clone());

    public static NodeTypeDto From(NodeType t) => From(new NodeTypeView(t, null));
}

/// <summary>The organization's own fields of a shared base type (JSON Schema; null or no properties: removed).</summary>
public record NodeTypeExtensionRequest(JsonElement? AttrSchema);

public static class NodeTypeEndpoints
{
    public static RouteGroupBuilder MapNodeTypeEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/node-types").WithTags("Node types");

        group.MapGet("/", List).RequireAuthorization(Policies.Member).WithSummary("Shared base types and the organization's own types");
        group.MapPost("/", Create).RequireAuthorization(Policies.OrgAdmin)
            .WithSummary("New node type with optional JSON Schema for the technical data (no code change needed)");
        group.MapPatch("/{id:long}", Update).RequireAuthorization(Policies.OrgAdmin)
            .WithSummary("Name and attrSchema of an own type (null removes the schema)");
        group.MapDelete("/{id:long}", Delete).RequireAuthorization(Policies.OrgAdmin).WithSummary("Delete an unused own type");
        group.MapPut("/{id:long}/extension", SetExtension).RequireAuthorization(Policies.OrgAdmin)
            .WithSummary("The organization's own fields added to a shared base type (its records get them after the base fields)");
        return group;
    }

    private static async Task<Ok<List<NodeTypeDto>>> List(NodeTypeService types, CancellationToken ct) =>
        TypedResults.Ok((await types.ListAsync(ct)).Select(NodeTypeDto.From).ToList());

    private static async Task<Created<NodeTypeDto>> Create(NodeTypeCreate request, NodeTypeService types, CancellationToken ct)
    {
        var type = await types.CreateAsync(request, ct);
        return TypedResults.Created($"/api/v1/node-types/{type.Id}", NodeTypeDto.From(type));
    }

    private static async Task<Ok<NodeTypeDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, NodeTypeService types, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new NodeTypeUpdate(patch.Get<string>("name"), patch.Json("attrSchema"), patch.Has("attrSchema"));
        return TypedResults.Ok(NodeTypeDto.From(await types.UpdateAsync(id, request, ct)));
    }

    private static async Task<Ok<NodeTypeDto>> SetExtension(long id, NodeTypeExtensionRequest request, NodeTypeService types, CancellationToken ct) =>
        TypedResults.Ok(NodeTypeDto.From(await types.SetExtensionAsync(id, request.AttrSchema, ct)));

    private static async Task<NoContent> Delete(long id, NodeTypeService types, CancellationToken ct)
    {
        await types.DeleteAsync(id, ct);
        return TypedResults.NoContent();
    }
}
