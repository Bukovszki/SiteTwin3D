using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Infrastructure;

namespace Api.Tests.Drawing;

/// <summary>Drawing layers (spec 8.6): editing in EPSG:3857, storage in WGS84, all-or-nothing transactions, versions.</summary>
public sealed class DrawTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    // A 10 m × 6 m (real) zone near 19.08° E, 47.43° N, in EPSG:3857 metres (1 real m ≈ 1.477 Web Mercator m here).
    private const double X0 = 2123978.0, Y0 = 6013000.0;
    private static double[][][] Zone(double dx = 0) =>
        [[[X0 + dx, Y0], [X0 + dx + 14.77, Y0], [X0 + dx + 14.77, Y0 + 8.86], [X0 + dx, Y0 + 8.86], [X0 + dx, Y0]]];

    private async Task<(OrgClient Org, long Layer)> LayerAsync(string geometryType = "MultiPolygon")
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var created = await org.Http.PostAsJsonAsync("/api/v1/draw-layers", new
        {
            siteId = site, name = "Zónák", geometryType,
            fields = new object[]
            {
                new { name = "nev", type = "text", length = 20, alias = "Név" },
                new { name = "szint", type = "integer" },
                new { name = "terulet", type = "number", decimals = 1 },
                new { name = "veszelyes", type = "boolean" },
                new { name = "ellenorizve", type = "date" },
            },
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (org, (await TestApi.ReadJson(created)).GetProperty("id").GetInt64());
    }

    private static Task<HttpResponseMessage> Tx(HttpClient http, long layer, object body) => http.PostAsJsonAsync($"/api/v1/draw-layers/{layer}/transaction", body);

    [Fact]
    public async Task Insert_StoresWgs84_ReturnsEpsg3857_WithVersion()
    {
        var (org, layer) = await LayerAsync();

        var result = await TestApi.ReadJson(await Tx(org.Http, layer, new
        {
            inserts = new object[] { new { geometry = new { type = "Polygon", coordinates = Zone() }, attrs = new Dictionary<string, object> { ["nev"] = "Veszélyes zóna", ["veszelyes"] = true } } },
        }));
        var features = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/draw-layers/{layer}/features");
        await using var sql = await Database.SqlTestData.OpenAsync(Postgis);
        var area = await sql.ScalarAsync<double>("SELECT ST_Area(geom::geography) FROM draw_geometry WHERE record_id = @id",
            ("id", result.GetProperty("inserted")[0].GetInt64()));
        var srid = await sql.ScalarAsync<int>("SELECT ST_SRID(geom) FROM draw_geometry WHERE record_id = @id", ("id", result.GetProperty("inserted")[0].GetInt64()));

        var feature = Assert.Single(features.GetProperty("features").EnumerateArray());
        Assert.Equal("MultiPolygon", feature.GetProperty("geometry").GetProperty("type").GetString()); // promoted (ST_Multi)
        Assert.Equal(X0, feature.GetProperty("geometry").GetProperty("coordinates")[0][0][0][0].GetDouble(), 3); // back in 3857
        Assert.Equal(1, feature.GetProperty("version").GetInt32());
        Assert.Equal("Veszélyes zóna", feature.GetProperty("properties").GetProperty("nev").GetString());
        Assert.Equal(4326, srid);
        Assert.InRange(area, 58, 62); // ~10 m × 6 m real, measured on the ellipsoid
    }

    [Fact]
    public async Task InvalidTransaction_ReportsEveryError_AndSavesNothing()
    {
        var (org, layer) = await LayerAsync();
        var bowtie = new[] { new[] { new[] { X0, Y0 }, new[] { X0 + 10, Y0 + 10 }, new[] { X0 + 10, Y0 }, new[] { X0, Y0 + 10 }, new[] { X0, Y0 } } };

        var response = await Tx(org.Http, layer, new
        {
            inserts = new object[]
            {
                new { geometry = new { type = "Polygon", coordinates = Zone() }, attrs = new Dictionary<string, object> { ["nev"] = "Jó" } },
                new { geometry = new { type = "Polygon", coordinates = bowtie } },
                new { geometry = new { type = "Point", coordinates = new[] { X0, Y0 } } },
                new { geometry = new { type = "Polygon", coordinates = Zone(30) }, attrs = new Dictionary<string, object>
                    { ["nev"] = "Ez a név húsz karakternél hosszabb", ["szint"] = 1.5, ["terulet"] = 12.34, ["ellenorizve"] = "2026.10.23", ["szin"] = "piros" } },
            },
        });
        var problem = await TestApi.ReadJson(response);
        var errors = problem.GetProperty("errors").EnumerateArray().Select(e => $"{e.GetProperty("index").GetInt32()}:{e.GetProperty("code").GetString()}:{e.GetProperty("field").GetString()}").ToList();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("draw.invalid", problem.GetProperty("code").GetString());
        Assert.Equal(["1:geometry.invalid:geometry", "2:draw.wrong_geometry_type:geometry", "3:draw.invalid_value:nev", "3:draw.invalid_value:szint",
            "3:draw.invalid_value:terulet", "3:draw.invalid_value:ellenorizve", "3:draw.unknown_field:szin"], errors);
        Assert.Empty((await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/draw-layers/{layer}/features")).GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task Update_AndDelete_NeedTheCurrentVersion()
    {
        var (org, layer) = await LayerAsync();
        var inserted = await TestApi.ReadJson(await Tx(org.Http, layer, new
        {
            inserts = new object[]
            {
                new { geometry = new { type = "Polygon", coordinates = Zone() }, attrs = new Dictionary<string, object> { ["nev"] = "A" } },
                new { geometry = new { type = "Polygon", coordinates = Zone(30) }, attrs = new Dictionary<string, object> { ["nev"] = "B" } },
            },
        }));
        var (a, b) = (inserted.GetProperty("inserted")[0].GetInt64(), inserted.GetProperty("inserted")[1].GetInt64());

        // Someone saves A (version 1 → 2) …
        var first = await TestApi.ReadJson(await Tx(org.Http, layer, new { updates = new object[] { new { id = a, version = 1, attrs = new Dictionary<string, object> { ["szint"] = 2 } } } }));
        // … another editor still has version 1 of A: conflict, and B is not deleted either.
        var stale = await Tx(org.Http, layer, new
        {
            updates = new object[] { new { id = a, version = 1, attrs = new Dictionary<string, object> { ["nev"] = "Régi" } } },
            deletes = new object[] { new { id = b, version = 1 } },
        });
        var current = await Tx(org.Http, layer, new
        {
            updates = new object[] { new { id = a, version = 2, geometry = new { type = "Polygon", coordinates = Zone(5) } } },
            deletes = new object[] { new { id = b, version = 1 } },
        });
        var features = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/draw-layers/{layer}/features");

        Assert.Equal(2, first.GetProperty("versions").GetProperty(a.ToString()).GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await TestApi.ReadJson(stale);
        Assert.Equal("draw.version_conflict", conflict.GetProperty("code").GetString());
        Assert.Equal([a], conflict.GetProperty("errors").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var remaining = Assert.Single(features.GetProperty("features").EnumerateArray());
        Assert.Equal(3, remaining.GetProperty("version").GetInt32());
        Assert.Equal("A", remaining.GetProperty("properties").GetProperty("nev").GetString()); // attrs merged, not replaced
        Assert.Equal(2, remaining.GetProperty("properties").GetProperty("szint").GetInt32());
        Assert.Equal(X0 + 5, remaining.GetProperty("geometry").GetProperty("coordinates")[0][0][0][0].GetDouble(), 3);
    }

    [Fact]
    public async Task Features_FilteredByBbox_AndHiddenFromOtherOrganizations()
    {
        var (org, layer) = await LayerAsync();
        await Tx(org.Http, layer, new
        {
            inserts = new object[]
            {
                new { geometry = new { type = "Polygon", coordinates = Zone() } },
                new { geometry = new { type = "Polygon", coordinates = Zone(1000) } },
            },
        });
        var other = await Api.NewOrganizationAsync();

        var near = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/draw-layers/{layer}/features?bbox={X0 - 50},{Y0 - 50},{X0 + 50},{Y0 + 50}");

        Assert.Single(near.GetProperty("features").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/draw-layers/{layer}/features")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Tx(other.Http, layer, new { inserts = new object[] { new { geometry = new { type = "Polygon", coordinates = Zone() } } } })).StatusCode);
    }
}
