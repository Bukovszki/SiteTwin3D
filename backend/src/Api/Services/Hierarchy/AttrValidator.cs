using System.Text.Json;
using Json.Schema;

namespace Api.Services.Hierarchy;

/// <summary>
/// Technical data (node.attrs) against the node type's JSON Schema (FR-H5): a new field needs no code change, only
/// a schema change. All errors are returned at once.
/// </summary>
public static class AttrValidator
{
    public record AttrError(string Path, string Message);

    /// <summary>
    /// Link of a skeleton record to a MapManager feature (spec 6.3): <c>{ "layer": "ws:layer", "featureId": "layer.12" }</c>.
    /// Reserved: set only by the link service, never validated against the type's schema.
    /// </summary>
    public const string MapManagerRefKey = "mapmanager_ref";

    public static bool IsLinked(JsonElement attrs) =>
        attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(MapManagerRefKey, out var r) && r.ValueKind == JsonValueKind.Object;

    public static void RejectReserved(JsonElement attrs)
    {
        if (attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(MapManagerRefKey, out _))
            throw AppException.BadRequest("node.reserved_attr", $"'{MapManagerRefKey}' is set by the MapManager link only.");
    }

    /// <summary>The schema itself must be a valid JSON Schema describing an object.</summary>
    public static void ValidateSchema(JsonElement? schema)
    {
        if (schema is not { } s || s.ValueKind == JsonValueKind.Null) return;
        if (s.ValueKind != JsonValueKind.Object)
            throw AppException.BadRequest("node_type.invalid_schema", "attrSchema must be a JSON object.");
        try
        {
            JsonSchema.FromText(s.GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException)
        {
            throw AppException.BadRequest("node_type.invalid_schema", $"attrSchema is not a valid JSON Schema: {ex.Message}");
        }
    }

    /// <summary>Throws 400 (code node.invalid_attrs, with every error) when the attributes do not match.</summary>
    public static void Validate(JsonDocument? schema, JsonElement attrs)
    {
        if (attrs.ValueKind != JsonValueKind.Object)
            throw AppException.BadRequest("node.invalid_attrs", "attrs must be a JSON object.");
        if (schema is null) return;

        var result = JsonSchema.FromText(schema.RootElement.GetRawText())
            .Evaluate(attrs, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return;

        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => new AttrError(d.InstanceLocation.ToString(), e.Value)))
            .Distinct()
            .ToList();
        throw AppException.BadRequest("node.invalid_attrs", "attrs do not match the node type's schema.", errors);
    }
}
