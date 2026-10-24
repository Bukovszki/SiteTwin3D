using System.Text.Json;
using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Locations;

/// <summary>Where a node is: the location node, its breadcrumb, and how it was found (relation | box).</summary>
public record LocationRef(long NodeId, string Name, List<string> Path, string Source);

public static class LocationSources
{
    /// <summary>An explicit located_in relation (spec 8.1, node_relation).</summary>
    public const string Relation = "relation";
    /// <summary>Derived: the smallest box of a location node (room, building…) containing one of the node's anchors.</summary>
    public const string Box = "box";
}

public record MemberLocation(long NodeId, string Name, string? Code, string TypeCode, long? ParentId, int Depth, LocationRef? Location,
    long? LevelId, string? LevelName);

/// <param name="Node">The node itself.</param>
/// <param name="Members">Its whole subtree (e.g. every element of a process line), in tree order.</param>
public record NodeMembers(MemberLocation Node, List<MemberLocation> Members);

/// <summary>
/// Functional view across the location tree (spec 8.1, M8): a process line lives in the functional tree and crosses
/// buildings and levels; where its elements are comes from an explicit <c>located_in</c> relation, or – without one –
/// from space: the smallest box of a location node (room, building) that contains one of the element's anchors.
/// </summary>
public class LocationService(AppDbContext db, CurrentUser currentUser)
{
    private record AnchorPoint(long NodeId, double Lon, double Lat, long? LevelId, double ZRelM);

    public async Task<NodeMembers> MembersAsync(long nodeId, CancellationToken ct)
    {
        var root = await db.Nodes.AsNoTracking().Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
        var path = root.Path;
        var nodes = await db.Nodes.AsNoTracking().Include(n => n.Type)
            .Where(n => n.SiteId == root.SiteId && n.Path.IsDescendantOf(path))
            .ToListAsync(ct);
        var ids = nodes.Select(n => n.Id).ToList();

        // Explicit relations; with several, the most specific (deepest) place wins.
        var relations = await db.NodeRelations.AsNoTracking()
            .Where(r => r.Kind == RelationKinds.LocatedIn && ids.Contains(r.FromId))
            .Join(db.Nodes, r => r.ToId, n => n.Id, (r, n) => new { r.FromId, Location = n })
            .ToListAsync(ct);

        // Levels (elevation) and the boxes of location nodes on this site.
        var levels = await db.Nodes.AsNoTracking().Where(n => n.SiteId == root.SiteId && n.Type.IsLevel).ToListAsync(ct);
        var elevation = levels.ToDictionary(l => l.Id, l => Number(l.Attrs, "elevation_m") ?? 0);
        double Elevation(long? levelId, double z) => (levelId is { } id && elevation.TryGetValue(id, out var e) ? e : 0) + z;

        var boxes = await db.Anchors.AsNoTracking()
            .Where(a => a.Kind == AnchorKinds.Region && a.RegionShape == RegionShapes.Box)
            .Join(db.Nodes.Where(n => n.SiteId == root.SiteId && n.Type.Aspect == "location"), a => a.NodeId, n => n.Id, (a, n) => new { a, Location = n })
            .ToListAsync(ct);
        var shapes = boxes.Select(b => (b.Location, Shape: new BoxShape(b.a.Position.X, b.a.Position.Y, Elevation(b.a.LevelId, b.a.ZRelM),
            b.a.SizeX!.Value, b.a.SizeY!.Value, b.a.SizeZ!.Value, b.a.HeadingDeg ?? 0))).ToList();

        var points = (await db.Anchors.AsNoTracking().Where(a => ids.Contains(a.NodeId)).OrderBy(a => a.Id).ToListAsync(ct))
            .Select(a => new AnchorPoint(a.NodeId, a.Position.X, a.Position.Y, a.LevelId, a.ZRelM))
            .ToLookup(p => p.NodeId);

        // Names of every ancestor of the places found, for the breadcrumbs.
        var places = relations.Select(r => r.Location).Concat(shapes.Select(s => s.Location)).DistinctBy(n => n.Id).ToList();
        var ancestorIds = places.SelectMany(p => p.Path.ToString().Split('.').Select(long.Parse)).Distinct().ToList();
        var names = await db.Nodes.AsNoTracking().Where(n => ancestorIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Name, ct);
        List<string> Breadcrumb(Node place) => place.Path.ToString().Split('.').Select(long.Parse).Select(id => names.GetValueOrDefault(id, "?")).ToList();

        MemberLocation Describe(Node node)
        {
            LocationRef? location = null;
            var related = relations.Where(r => r.FromId == node.Id).Select(r => r.Location).OrderByDescending(n => NodeService.Depth(n.Path)).FirstOrDefault();
            if (related is not null) location = new LocationRef(related.Id, related.Name, Breadcrumb(related), LocationSources.Relation);
            else
            {
                var hit = points[node.Id]
                    .SelectMany(p => shapes.Where(s => s.Location.Id != node.Id && BoxContainment.Contains(s.Shape, p.Lon, p.Lat, Elevation(p.LevelId, p.ZRelM))))
                    .OrderBy(s => s.Shape.Volume)
                    .Select(s => s.Location)
                    .FirstOrDefault();
                if (hit is not null) location = new LocationRef(hit.Id, hit.Name, Breadcrumb(hit), LocationSources.Box);
            }
            var levelId = points[node.Id].Select(p => p.LevelId).FirstOrDefault(l => l is not null);
            return new MemberLocation(node.Id, node.Name, node.Code, node.Type.Code, node.ParentId, NodeService.Depth(node.Path) - NodeService.Depth(path), location, levelId,
                levelId is { } l ? levels.FirstOrDefault(x => x.Id == l)?.Name : null);
        }

        var members = nodes.Where(n => n.Id != root.Id)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name)
            .ToList();
        // Tree order: parents before children, siblings by sort order.
        var ordered = new List<Node>();
        void Walk(long parentId)
        {
            foreach (var child in members.Where(n => n.ParentId == parentId)) { ordered.Add(child); Walk(child.Id); }
        }
        Walk(root.Id);
        return new NodeMembers(Describe(root), ordered.Select(Describe).ToList());
    }

    /// <summary>Sets (or with null clears) the explicit place of a node: its single located_in relation.</summary>
    public async Task SetLocationAsync(long nodeId, long? locationId, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
        currentUser.RequireEditableSite(node.SiteId);
        if (locationId is { } id)
        {
            var place = await db.Nodes.AsNoTracking().Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == id, ct)
                ?? throw AppException.NotFound("node.not_found", $"Node {id} not found.");
            if (place.SiteId != node.SiteId || place.Type.Aspect != "location" || place.Id == nodeId)
                throw AppException.BadRequest("location.invalid", "The place must be a node of the location tree on the same site.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.NodeRelations.Where(r => r.FromId == nodeId && r.Kind == RelationKinds.LocatedIn).ExecuteDeleteAsync(ct);
        if (locationId is { } to)
        {
            db.NodeRelations.Add(new NodeRelation { FromId = nodeId, ToId = to, Kind = RelationKinds.LocatedIn });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static double? Number(JsonDocument attrs, string name) =>
        attrs.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
