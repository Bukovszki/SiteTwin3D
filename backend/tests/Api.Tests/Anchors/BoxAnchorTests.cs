using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;

namespace Api.Tests.Anchors;

/// <summary>M6: boxes (region anchors, spec 8.2, 9.1) – real metre sizes, heading, centre above the level floor.</summary>
public sealed class BoxAnchorTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const double Lon = 19.08, Lat = 47.43;

    private static object Box(long level, double sizeX = 1.2, double heading = 370) => new
    {
        kind = "region", regionShape = "box", sizeX, sizeY = 0.8, sizeZ = 1.5, headingDeg = heading, levelId = level, zRelM = 0.75,
        position = new { type = "Point", coordinates = new[] { Lon, Lat } },
    };

    private async Task<(OrgClient Org, long Site, long Level, long Pump)> SetupAsync()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var level = (await org.Http.CreateNodeAsync(site, building, await org.Http.TypeIdAsync("level"), "Földszint", new { elevation_m = 0 }))
            .GetProperty("id").GetInt64();
        var pump = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("equipment"), "P1")).GetProperty("id").GetInt64();
        return (org, site, level, pump);
    }

    [Fact]
    public async Task Box_CreateResizeTurn_ListedForTheWholeSite()
    {
        var (org, site, level, pump) = await SetupAsync();
        var created = await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Box(level));
        var box = await TestApi.ReadJson(created);
        var id = box.GetProperty("id").GetInt64();
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", new { kind = "point", position = new { type = "Point", coordinates = new[] { Lon, Lat } } });

        var changed = await TestApi.ReadJson(await org.Http.PatchJsonAsync($"/api/v1/anchors/{id}", new { sizeX = 2.5, headingDeg = -30, zRelM = 1.1 }));
        var regions = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/anchors?allLevels=true&kind=region");
        var grid = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes?siteId={site}&typeId={await org.Http.TypeIdAsync("equipment")}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("box", box.GetProperty("regionShape").GetString());
        Assert.Equal(10, box.GetProperty("headingDeg").GetDouble(), 3);
        Assert.Equal(2.5, changed.GetProperty("sizeX").GetDouble(), 3);
        Assert.Equal(0.8, changed.GetProperty("sizeY").GetDouble(), 3);
        Assert.Equal(330, changed.GetProperty("headingDeg").GetDouble(), 3);
        Assert.Equal(1.1, changed.GetProperty("zRelM").GetDouble(), 3);
        Assert.Equal(id, Assert.Single(regions.EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Contains("region", grid.GetProperty("rows")[0].GetProperty("anchorKinds").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task Box_Validation_SiteRolesAndIsolation()
    {
        var (org, site, level, pump) = await SetupAsync();
        var noSizes = await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors",
            new { kind = "region", levelId = level, position = new { type = "Point", coordinates = new[] { Lon, Lat } } });
        var tooSmall = await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Box(level, sizeX: 0.001));
        var box = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Box(level)));
        var id = box.GetProperty("id").GetInt64();
        var tooBig = await org.Http.PatchJsonAsync($"/api/v1/anchors/{id}", new { sizeZ = 900 });
        var other = await Api.NewOrganizationAsync();
        var viewer = await Api.NewUserAsync(org.OrganizationId, org.OrganizationCode, Roles.Viewer);

        Assert.Equal("anchor.box_fields", await TestApi.ErrorCode(noSizes));
        Assert.Equal("anchor.invalid_size", await TestApi.ErrorCode(tooSmall));
        Assert.Equal("anchor.invalid_size", await TestApi.ErrorCode(tooBig));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.PatchJsonAsync($"/api/v1/anchors/{id}", new { sizeX = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/sites/{site}/anchors?allLevels=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PatchJsonAsync($"/api/v1/anchors/{id}", new { sizeX = 2 })).StatusCode);
        Assert.Single((await viewer.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/anchors?allLevels=true&kind=region")).EnumerateArray());
    }
}
