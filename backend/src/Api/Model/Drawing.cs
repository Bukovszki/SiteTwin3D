using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Api.Model;

/// <summary>
/// Drawn supplementary elements (spec 8.6), following the MapManager structure (upload → record → geometry),
/// but stored in WGS84.
/// </summary>
public class DrawLayer : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long SiteId { get; set; }
    /// <summary>Null: site-level (outdoor) layer.</summary>
    public long? LevelId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>'Point' | 'MultiLineString' | 'MultiPolygon' (one per layer).</summary>
    public string GeometryType { get; set; } = "";
    /// <summary>Field description (name, type, length, decimals, alias) like the MapManager adatstruktura.</summary>
    public JsonDocument Structure { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument Style { get; set; } = JsonDocument.Parse("{}");
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
}

public class DrawRecord : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long LayerId { get; set; }
    /// <summary>Sequence number inside the layer.</summary>
    public int Seq { get; set; }
    public JsonDocument Attrs { get; set; } = JsonDocument.Parse("{}");
    /// <summary>Optional link to a node.</summary>
    public long? NodeId { get; set; }
    /// <summary>Optimistic concurrency.</summary>
    public int Version { get; set; } = 1;
    public DateTime ModifiedAt { get; set; }
    public long? ModifiedBy { get; set; }
}

public class DrawGeometry : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long RecordId { get; set; }
    public long LayerId { get; set; }
    public Geometry Geom { get; set; } = null!;
}
