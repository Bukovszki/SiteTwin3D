using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Services.Models;

public record ModelUploadFile(string Path, long Size);

public record ModelUploadRequest(long SiteId, List<ModelUploadFile> Files);

public record ModelUploadUrl(string Path, string Url, string ContentType);

/// <summary>Signed PUT URLs for every file of the model; the files are registered together by <see cref="Prefix"/>.</summary>
public record ModelUploadTicket(string Prefix, List<ModelUploadUrl> Files, DateTime ExpiresAt);

/// <summary>
/// Placement of a model without georeference (FR-I1): the model origin at a WGS84 point, <see cref="HeightM"/> metres
/// above the site's base height, the model's forward axis (glTF −Z) turned <see cref="HeadingDeg"/> clockwise from north.
/// </summary>
public record ModelPlacement(double Lon, double Lat, double HeightM, double HeadingDeg);

/// <param name="Srs">Point clouds: the source coordinate system (e.g. EPSG:23700); without it the cloud is placed like a GLB.</param>
public record ModelCreate(string Prefix, string Name, long? NodeId, ModelPlacement? Placement, string? ElementIdProperty, string? Srs = null);

/// <summary>What an upload is: a ready 3D Tiles package or GLB, or a source the converter turns into one (spec 9.2).</summary>
public static class ModelFormats
{
    public const string Tileset = "3dtiles";
    public const string Glb = "glb";
    public const string Ifc = "ifc";
    public const string Mesh = "mesh";
    public const string PointCloud = "pointcloud";
    /// <summary>A ZIP of a model with its files (e.g. FBX with textures in folders): the converter unpacks it and finds the model.</summary>
    public const string Archive = "zip";

    /// <summary>Mesh formats of the converter (Assimp); a .gltf with its .bin and textures is converted to one GLB.</summary>
    public static readonly string[] MeshExtensions = [".obj", ".fbx", ".dae", ".stl", ".ply", ".3ds", ".x3d", ".off", ".gltf"];
    public static readonly string[] PointCloudExtensions = [".las", ".laz", ".e57"];
    /// <summary>Files that may come with a mesh: materials, textures, buffers.</summary>
    public static readonly string[] CompanionExtensions = [".mtl", ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tif", ".tiff", ".bin", ".webp"];

    /// <summary>The format and the main file of an upload (paths relative to it); null if it is none of them.</summary>
    public static (string Format, string Main)? Detect(IReadOnlyCollection<string> paths)
    {
        if (paths.Contains(ModelService.TilesetFile)) return (Tileset, ModelService.TilesetFile);
        string Ext(string p) => Path.GetExtension(p).ToLowerInvariant();
        if (paths.Count == 1 && Ext(paths.First()) == ".zip") return (Archive, paths.First());
        var mains = paths.Where(p => Ext(p) is ".glb" or ".ifc" || MeshExtensions.Contains(Ext(p)) || PointCloudExtensions.Contains(Ext(p))).ToList();
        if (mains.Count != 1) return null;
        var main = mains[0];
        var format = Ext(main) switch
        {
            ".glb" => Glb,
            ".ifc" => Ifc,
            var e when PointCloudExtensions.Contains(e) => PointCloud,
            _ => Mesh,
        };
        // Only a mesh may bring other files (materials, textures); everything else is a single file.
        var others = paths.Where(p => p != main).ToList();
        if (others.Count > 0 && (format != Mesh || others.Any(p => !CompanionExtensions.Contains(Ext(p))))) return null;
        return (format, main);
    }
}

/// <summary>Partial update; null = not sent (the *Set flags distinguish clearing a value).</summary>
public record ModelUpdate(string? Name = null, long? NodeId = null, bool NodeIdSet = false, ModelPlacement? Placement = null,
    string? ElementIdProperty = null, bool ElementIdPropertySet = false);

/// <summary>
/// Import check of the stable element ids (spec 9.1): how many elements were read, how many have no id, and ids that
/// occur twice in one property table. The same id in several tiles (levels of detail) is expected and not reported.
/// </summary>
public record ElementCheck(string Property, int ContentsChecked, int ContentsWithoutProperty, int Elements, int DistinctIds, int Missing,
    List<string> Duplicates, bool Truncated);

/// <summary>
/// 3D models of a site (spec 4.8, 9, FR-I1): a single GLB or a ready 3D Tiles package, uploaded file by file with
/// signed URLs straight to the object store. The bytes never pass the API (NFR-3): the API serves only the
/// tileset.json (metadata), rewritten so that every tile content is a short-lived signed URL of the store and every
/// nested tileset comes through this endpoint again. A GLB is served as a one-tile 3D Tiles 1.1 tileset whose root
/// transform carries the placement – so the classification volumes of the boxes (spec 9.1) work on every model.
/// Processing chain (spec 9.2, open): conversion from IFC / DWG / photogrammetry happens outside, see docs.
/// </summary>
public partial class ModelService(AppDbContext db, IObjectStorage storage, CurrentUser currentUser, IOptions<StorageOptions> storageOptions,
    ModelConversionQueue conversions)
{
    public const string TilesetFile = "tileset.json";
    private const int MaxFiles = 20000;
    private const long MaxTilesetBytes = 64L * 1024 * 1024;
    private const int MaxCheckedContents = 500;
    private const long MaxCheckedBytes = 512L * 1024 * 1024;

    [GeneratedRegex(@"^org/\d+/models/[0-9a-f]{32}/")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"^EPSG:\d{4,6}$")]
    private static partial Regex EpsgRegex();

    /// <summary>The upload folder of a model (its sources and the converted files are under it).</summary>
    public static string FolderOf(string storageKey) => PrefixRegex().Match(storageKey).Value;

    private string ModelsPrefix => $"org/{currentUser.OrganizationId}/models/";

    public async Task<ModelUploadTicket> CreateUploadAsync(ModelUploadRequest request, CancellationToken ct)
    {
        await RequireSiteAsync(request.SiteId, ct);
        currentUser.RequireEditableSite(request.SiteId);
        var files = request.Files ?? [];
        if (files.Count == 0 || files.Count > MaxFiles)
            throw AppException.BadRequest("model.invalid_files", $"A model is 1 to {MaxFiles} files.");
        var maxBytes = storageOptions.Value.MaxModelMb * 1024L * 1024L;
        if (files.Any(f => f.Size < 0) || files.Sum(f => f.Size) > maxBytes)
            throw AppException.BadRequest("model.too_large", $"A model may be at most {storageOptions.Value.MaxModelMb} MB.");
        var paths = files.Select(f => ValidPath(f.Path)).ToList();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw AppException.BadRequest("model.invalid_files", "Duplicate file paths.");
        if (ModelFormats.Detect(paths) is null)
            throw AppException.BadRequest("model.unknown_format",
                "Upload one model: .glb, .ifc, a mesh (.obj with .mtl and textures, .fbx, .dae, .stl, .ply, .3ds, .gltf), a point cloud (.las, .laz, .e57), or a 3D Tiles package with tileset.json at its root.");

        var prefix = $"{ModelsPrefix}{Guid.NewGuid():N}/";
        var urls = paths.Select(p => new ModelUploadUrl(p, storage.PresignPut(prefix + p, ContentTypeOf(p)), ContentTypeOf(p))).ToList();
        return new ModelUploadTicket(prefix, urls, DateTime.UtcNow.AddMinutes(storageOptions.Value.UploadUrlMinutes));
    }

    public async Task<ModelAsset> CreateAsync(long siteId, ModelCreate request, CancellationToken ct)
    {
        await RequireSiteAsync(siteId, ct);
        var prefix = request.Prefix ?? "";
        // Only this organization's upload prefixes, never an arbitrary part of the store.
        if (!prefix.StartsWith(ModelsPrefix, StringComparison.Ordinal) || PrefixRegex().Match(prefix) is not { Success: true } m || m.Value != prefix)
            throw AppException.BadRequest("model.invalid_upload", "Unknown upload.");
        if (await db.ModelAssets.IgnoreQueryFilters().AnyAsync(a => a.StorageKey.StartsWith(prefix), ct))
            throw AppException.Conflict("model.upload_used", "This upload is already registered.");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw AppException.BadRequest("model.name_required", "name is required.");
        if (request.NodeId is { } nodeId) await RequireNodeAsync(nodeId, siteId, ct);

        var keys = (await storage.ListAsync(prefix, ct)).Select(o => o.Key).ToList();
        var asset = new ModelAsset { SiteId = siteId, NodeId = request.NodeId };
        var attrs = new JsonObject { ["name"] = request.Name.Trim() };
        var detected = ModelFormats.Detect(keys.Select(k => k[prefix.Length..]).ToList());
        if (detected is { Format: ModelFormats.Ifc or ModelFormats.Mesh or ModelFormats.PointCloud or ModelFormats.Archive } source)
        {
            // Converted in the background (spec 9.2); the model shows once it is ready.
            asset.Status = ModelStatus.Converting;
            asset.SourceFormat = source.Format;
            // A ZIP's model is known after unpacking; the converter's answer sets the kind and the format.
            asset.Kind = source.Format == ModelFormats.PointCloud ? "3dtiles" : "gltf";
            asset.StorageKey = prefix + source.Main;
            if (source.Format == ModelFormats.Mesh && request.Placement is null)
                throw AppException.BadRequest("model.placement_required", "A mesh has no georeference: placement (lon, lat, heightM, headingDeg) is required.");
            if (request.Placement is { } p) attrs["placement"] = PlacementNode(p);
            if (source.Format is ModelFormats.PointCloud or ModelFormats.Archive && !string.IsNullOrWhiteSpace(request.Srs))
            {
                var srs = request.Srs.Trim().ToUpperInvariant();
                if (!EpsgRegex().IsMatch(srs)) throw AppException.BadRequest("model.invalid_srs", "srs: EPSG:<code>, e.g. EPSG:23700.");
                attrs["srs"] = srs;
            }
        }
        else if (keys.Contains(prefix + TilesetFile))
        {
            // A 3D Tiles package carries its own placement (georeferenced root transform).
            asset.Kind = "3dtiles";
            asset.StorageKey = prefix + TilesetFile;
            var root = await ReadTilesetAsync(asset.StorageKey, ct);
            if (root["asset"]?["version"] is null)
                throw AppException.BadRequest("model.invalid_tileset", "tileset.json has no asset.version.");
        }
        else if (keys.Count == 1 && IsGlb(keys[0]))
        {
            asset.Kind = "gltf";
            asset.StorageKey = keys[0];
            attrs["placement"] = PlacementNode(request.Placement
                ?? throw AppException.BadRequest("model.placement_required", "A GLB has no georeference: placement (lon, lat, heightM, headingDeg) is required."));
        }
        else throw AppException.BadRequest("model.upload_missing", "The files have not been uploaded (or the upload expired).");

        if (!string.IsNullOrWhiteSpace(request.ElementIdProperty))
        {
            attrs["element_id_property"] = request.ElementIdProperty.Trim();
            asset.Segmented = true;
        }
        asset.Attrs = JsonSerializer.SerializeToDocument(attrs);
        db.ModelAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        if (asset.Status == ModelStatus.Converting) conversions.Wake();
        return asset;
    }

    public async Task<List<ModelAsset>> ListAsync(long siteId, CancellationToken ct)
    {
        await RequireSiteAsync(siteId, ct);
        return await db.ModelAssets.AsNoTracking().Where(m => m.SiteId == siteId).OrderBy(m => m.Id).ToListAsync(ct);
    }

    public async Task<ModelAsset> GetAsync(long id, CancellationToken ct) =>
        await db.ModelAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct)
        ?? throw AppException.NotFound("model.not_found", $"Model {id} not found.");

    public async Task<ModelAsset> UpdateAsync(long id, ModelUpdate request, CancellationToken ct)
    {
        var asset = await db.ModelAssets.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw AppException.NotFound("model.not_found", $"Model {id} not found.");
        var attrs = JsonNode.Parse(asset.Attrs.RootElement.GetRawText())!.AsObject();
        if (request.Name is { } name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw AppException.BadRequest("model.name_required", "name is required.");
            attrs["name"] = name.Trim();
        }
        if (request.Placement is { } placement)
        {
            if (asset.Kind != "gltf" && (asset.SourceFormat != ModelFormats.PointCloud || AttrString(asset, "srs") is not null))
                throw AppException.BadRequest("model.georeferenced", "A georeferenced package carries its own placement.");
            attrs["placement"] = PlacementNode(placement);
        }
        if (request.NodeIdSet)
        {
            if (request.NodeId is { } nodeId) await RequireNodeAsync(nodeId, asset.SiteId, ct);
            asset.NodeId = request.NodeId;
        }
        if (request.ElementIdPropertySet)
        {
            // Segmented = its elements are selectable by a stable id property (spec 9.1).
            var property = request.ElementIdProperty?.Trim();
            if (string.IsNullOrEmpty(property)) attrs.Remove("element_id_property");
            else attrs["element_id_property"] = property;
            asset.Segmented = !string.IsNullOrEmpty(property);
        }
        asset.Attrs = JsonSerializer.SerializeToDocument(attrs);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var asset = await db.ModelAssets.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw AppException.NotFound("model.not_found", $"Model {id} not found.");
        // Its element anchors go with it; those of finalized records are recorded (reason) instead of cascading silently.
        await db.RemoveFinalizedAnchorsAsync(db.Anchors.Where(a => a.ModelAssetId == id), ct);
        db.ModelAssets.Remove(asset);
        await db.SaveChangesAsync(ct);
        // Boxes (region anchors) do not depend on the model and stay valid for a replacement survey (spec 9.1).
        // Objects after the row: an orphan file is cleaned up later (spec 6.2), a row without its files would be worse.
        try { await storage.DeletePrefixAsync(PrefixRegex().Match(asset.StorageKey).Value, ct); } catch (Amazon.S3.AmazonS3Exception) { }
    }

    /// <summary>
    /// The tileset.json of a model (<paramref name="path"/>: a nested tileset of the package) with signed content URLs.
    /// </summary>
    public async Task<JsonObject> TilesetAsync(long id, string? path, string apiBase, CancellationToken ct)
    {
        var asset = await GetAsync(id, ct);
        if (asset.Status != ModelStatus.Ready)
            throw AppException.Conflict("model.not_ready", asset.Status == ModelStatus.Failed ? "The conversion failed." : "The model is being converted.");
        var lifetime = TimeSpan.FromMinutes(storageOptions.Value.TileUrlMinutes);
        var site = await db.Sites.AsNoTracking().FirstAsync(s => s.Id == asset.SiteId, ct);
        if (asset.Kind == "gltf")
        {
            if (path is not null) throw AppException.NotFound("model.not_found", "A GLB model has no nested tilesets.");
            var placement = PlacementOf(asset) ?? new ModelPlacement(site.RefPoint.X, site.RefPoint.Y, 0, 0);
            return GlbTileset(storage.PresignGet(asset.StorageKey, lifetime), placement, site.BaseHeightM);
        }

        var prefix = PrefixRegex().Match(asset.StorageKey).Value;
        // The root tileset is the model's key (an upload's root, or converted/ for a converted point cloud).
        var relative = path is null ? asset.StorageKey[prefix.Length..] : ValidPath(path);
        if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw AppException.NotFound("model.not_found", "Not a tileset.");
        var tileset = await ReadTilesetAsync(prefix + relative, ct);
        var directory = relative.Contains('/') ? relative[..(relative.LastIndexOf('/') + 1)] : "";
        RewriteTile(tileset["root"] as JsonObject, uri =>
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out _)) return uri; // http(s): or data: stays as it is
            var resolved = Resolve(directory, uri);
            return resolved.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? $"{apiBase}/models/{id}/tileset.json?path={Uri.EscapeDataString(resolved)}"
                : storage.PresignGet(prefix + resolved, lifetime);
        });
        // A package in local coordinates (e.g. a point cloud without its coordinate system) is placed like a GLB:
        // the placement frame goes before the root's own transform.
        if (path is null && PlacementOf(asset) is { } local && tileset["root"] is JsonObject root)
        {
            var frame = Geodesy.EnuTransform(local.Lon, local.Lat, site.BaseHeightM + local.HeightM, local.HeadingDeg);
            var own = (root["transform"] as JsonArray)?.Select(v => v!.GetValue<double>()).ToArray();
            var combined = own is { Length: 16 } ? Multiply(frame, own) : frame;
            root["transform"] = new JsonArray(combined.Select(v => (JsonNode)JsonValue.Create(Math.Round(v, 6))!).ToArray());
        }
        return tileset;
    }

    /// <summary>
    /// Reads the element ids of the model's GLB contents (server-side processing like the panorama analysis; nothing
    /// is returned to the browser but the counts). At most <see cref="MaxCheckedContents"/> contents are opened.
    /// </summary>
    public async Task<ElementCheck> CheckElementsAsync(long id, CancellationToken ct)
    {
        var asset = await GetAsync(id, ct);
        var property = AttrString(asset, "element_id_property")
            ?? throw AppException.BadRequest("model.not_segmented", "Set element_id_property first.");
        var keys = new List<string>();
        var truncated = false;
        if (asset.Kind == "gltf") keys.Add(asset.StorageKey);
        else
        {
            var prefix = PrefixRegex().Match(asset.StorageKey).Value;
            var pending = new Queue<string>([asset.StorageKey[prefix.Length..]]);
            var seen = new HashSet<string>();
            while (pending.TryDequeue(out var relative) && keys.Count < MaxCheckedContents)
            {
                if (!seen.Add(relative)) continue;
                var tileset = await ReadTilesetAsync(prefix + relative, ct);
                var directory = relative.Contains('/') ? relative[..(relative.LastIndexOf('/') + 1)] : "";
                RewriteTile(tileset["root"] as JsonObject, uri =>
                {
                    if (Uri.TryCreate(uri, UriKind.Absolute, out _)) return uri;
                    var resolved = Resolve(directory, uri);
                    if (resolved.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) pending.Enqueue(resolved);
                    else if (IsGlb(resolved)) keys.Add(prefix + resolved);
                    return uri;
                });
            }
            truncated = keys.Count > MaxCheckedContents || pending.Count > 0;
            keys = keys.Distinct().Take(MaxCheckedContents).ToList();
        }

        int withoutProperty = 0, elements = 0, missing = 0;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if ((await storage.SizeAsync(key, ct) ?? 0) > MaxCheckedBytes) { truncated = true; continue; }
            await using var stream = await storage.OpenReadAsync(key, ct);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            List<List<string?>>? tables;
            try { tables = GlbMetadata.ReadProperty(memory.ToArray(), property); }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException or KeyNotFoundException or InvalidOperationException)
            {
                throw AppException.BadRequest("model.invalid_glb", $"{key[(key.LastIndexOf('/') + 1)..]}: {e.Message}");
            }
            if (tables is null) { withoutProperty++; continue; }
            foreach (var table in tables)
            {
                var inTable = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in table)
                {
                    elements++;
                    if (value is null) { missing++; continue; }
                    distinct.Add(value);
                    if (!inTable.Add(value) && duplicates.Count < 50) duplicates.Add(value);
                }
            }
        }
        return new ElementCheck(property, keys.Count, withoutProperty, elements, distinct.Count, missing, duplicates.ToList(), truncated);
    }

    private async Task<JsonObject> ReadTilesetAsync(string key, CancellationToken ct)
    {
        var size = await storage.SizeAsync(key, ct) ?? throw AppException.NotFound("model.not_found", "Tileset not found.");
        if (size > MaxTilesetBytes) throw AppException.BadRequest("model.invalid_tileset", "tileset.json is too large.");
        await using var stream = await storage.OpenReadAsync(key, ct);
        try
        {
            var tileset = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsObject()
                ?? throw new JsonException();
            if (tileset["root"] is not JsonObject root) throw new JsonException();
            // Implicit tiling uses URI templates, which cannot be signed one by one.
            if (HasImplicitTiling(root))
                throw AppException.BadRequest("model.implicit_tiling", "Implicit tiling is not supported; export explicit tiles.");
            return tileset;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw AppException.BadRequest("model.invalid_tileset", "tileset.json is not a valid 3D Tiles tileset.");
        }
    }

    private static bool HasImplicitTiling(JsonObject tile) =>
        tile["implicitTiling"] is not null || (tile["children"] as JsonArray ?? []).OfType<JsonObject>().Any(HasImplicitTiling);

    /// <summary>content.uri (3D Tiles 1.0 also content.url) and contents[].uri of every tile.</summary>
    private static void RewriteTile(JsonObject? tile, Func<string, string> rewrite)
    {
        if (tile is null) return;
        var contents = new List<JsonObject>();
        if (tile["content"] is JsonObject content) contents.Add(content);
        contents.AddRange((tile["contents"] as JsonArray ?? []).OfType<JsonObject>());
        foreach (var c in contents)
            foreach (var name in new[] { "uri", "url" })
                if (c[name]?.GetValue<string>() is { } uri) c[name] = rewrite(uri);
        foreach (var child in (tile["children"] as JsonArray ?? []).OfType<JsonObject>()) RewriteTile(child, rewrite);
    }

    /// <summary>A relative URI of the package against the directory of its tileset; never leaves the package.</summary>
    private static string Resolve(string directory, string uri)
    {
        var clean = Uri.UnescapeDataString(uri.Split('?', '#')[0]);
        var parts = new List<string>(directory.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in clean.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) throw AppException.BadRequest("model.invalid_tileset", "A tile points outside the package.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    /// <summary>Column-major 4×4 product a·b.</summary>
    private static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[16];
        for (var col = 0; col < 4; col++)
            for (var row = 0; row < 4; row++)
                for (var k = 0; k < 4; k++)
                    r[col * 4 + row] += a[k * 4 + row] * b[col * 4 + k];
        return r;
    }

    /// <summary>
    /// One-tile tileset for a GLB. The root transform is the east-north-up frame at the placement point turned by the
    /// heading; Cesium turns the glTF y-up content to z-up, so glTF −Z points to the heading, +Y up.
    /// </summary>
    internal static JsonObject GlbTileset(string contentUrl, ModelPlacement placement, double baseHeightM)
    {
        var transform = Geodesy.EnuTransform(placement.Lon, placement.Lat, baseHeightM + placement.HeightM, placement.HeadingDeg);
        return new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "1.1" },
            ["geometricError"] = 500,
            ["root"] = new JsonObject
            {
                ["transform"] = new JsonArray(transform.Select(v => (JsonNode)JsonValue.Create(Math.Round(v, 6))!).ToArray()),
                // Generous bound: the GLB is not opened here (its bytes stay in the store); it only affects culling.
                ["boundingVolume"] = new JsonObject { ["sphere"] = new JsonArray(0, 0, 0, 500) },
                ["geometricError"] = 0,
                ["refine"] = "ADD",
                ["content"] = new JsonObject { ["uri"] = contentUrl },
            },
        };
    }

    public static ModelPlacement? PlacementOf(ModelAsset asset) =>
        asset.Attrs.RootElement.TryGetProperty("placement", out var p) && p.ValueKind == JsonValueKind.Object
            ? new ModelPlacement(p.GetProperty("lon").GetDouble(), p.GetProperty("lat").GetDouble(), p.GetProperty("height_m").GetDouble(),
                p.GetProperty("heading_deg").GetDouble())
            : null;

    public static string? AttrString(ModelAsset asset, string name) =>
        asset.Attrs.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonObject PlacementNode(ModelPlacement p)
    {
        if (p.Lon is < -180 or > 180 || p.Lat is < -90 or > 90 || double.IsNaN(p.HeightM) || Math.Abs(p.HeightM) > 10000 || double.IsNaN(p.HeadingDeg))
            throw AppException.BadRequest("model.invalid_placement", "placement: lon −180…180, lat −90…90, heightM (m above the site base), headingDeg.");
        return new JsonObject
        {
            ["lon"] = p.Lon, ["lat"] = p.Lat, ["height_m"] = p.HeightM, ["heading_deg"] = ((p.HeadingDeg % 360) + 360) % 360,
        };
    }

    private static string ValidPath(string? path)
    {
        var p = (path ?? "").Replace('\\', '/');
        var segments = p.Split('/');
        if (p.Length is 0 or > 512 || p.StartsWith('/') || segments.Any(s => s is "" or "." or ".." || s.Any(char.IsControl)))
            throw AppException.BadRequest("model.invalid_files", $"Invalid file path: {path}");
        return p;
    }

    private static bool IsGlb(string path) => path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".glb" => "model/gltf-binary",
        ".gltf" => "model/gltf+json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".ktx2" => "image/ktx2",
        ".webp" => "image/webp",
        _ => "application/octet-stream", // b3dm, i3dm, pnts, cmpt, subtree, bin
    };

    private async Task RequireSiteAsync(long siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
    }

    private async Task RequireNodeAsync(long nodeId, long siteId, CancellationToken ct)
    {
        var nodeSite = await db.Nodes.Where(n => n.Id == nodeId).Select(n => (long?)n.SiteId).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("node.not_found", $"Node {nodeId} not found.");
        if (nodeSite != siteId) throw AppException.BadRequest("model.other_site", "The node is on another site.");
    }
}
