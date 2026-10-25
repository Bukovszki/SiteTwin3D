using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Hierarchy;

/// <summary>One table row; attribute values may be text (CSV) or typed (XLSX), they are converted by the schema.</summary>
public record ImportRow(string? Code, string? Name, Dictionary<string, JsonElement>? Attrs);

public record ImportRequest(long TypeId, long? ParentId, List<ImportRow> Rows);

public record ImportResult(int Created, int Updated);

/// <summary>An error of a row (1-based, as in the file without its header).</summary>
public record ImportError(int Row, string Code, string? Field, string Message);

/// <summary>
/// Bulk import of technical data from a table (FR-I3), for records not in MapManager. The client reads CSV/XLSX and
/// maps the columns; here the values are converted to the schema types, rows with an existing code (same site and
/// type) are updated (given fields merged), others created under the parent. All or nothing: any error → 400 with
/// every error, nothing saved.
/// </summary>
public class ImportService(AppDbContext db, NodeService nodes, NodeTypeService types)
{
    public const int MaxRows = 10_000;

    public async Task<ImportResult> ImportAsync(long siteId, ImportRequest request, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
        if (request.Rows is not { Count: > 0 })
            throw AppException.BadRequest("import.empty", "The import contains no rows.");
        if (request.Rows.Count > MaxRows)
            throw AppException.BadRequest("import.too_many_rows", $"At most {MaxRows} rows can be imported at once.");
        var type = await db.NodeTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TypeId, ct)
            ?? throw AppException.BadRequest("node.unknown_type", $"Node type {request.TypeId} does not exist.");
        await nodes.CheckParentAsync(request.ParentId, siteId, type, ct);

        // The type's fields with the organization's own ones (added to a shared base type).
        var schema = await types.SchemaOfAsync(type, ct);
        var properties = SchemaProperties(schema);
        var codes = request.Rows.Select(r => r.Code?.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToList();
        var existing = await db.Nodes.Where(n => n.SiteId == siteId && n.TypeId == type.Id && n.Code != null && codes.Contains(n.Code))
            .ToDictionaryAsync(n => n.Code!, ct);

        var errors = new List<ImportError>();
        var seen = new HashSet<string>();
        int created = 0, updated = 0;
        for (var i = 0; i < request.Rows.Count; i++)
        {
            var rowNumber = i + 1;
            var row = request.Rows[i];
            var code = string.IsNullOrWhiteSpace(row.Code) ? null : row.Code.Trim();
            var name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim();
            if (code is not null && !seen.Add(code))
            {
                errors.Add(new(rowNumber, "import.duplicate_code", "code", $"Code '{code}' appears more than once."));
                continue;
            }

            var values = Convert(row.Attrs, properties, rowNumber, errors);
            if (values is null) continue;

            if (code is not null && existing.TryGetValue(code, out var node))
            {
                if (AttrValidator.IsLinked(node.Attrs.RootElement))
                {
                    errors.Add(new(rowNumber, "node.linked_read_only", null, "The record is linked to MapManager; its data is read only."));
                    continue;
                }
                var merged = JsonNode.Parse(node.Attrs.RootElement.GetRawText())!.AsObject();
                foreach (var (key, value) in values) merged[key] = value?.DeepClone();
                if (!Validate(schema, merged, rowNumber, errors)) continue;
                node.Attrs = JsonSerializer.SerializeToDocument(merged);
                if (name is not null) node.Name = name;
                updated++;
            }
            else
            {
                if ((name ?? code) is not { } nodeName)
                {
                    errors.Add(new(rowNumber, "import.name_required", "name", "A new record needs a name (or at least a code)."));
                    continue;
                }
                if (!Validate(schema, values, rowNumber, errors)) continue;
                db.Nodes.Add(new Node
                {
                    SiteId = siteId,
                    ParentId = request.ParentId,
                    TypeId = type.Id,
                    Code = code,
                    Name = nodeName.Length > NodeService.MaxNameLength ? nodeName[..NodeService.MaxNameLength] : nodeName,
                    Attrs = JsonSerializer.SerializeToDocument(values),
                });
                created++;
            }
        }

        if (errors.Count > 0)
            throw AppException.BadRequest("import.invalid", $"{errors.Count} error(s); nothing was imported.", errors);
        await db.SaveChangesAsync(ct);
        return new ImportResult(created, updated);
    }

    private static bool Validate(JsonDocument? schema, JsonObject attrs, int rowNumber, List<ImportError> errors)
    {
        try
        {
            AttrValidator.Validate(schema, JsonSerializer.SerializeToElement(attrs));
            return true;
        }
        catch (AppException ex) when (ex.Errors is List<AttrValidator.AttrError> attrErrors)
        {
            errors.AddRange(attrErrors.Select(e => new ImportError(rowNumber, "node.invalid_attrs", e.Path.TrimStart('/'), e.Message)));
            return false;
        }
    }

    /// <summary>Schema property name → JSON type ("string" when not given). Null: the type has no schema.</summary>
    private static Dictionary<string, string>? SchemaProperties(JsonDocument? schema)
    {
        if (schema is null || !schema.RootElement.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;
        return properties.EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "string");
    }

    /// <summary>Converted values; null if the row has conversion errors (added to <paramref name="errors"/>).</summary>
    private static JsonObject? Convert(Dictionary<string, JsonElement>? attrs, Dictionary<string, string>? properties, int row, List<ImportError> errors)
    {
        var result = new JsonObject();
        var ok = true;
        foreach (var (key, value) in attrs ?? [])
        {
            if (key == AttrValidator.MapManagerRefKey || (properties is not null && !properties.ContainsKey(key)))
            {
                errors.Add(new(row, "import.unknown_field", key, $"'{key}' is not a field of the node type."));
                ok = false;
                continue;
            }
            // Empty cell: the field is left out (an existing value stays).
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                continue;

            var converted = (properties?.GetValueOrDefault(key) ?? "string") switch
            {
                "number" => ToNumber(value, integer: false),
                "integer" => ToNumber(value, integer: true),
                "boolean" => ToBoolean(value),
                _ => ToText(value),
            };
            if (converted is null)
            {
                errors.Add(new(row, "import.invalid_value", key, $"'{value}' cannot be converted to {properties![key]}."));
                ok = false;
                continue;
            }
            result[key] = converted;
        }
        return ok ? result : null;
    }

    internal static JsonNode? ToNumber(JsonElement value, bool integer)
    {
        double number;
        if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble();
        else if (value.ValueKind == JsonValueKind.String)
        {
            // Hungarian tables: decimal comma, space or non-breaking space as thousands separator.
            var text = value.GetString()!.Trim().Replace(" ", "").Replace(" ", "").Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
        }
        else return null;

        if (!integer) return JsonValue.Create(number);
        return number == Math.Floor(number) && Math.Abs(number) < long.MaxValue ? JsonValue.Create((long)number) : null;
    }

    private static JsonNode? ToBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => JsonValue.Create(true),
        JsonValueKind.False => JsonValue.Create(false),
        JsonValueKind.Number => value.GetDouble() switch { 1 => JsonValue.Create(true), 0 => JsonValue.Create(false), _ => null },
        JsonValueKind.String => value.GetString()!.Trim().ToLowerInvariant() switch
        {
            "igen" or "i" or "true" or "1" or "x" => JsonValue.Create(true),
            "nem" or "n" or "false" or "0" => JsonValue.Create(false),
            _ => null,
        },
        _ => null,
    };

    private static JsonNode? ToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => JsonValue.Create(value.GetString()!.Trim()),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => JsonValue.Create(value.GetRawText()),
        _ => null,
    };
}
