using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Model;
using Api.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;

namespace Api.Services.Maps;

/// <summary>A field of a MapManager layer (from DescribeFeatureType); <see cref="Kind"/>: text | number | boolean | date | geometry.</summary>
public record LayerField(string Name, string Kind);

public record WfsFeature(string Id, JsonElement Properties);

public record WfsPage(int? Total, List<WfsFeature> Features);

/// <summary>
/// Read-only access to MapManager's GeoServer proxy (spec 6.3, MAPMANAGER_EXTENSIONS.md A.9) with the organization's
/// service token. Never connects to MapManager's database and never uses its editing endpoints. The browser never
/// sees the token: everything goes through this backend.
/// </summary>
public class MapManagerClient(
    IHttpClientFactory httpFactory, AppDbContext db, CurrentUser currentUser, IOptions<MapManagerOptions> options, IMemoryCache cache)
{
    private static readonly TimeSpan FieldCacheTime = TimeSpan.FromMinutes(5);

    public record Connection(string BaseUrl, string Token, long OrganizationId);

    /// <summary>The organization's MapManager connection; 503 (maps.mapmanager_not_configured) if it has none.</summary>
    public async Task<Connection> ConnectionAsync(CancellationToken ct)
    {
        var organizationId = currentUser.OrganizationId ?? throw new InvalidOperationException("No organization in the request.");
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId, ct);
        if (string.IsNullOrWhiteSpace(organization.MapmanagerUrl) || string.IsNullOrWhiteSpace(organization.MapmanagerTokenRef)
            || !options.Value.Tokens.TryGetValue(organization.MapmanagerTokenRef, out var token) || string.IsNullOrWhiteSpace(token))
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "maps.mapmanager_not_configured",
                "The organization has no MapManager connection (URL and service token).");
        return new Connection(organization.MapmanagerUrl.TrimEnd('/'), token, organizationId);
    }

    /// <summary>A GET to MapManager's GeoServer proxy (path: "wms" or "wfs") with the service token.</summary>
    public async Task<HttpResponseMessage> SendAsync(Connection connection, string service, IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        var url = $"{connection.BaseUrl}/geoserver/{service}{QueryString.Create(query.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        try
        {
            return await httpFactory.CreateClient(OutboundHttp.MapManagerClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw Unavailable(ex.Message);
        }
    }

    /// <summary>Fields of a layer (DescribeFeatureType, JSON output of GeoServer); cached briefly.</summary>
    public async Task<List<LayerField>> FieldsAsync(string layer, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct);
        return (await cache.GetOrCreateAsync($"mm-fields:{connection.OrganizationId}:{layer}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = FieldCacheTime;
            using var json = await GetJsonAsync(connection, [
                new("SERVICE", "WFS"), new("VERSION", "2.0.0"), new("REQUEST", "DescribeFeatureType"),
                new("TYPENAMES", layer), new("OUTPUTFORMAT", "application/json")], ct);
            var types = json.RootElement.GetProperty("featureTypes");
            if (types.GetArrayLength() == 0) throw AppException.NotFound("maps.layer_not_found", $"Layer '{layer}' not found in MapManager.");
            return types[0].GetProperty("properties").EnumerateArray()
                .Select(p => new LayerField(p.GetProperty("name").GetString()!, FieldKind(p)))
                .ToList();
        }))!;
    }

    /// <summary>A page of features without geometry (GetFeature, GeoJSON output).</summary>
    public async Task<WfsPage> FeaturesAsync(string layer, IReadOnlyCollection<string> propertyNames, string? cql, string? sortBy,
        int startIndex, int count, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct);
        var query = new List<KeyValuePair<string, string>>
        {
            new("SERVICE", "WFS"), new("VERSION", "2.0.0"), new("REQUEST", "GetFeature"), new("TYPENAMES", layer),
            new("OUTPUTFORMAT", "application/json"), new("STARTINDEX", startIndex.ToString()), new("COUNT", count.ToString()),
        };
        if (propertyNames.Count > 0) query.Add(new("PROPERTYNAME", string.Join(',', propertyNames)));
        if (!string.IsNullOrEmpty(cql)) query.Add(new("CQL_FILTER", cql));
        if (!string.IsNullOrEmpty(sortBy)) query.Add(new("SORTBY", sortBy));
        using var json = await GetJsonAsync(connection, query, ct);
        return ParsePage(json.RootElement);
    }

    /// <summary>Features by id (featureID), e.g. the data sheet of linked records.</summary>
    public async Task<List<WfsFeature>> FeaturesByIdAsync(string layer, IReadOnlyCollection<string> featureIds, CancellationToken ct)
    {
        if (featureIds.Count == 0) return [];
        var connection = await ConnectionAsync(ct);
        using var json = await GetJsonAsync(connection, [
            new("SERVICE", "WFS"), new("VERSION", "2.0.0"), new("REQUEST", "GetFeature"), new("TYPENAMES", layer),
            new("OUTPUTFORMAT", "application/json"), new("FEATUREID", string.Join(',', featureIds))], ct);
        return ParsePage(json.RootElement).Features;
    }

    /// <summary>
    /// Geometries of a layer (e.g. the walls and rooms of a floorplan) in WGS84. Asked for in EPSG:3857 – the GeoJSON
    /// axis order is then unambiguous – and converted with the exact formula; never used for metric values here.
    /// </summary>
    public async Task<List<Geometry>> GeometriesAsync(string layer, string? cql, int maxCount, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct);
        var query = new List<KeyValuePair<string, string>>
        {
            new("SERVICE", "WFS"), new("VERSION", "2.0.0"), new("REQUEST", "GetFeature"), new("TYPENAMES", layer),
            new("OUTPUTFORMAT", "application/json"), new("SRSNAME", "EPSG:3857"), new("COUNT", maxCount.ToString()),
        };
        if (!string.IsNullOrEmpty(cql)) query.Add(new("CQL_FILTER", cql));
        using var json = await GetJsonAsync(connection, query, ct);
        var reader = new JsonSerializerOptions { Converters = { new GeoJsonConverterFactory(new GeometryFactory(new PrecisionModel(), 3857)) } };
        return json.RootElement.GetProperty("features").EnumerateArray()
            .Where(f => f.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object)
            .Select(f => f.GetProperty("geometry").Deserialize<Geometry>(reader))
            .OfType<Geometry>()
            .Select(Drawing.WebMercator.ToWgs84)
            .ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(Connection connection, IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        using var response = await SendAsync(connection, "wfs", query, ct);
        if (!response.IsSuccessStatusCode)
            throw Unavailable($"MapManager answered {(int)response.StatusCode}.");
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException)
        {
            // GeoServer reports errors (unknown layer, bad filter) as XML with status 200.
            throw Unavailable("MapManager did not answer with JSON (unknown layer or invalid request).");
        }
    }

    private static WfsPage ParsePage(JsonElement root)
    {
        int? total = root.TryGetProperty("numberMatched", out var matched) && matched.ValueKind == JsonValueKind.Number ? matched.GetInt32()
            : root.TryGetProperty("totalFeatures", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
        var features = root.GetProperty("features").EnumerateArray()
            .Select(f => new WfsFeature(f.GetProperty("id").GetString()!,
                f.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : JsonDocument.Parse("{}").RootElement.Clone()))
            .ToList();
        return new WfsPage(total, features);
    }

    /// <summary>GeoServer's localType (or XSD type) → comparison kind.</summary>
    private static string FieldKind(JsonElement property)
    {
        var type = (property.TryGetProperty("localType", out var local) ? local.GetString() : null)
            ?? (property.TryGetProperty("type", out var xsd) ? xsd.GetString() : null) ?? "string";
        type = type.Split(':').Last().ToLowerInvariant();
        return type switch
        {
            "int" or "integer" or "long" or "short" or "double" or "float" or "decimal" or "number" or "byte" => "number",
            "boolean" => "boolean",
            "date" or "datetime" or "date-time" or "time" => "date",
            "point" or "multipoint" or "linestring" or "multilinestring" or "polygon" or "multipolygon" or "geometry"
                or "geometrypropertytype" or "pointpropertytype" or "multisurfacepropertytype" or "multicurvepropertytype"
                or "surfacepropertytype" or "curvepropertytype" or "multipointpropertytype" => "geometry",
            _ => "text",
        };
    }

    private static AppException Unavailable(string message) =>
        new(StatusCodes.Status502BadGateway, "maps.mapmanager_unavailable", $"MapManager is not available: {message}");
}
