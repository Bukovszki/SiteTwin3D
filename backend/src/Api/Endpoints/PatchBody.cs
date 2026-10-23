using System.Text.Json;
using Api.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Api.Endpoints;

/// <summary>
/// JSON body of a PATCH request: tells "property not sent" from "property sent as null" (e.g. parentId: null moves a
/// node to the root). Values are read with the API's JSON options (GeoJSON geometries, camelCase).
/// </summary>
public class PatchBody(JsonElement body, JsonSerializerOptions options)
{
    public static PatchBody From(JsonElement body, IOptions<JsonOptions> options)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw AppException.BadRequest("request.invalid_body", "The request body must be a JSON object.");
        return new PatchBody(body, options.Value.SerializerOptions);
    }

    public bool Has(string name) => body.TryGetProperty(name, out _);

    /// <summary>The value if sent (null if sent as null); default if not sent.</summary>
    public T? Get<T>(string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return default;
        try
        {
            return value.Deserialize<T>(options);
        }
        catch (JsonException ex)
        {
            throw AppException.BadRequest("request.invalid_field", $"Invalid value of '{name}': {ex.Message}");
        }
    }

    /// <summary>A raw JSON value (attrs, schemas) if sent and not null.</summary>
    public JsonElement? Json(string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;
}
