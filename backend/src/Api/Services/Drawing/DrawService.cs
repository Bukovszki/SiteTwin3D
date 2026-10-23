using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Services.Drawing;

/// <summary>A field of a drawing layer (like MapManager's adatstruktura): text | integer | number | boolean | date.</summary>
public record DrawField(string Name, string Type, int? Length, int? Decimals, string? Alias);

public record DrawLayerCreate(long SiteId, long? LevelId, string Name, string GeometryType, List<DrawField>? Fields, JsonElement? Style, int? SortOrder);

public record DrawLayerUpdate(string? Name = null, List<DrawField>? Fields = null, JsonElement? Style = null, int? SortOrder = null);

/// <summary>A drawn element as GeoJSON in EPSG:3857 with its record version (optimistic locking).</summary>
public record DrawFeature(long Id, int Seq, int Version, long? NodeId, Geometry Geometry, JsonElement Attrs);

public record DrawInsert(string? Ref, Geometry Geometry, Dictionary<string, JsonElement>? Attrs, long? NodeId);

/// <summary>An update; geometry / attrs / node only if sent. <see cref="ClearNode"/> removes the link to the node.</summary>
public record DrawUpdate(long Id, int Version, Geometry? Geometry, Dictionary<string, JsonElement>? Attrs, long? NodeId, bool? ClearNode);

public record DrawDelete(long Id, int Version);

public record DrawTransaction(List<DrawInsert>? Inserts, List<DrawUpdate>? Updates, List<DrawDelete>? Deletes);

/// <summary>Result: new ids of the inserts (in order) and the new versions of the updated records.</summary>
public record DrawTransactionResult(List<long> Inserted, Dictionary<long, int> Versions, int Deleted);

/// <summary>One error of a transaction: which item (insert / update / delete and its index) and what.</summary>
public record DrawError(string Item, int Index, string Code, string? Field, string Message);

/// <summary>
/// Drawn supplementary elements (spec 8.6, FR-AR7), following MapManager's structure and editing: layer (field
/// description) → record (attributes, version) → geometry (1:N; here one per record). Editing in EPSG:3857, storage in
/// WGS84. A transaction is all or nothing: every validation error at once (400); a stale version → 409 with the ids.
/// </summary>
public partial class DrawService(AppDbContext db, CurrentUser currentUser)
{
    public const int MaxTransactionItems = 2000;
    private static readonly string[] GeometryTypes = ["Point", "MultiLineString", "MultiPolygon"];
    private static readonly string[] FieldTypes = ["text", "integer", "number", "boolean", "date"];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex FieldNameRegex();

    public async Task<List<DrawLayer>> LayersAsync(long siteId, long? levelId, bool outdoor, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
        var query = db.DrawLayers.AsNoTracking().Where(l => l.SiteId == siteId);
        if (levelId is { } level) query = query.Where(l => l.LevelId == level);
        else if (outdoor) query = query.Where(l => l.LevelId == null);
        return await query.OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
    }

    public async Task<DrawLayer> CreateLayerAsync(DrawLayerCreate request, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == request.SiteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {request.SiteId} not found.");
        if (request.LevelId is { } levelId)
        {
            var ok = await db.Nodes.AnyAsync(n => n.Id == levelId && n.SiteId == request.SiteId && n.Type.IsLevel, ct);
            if (!ok) throw AppException.BadRequest("level.invalid", "levelId must be a level of the same site.");
        }
        if (!GeometryTypes.Contains(request.GeometryType))
            throw AppException.BadRequest("draw.invalid_geometry_type", "geometryType: Point, MultiLineString or MultiPolygon.");
        var layer = new DrawLayer
        {
            SiteId = request.SiteId,
            LevelId = request.LevelId,
            Name = ValidName(request.Name),
            GeometryType = request.GeometryType,
            Structure = StructureDocument(request.Fields ?? []),
            Style = SiteService.ObjectDocument(request.Style),
            SortOrder = request.SortOrder ?? 0,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = currentUser.UserId,
        };
        db.DrawLayers.Add(layer);
        await db.SaveChangesAsync(ct);
        return layer;
    }

    /// <summary>Name, style, order; fields can be added or renamed (alias), existing data is not converted.</summary>
    public async Task<DrawLayer> UpdateLayerAsync(long id, DrawLayerUpdate request, CancellationToken ct)
    {
        var layer = await FindLayerAsync(id, ct);
        if (request.Name is not null) layer.Name = ValidName(request.Name);
        if (request.Fields is not null) layer.Structure = StructureDocument(request.Fields);
        if (request.Style is not null) layer.Style = SiteService.ObjectDocument(request.Style);
        if (request.SortOrder is { } sortOrder) layer.SortOrder = sortOrder;
        await db.SaveChangesAsync(ct);
        return layer;
    }

    public async Task DeleteLayerAsync(long id, CancellationToken ct)
    {
        currentUser.RequireEditableSite((await FindLayerAsync(id, ct)).SiteId);
        if (await db.DrawLayers.Where(l => l.Id == id).ExecuteDeleteAsync(ct) == 0)
            throw AppException.NotFound("draw_layer.not_found", $"Drawing layer {id} not found.");
    }

    public async Task<DrawLayer> FindLayerAsync(long id, CancellationToken ct) =>
        await db.DrawLayers.FirstOrDefaultAsync(l => l.Id == id, ct)
        ?? throw AppException.NotFound("draw_layer.not_found", $"Drawing layer {id} not found.");

    public static List<DrawField> Fields(DrawLayer layer) =>
        layer.Structure.RootElement.TryGetProperty("fields", out var f)
            ? f.Deserialize<List<DrawField>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [] : [];

    /// <summary>Elements of a layer, optionally within a bounding box (EPSG:3857), geometries in EPSG:3857.</summary>
    public async Task<List<DrawFeature>> FeaturesAsync(long layerId, double[]? bbox3857, CancellationToken ct)
    {
        await FindLayerAsync(layerId, ct);
        var query = db.DrawGeometries.AsNoTracking().Where(g => g.LayerId == layerId);
        if (bbox3857 is { Length: 4 } b)
        {
            var envelope = WebMercator.EnvelopeToWgs84(new Envelope(b[0], b[2], b[1], b[3]));
            var box = new GeometryFactory(new PrecisionModel(), 4326).ToGeometry(envelope);
            query = query.Where(g => g.Geom.Intersects(box));
        }
        var rows = await query.Join(db.DrawRecords.AsNoTracking(), g => g.RecordId, r => r.Id, (g, r) => new { g.Geom, Record = r })
            .OrderBy(x => x.Record.Seq).ToListAsync(ct);
        return rows.Select(x => new DrawFeature(x.Record.Id, x.Record.Seq, x.Record.Version, x.Record.NodeId,
            WebMercator.FromWgs84(x.Geom), x.Record.Attrs.RootElement.Clone())).ToList();
    }

    public async Task<DrawTransactionResult> ApplyAsync(long layerId, DrawTransaction tx, CancellationToken ct)
    {
        var layer = await FindLayerAsync(layerId, ct);
        currentUser.RequireEditableSite(layer.SiteId); // geometry updates are bulk updates
        var fields = Fields(layer).ToDictionary(f => f.Name);
        var inserts = tx.Inserts ?? [];
        var updates = tx.Updates ?? [];
        var deletes = tx.Deletes ?? [];
        if (inserts.Count + updates.Count + deletes.Count > MaxTransactionItems)
            throw AppException.BadRequest("draw.too_many_items", $"At most {MaxTransactionItems} changes in one transaction.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ids = updates.Select(u => u.Id).Concat(deletes.Select(d => d.Id)).ToList();
        var records = await db.DrawRecords.Where(r => r.LayerId == layerId && ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        // Versions first: someone else saved in the meantime → 409, nothing is changed.
        var conflicts = updates.Select(u => (u.Id, u.Version)).Concat(deletes.Select(d => (d.Id, d.Version)))
            .Where(x => !records.TryGetValue(x.Id, out var r) || r.Version != x.Version).Select(x => x.Id).Distinct().ToList();
        if (conflicts.Count > 0)
            throw new AppException(StatusCodes.Status409Conflict, "draw.version_conflict",
                "Some elements were changed or deleted by someone else; reload the layer.", conflicts);

        var nodeIds = inserts.Select(i => i.NodeId).Concat(updates.Select(u => u.NodeId)).OfType<long>().Distinct().ToList();
        var knownNodes = await db.Nodes.Where(n => nodeIds.Contains(n.Id) && n.SiteId == layer.SiteId).Select(n => n.Id).ToListAsync(ct);

        var errors = new List<DrawError>();
        var preparedInserts = inserts.Select((insert, i) => (
            Geom: CheckGeometry(layer, insert.Geometry, "insert", i, errors),
            Attrs: CheckAttrs(fields, insert.Attrs, null, "insert", i, errors),
            insert.NodeId)).ToList();
        for (var i = 0; i < inserts.Count; i++)
            if (inserts[i].NodeId is { } n && !knownNodes.Contains(n))
                errors.Add(new DrawError("insert", i, "node.not_found", "nodeId", "Node not found on this site."));
        var preparedUpdates = updates.Select((update, i) => (
            update,
            Geom: update.Geometry is null ? null : CheckGeometry(layer, update.Geometry, "update", i, errors),
            Attrs: update.Attrs is null ? null : CheckAttrs(fields, update.Attrs, records[update.Id].Attrs, "update", i, errors))).ToList();
        for (var i = 0; i < updates.Count; i++)
            if (updates[i].NodeId is { } n && !knownNodes.Contains(n))
                errors.Add(new DrawError("update", i, "node.not_found", "nodeId", "Node not found on this site."));
        if (errors.Count > 0)
            throw AppException.BadRequest("draw.invalid", $"{errors.Count} error(s); nothing was saved.", errors);

        var now = DateTime.UtcNow;
        var userId = currentUser.UserId;
        // Deletes: the geometries, and the record itself (it has no other geometry here, spec 8.6).
        foreach (var d in deletes) db.DrawRecords.Remove(records[d.Id]);

        var versions = new Dictionary<long, int>();
        foreach (var (update, geom, attrs) in preparedUpdates)
        {
            var record = records[update.Id];
            if (attrs is not null) record.Attrs = attrs;
            if (update.NodeId is { } nodeId) record.NodeId = nodeId;
            if (update.ClearNode == true) record.NodeId = null;
            if (geom is not null)
                await db.DrawGeometries.Where(g => g.RecordId == record.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.Geom, geom), ct);
            record.Version++;
            record.ModifiedAt = now;
            record.ModifiedBy = userId;
            versions[record.Id] = record.Version;
        }

        var seq = await db.DrawRecords.Where(r => r.LayerId == layerId).MaxAsync(r => (int?)r.Seq, ct) ?? 0;
        var newRecords = new List<DrawRecord>();
        foreach (var (geom, attrs, nodeId) in preparedInserts)
        {
            var record = new DrawRecord { LayerId = layerId, Seq = ++seq, Attrs = attrs!, NodeId = nodeId, ModifiedAt = now, ModifiedBy = userId };
            db.DrawRecords.Add(record);
            newRecords.Add(record);
        }
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < newRecords.Count; i++)
            db.DrawGeometries.Add(new DrawGeometry { RecordId = newRecords[i].Id, LayerId = layerId, Geom = preparedInserts[i].Geom! });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DrawTransactionResult(newRecords.Select(r => r.Id).ToList(), versions, deletes.Count);
    }

    /// <summary>3857 → WGS84, type of the layer (single parts promoted to multi, like ST_Multi), valid.</summary>
    private static Geometry? CheckGeometry(DrawLayer layer, Geometry? geometry, string item, int index, List<DrawError> errors)
    {
        if (geometry is null)
        {
            errors.Add(new DrawError(item, index, "draw.geometry_required", "geometry", "Geometry is required."));
            return null;
        }
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var wgs = WebMercator.ToWgs84(geometry);
        Geometry? typed = (layer.GeometryType, wgs) switch
        {
            ("Point", Point p) => p,
            ("MultiLineString", LineString l) => factory.CreateMultiLineString([l]),
            ("MultiLineString", MultiLineString m) => m,
            ("MultiPolygon", Polygon p) => factory.CreateMultiPolygon([p]),
            ("MultiPolygon", MultiPolygon m) => m,
            _ => null,
        };
        if (typed is null)
        {
            errors.Add(new DrawError(item, index, "draw.wrong_geometry_type", "geometry", $"The layer takes {layer.GeometryType}, not {geometry.GeometryType}."));
            return null;
        }
        typed.SRID = 4326;
        if (!typed.IsValid)
        {
            errors.Add(new DrawError(item, index, "geometry.invalid", "geometry", "The geometry is not valid (e.g. self-intersecting)."));
            return null;
        }
        return typed;
    }

    /// <summary>Attributes by the field description: unknown field, type, length, decimals. Merged into the existing ones.</summary>
    private static JsonDocument? CheckAttrs(Dictionary<string, DrawField> fields, Dictionary<string, JsonElement>? attrs, JsonDocument? existing,
        string item, int index, List<DrawError> errors)
    {
        var result = existing is null ? new JsonObject() : JsonNode.Parse(existing.RootElement.GetRawText())!.AsObject();
        var ok = true;
        foreach (var (name, value) in attrs ?? [])
        {
            if (!fields.TryGetValue(name, out var field))
            {
                errors.Add(new DrawError(item, index, "draw.unknown_field", name, $"'{name}' is not a field of the layer."));
                ok = false;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString() == ""))
            {
                result.Remove(name);
                continue;
            }
            var (converted, problem) = Convert(field, value);
            if (problem is not null)
            {
                errors.Add(new DrawError(item, index, "draw.invalid_value", name, problem));
                ok = false;
                continue;
            }
            result[name] = converted;
        }
        return ok ? JsonSerializer.SerializeToDocument(result) : null;
    }

    private static (JsonNode? Value, string? Problem) Convert(DrawField field, JsonElement value)
    {
        switch (field.Type)
        {
            case "text":
                var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                return field.Length is { } max && text.Length > max ? (null, $"At most {max} characters.") : (JsonValue.Create(text), null);
            case "integer":
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l) ? (JsonValue.Create(l), null) : (null, "A whole number is required.");
            case "number":
                if (value.ValueKind != JsonValueKind.Number) return (null, "A number is required.");
                var d = value.GetDouble();
                return field.Decimals is { } decimals && Math.Round(d, decimals) != d ? (null, $"At most {decimals} decimals.") : (JsonValue.Create(d), null);
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? (JsonValue.Create(value.GetBoolean()), null) : (null, "true or false is required.");
            case "date":
                return value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? (JsonValue.Create(value.GetString()), null) : (null, "A date (YYYY-MM-DD) is required.");
            default:
                return (null, "Unknown field type.");
        }
    }

    private static JsonDocument StructureDocument(List<DrawField> fields)
    {
        foreach (var f in fields)
        {
            if (!FieldNameRegex().IsMatch(f.Name ?? ""))
                throw AppException.BadRequest("draw.invalid_field_name", "Field names: lowercase letter first, then lowercase letters, digits, _ (max 63).");
            if (!FieldTypes.Contains(f.Type))
                throw AppException.BadRequest("draw.invalid_field_type", "Field type: text, integer, number, boolean or date.");
        }
        if (fields.Select(f => f.Name).Distinct().Count() != fields.Count)
            throw AppException.BadRequest("draw.duplicate_field", "Field names must be unique.");
        return JsonSerializer.SerializeToDocument(new { fields }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string ValidName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 200) throw AppException.BadRequest("draw_layer.invalid_name", "Name: 1–200 characters.");
        return trimmed;
    }
}
