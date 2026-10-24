using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.History;

/// <param name="Fields">Changed fields (camelCase) between before and after.</param>
public record ChangeView(long Id, string Entity, long EntityId, string Action, DateOnly ValidFrom, DateTime RecordedAt, string? RecordedByName,
    string? Reason, List<string> Fields, JsonElement? Before, JsonElement? After);

/// <param name="Tracked">False before the finalization day: earlier states were not recorded.</param>
/// <param name="Node">Name, code and technical data valid on that day.</param>
/// <param name="Anchors">The anchors existing on that day, as recorded (snapshots).</param>
public record NodeState(DateOnly AsOf, bool Tracked, JsonElement? Node, List<JsonElement> Anchors);

/// <summary>
/// Finalization and history of nodes (bitemporal: valid-from day + recording time, after SmartRoute). Draft nodes are
/// edited freely; from finalization on, every change of descriptive data and anchors is recorded with a reason
/// (AppDbContext.RecordChangesAsync) and the state of any day since the finalization can be rebuilt.
/// </summary>
public class HistoryService(AppDbContext db, CurrentUser currentUser, ChangeContext changes)
{
    /// <summary>
    /// Finalizes the node (or its whole subtree): the current state becomes the baseline, valid from
    /// <paramref name="validFrom"/> (default today; may be earlier, e.g. the commissioning day). Already finalized ones stay.
    /// </summary>
    public async Task<int> FinalizeAsync(long nodeId, bool subtree, DateOnly? validFrom, CancellationToken ct)
    {
        var root = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
        currentUser.RequireEditableSite(root.SiteId);
        var day = validFrom ?? AppDbContext.Today;
        if (day > AppDbContext.Today) throw AppException.BadRequest("change.valid_from_future", "The valid-from day cannot be in the future.");

        var path = root.Path;
        var nodes = await (subtree ? db.Nodes.Where(n => n.Path.IsDescendantOf(path)) : db.Nodes.Where(n => n.Id == nodeId))
            .Where(n => n.FinalizedAt == null).ToListAsync(ct);
        var ids = nodes.Select(n => n.Id).ToList();
        var anchors = await db.Anchors.Where(a => ids.Contains(a.NodeId)).ToListAsync(ct);
        var now = DateTime.UtcNow;
        ChangeEntry Baseline(long id, string entity, long entityId, JsonDocument state) => new()
        {
            SiteId = root.SiteId, NodeId = id, Entity = entity, EntityId = entityId, Action = ChangeActions.Finalize, ValidFrom = day,
            RecordedAt = now, RecordedBy = currentUser.UserId, RecordedByName = currentUser.DisplayName, Reason = changes.Reason, After = state,
        };
        foreach (var node in nodes)
        {
            db.ChangeEntries.Add(Baseline(node.Id, ChangeEntities.Node, node.Id, AppDbContext.Snapshot(db.Entry(node), false, AppDbContext.TrackedNodeProperties)));
            foreach (var anchor in anchors.Where(a => a.NodeId == node.Id))
                db.ChangeEntries.Add(Baseline(node.Id, ChangeEntities.Anchor, anchor.Id, AppDbContext.Snapshot(db.Entry(anchor), false, null)));
            node.FinalizedAt = now;
            node.FinalizedBy = currentUser.UserId;
        }
        await db.SaveChangesAsync(ct);
        return nodes.Count;
    }

    /// <summary>Changes of the node and its anchors, newest recording first.</summary>
    public async Task<List<ChangeView>> HistoryAsync(long nodeId, CancellationToken ct)
    {
        await RequireNodeAsync(nodeId, ct);
        var entries = await db.ChangeEntries.AsNoTracking().Where(c => c.NodeId == nodeId)
            .OrderByDescending(c => c.RecordedAt).ThenByDescending(c => c.Id).ToListAsync(ct);
        return entries.Select(c => new ChangeView(c.Id, c.Entity, c.EntityId, c.Action, c.ValidFrom, c.RecordedAt, c.RecordedByName, c.Reason,
            ChangedFields(c.Before, c.After), c.Before?.RootElement.Clone(), c.After?.RootElement.Clone())).ToList();
    }

    /// <summary>
    /// The node as valid at the end of <paramref name="asOf"/>: per entity the last change valid from that day or
    /// earlier (by valid-from day, then recording time – a later correction of the same day wins).
    /// </summary>
    public async Task<NodeState> StateAsync(long nodeId, DateOnly asOf, CancellationToken ct)
    {
        await RequireNodeAsync(nodeId, ct);
        var entries = await db.ChangeEntries.AsNoTracking().Where(c => c.NodeId == nodeId && c.ValidFrom <= asOf)
            .OrderBy(c => c.ValidFrom).ThenBy(c => c.RecordedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var baseline = entries.Any(c => c.Entity == ChangeEntities.Node && c.Action == ChangeActions.Finalize);
        if (!baseline) return new NodeState(asOf, false, null, []);
        var latest = entries.GroupBy(c => (c.Entity, c.EntityId)).Select(g => g.Last()).ToList();
        var node = latest.FirstOrDefault(c => c.Entity == ChangeEntities.Node)?.After?.RootElement.Clone();
        var anchors = latest.Where(c => c.Entity == ChangeEntities.Anchor && c.After is not null)
            .OrderBy(c => c.EntityId)
            .Select(c =>
            {
                var state = JsonNode.Parse(c.After!.RootElement.GetRawText())!.AsObject();
                state["id"] = c.EntityId;
                return JsonSerializer.SerializeToElement(state);
            })
            .ToList();
        return new NodeState(asOf, true, node, anchors);
    }

    private async Task RequireNodeAsync(long nodeId, CancellationToken ct)
    {
        if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, ct))
            throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
    }

    /// <summary>Changed fields; inside the technical data (attrs) the changed attribute names, as "attrs.power_kw".</summary>
    private static List<string> ChangedFields(JsonDocument? before, JsonDocument? after)
    {
        if (before is null || after is null) return [];
        static IEnumerable<string> Diff(JsonElement b, JsonElement a, string prefix) =>
            a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name))
                .Where(name => !(b.TryGetProperty(name, out var x) && a.TryGetProperty(name, out var y) && x.GetRawText() == y.GetRawText()))
                .SelectMany(name =>
                    prefix == "" && name == "attrs" && b.TryGetProperty(name, out var bo) && a.TryGetProperty(name, out var ao)
                    && bo.ValueKind == JsonValueKind.Object && ao.ValueKind == JsonValueKind.Object
                        ? Diff(bo, ao, "attrs.")
                        : [prefix + name]);
        return Diff(before.RootElement, after.RootElement, "").ToList();
    }
}
