using System.Text.Json;
using Api.Model;

namespace Api.Services.Maps;

/// <summary>
/// WMS proxy (spec 6.3): the browser asks the backend; after the access check the request goes to MapManager's
/// GeoServer proxy with the organization's service token, or to the external WMS. The only place where binary
/// content passes the backend (spec NFR-3 exception).
/// <list type="bullet">
/// <item>Only GetMap, GetFeatureInfo and GetLegendGraphic.</item>
/// <item>Allow-listed parameters only (e.g. SLD_BODY, SLD, REMOTE_OWS_* are dropped); LAYERS / QUERY_LAYERS / LAYER
/// are always the configured layer, so the client cannot reach any other layer.</item>
/// <item>The configured wms_params (and CQL filter) override the client's.</item>
/// </list>
/// </summary>
public class WmsProxy(IHttpClientFactory httpFactory, MapManagerClient mapManager)
{
    private static readonly HashSet<string> Requests = new(StringComparer.OrdinalIgnoreCase) { "GetMap", "GetFeatureInfo", "GetLegendGraphic" };

    private static readonly HashSet<string> AllowedParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "SERVICE", "VERSION", "REQUEST", "STYLES", "STYLE", "CRS", "SRS", "BBOX", "WIDTH", "HEIGHT", "FORMAT", "TRANSPARENT",
        "BGCOLOR", "EXCEPTIONS", "TIME", "ELEVATION", "INFO_FORMAT", "I", "J", "X", "Y", "FEATURE_COUNT", "SCALE", "DPI",
        "MAP_RESOLUTION", "FORMAT_OPTIONS", "TILED", "LEGEND_OPTIONS", "RULE", "SLD_VERSION",
    };

    /// <summary>Layer-identifying parameters: always set from the configuration.</summary>
    private static readonly string[] LayerParams = ["LAYERS", "QUERY_LAYERS", "LAYER"];

    public record WmsTarget(string Source, string Layer, string? ExternalUrl, JsonDocument WmsParams, string? CqlFilter);

    public async Task<IResult> ForwardAsync(HttpContext context, WmsTarget target, CancellationToken ct)
    {
        var query = BuildQuery(context.Request.Query, target);

        HttpResponseMessage response;
        if (target.Source == LayerSources.MapManager)
        {
            var connection = await mapManager.ConnectionAsync(ct);
            response = await mapManager.SendAsync(connection, "wms", query, ct);
        }
        else
        {
            var url = target.ExternalUrl + QueryString.Create(query.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
            try
            {
                response = await httpFactory.CreateClient(OutboundHttp.ExternalWmsClientName)
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new AppException(StatusCodes.Status502BadGateway, "maps.wms_unavailable", $"The WMS server is not available: {ex.Message}");
            }
        }

        context.Response.RegisterForDispose(response);
        if (!response.IsSuccessStatusCode)
            throw new AppException(StatusCodes.Status502BadGateway, "maps.wms_unavailable", $"The WMS server answered {(int)response.StatusCode}.");
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        // A map asked for, an error message got (wrong layer name, unsupported coordinate system …): the reason goes to
        // the client instead of an image the map cannot show.
        var isMap = query.Any(p => p.Key == "REQUEST" && p.Value.Equals("GetMap", StringComparison.OrdinalIgnoreCase));
        if (isMap && (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
            throw new AppException(StatusCodes.Status502BadGateway, "maps.wms_error",
                $"The WMS server answered with a message instead of a map: {await ServiceExceptionAsync(response, ct)}");
        return TypedResults.Stream(await response.Content.ReadAsStreamAsync(ct), contentType);
    }

    /// <summary>The text of a WMS ServiceException (or the start of whatever came), at most 500 characters.</summary>
    private static async Task<string> ServiceExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var buffer = new char[8192];
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var text = new string(buffer, 0, await reader.ReadBlockAsync(buffer, ct));
        var match = System.Text.RegularExpressions.Regex.Match(text, @"<(?:\w+:)?ServiceException[^>]*>(.*?)</(?:\w+:)?ServiceException>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var message = (match.Success ? match.Groups[1].Value : text).Trim();
        message = System.Text.RegularExpressions.Regex.Replace(message, @"\s+", " ");
        return message.Length > 500 ? message[..500] + "…" : message;
    }

    public static List<KeyValuePair<string, string>> BuildQuery(IQueryCollection request, WmsTarget target)
    {
        var requestType = request.FirstOrDefault(p => p.Key.Equals("REQUEST", StringComparison.OrdinalIgnoreCase)).Value.ToString();
        if (!Requests.Contains(requestType))
            throw AppException.BadRequest("maps.wms_request_not_allowed", "Only GetMap, GetFeatureInfo and GetLegendGraphic are allowed.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request)
            if (AllowedParams.Contains(key)) result[key.ToUpperInvariant()] = value.ToString();
        result["SERVICE"] = "WMS";

        foreach (var name in LayerParams)
            if (name == "LAYERS" || request.ContainsKey(name)) result[name] = target.Layer;
        if (requestType.Equals("GetFeatureInfo", StringComparison.OrdinalIgnoreCase)) result["QUERY_LAYERS"] = target.Layer;
        if (requestType.Equals("GetLegendGraphic", StringComparison.OrdinalIgnoreCase)) { result["LAYER"] = target.Layer; result.Remove("LAYERS"); }

        foreach (var property in target.WmsParams.RootElement.EnumerateObject())
            result[property.Name.ToUpperInvariant()] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()! : property.Value.GetRawText();
        if (!string.IsNullOrEmpty(target.CqlFilter)) result["CQL_FILTER"] = target.CqlFilter;

        return result.ToList();
    }
}
