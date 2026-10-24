using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Maps;

/// <summary>M7: vector geometry of a level for snapping – own drawing layers plus the MapManager floorplan (WFS, server-side).</summary>
public sealed class FloorGeometryTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    // About 19.08° E, 47.43° N in EPSG:3857.
    private const double X0 = 2123978.0, Y0 = 6013000.0, R = 6378137;

    private async Task<(OrgClient Org, long Site, long Level)> LevelAsync(bool mapManager)
    {
        var org = await Api.NewOrganizationAsync();
        if (mapManager)
            await using (var sql = await SqlTestData.OpenAsync(Postgis))
                await sql.ExecuteAsync("UPDATE organization SET mapmanager_url = 'http://mapmanager.test', mapmanager_token_ref = @ref WHERE id = @id",
                    ("ref", ApiFactory.TokenRef), ("id", org.OrganizationId));
        var site = await org.Http.CreateSiteAsync();
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var level = (await org.Http.CreateNodeAsync(site, building, await org.Http.TypeIdAsync("level"), "Földszint", new { elevation_m = 0 }))
            .GetProperty("id").GetInt64();
        await org.Http.PostAsJsonAsync($"/api/v1/levels/{level}/floorplans", new { source = "mapmanager", name = "Építészet", layer = "ceg:alaprajz", cqlFilter = "szint = 'F0'" });
        var layer = (await TestApi.ReadJson(await org.Http.PostAsJsonAsync("/api/v1/draw-layers",
            new { siteId = site, levelId = level, name = "Zónák", geometryType = "MultiLineString", fields = Array.Empty<object>() }))).GetProperty("id").GetInt64();
        var tx = await org.Http.PostAsJsonAsync($"/api/v1/draw-layers/{layer}/transaction", new
        {
            inserts = new object[] { new { geometry = new { type = "LineString", coordinates = new[] { new[] { X0, Y0 }, new[] { X0 + 10, Y0 } } }, attrs = new { } } },
        });
        Assert.Equal(HttpStatusCode.OK, tx.StatusCode);
        return (org, site, level);
    }

    [Fact]
    public async Task Geometry_CombinesDrawingAndFloorplan_InWgs84()
    {
        Factory.GeoServer.GeometryLayers["ceg:alaprajz"] =
        [
            JsonNode.Parse($$"""{"type":"Polygon","coordinates":[[[{{X0}},{{Y0}}],[{{X0 + 20}},{{Y0}}],[{{X0 + 20}},{{Y0 + 20}}],[{{X0}},{{Y0}}]]]}""")!.AsObject(),
        ];
        var (org, site, level) = await LevelAsync(mapManager: true);

        var result = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/floors/{level}/geometry");
        var features = result.GetProperty("features").EnumerateArray().ToList();
        var floorplan = features.Single(f => f.GetProperty("properties").GetProperty("source").GetString() == "floorplan");
        var query = Factory.GeoServer.Queries.Last();

        Assert.Equal("FeatureCollection", result.GetProperty("type").GetString());
        Assert.False(result.GetProperty("partial").GetBoolean());
        Assert.Single(features, f => f.GetProperty("properties").GetProperty("source").GetString() == "draw");
        var corner = floorplan.GetProperty("geometry").GetProperty("coordinates")[0][0];
        Assert.Equal(X0 / R * 180 / Math.PI, corner[0].GetDouble(), 7);
        Assert.Equal((2 * Math.Atan(Math.Exp(Y0 / R)) - Math.PI / 2) * 180 / Math.PI, corner[1].GetDouble(), 7);
        Assert.Equal("szint = 'F0'", query["CQL_FILTER"]);
        Assert.Equal("EPSG:3857", query["SRSNAME"]);
    }

    [Fact]
    public async Task Geometry_WithoutMapManager_IsPartial_OtherOrganizationGetsNotFound()
    {
        var (org, site, level) = await LevelAsync(mapManager: false);
        var other = await Api.NewOrganizationAsync();

        var result = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/floors/{level}/geometry");

        Assert.True(result.GetProperty("partial").GetBoolean());
        Assert.Single(result.GetProperty("features").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/sites/{site}/floors/{level}/geometry")).StatusCode);
    }
}
