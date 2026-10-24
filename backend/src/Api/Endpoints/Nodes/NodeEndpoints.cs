using System.Text.Json;
using Api.Model;
using Api.Services;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Api.Services.Locations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace Api.Endpoints.Nodes;

/// <summary>A node of the hierarchy; <see cref="Path"/> is the dot-separated id chain from the root.</summary>
public record NodeDto(long Id, long SiteId, long? ParentId, string Path, int Depth, long TypeId, string TypeCode, string Aspect,
    string? Code, string Name, int SortOrder, Geometry? Geom, JsonElement Attrs)
{
    public static NodeDto From(Node n) => new(n.Id, n.SiteId, n.ParentId, n.Path.ToString(), NodeService.Depth(n.Path), n.TypeId, n.Type.Code,
        n.Type.Aspect, n.Code, n.Name, n.SortOrder, n.Geom, n.Attrs.RootElement.Clone());
}

/// <summary>Grid row: the node plus the kinds of its anchors (FR-G4: what is still unassigned).</summary>
public record GridRowDto(NodeDto Node, List<string> AnchorKinds, bool Linked);

public record GridPageDto(int Total, List<GridRowDto> Rows);

public record LocationRequest(long? LocationId);

public record RelationDto(long FromId, long ToId, string Kind)
{
    public static RelationDto From(NodeRelation r) => new(r.FromId, r.ToId, r.Kind);
}

/// <summary>Hierarchy (spec 4.1, 10): trees, nodes, moves and relations.</summary>
public static class NodeEndpoints
{
    public static RouteGroupBuilder MapNodeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/sites/{siteId:long}/nodes", Tree).WithTags("Nodes").RequireAuthorization(Policies.Member)
            .WithSummary("Tree or subtree, flat (build it from parentId): aspect filters the tree, root the subtree, depth limits the levels");

        api.MapPost("/sites/{siteId:long}/import/attributes", Import).WithTags("Nodes").RequireAuthorization(Policies.Editor)
            .WithSummary("Bulk import of technical data (rows read from CSV/XLSX by the client): all or nothing, every error at once");

        var group = api.MapGroup("/nodes").WithTags("Nodes");
        group.MapGet("/", Grid).RequireAuthorization(Policies.Member)
            .WithSummary("Grid: siteId, typeId, search, filter (JSON array of {field, op, value}; field: name, code, attrs.<x>; " +
                "op: contains, eq, gte, lte, empty), sort (name, code, attrs.<x>), dir (asc|desc), first, rows");
        group.MapGet("/{id:long}", Get).RequireAuthorization(Policies.Member).WithSummary("Node details");
        group.MapGet("/{id:long}/ancestors", Ancestors).RequireAuthorization(Policies.Member)
            .WithSummary("Ancestors from the root to the parent (breadcrumb)");
        group.MapPost("/", Create).RequireAuthorization(Policies.Editor)
            .WithSummary("New node; attrs are validated against the node type's JSON Schema");
        group.MapPatch("/{id:long}", Update).RequireAuthorization(Policies.Editor)
            .WithSummary("Partial update; parentId moves the node with its subtree (null: to the root). A cycle is rejected (409 node.cycle)");
        group.MapDelete("/{id:long}", Delete).RequireAuthorization(Policies.Editor)
            .WithSummary("Delete; a node with children only with recursive=true (deletes the subtree and its anchors)");

        group.MapGet("/{id:long}/relations", Relations).RequireAuthorization(Policies.Member)
            .WithSummary("Relations of the node in both directions");
        group.MapPost("/{id:long}/relations", AddRelation).RequireAuthorization(Policies.Editor)
            .WithSummary("New relation from this node (kind e.g. located_in, connected_to, feeds)");
        group.MapDelete("/{id:long}/relations", DeleteRelation).RequireAuthorization(Policies.Editor)
            .WithSummary("Delete the relation from this node to toId of the given kind");
        group.MapGet("/{id:long}/members", Members).RequireAuthorization(Policies.Member)
            .WithSummary("The node and its whole subtree (e.g. a process line) with where each element is: located_in relation, else the smallest location box containing its anchor");
        group.MapPut("/{id:long}/location", SetLocation).RequireAuthorization(Policies.Editor)
            .WithSummary("Sets the explicit place of the node (its single located_in relation); locationId null clears it");
        return api;
    }

    private static async Task<Ok<List<NodeDto>>> Tree(long siteId, string? aspect, long? root, int? depth, NodeService nodes, CancellationToken ct) =>
        TypedResults.Ok((await nodes.TreeAsync(siteId, aspect, root, depth, ct)).Select(NodeDto.From).ToList());

    private static async Task<Ok<GridPageDto>> Grid(long siteId, long? typeId, string? search, string? filter, string? sort, string? dir,
        int? first, int? rows, NodeGridService grid, IOptions<JsonOptions> json, CancellationToken ct)
    {
        var page = await grid.QueryAsync(new GridQuery(siteId, typeId, search, ParseFilters(filter, json), sort, dir == "desc",
            first ?? 0, rows ?? 50), ct);
        return TypedResults.Ok(new GridPageDto(page.Total, page.Rows.Select(r =>
            new GridRowDto(NodeDto.From(r.Node), r.AnchorKinds, AttrValidator.IsLinked(r.Node.Attrs.RootElement))).ToList()));
    }

    private static async Task<Ok<ImportResult>> Import(long siteId, ImportRequest request, ImportService import, CancellationToken ct) =>
        TypedResults.Ok(await import.ImportAsync(siteId, request, ct));

    /// <summary>The filter query parameter: a JSON array of {field, op, value}.</summary>
    public static List<GridFilter> ParseFilters(string? filter, IOptions<JsonOptions> json)
    {
        if (string.IsNullOrWhiteSpace(filter)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<GridFilter>>(filter, json.Value.SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            throw AppException.BadRequest("grid.invalid_filter", "filter must be a JSON array of {field, op, value}.");
        }
    }

    private static async Task<Ok<NodeDto>> Get(long id, NodeService nodes, CancellationToken ct) =>
        TypedResults.Ok(NodeDto.From(await nodes.GetAsync(id, ct)));

    private static async Task<Ok<List<NodeDto>>> Ancestors(long id, NodeService nodes, CancellationToken ct) =>
        TypedResults.Ok((await nodes.AncestorsAsync(id, ct)).Select(NodeDto.From).ToList());

    private static async Task<Created<NodeDto>> Create(NodeCreate request, NodeService nodes, CancellationToken ct)
    {
        var node = await nodes.CreateAsync(request, ct);
        return TypedResults.Created($"/api/v1/nodes/{node.Id}", NodeDto.From(node));
    }

    private static async Task<Ok<NodeDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, NodeService nodes, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var request = new NodeUpdate(
            Name: patch.Get<string>("name"),
            Code: patch.Get<string>("code"),
            CodeSet: patch.Has("code"),
            SortOrder: patch.Get<int?>("sortOrder"),
            TypeId: patch.Get<long?>("typeId"),
            ParentId: patch.Get<long?>("parentId"),
            ParentIdSet: patch.Has("parentId"),
            Geom: patch.Get<Geometry>("geom"),
            GeomSet: patch.Has("geom"),
            Attrs: patch.Json("attrs"));
        return TypedResults.Ok(NodeDto.From(await nodes.UpdateAsync(id, request, ct)));
    }

    private static async Task<NoContent> Delete(long id, bool? recursive, NodeService nodes, CancellationToken ct)
    {
        await nodes.DeleteAsync(id, recursive ?? false, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<RelationDto>>> Relations(long id, NodeService nodes, CancellationToken ct) =>
        TypedResults.Ok((await nodes.RelationsAsync(id, ct)).Select(RelationDto.From).ToList());

    private static async Task<Created<RelationDto>> AddRelation(long id, RelationCreate request, NodeService nodes, CancellationToken ct)
    {
        var relation = await nodes.AddRelationAsync(id, request, ct);
        return TypedResults.Created($"/api/v1/nodes/{id}/relations", RelationDto.From(relation));
    }

    private static async Task<Ok<NodeMembers>> Members(long id, LocationService locations, CancellationToken ct) =>
        TypedResults.Ok(await locations.MembersAsync(id, ct));

    private static async Task<NoContent> SetLocation(long id, LocationRequest request, LocationService locations, CancellationToken ct)
    {
        await locations.SetLocationAsync(id, request.LocationId, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteRelation(long id, long toId, string kind, NodeService nodes, CancellationToken ct)
    {
        await nodes.DeleteRelationAsync(id, toId, kind, ct);
        return TypedResults.NoContent();
    }
}
