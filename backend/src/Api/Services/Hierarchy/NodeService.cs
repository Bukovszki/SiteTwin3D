using System.Text.Json;
using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Services.Hierarchy;

public record NodeCreate(long SiteId, long? ParentId, long TypeId, string? Code, string Name, int? SortOrder, Geometry? Geom, JsonElement? Attrs);

/// <summary>
/// Partial update of a node. A null property means "not sent"; <see cref="ParentIdSet"/> distinguishes
/// "move to root" (ParentId null, ParentIdSet true) from "parent unchanged".
/// </summary>
public record NodeUpdate(
    string? Name = null, string? Code = null, bool CodeSet = false, int? SortOrder = null, long? TypeId = null,
    long? ParentId = null, bool ParentIdSet = false, Geometry? Geom = null, bool GeomSet = false, JsonElement? Attrs = null);

public record RelationCreate(long ToId, string Kind);

/// <summary>
/// The hierarchy (spec 4.1, 8.1): one table, several trees per site, separated by the node types' aspect.
/// The path (ltree) is maintained by database triggers; the database also rejects cycles (node_no_cycle) and parents
/// on another site (fk_node_parent_same_site). Every query is organization-filtered: another organization's node is
/// simply not found (404).
/// </summary>
public class NodeService(AppDbContext db, Auth.CurrentUser currentUser, NodeTypeService types)
{
    public const int MaxNameLength = 200;

    public async Task<Node> GetAsync(long id, CancellationToken ct) =>
        await db.Nodes.AsNoTracking().Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == id, ct)
        ?? throw AppException.NotFound("node.not_found", $"Node {id} not found.");

    /// <summary>
    /// A tree or subtree of a site, flat, ordered by depth, then sort order and name (the client builds the tree
    /// from parentId). <paramref name="depth"/> counts from the root(s): 0 = only the root(s).
    /// </summary>
    public async Task<List<Node>> TreeAsync(long siteId, string? aspect, long? rootId, int? depth, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");

        var query = db.Nodes.AsNoTracking().Include(n => n.Type).Where(n => n.SiteId == siteId);
        if (!string.IsNullOrEmpty(aspect)) query = query.Where(n => n.Type.Aspect == aspect);

        var baseLevel = 1;
        if (rootId is { } r)
        {
            var root = await db.Nodes.AsNoTracking().Where(n => n.Id == r && n.SiteId == siteId).Select(n => (LTree?)n.Path).FirstOrDefaultAsync(ct)
                ?? throw AppException.NotFound("node.not_found", $"Node {r} not found on site {siteId}.");
            query = query.Where(n => n.Path.IsDescendantOf(root));
            baseLevel = Depth(root);
        }
        if (depth is { } d)
        {
            var maxLevel = baseLevel + Math.Max(0, d);
            query = query.Where(n => n.Path.NLevel <= maxLevel);
        }
        return await query.OrderBy(n => n.Path.NLevel).ThenBy(n => n.SortOrder).ThenBy(n => n.Name).ToListAsync(ct);
    }

    /// <summary>Ancestors from the root down to the parent (breadcrumb, FR-H7); the node itself is not included.</summary>
    public async Task<List<Node>> AncestorsAsync(long id, CancellationToken ct)
    {
        var node = await GetAsync(id, ct);
        var path = node.Path;
        return await db.Nodes.AsNoTracking().Include(n => n.Type)
            .Where(n => n.Path.IsAncestorOf(path) && n.Id != id)
            .OrderBy(n => n.Path.NLevel)
            .ToListAsync(ct);
    }

    public async Task<Node> CreateAsync(NodeCreate request, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == request.SiteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {request.SiteId} not found.");
        var type = await FindTypeAsync(request.TypeId, ct);
        await CheckParentAsync(request.ParentId, request.SiteId, type, ct);

        var attrs = request.Attrs ?? EmptyObject();
        AttrValidator.RejectReserved(attrs);
        // The type's fields with the organization's own ones (added to a shared base type).
        AttrValidator.Validate(await types.SchemaOfAsync(type, ct), attrs);

        var node = new Node
        {
            SiteId = request.SiteId,
            ParentId = request.ParentId,
            TypeId = type.Id,
            Code = Trimmed(request.Code),
            Name = ValidName(request.Name),
            SortOrder = request.SortOrder ?? 0,
            Geom = Wgs84(request.Geom),
            Attrs = JsonSerializer.SerializeToDocument(attrs),
        };
        db.Nodes.Add(node);
        await db.SaveChangesAsync(ct);
        return await GetAsync(node.Id, ct);
    }

    /// <summary>Update and move (FR-H1): a new parent moves the whole subtree (the triggers rewrite the paths).</summary>
    public async Task<Node> UpdateAsync(long id, NodeUpdate request, CancellationToken ct)
    {
        var node = await db.Nodes.Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == id, ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {id} not found.");

        var type = node.Type;
        if (request.TypeId is { } typeId && typeId != node.TypeId)
        {
            type = await FindTypeAsync(typeId, ct);
            if (type.Aspect != node.Type.Aspect)
                throw AppException.BadRequest("node.aspect_mismatch", "The new type must have the same aspect as the old one.");
            node.TypeId = type.Id;
        }
        if (request.ParentIdSet && request.ParentId != node.ParentId)
        {
            await CheckParentAsync(request.ParentId, node.SiteId, type, ct);
            node.ParentId = request.ParentId;
        }
        else if (type.Id != node.Type.Id)
            await CheckParentAsync(node.ParentId, node.SiteId, type, ct);
        if (request.Name is not null) node.Name = ValidName(request.Name);
        if (request.CodeSet) node.Code = Trimmed(request.Code);
        if (request.SortOrder is { } sortOrder) node.SortOrder = sortOrder;
        if (request.GeomSet) node.Geom = Wgs84(request.Geom);

        // Attributes are validated also when only the type changed.
        var attrs = request.Attrs ?? node.Attrs.RootElement;
        if (request.Attrs is { } sent)
        {
            // Linked records: the data lives in MapManager and is read only here (spec 6.3).
            if (AttrValidator.IsLinked(node.Attrs.RootElement))
                throw AppException.BadRequest("node.linked_read_only", "The technical data of a MapManager-linked node is read only.");
            AttrValidator.RejectReserved(sent);
        }
        if ((request.Attrs is not null || request.TypeId is not null) && !AttrValidator.IsLinked(node.Attrs.RootElement))
        {
            AttrValidator.Validate(await types.SchemaOfAsync(type, ct), attrs);
            node.Attrs = JsonSerializer.SerializeToDocument(attrs);
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Deletes a node; with children only if <paramref name="recursive"/> (the client asks for confirmation).
    /// Anchors are deleted with their node; nodes still referenced by panoramas or models give 409 (db.referenced).
    /// </summary>
    public async Task DeleteAsync(long id, bool recursive, CancellationToken ct)
    {
        var node = await GetAsync(id, ct);
        currentUser.RequireEditableSite(node.SiteId); // bulk delete: not seen by the SaveChanges check
        var path = node.Path;
        var hasChildren = await db.Nodes.AnyAsync(n => n.ParentId == id, ct);
        if (hasChildren && !recursive)
            throw AppException.Conflict("node.has_children", "The node has children: delete the subtree explicitly (recursive=true).");

        // Finalized nodes leave history (with a reason) before they go; their anchors as tracked deletes.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var subtree = db.Nodes.Where(n => n.Path.IsDescendantOf(path));
        await db.RemoveFinalizedAnchorsAsync(db.Anchors.Where(a => subtree.Any(n => n.Id == a.NodeId)), ct);
        await db.RecordNodeDeletionsAsync(await subtree.Where(n => n.FinalizedAt != null).ToListAsync(ct), ct);
        // One statement: the parent references inside the subtree are checked at its end.
        await subtree.ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<List<NodeRelation>> RelationsAsync(long id, CancellationToken ct)
    {
        await GetAsync(id, ct);
        return await db.NodeRelations.AsNoTracking().Where(r => r.FromId == id || r.ToId == id)
            .OrderBy(r => r.Kind).ThenBy(r => r.FromId).ThenBy(r => r.ToId).ToListAsync(ct);
    }

    public async Task<NodeRelation> AddRelationAsync(long fromId, RelationCreate request, CancellationToken ct)
    {
        var kind = (request.Kind ?? "").Trim();
        if (kind.Length is 0 or > 64 || !kind.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            throw AppException.BadRequest("relation.invalid_kind", "Relation kind: 1–64 lowercase letters, digits or _ (e.g. located_in).");
        if (fromId == request.ToId)
            throw AppException.BadRequest("relation.self", "A node cannot be related to itself.");
        await GetAsync(fromId, ct);
        if (!await db.Nodes.AnyAsync(n => n.Id == request.ToId, ct))
            throw AppException.NotFound("node.not_found", $"Node {request.ToId} not found.");
        if (await db.NodeRelations.AnyAsync(r => r.FromId == fromId && r.ToId == request.ToId && r.Kind == kind, ct))
            throw AppException.Conflict("relation.exists", "The relation already exists.");

        var relation = new NodeRelation { FromId = fromId, ToId = request.ToId, Kind = kind };
        db.NodeRelations.Add(relation);
        await db.SaveChangesAsync(ct);
        return relation;
    }

    public async Task DeleteRelationAsync(long fromId, long toId, string kind, CancellationToken ct)
    {
        currentUser.RequireEditableSite((await GetAsync(fromId, ct)).SiteId);
        var deleted = await db.NodeRelations.Where(r => r.FromId == fromId && r.ToId == toId && r.Kind == kind).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw AppException.NotFound("relation.not_found", "Relation not found.");
    }

    private async Task<NodeType> FindTypeAsync(long typeId, CancellationToken ct) =>
        await db.NodeTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId, ct)
        ?? throw AppException.BadRequest("node.unknown_type", $"Node type {typeId} does not exist.");

    /// <summary>
    /// The parent exists (in the organization), is on the same site and belongs to the same tree (aspect), and the type
    /// may be there (<see cref="NodeType.AllowedParents"/>; null parent: at the top of the tree).
    /// </summary>
    internal async Task CheckParentAsync(long? parentId, long siteId, NodeType childType, CancellationToken ct)
    {
        if (parentId is not { } id)
        {
            if (childType.AllowedParents is { } top && !top.Contains(NodeType.TopLevel))
                throw ParentNotAllowed(childType);
            return;
        }
        var parent = await db.Nodes.AsNoTracking().Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == id, ct)
            ?? throw AppException.BadRequest("node.parent_not_found", $"Parent node {id} not found.");
        if (parent.SiteId != siteId)
            throw AppException.BadRequest("node.parent_other_site", "The parent node is on another site.");
        if (parent.Type.Aspect != childType.Aspect)
            throw AppException.BadRequest("node.aspect_mismatch",
                "Parent and child must belong to the same tree (aspect); link the trees with relations instead.");
        if (childType.AllowedParents is { } allowed && !allowed.Contains(parent.Type.Code))
            throw ParentNotAllowed(childType);
    }

    private static AppException ParentNotAllowed(NodeType type) =>
        AppException.BadRequest("node.parent_type_not_allowed",
            $"A '{type.Code}' may only be under: {string.Join(", ", type.AllowedParents!.Select(p => p == NodeType.TopLevel ? "(top level)" : p))}.");

    /// <summary>Number of labels of a path (LTree.NLevel only translates inside queries).</summary>
    public static int Depth(LTree path) => ((string)path).Count(c => c == '.') + 1;

    private static string ValidName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
            throw AppException.BadRequest("node.invalid_name", $"Name: 1–{MaxNameLength} characters.");
        return trimmed;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Geometries arrive as GeoJSON in EPSG:4326 (spec 10).</summary>
    internal static T? Wgs84<T>(T? geometry) where T : Geometry
    {
        if (geometry is null) return null;
        if (!geometry.IsValid)
            throw AppException.BadRequest("geometry.invalid", "The geometry is not valid.");
        geometry.SRID = 4326;
        return geometry;
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();
}
