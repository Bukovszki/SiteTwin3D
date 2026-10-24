using System.Text.Json;

namespace Api.Model;

public static class ChangeEntities
{
    /// <summary>The descriptive data of a node: name, code, technical data.</summary>
    public const string Node = "node";
    /// <summary>An anchor of the node: place, level, height, size, direction.</summary>
    public const string Anchor = "anchor";
}

public static class ChangeActions
{
    /// <summary>The baseline: the state when the node was finalized.</summary>
    public const string Finalize = "finalize";
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
}

/// <summary>
/// One change of a finalized node (bitemporal, like SmartRoute): <see cref="ValidFrom"/> – since when it is so in
/// reality (a day, may be earlier than the recording), and <see cref="RecordedAt"/> / <see cref="RecordedBy"/> – who
/// recorded it and when, with the reason. Before / after are snapshots, so the state on any day can be rebuilt, and a
/// correction (the same day recorded again) never overwrites the record of the earlier one. No foreign key to the
/// node: the history outlives a deleted node.
/// </summary>
public class ChangeEntry : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long SiteId { get; set; }
    public long NodeId { get; set; }
    public string Entity { get; set; } = ChangeEntities.Node;
    public long EntityId { get; set; }
    public string Action { get; set; } = ChangeActions.Update;
    public DateOnly ValidFrom { get; set; }
    public DateTime RecordedAt { get; set; }
    public long? RecordedBy { get; set; }
    public string? RecordedByName { get; set; }
    public string? Reason { get; set; }
    public JsonDocument? Before { get; set; }
    public JsonDocument? After { get; set; }
}
