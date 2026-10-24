using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Api.Model;

/// <summary>
/// Processing of a panorama (FR-P8): pending (queued for analysis) → review (faces suggested, an editor checks) →
/// rendering (approved, the blurred display version is being made) → approved (visible to viewers). failed: the
/// image could not be processed (see processing_error).
/// </summary>
public static class PrivacyStatus
{
    public const string Pending = "pending";
    public const string Review = "review";
    public const string Rendering = "rendering";
    public const string Approved = "approved";
    public const string Failed = "failed";
}

/// <summary>360° panorama (spec 4.4). The image itself is in the object store, here only its key and hash.</summary>
public class Panorama : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long NodeId { get; set; }
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public Point Position { get; set; } = null!;
    /// <summary>Null: outdoor.</summary>
    public long? LevelId { get; set; }
    /// <summary>Camera height above the level floor (metres).</summary>
    public float ZRelM { get; set; } = 1.6f;
    /// <summary>Direction of the image centre from geographic north, clockwise.</summary>
    public float? HeadingDeg { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string PrivacyStatus { get; set; } = Model.PrivacyStatus.Pending;
    public JsonDocument PrivacyRegions { get; set; } = JsonDocument.Parse("[]");
    public string? DisplayKey { get; set; }
    /// <summary>Null: the original has been deleted.</summary>
    public DateTime? OriginalRetainedUntil { get; set; }
    /// <summary>Size of the display version (px).</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ProcessingError { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Walk-through neighbourhood between panoramas.</summary>
public class PanoramaLink : IOrganizationOwned
{
    public long FromId { get; set; }
    public long ToId { get; set; }
    public long OrganizationId { get; set; }
}

/// <summary>3D model or tileset (spec 4.8, 9). Binary content in the object store.</summary>
public class ModelAsset : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long SiteId { get; set; }
    public long? NodeId { get; set; }
    /// <summary>'gltf' | '3dtiles'</summary>
    public string Kind { get; set; } = "";
    public bool Segmented { get; set; }
    public string StorageKey { get; set; } = "";
    public JsonDocument Attrs { get; set; } = JsonDocument.Parse("{}");
    /// <summary>ready | converting (IFC, mesh formats, point clouds: the converter works on it) | failed.</summary>
    public string Status { get; set; } = ModelStatus.Ready;
    /// <summary>The uploaded format if converted: ifc | mesh | pointcloud (null: GLB or 3D Tiles as uploaded).</summary>
    public string? SourceFormat { get; set; }
    public string? ProcessingError { get; set; }
}

public static class ModelStatus
{
    public const string Ready = "ready";
    public const string Converting = "converting";
    public const string Failed = "failed";
}

public static class LayerSources
{
    public const string MapManager = "mapmanager";
    public const string ExternalWms = "external_wms";
}

/// <summary>Floorplan of a level: only a REFERENCE to a WMS layer, no file (spec 4.3, 6.3).</summary>
public class Floorplan : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long LevelId { get; set; }
    public string Source { get; set; } = LayerSources.MapManager;
    public string Name { get; set; } = "";
    /// <summary>WMS LAYERS; for MapManager 'workspace:layer'.</summary>
    public string Layer { get; set; } = "";
    public string? CqlFilter { get; set; }
    public JsonDocument WmsParams { get; set; } = JsonDocument.Parse("{}");
    public string? ExternalUrl { get; set; }
    public int SortOrder { get; set; }
    public float Opacity { get; set; } = 1;
    public JsonDocument Attrs { get; set; } = JsonDocument.Parse("{}");
}

/// <summary>Background layer (orthophoto, utilities) of a site or of the whole organization.</summary>
public class MapLayer : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    /// <summary>Null: on every site of the organization.</summary>
    public long? SiteId { get; set; }
    public string Source { get; set; } = LayerSources.MapManager;
    public string Name { get; set; } = "";
    public string Layer { get; set; } = "";
    public JsonDocument WmsParams { get; set; } = JsonDocument.Parse("{}");
    public string? ExternalUrl { get; set; }
    public int SortOrder { get; set; }
    public bool VisibleDefault { get; set; } = true;
}

public static class AnchorKinds
{
    public const string ModelFeature = "model_feature";
    public const string PanoramaPoint = "panorama_point";
    public const string Region = "region";
    public const string Point = "point";
}

public static class RegionShapes
{
    public const string Box = "box";
    public const string Prism = "prism";
}

/// <summary>
/// Spatial appearance of a node (spec 8.2). Always belongs to the node; display-side ids live only here.
/// Sizes are real metres, never EPSG:3857 units (spec 7.3). The kind-specific fields are enforced by a CHECK constraint.
/// </summary>
public class Anchor : IOrganizationOwned
{
    public long Id { get; set; }
    public long OrganizationId { get; set; }
    public long NodeId { get; set; }
    public string Kind { get; set; } = AnchorKinds.Point;
    // model_feature
    public long? ModelAssetId { get; set; }
    /// <summary>STABLE element id from the model metadata (e.g. IFC GlobalId), never the per-tile feature index.</summary>
    public string? FeatureRef { get; set; }
    // panorama_point
    public long? PanoramaId { get; set; }
    public float? YawDeg { get; set; }
    public float? PitchDeg { get; set; }
    // region
    public string? RegionShape { get; set; }
    public float? SizeX { get; set; }
    public float? SizeY { get; set; }
    public float? SizeZ { get; set; }
    public float? HeadingDeg { get; set; }
    public Polygon? Footprint { get; set; }
    public float? ZMin { get; set; }
    public float? ZMax { get; set; }
    // every kind: display position (floorplan, label; box centre)
    public Point Position { get; set; } = null!;
    public long? LevelId { get; set; }
    public float ZRelM { get; set; }
    public JsonDocument Attrs { get; set; } = JsonDocument.Parse("{}");
}
