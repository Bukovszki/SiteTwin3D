using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Anchors;

/// <summary>
/// One element, one appearance (user decision): one point per node, one info point per panorama (placing again moves
/// it), and a single place per node – model element or box, else point, else the info point's approximate place.
/// </summary>
public sealed class PlaceTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const double Lon = 19.08, Lat = 47.43;

    private static object Pos(double east) => new { type = "Point", coordinates = new[] { Lon + east / 75470.0, Lat } };

    private async Task<(OrgClient Org, long Site, long Level, long Pump, long Panorama)> SetupAsync()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var level = (await org.Http.CreateNodeAsync(site, building, await org.Http.TypeIdAsync("level"), "Földszint", new { elevation_m = 0 }))
            .GetProperty("id").GetInt64();
        var pump = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("equipment"), "P1")).GetProperty("id").GetInt64();
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        var panorama = await sql.PanoramaAsync(org.OrganizationId, level, Lon, Lat, level);
        return (org, site, level, pump, panorama);
    }

    [Fact]
    public async Task SecondInfoPointOnTheSamePanorama_MovesTheFirst_OtherPanoramaIsAllowed()
    {
        var (org, _, level, pump, panorama) = await SetupAsync();
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        var other = await sql.PanoramaAsync(org.OrganizationId, level, Lon + 0.0001, Lat, level);
        object Info(long p, double yaw) => new { kind = "panorama_point", panoramaId = p, yawDeg = yaw, pitchDeg = 0, levelId = level, position = Pos(1) };

        var first = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Info(panorama, 10)));
        var again = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Info(panorama, 40)));
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Info(other, -20));
        var anchors = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/anchors");

        Assert.Equal(first.GetProperty("id").GetInt64(), again.GetProperty("id").GetInt64());
        Assert.Equal(40, again.GetProperty("yawDeg").GetDouble());
        Assert.Equal(2, anchors.GetArrayLength());
    }

    [Fact]
    public async Task OnePointPerNode_AndThePlaceComesFromTheMostExactAnchor()
    {
        var (org, site, level, pump, panorama) = await SetupAsync();
        var info = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors",
            new { kind = "panorama_point", panoramaId = panorama, yawDeg = 0, pitchDeg = 0, levelId = level, position = Pos(5) }));
        var placeFromInfo = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/place");
        var p1 = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", new { kind = "point", levelId = level, position = Pos(1) }));
        var p2 = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", new { kind = "point", levelId = level, position = Pos(2) }));
        var placeFromPoint = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/place");
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", new
        {
            kind = "region", regionShape = "box", sizeX = 1, sizeY = 1, sizeZ = 1, headingDeg = 0, levelId = level, zRelM = 0.5, position = Pos(3),
        });
        var places = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/places?levelId={level}");

        Assert.Equal("panorama_point", placeFromInfo.GetProperty("source").GetString());
        Assert.Equal(p1.GetProperty("id").GetInt64(), p2.GetProperty("id").GetInt64());
        Assert.Equal("point", placeFromPoint.GetProperty("source").GetString());
        Assert.Equal(2, (placeFromPoint.GetProperty("position").GetProperty("coordinates")[0].GetDouble() - Lon) * 75470, 3);
        var place = Assert.Single(places.EnumerateArray());
        Assert.Equal("region", place.GetProperty("source").GetString());
        Assert.Equal(info.GetProperty("nodeId").GetInt64(), place.GetProperty("nodeId").GetInt64());
    }

    [Fact]
    public async Task InfoPointWithoutPosition_TakesTheNodesPlace()
    {
        var (org, _, level, pump, panorama) = await SetupAsync();
        var noPlace = await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors",
            new { kind = "panorama_point", panoramaId = panorama, yawDeg = 0, pitchDeg = 0, levelId = level });
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", new { kind = "point", levelId = level, position = Pos(4) });
        var info = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors",
            new { kind = "panorama_point", panoramaId = panorama, yawDeg = 0, pitchDeg = 0, levelId = level }));

        Assert.Equal("anchor.position_required", await TestApi.ErrorCode(noPlace));
        Assert.Equal(4, (info.GetProperty("position").GetProperty("coordinates")[0].GetDouble() - Lon) * 75470, 3);
        Assert.Equal(HttpStatusCode.NotFound, (await (await Api.NewOrganizationAsync()).Http.GetAsync($"/api/v1/nodes/{pump}/place")).StatusCode);
    }
}
