using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Model;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NetTopologySuite.Geometries;

namespace Api.Data;

/// <summary>
/// Change history of finalized nodes, centrally at save time: no service, endpoint or import can change a finalized
/// node's descriptive data or anchors without a reason, and none can skip the history row.
/// </summary>
public partial class AppDbContext
{
    /// <summary>The descriptive data of a node under history (its place in the tree is not).</summary>
    public static readonly string[] TrackedNodeProperties = [nameof(Node.Name), nameof(Node.Code), nameof(Node.Attrs)];

    private static readonly string[] UntrackedAnchorProperties = [nameof(Anchor.Id), nameof(Anchor.OrganizationId), nameof(Anchor.NodeId)];

    /// <summary>The day "today" in the history (the users' day, not UTC midnight).</summary>
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, HungarianTime));

    private static readonly JsonSerializerOptions GeoJson = new() { Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() } };

    private static readonly TimeZoneInfo HungarianTime =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Budapest", out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>
    /// History rows for the changes about to be saved; with the tracked entity, so that a new anchor's id can be set
    /// after the insert. Throws 428 change.reason_required if a finalized node changes without a reason.
    /// </summary>
    private async Task<List<(ChangeEntry Entry, object Entity)>> RecordChangesAsync(CancellationToken ct)
    {
        var result = new List<(ChangeEntry, object)>();
        if (currentUser?.UserId is null) return result; // background work never changes finalized data

        var candidates = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is Anchor || (e.Entity is Node && e.State != EntityState.Added))
            .ToList();
        if (candidates.Count == 0) return result;

        var nodeIds = candidates.Select(e => e.Entity is Anchor a ? a.NodeId : ((Node)e.Entity).Id).Distinct().ToList();
        // Original values: a node finalized in this very save is not "changed after finalization".
        var finalized = await Set<Node>().IgnoreQueryFilters().AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id) && n.FinalizedAt != null)
            .Select(n => new { n.Id, n.SiteId, n.FinalizedAt })
            .ToDictionaryAsync(n => n.Id, ct);
        if (finalized.Count == 0) return result;

        var today = Today;
        foreach (var entry in candidates)
        {
            var (entity, nodeId) = entry.Entity is Anchor anchor ? (ChangeEntities.Anchor, anchor.NodeId) : (ChangeEntities.Node, ((Node)entry.Entity).Id);
            if (!finalized.TryGetValue(nodeId, out var node)) continue;

            JsonDocument? before = null, after = null;
            if (entity == ChangeEntities.Anchor)
            {
                if (entry.State != EntityState.Added) before = Snapshot(entry, original: true, null);
                if (entry.State != EntityState.Deleted) after = Snapshot(entry, original: false, null);
            }
            else
            {
                if (entry.State == EntityState.Deleted) before = Snapshot(entry, original: true, TrackedNodeProperties);
                else
                {
                    before = Snapshot(entry, original: true, TrackedNodeProperties);
                    after = Snapshot(entry, original: false, TrackedNodeProperties);
                }
            }
            // Saved without a real difference (e.g. the same attributes again): nothing to record or justify.
            if (before is not null && after is not null && before.RootElement.GetRawText() == after.RootElement.GetRawText()) continue;

            var reason = changes?.Reason;
            if (string.IsNullOrWhiteSpace(reason))
                throw new AppException(StatusCodes.Status428PreconditionRequired, "change.reason_required",
                    "The record is finalized: give the reason of the change (and the day it is valid from).");
            var validFrom = changes?.ValidFrom ?? today;
            var entityId = entry.Entity is Anchor a ? a.Id : nodeId;
            await CheckValidFromAsync(entity, entityId, nodeId, validFrom, ct);

            var change = new ChangeEntry
            {
                OrganizationId = ((IOrganizationOwned)entry.Entity).OrganizationId,
                SiteId = node.SiteId,
                NodeId = nodeId,
                Entity = entity,
                EntityId = entityId,
                Action = entry.State switch { EntityState.Added => ChangeActions.Create, EntityState.Deleted => ChangeActions.Delete, _ => ChangeActions.Update },
                ValidFrom = validFrom,
                RecordedAt = DateTime.UtcNow,
                RecordedBy = currentUser.UserId,
                RecordedByName = currentUser.DisplayName,
                Reason = reason.Trim(),
                Before = before,
                After = after,
            };
            Set<ChangeEntry>().Add(change);
            result.Add((change, entry.Entity));
        }
        return result;
    }

    /// <summary>
    /// The valid-from day of a change: not in the future, and not before the last recorded state of the same entity
    /// (the same day is a correction). A new anchor of a finalized node: not before the finalization baseline.
    /// </summary>
    internal async Task CheckValidFromAsync(string entity, long entityId, long nodeId, DateOnly validFrom, CancellationToken ct)
    {
        if (validFrom > Today)
            throw AppException.BadRequest("change.valid_from_future", "The valid-from day cannot be in the future.");
        var history = Set<ChangeEntry>().IgnoreQueryFilters().AsNoTracking();
        var last = entityId > 0
            ? await history.Where(c => c.Entity == entity && c.EntityId == entityId).MaxAsync(c => (DateOnly?)c.ValidFrom, ct)
            : null;
        last ??= await history.Where(c => c.NodeId == nodeId && c.Entity == ChangeEntities.Node && c.Action == ChangeActions.Finalize)
            .MaxAsync(c => (DateOnly?)c.ValidFrom, ct);
        if (last is { } day && validFrom < day)
            throw AppException.BadRequest("change.valid_from_too_early", $"The valid-from day cannot be before {day:yyyy-MM-dd}, the last recorded state.");
    }

    /// <summary>
    /// Before a bulk delete: anchors of finalized nodes among <paramref name="anchors"/> are removed as tracked
    /// entities (so they need a reason and leave history), the rest is left to the database cascade.
    /// </summary>
    public async Task RemoveFinalizedAnchorsAsync(IQueryable<Anchor> anchors, CancellationToken ct)
    {
        var finalized = await anchors.Where(a => Set<Node>().Any(n => n.Id == a.NodeId && n.FinalizedAt != null)).ToListAsync(ct);
        if (finalized.Count == 0) return;
        Set<Anchor>().RemoveRange(finalized);
        await SaveChangesAsync(ct);
    }

    /// <summary>History rows for deleting finalized nodes (their anchors first, through <see cref="RemoveFinalizedAnchorsAsync"/>).</summary>
    public async Task RecordNodeDeletionsAsync(List<Node> nodes, CancellationToken ct)
    {
        var finalized = nodes.Where(n => n.FinalizedAt != null).ToList();
        if (finalized.Count == 0) return;
        if (string.IsNullOrWhiteSpace(changes?.Reason))
            throw new AppException(StatusCodes.Status428PreconditionRequired, "change.reason_required",
                "The record is finalized: give the reason of the change (and the day it is valid from).");
        var validFrom = changes!.ValidFrom ?? Today;
        foreach (var node in finalized)
        {
            await CheckValidFromAsync(ChangeEntities.Node, node.Id, node.Id, validFrom, ct);
            Set<ChangeEntry>().Add(new ChangeEntry
            {
                OrganizationId = node.OrganizationId, SiteId = node.SiteId, NodeId = node.Id, Entity = ChangeEntities.Node, EntityId = node.Id,
                Action = ChangeActions.Delete, ValidFrom = validFrom, RecordedAt = DateTime.UtcNow, RecordedBy = currentUser?.UserId,
                RecordedByName = currentUser?.DisplayName, Reason = changes.Reason!.Trim(), Before = Snapshot(Entry(node), true, TrackedNodeProperties),
            });
        }
        await base.SaveChangesAsync(ct);
    }

    /// <summary>The state of a tracked entity as JSON (geometries as [lon, lat], polygons as GeoJSON coordinates).</summary>
    public static JsonDocument Snapshot(EntityEntry entry, bool original, string[]? only)
    {
        var json = new JsonObject();
        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (only is not null ? !only.Contains(name) : UntrackedAnchorProperties.Contains(name)) continue;
            var value = original ? property.OriginalValue : property.CurrentValue;
            json[JsonNamingPolicy.CamelCase.ConvertName(name)] = ToJson(value);
        }
        return JsonSerializer.SerializeToDocument(json);
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        Point p => new JsonArray(Math.Round(p.X, 9), Math.Round(p.Y, 9)),
        Geometry g => JsonSerializer.SerializeToNode(g, GeoJson),
        JsonDocument d => JsonNode.Parse(d.RootElement.GetRawText()),
        _ => JsonValue.Create(value),
    };
}
