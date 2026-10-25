using System.Linq.Expressions;
using System.Text.Json;
using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Hierarchy;

/// <summary>A filter of the grid: field is "name", "code" or "attrs.&lt;property of the type's schema&gt;".</summary>
public record GridFilter(string Field, string Op, JsonElement? Value);

public record GridQuery(long SiteId, long? TypeId, string? Search, IReadOnlyList<GridFilter> Filters, string? Sort, bool Descending, int First, int Rows);

public record GridRow(Node Node, List<string> AnchorKinds);

public record GridPage(int Total, List<GridRow> Rows);

/// <summary>
/// Table view of nodes (FR-G1, G2, G4): filtered by type, with filtering, sorting and search on the technical data
/// (JSONB) too. Attribute fields must be properties of the type's JSON Schema; their schema type decides how they
/// compare (text: contains / equals, number and integer: equals / range, boolean: equals).
/// </summary>
public class NodeGridService(AppDbContext db, NodeTypeService types)
{
    public const int MaxRows = 500;

    private enum FieldKind { Text, Number, Boolean }

    public async Task<GridPage> QueryAsync(GridQuery request, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == request.SiteId, ct))
            throw AppException.NotFound("site.not_found", $"Site {request.SiteId} not found.");

        var fields = new Dictionary<string, FieldKind>();
        var query = db.Nodes.AsNoTracking().Include(n => n.Type).Where(n => n.SiteId == request.SiteId);
        if (request.TypeId is { } typeId)
        {
            var type = await db.NodeTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId, ct)
                ?? throw AppException.BadRequest("node.unknown_type", $"Node type {typeId} does not exist.");
            fields = SchemaFields(await types.SchemaOfAsync(type, ct));
            query = query.Where(n => n.TypeId == typeId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = $"%{EscapeLike(request.Search.Trim())}%";
            query = query.Where(n => EF.Functions.ILike(n.Name, pattern) || (n.Code != null && EF.Functions.ILike(n.Code, pattern)));
        }
        foreach (var filter in request.Filters)
            query = query.Where(FilterExpression(filter, fields));

        var total = await query.CountAsync(ct);

        var ordered = Sort(query, request.Sort, request.Descending, fields);
        var rows = Math.Clamp(request.Rows, 1, MaxRows);
        var page = await ordered.Skip(Math.Max(0, request.First)).Take(rows)
            .Select(n => new { Node = n, Kinds = db.Anchors.Where(a => a.NodeId == n.Id).Select(a => a.Kind).Distinct().ToList() })
            .ToListAsync(ct);
        return new GridPage(total, page.Select(p => new GridRow(p.Node, p.Kinds.OrderBy(k => k).ToList())).ToList());
    }

    /// <summary>Top-level properties of the JSON Schema with their comparison kind.</summary>
    private static Dictionary<string, FieldKind> SchemaFields(JsonDocument? schema)
    {
        var fields = new Dictionary<string, FieldKind>();
        if (schema is null || !schema.RootElement.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return fields;
        foreach (var property in properties.EnumerateObject())
        {
            var type = property.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "string";
            fields[property.Name] = type switch
            {
                "number" or "integer" => FieldKind.Number,
                "boolean" => FieldKind.Boolean,
                _ => FieldKind.Text,
            };
        }
        return fields;
    }

    private static Expression<Func<Node, bool>> FilterExpression(GridFilter filter, Dictionary<string, FieldKind> fields)
    {
        var op = filter.Op;
        if (filter.Field is "name" or "code")
        {
            var text = TextValue(filter);
            var pattern = $"%{EscapeLike(text)}%";
            return (filter.Field, op) switch
            {
                ("name", "contains") => n => EF.Functions.ILike(n.Name, pattern),
                ("name", "eq") => n => n.Name == text,
                ("code", "contains") => n => n.Code != null && EF.Functions.ILike(n.Code, pattern),
                ("code", "eq") => n => n.Code == text,
                _ => throw InvalidOp(filter),
            };
        }

        var property = AttrProperty(filter.Field, fields);
        switch (fields[property])
        {
            case FieldKind.Text:
            {
                if (op == "empty") return n => n.Attrs.RootElement.GetProperty(property).GetString() == null;
                var text = TextValue(filter);
                var pattern = $"%{EscapeLike(text)}%";
                return op switch
                {
                    "contains" => n => EF.Functions.ILike(n.Attrs.RootElement.GetProperty(property).GetString()!, pattern),
                    "eq" => n => n.Attrs.RootElement.GetProperty(property).GetString() == text,
                    _ => throw InvalidOp(filter),
                };
            }
            case FieldKind.Number:
            {
                if (op == "empty") return n => n.Attrs.RootElement.GetProperty(property).GetString() == null;
                var number = filter.Value is { ValueKind: JsonValueKind.Number } v ? v.GetDouble()
                    : throw AppException.BadRequest("grid.invalid_filter", $"Filter on '{filter.Field}' needs a number.");
                return op switch
                {
                    "eq" => n => n.Attrs.RootElement.GetProperty(property).GetDouble() == number,
                    "gte" => n => n.Attrs.RootElement.GetProperty(property).GetDouble() >= number,
                    "lte" => n => n.Attrs.RootElement.GetProperty(property).GetDouble() <= number,
                    _ => throw InvalidOp(filter),
                };
            }
            default:
            {
                if (op != "eq" || filter.Value is not { ValueKind: JsonValueKind.True or JsonValueKind.False } v)
                    throw InvalidOp(filter);
                var flag = v.GetBoolean();
                return n => n.Attrs.RootElement.GetProperty(property).GetBoolean() == flag;
            }
        }
    }

    private static IOrderedQueryable<Node> Sort(IQueryable<Node> query, string? sort, bool descending, Dictionary<string, FieldKind> fields)
    {
        IOrderedQueryable<Node> ordered;
        if (string.IsNullOrEmpty(sort))
            ordered = query.OrderBy(n => n.SortOrder).ThenBy(n => n.Name);
        else if (sort == "name")
            ordered = descending ? query.OrderByDescending(n => n.Name) : query.OrderBy(n => n.Name);
        else if (sort == "code")
            ordered = descending ? query.OrderByDescending(n => n.Code) : query.OrderBy(n => n.Code);
        else
        {
            var property = AttrProperty(sort, fields);
            ordered = fields[property] switch
            {
                FieldKind.Number => descending
                    ? query.OrderByDescending(n => (double?)n.Attrs.RootElement.GetProperty(property).GetDouble())
                    : query.OrderBy(n => (double?)n.Attrs.RootElement.GetProperty(property).GetDouble()),
                FieldKind.Boolean => descending
                    ? query.OrderByDescending(n => (bool?)n.Attrs.RootElement.GetProperty(property).GetBoolean())
                    : query.OrderBy(n => (bool?)n.Attrs.RootElement.GetProperty(property).GetBoolean()),
                _ => descending
                    ? query.OrderByDescending(n => n.Attrs.RootElement.GetProperty(property).GetString())
                    : query.OrderBy(n => n.Attrs.RootElement.GetProperty(property).GetString()),
            };
        }
        // Stable order for paging.
        return ordered.ThenBy(n => n.Id);
    }

    private static string AttrProperty(string field, Dictionary<string, FieldKind> fields)
    {
        var property = field.StartsWith("attrs.", StringComparison.Ordinal) ? field["attrs.".Length..] : "";
        if (!fields.ContainsKey(property))
            throw AppException.BadRequest("grid.unknown_field", $"Unknown field '{field}' (name, code or attrs.<property of the type's schema>).");
        return property;
    }

    private static string TextValue(GridFilter filter) => filter.Value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString()!,
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => throw AppException.BadRequest("grid.invalid_filter", $"Filter on '{filter.Field}' needs a text value."),
    };

    private static AppException InvalidOp(GridFilter filter) =>
        AppException.BadRequest("grid.invalid_filter", $"Operator '{filter.Op}' is not supported on '{filter.Field}'.");

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
