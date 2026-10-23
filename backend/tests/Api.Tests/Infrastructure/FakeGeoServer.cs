using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Stand-in for MapManager's GeoServer proxy (and for external WMS servers): answers WFS DescribeFeatureType /
/// GetFeature in GeoServer's JSON format from in-memory features, WMS with a tiny PNG, and records every request.
/// It does not evaluate CQL – tests assert the CQL that was sent.
/// </summary>
public sealed class FakeGeoServer : HttpMessageHandler
{
    public const string Layer = "ceg:szivattyuk";
    private const string TypeName = "szivattyuk";

    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>The layer's features (id → properties); tests may remove some to create orphans.</summary>
    public Dictionary<string, JsonObject> Features { get; } = new()
    {
        [$"{TypeName}.1"] = new JsonObject { ["nev"] = "P1 szivattyú", ["gyarto"] = "Grundfos", ["teljesitmeny"] = 7.5 },
        [$"{TypeName}.2"] = new JsonObject { ["nev"] = "P2 szivattyú", ["gyarto"] = "Flygt", ["teljesitmeny"] = 11.0 },
        [$"{TypeName}.3"] = new JsonObject { ["nev"] = "P3 szivattyú", ["gyarto"] = "Grundfos", ["teljesitmeny"] = 4.0 },
    };

    public IEnumerable<Dictionary<string, string>> Queries =>
        Requests.Select(r => QueryHelpers.ParseQuery(r.RequestUri!.Query).ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value.ToString()));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var query = QueryHelpers.ParseQuery(request.RequestUri!.Query)
            .ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value.ToString());
        var requestType = query.GetValueOrDefault("REQUEST", "");

        if (request.RequestUri.AbsolutePath.EndsWith("/wms") || query.GetValueOrDefault("SERVICE") == "WMS")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { { "Content-Type", "image/png" } } } });

        if (query.GetValueOrDefault("TYPENAMES") != Layer)
            return Json(new JsonObject { ["featureTypes"] = new JsonArray(), ["features"] = new JsonArray() });

        if (requestType == "DescribeFeatureType")
            return Json(new JsonObject
            {
                ["featureTypes"] = new JsonArray(new JsonObject
                {
                    ["typeName"] = TypeName,
                    ["properties"] = new JsonArray(
                        new JsonObject { ["name"] = "geom", ["localType"] = "Point" },
                        new JsonObject { ["name"] = "nev", ["localType"] = "string" },
                        new JsonObject { ["name"] = "gyarto", ["localType"] = "string" },
                        new JsonObject { ["name"] = "teljesitmeny", ["localType"] = "number" }),
                }),
            });

        IEnumerable<KeyValuePair<string, JsonObject>> selected = Features.OrderBy(f => f.Key);
        if (query.TryGetValue("FEATUREID", out var ids))
        {
            var wanted = ids.Split(',').ToHashSet();
            selected = selected.Where(f => wanted.Contains(f.Key));
        }
        var all = selected.ToList();
        var start = int.Parse(query.GetValueOrDefault("STARTINDEX", "0"));
        var count = int.Parse(query.GetValueOrDefault("COUNT", "1000"));
        var page = all.Skip(start).Take(count);
        return Json(new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["numberMatched"] = all.Count,
            ["features"] = new JsonArray(page.Select(f => (JsonNode)new JsonObject
            {
                ["type"] = "Feature",
                ["id"] = f.Key,
                ["geometry"] = null,
                ["properties"] = f.Value.DeepClone(),
            }).ToArray()),
        });
    }

    private static Task<HttpResponseMessage> Json(JsonObject body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        });
}
