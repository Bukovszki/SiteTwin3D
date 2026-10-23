using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Model;
using Api.Services.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Maps;

public record LinkLayerRequest(string Layer, long TypeId, string NameField, long? ParentId);

public record LinkLayerResult(int Created, int AlreadyLinked);

public record LinkedLayer(string Layer, int Count);

public record OrphanNode(long NodeId, string Name, string FeatureId);

public record SyncResult(int Features, int Linked, int Unlinked, List<OrphanNode> Orphans);

public record LayerGridRow(string FeatureId, long? NodeId, string? NodeName, JsonElement Properties);

public record LayerGridPage(int? Total, List<LayerField> Fields, List<LayerGridRow> Rows);

/// <summary>
/// Descriptive data from MapManager layers, as a live reference (spec 6.3, decided): skeleton records in the node
/// table carry attrs.mapmanager_ref = { layer, featureId }; the attributes are NOT copied, they are read per request
/// over WFS. Only layers referenced by the site's records may be read.
/// </summary>
public class LinkService(AppDbContext db, MapManagerClient mapManager, NodeService nodes)
{
    private const int PageSize = 1000;
    public const int MaxGridRows = 200;

    /// <summary>Creates skeleton records for the layer's features that are not linked yet on this site.</summary>
    public async Task<LinkLayerResult> LinkLayerAsync(long siteId, LinkLayerRequest request, CancellationToken ct)
    {
        await RequireSiteAsync(siteId, ct);
        var layer = ValidLayerName(request.Layer);
        var type = await db.NodeTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TypeId, ct)
            ?? throw AppException.BadRequest("node.unknown_type", $"Node type {request.TypeId} does not exist.");
        if (request.ParentId is { } parentId) await nodes.CheckParentAsync(parentId, siteId, type, ct);

        var fields = await mapManager.FieldsAsync(layer, ct);
        if (!fields.Any(f => f.Name == request.NameField && f.Kind != "geometry"))
            throw AppException.BadRequest("maps.unknown_field", $"'{request.NameField}' is not a field of the layer.");

        var linked = (await LinkedFeatureIdsAsync(siteId, layer, ct)).Keys.ToHashSet();
        int created = 0, already = 0;
        for (var start = 0; ; start += PageSize)
        {
            var page = await mapManager.FeaturesAsync(layer, [request.NameField], null, null, start, PageSize, ct);
            foreach (var feature in page.Features)
            {
                if (!linked.Add(feature.Id)) { already++; continue; }
                var name = feature.Properties.TryGetProperty(request.NameField, out var value) && value.ValueKind != JsonValueKind.Null
                    ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()) : null;
                name = string.IsNullOrWhiteSpace(name) ? feature.Id : name.Trim();
                db.Nodes.Add(new Node
                {
                    SiteId = siteId,
                    ParentId = request.ParentId,
                    TypeId = type.Id,
                    Name = name.Length > NodeService.MaxNameLength ? name[..NodeService.MaxNameLength] : name,
                    Attrs = RefDocument(layer, feature.Id),
                });
                created++;
            }
            if (page.Features.Count < PageSize) break;
        }
        await db.SaveChangesAsync(ct);
        return new LinkLayerResult(created, already);
    }

    /// <summary>Fields of any layer the organization's token may read (to choose the name field before linking).</summary>
    public async Task<List<LayerField>> LayerFieldsAsync(string layer, CancellationToken ct) =>
        (await mapManager.FieldsAsync(ValidLayerName(layer), ct)).Where(f => f.Kind != "geometry").ToList();

    /// <summary>Layers referenced by the site's records, with the number of linked records.</summary>
    public async Task<List<LinkedLayer>> LinkedLayersAsync(long siteId, CancellationToken ct)
    {
        await RequireSiteAsync(siteId, ct);
        // Grouping by a JSON path does not translate to SQL: the layer names are read and counted here.
        var layers = await db.Nodes.AsNoTracking()
            .Where(n => n.SiteId == siteId)
            .Select(n => n.Attrs.RootElement.GetProperty(AttrValidator.MapManagerRefKey).GetProperty("layer").GetString())
            .Where(layer => layer != null)
            .ToListAsync(ct);
        return layers.GroupBy(l => l!).Select(g => new LinkedLayer(g.Key, g.Count())).OrderBy(l => l.Layer).ToList();
    }

    /// <summary>
    /// Compares the layer with the linked records: features without a record (new in MapManager) and records whose
    /// feature is gone (orphans – deleted only after confirmation, by the user).
    /// </summary>
    public async Task<SyncResult> SyncAsync(long siteId, string layer, CancellationToken ct)
    {
        await RequireLinkedAsync(siteId, layer, ct);
        var fields = await mapManager.FieldsAsync(layer, ct);
        var probe = fields.FirstOrDefault(f => f.Kind != "geometry")?.Name;
        var featureIds = new HashSet<string>();
        for (var start = 0; ; start += PageSize)
        {
            var page = await mapManager.FeaturesAsync(layer, probe is null ? [] : [probe], null, null, start, PageSize, ct);
            foreach (var feature in page.Features) featureIds.Add(feature.Id);
            if (page.Features.Count < PageSize) break;
        }

        var linked = await LinkedFeatureIdsAsync(siteId, layer, ct);
        var orphans = linked.Where(l => !featureIds.Contains(l.Key))
            .Select(l => new OrphanNode(l.Value.Id, l.Value.Name, l.Key)).OrderBy(o => o.Name).ToList();
        return new SyncResult(featureIds.Count, linked.Count - orphans.Count, featureIds.Count(id => !linked.ContainsKey(id)), orphans);
    }

    /// <summary>
    /// A page of the layer for the grid (spec 6.3): rows come from MapManager (paging, sorting, filtering there), each
    /// with its linked record. Filters are turned into CQL here, on fields of the layer only, with quoted values.
    /// </summary>
    public async Task<LayerGridPage> GridAsync(long siteId, string layer, IReadOnlyList<GridFilter> filters, string? sort, bool descending,
        int first, int rows, CancellationToken ct)
    {
        await RequireLinkedAsync(siteId, layer, ct);
        var fields = (await mapManager.FieldsAsync(layer, ct)).Where(f => f.Kind != "geometry").ToList();
        var byName = fields.ToDictionary(f => f.Name);

        var cql = string.Join(" AND ", filters.Select(f => CqlCondition(f, byName)));
        string? sortBy = null;
        if (!string.IsNullOrEmpty(sort))
        {
            if (!byName.ContainsKey(sort)) throw AppException.BadRequest("grid.unknown_field", $"Unknown field '{sort}'.");
            sortBy = $"{sort} {(descending ? "D" : "A")}";
        }
        var page = await mapManager.FeaturesAsync(layer, fields.Select(f => f.Name).ToList(), cql.Length == 0 ? null : cql, sortBy,
            Math.Max(0, first), Math.Clamp(rows, 1, MaxGridRows), ct);

        var linked = await LinkedFeatureIdsAsync(siteId, layer, ct);
        return new LayerGridPage(page.Total, fields, page.Features.Select(f => linked.TryGetValue(f.Id, out var n)
            ? new LayerGridRow(f.Id, n.Id, n.Name, f.Properties)
            : new LayerGridRow(f.Id, null, null, f.Properties)).ToList());
    }

    /// <summary>
    /// Live attributes of a linked record (data sheet). Null if the node is not linked; 502 if MapManager is not
    /// reachable – the hierarchy, panoramas and anchors stay usable (spec 6.3).
    /// </summary>
    public async Task<JsonElement?> LinkedAttributesAsync(long nodeId, CancellationToken ct)
    {
        var node = await nodes.GetAsync(nodeId, ct);
        if (!TryRef(node.Attrs.RootElement, out var layer, out var featureId)) return null;
        var features = await mapManager.FeaturesByIdAsync(layer, [featureId], ct);
        return features.FirstOrDefault(f => f.Id == featureId)?.Properties
            ?? throw AppException.NotFound("maps.feature_not_found", "The linked feature no longer exists in MapManager (orphan record).");
    }

    public static bool TryRef(JsonElement attrs, out string layer, out string featureId)
    {
        layer = featureId = "";
        if (!AttrValidator.IsLinked(attrs)) return false;
        var r = attrs.GetProperty(AttrValidator.MapManagerRefKey);
        if (!r.TryGetProperty("layer", out var l) || !r.TryGetProperty("featureId", out var f)) return false;
        layer = l.GetString() ?? "";
        featureId = f.GetString() ?? "";
        return layer.Length > 0 && featureId.Length > 0;
    }

    private async Task<Dictionary<string, (long Id, string Name)>> LinkedFeatureIdsAsync(long siteId, string layer, CancellationToken ct)
    {
        var rows = await db.Nodes.AsNoTracking()
            .Where(n => n.SiteId == siteId && n.Attrs.RootElement.GetProperty(AttrValidator.MapManagerRefKey).GetProperty("layer").GetString() == layer)
            .Select(n => new { n.Id, n.Name, FeatureId = n.Attrs.RootElement.GetProperty(AttrValidator.MapManagerRefKey).GetProperty("featureId").GetString() })
            .ToListAsync(ct);
        return rows.Where(r => r.FeatureId != null).GroupBy(r => r.FeatureId!).ToDictionary(g => g.Key, g => (g.First().Id, g.First().Name));
    }

    private async Task RequireSiteAsync(long siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
    }

    /// <summary>Only layers that the site's records reference may be read (spec 6.3).</summary>
    private async Task RequireLinkedAsync(long siteId, string layer, CancellationToken ct)
    {
        await RequireSiteAsync(siteId, ct);
        ValidLayerName(layer);
        var referenced = await db.Nodes.AnyAsync(n => n.SiteId == siteId
            && n.Attrs.RootElement.GetProperty(AttrValidator.MapManagerRefKey).GetProperty("layer").GetString() == layer, ct);
        if (!referenced) throw AppException.NotFound("maps.layer_not_linked", $"Layer '{layer}' is not linked on this site.");
    }

    /// <summary>"workspace:layer" of letters, digits, _ and - only (it goes into a WFS request).</summary>
    private static string ValidLayerName(string? layer)
    {
        var value = (layer ?? "").Trim();
        var parts = value.Split(':');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw AppException.BadRequest("maps.invalid_layer", "Layer: workspace:layer.");
        return value;
    }

    private static JsonDocument RefDocument(string layer, string featureId) =>
        JsonSerializer.SerializeToDocument(new JsonObject
        {
            [AttrValidator.MapManagerRefKey] = new JsonObject { ["layer"] = layer, ["featureId"] = featureId },
        });

    /// <summary>One ECQL condition; the field must be a field of the layer, values are quoted / numeric only.</summary>
    private static string CqlCondition(GridFilter filter, Dictionary<string, LayerField> fields)
    {
        if (!fields.TryGetValue(filter.Field, out var field))
            throw AppException.BadRequest("grid.unknown_field", $"Unknown field '{filter.Field}'.");
        var name = field.Name;
        string Text() => filter.Value switch
        {
            { ValueKind: JsonValueKind.String } v => "'" + v.GetString()!.Replace("'", "''") + "'",
            _ => throw AppException.BadRequest("grid.invalid_filter", $"Filter on '{name}' needs a text value."),
        };
        string Number() => filter.Value is { ValueKind: JsonValueKind.Number } v
            ? v.GetDouble().ToString(CultureInfo.InvariantCulture)
            : throw AppException.BadRequest("grid.invalid_filter", $"Filter on '{name}' needs a number.");

        return (field.Kind, filter.Op) switch
        {
            (_, "empty") => $"{name} IS NULL",
            ("text", "contains") => $"{name} ILIKE {Like(filter)}",
            ("text", "eq") or ("date", "eq") => $"{name} = {Text()}",
            ("number", "eq") => $"{name} = {Number()}",
            ("number", "gte") => $"{name} >= {Number()}",
            ("number", "lte") => $"{name} <= {Number()}",
            ("boolean", "eq") when filter.Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } v =>
                $"{name} = {(v.GetBoolean() ? "true" : "false")}",
            _ => throw AppException.BadRequest("grid.invalid_filter", $"Operator '{filter.Op}' is not supported on '{name}'."),
        };
    }

    private static string Like(GridFilter filter)
    {
        if (filter.Value is not { ValueKind: JsonValueKind.String } v)
            throw AppException.BadRequest("grid.invalid_filter", $"Filter on '{filter.Field}' needs a text value.");
        var escaped = new StringBuilder();
        foreach (var c in v.GetString()!)
        {
            if (c is '%' or '_' or '\\') escaped.Append('\\');
            escaped.Append(c == '\'' ? "''" : c.ToString());
        }
        return $"'%{escaped}%'";
    }
}
