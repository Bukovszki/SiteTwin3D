using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Model;

/// <summary>Top-level location unit (spec 2, 7.1). Geometries in WGS84 (EPSG:4326).</summary>
public class Site : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Reference point (default view).</summary>
    public Point RefPoint { get; set; } = null!;
    /// <summary>Base height (ellipsoidal, metres); levels are relative to it.</summary>
    public double BaseHeightM { get; set; }
    public Polygon? Extent { get; set; }
    public JsonDocument Attrs { get; set; } = JsonDocument.Parse("{}");
}

public static class Aspects
{
    public const string Location = "location";
    public const string Function = "function";
}

/// <summary>
/// Node type (spec 8.2, FR-H2). Shared base types have no organization; organizations add their own.
/// Deviation from the starting DDL: surrogate key + <see cref="Code"/> unique per organization, because a text
/// primary key would make the codes of different organizations collide.
/// </summary>
public class NodeType
{
    public long Id { get; set; }
    /// <summary>Null: shared base type, visible to every organization.</summary>
    public long? OrganizationId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Aspect { get; set; } = Aspects.Location;
    /// <summary>Nodes of this type are levels (floors): floorplans, panoramas and anchors refer to them (spec 7.1).</summary>
    public bool IsLevel { get; set; }
    /// <summary>JSON Schema of the technical data (validation, form, grid columns; FR-H5).</summary>
    public JsonDocument? AttrSchema { get; set; }
    /// <summary>
    /// Under which types a node of this type may be (type codes; <see cref="TopLevel"/> = at the top of its tree);
    /// null: anywhere in its tree. E.g. a room only under a level (user decision).
    /// </summary>
    public List<string>? AllowedParents { get; set; }

    public const string TopLevel = "";
}

/// <summary>
/// Fields an organization adds to a shared base type (user decision): its records of that type get them after the base
/// type's own fields. The shared base types themselves stay read only.
/// </summary>
public class NodeTypeExtension : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long NodeTypeId { get; set; }
    /// <summary>JSON Schema of the added fields (properties in form order; json, not jsonb, keeps the order).</summary>
    public JsonDocument AttrSchema { get; set; } = JsonDocument.Parse("{}");
}

/// <summary>
/// Any element of the hierarchy (spec 8.1). <see cref="Path"/> (ltree of ids) is maintained by database triggers
/// (db/sql/node_path.sql); the application only sets <see cref="ParentId"/>.
/// </summary>
public class Node : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long SiteId { get; set; }
    public long? ParentId { get; set; }
    public LTree Path { get; set; }
    public long TypeId { get; set; }
    public NodeType Type { get; set; } = null!;
    /// <summary>Professional designation (e.g. KKS / IEC 81346).</summary>
    public string? Code { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    /// <summary>Own 2D geometry if any (e.g. room polygon).</summary>
    public Geometry? Geom { get; set; }
    /// <summary>Technical data, by the node type's attr_schema.</summary>
    public JsonDocument Attrs { get; set; } = JsonDocument.Parse("{}");
    /// <summary>
    /// Finalized (null: draft). From then on every change of its descriptive data and anchors needs a reason and is
    /// kept in <see cref="ChangeEntry"/> with the day it is valid from.
    /// </summary>
    public DateTime? FinalizedAt { get; set; }
    public long? FinalizedBy { get; set; }
}

public static class RelationKinds
{
    public const string LocatedIn = "located_in";
    public const string ConnectedTo = "connected_to";
    public const string Feeds = "feeds";
}

/// <summary>Typed, non-hierarchical relation between nodes, also across trees (FR-H6).</summary>
public class NodeRelation : IOrganizationOwned
{
    public long FromId { get; set; }
    public long ToId { get; set; }
    public string Kind { get; set; } = "";
    public long OrganizationId { get; set; }
}
