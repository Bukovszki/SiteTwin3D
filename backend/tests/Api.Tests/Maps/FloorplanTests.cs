using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Maps;

/// <summary>Floorplans of levels (FR-AR1): references to WMS layers, served through the proxy with their CQL filter.</summary>
public sealed class FloorplanTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private async Task<(OrgClient Org, long Site, long Building, long Ground, long First)> LevelsAsync()
    {
        var org = await Api.NewOrganizationAsync();
        await using (var sql = await SqlTestData.OpenAsync(Postgis))
            await sql.ExecuteAsync("UPDATE organization SET mapmanager_url = 'http://mapmanager.test', mapmanager_token_ref = @ref WHERE id = @id",
                ("ref", ApiFactory.TokenRef), ("id", org.OrganizationId));
        var site = await org.Http.CreateSiteAsync();
        var levelType = await org.Http.TypeIdAsync("level");
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var first = (await org.Http.CreateNodeAsync(site, building, levelType, "Emelet", new { elevation_m = 4.2 })).GetProperty("id").GetInt64();
        var ground = (await org.Http.CreateNodeAsync(site, building, levelType, "Földszint", new { elevation_m = 0 })).GetProperty("id").GetInt64();
        return (org, site, building, ground, first);
    }

    [Fact]
    public async Task Levels_AreListedByElevation_WithCounts()
    {
        var t = await LevelsAsync();
        await t.Org.Http.PostAsJsonAsync($"/api/v1/levels/{t.Ground}/floorplans", new { source = "mapmanager", name = "Építészet", layer = "ceg:alaprajz" });

        var levels = await t.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/levels");

        Assert.Equal(["Földszint", "Emelet"], levels.EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal(1, levels[0].GetProperty("floorplanCount").GetInt32());
        Assert.Equal(4.2, levels[1].GetProperty("elevationM").GetDouble());
    }

    [Fact]
    public async Task FloorplanWms_UsesTheLayerAndItsCqlFilter_NotTheClients()
    {
        var t = await LevelsAsync();
        var created = await t.Org.Http.PostAsJsonAsync($"/api/v1/levels/{t.First}/floorplans", new
        {
            source = "mapmanager", name = "Építészet", layer = "ceg:alaprajz", cqlFilter = "szint = 'F1'",
        });
        var id = (await TestApi.ReadJson(created)).GetProperty("id").GetInt64();

        var response = await t.Org.Http.GetAsync(
            $"/api/v1/maps/wms?floorplanId={id}&SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&CRS=EPSG:3857&BBOX=0,0,1,1&WIDTH=256&HEIGHT=256" +
            "&FORMAT=image/png&LAYERS=ceg:titkos&CQL_FILTER=1=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Factory.GeoServer.Queries.Single();
        Assert.Equal("ceg:alaprajz", query["LAYERS"]);
        Assert.Equal("szint = 'F1'", query["CQL_FILTER"]);
        Assert.Equal("EPSG:3857", query["CRS"]);
        Assert.Equal($"Bearer {ApiFactory.Token}", Factory.GeoServer.Requests.Single().Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Floorplan_OnlyOnLevels_AndOnlyInOwnOrganization()
    {
        var t = await LevelsAsync();
        var onBuilding = await t.Org.Http.PostAsJsonAsync($"/api/v1/levels/{t.Building}/floorplans", new { source = "mapmanager", name = "X", layer = "a:b" });
        var created = await t.Org.Http.PostAsJsonAsync($"/api/v1/levels/{t.Ground}/floorplans", new { source = "mapmanager", name = "X", layer = "a:b" });
        var id = (await TestApi.ReadJson(created)).GetProperty("id").GetInt64();
        var other = await Api.NewOrganizationAsync();

        Assert.Equal("level.invalid", await TestApi.ErrorCode(onBuilding));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/levels/{t.Ground}/floorplans")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Http.GetAsync($"/api/v1/maps/wms?floorplanId={id}&SERVICE=WMS&REQUEST=GetMap")).StatusCode);
        Assert.Empty(Factory.GeoServer.Requests);
    }
}
