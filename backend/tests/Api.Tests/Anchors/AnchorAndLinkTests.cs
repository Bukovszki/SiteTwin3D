using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Anchors;

/// <summary>
/// M4: info points (FR-A, decided: approximate place marked on the floorplan) and the walk-through neighbourhood
/// (FR-P4) with real distances.
/// </summary>
public sealed class AnchorAndLinkTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    // 1e-5° of longitude at 47.43° ≈ 0.753 m; 1e-5° of latitude ≈ 1.112 m.
    private const double Lon = 19.08, Lat = 47.43;

    private record Setup(OrgClient Org, long Site, long Level, long Pump, long[] Panoramas);

    /// <summary>A level with five panoramas 8 m apart west → east, and a pump (function tree).</summary>
    private async Task<Setup> LevelWithPanoramasAsync(string fifthStatus = "approved")
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var level = (await org.Http.CreateNodeAsync(site, building, await org.Http.TypeIdAsync("level"), "Földszint", new { elevation_m = 0 }))
            .GetProperty("id").GetInt64();
        var pump = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("equipment"), "P1 szivattyú")).GetProperty("id").GetInt64();
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        var ids = new long[5];
        for (var i = 0; i < 5; i++)
            ids[i] = await sql.PanoramaAsync(org.OrganizationId, level, Lon + i * 8 / 75300.0, Lat, level, i == 4 ? fifthStatus : "approved", 0);
        return new Setup(org, site, level, pump, ids);
    }

    private static object InfoPoint(long panorama, long level, double lon = Lon, string? source = null) => new
    {
        kind = "panorama_point", panoramaId = panorama, yawDeg = 35.5, pitchDeg = -10, levelId = level, zRelM = 0.8,
        position = new { type = "Point", coordinates = new[] { lon, Lat + 3 / 111200.0 } }, positionSource = source,
    };

    [Fact]
    public async Task InfoPoint_FromGridRecord_ShowsOnPanoramaAndFloorplan()
    {
        var s = await LevelWithPanoramasAsync();

        var created = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", InfoPoint(s.Panoramas[0], s.Level));
        var onPanorama = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/panoramas/{s.Panoramas[0]}/anchors");
        var onLevel = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{s.Site}/anchors?levelId={s.Level}");
        var grid = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes?siteId={s.Site}&typeId={await s.Org.Http.TypeIdAsync("equipment")}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var point = Assert.Single(onPanorama.EnumerateArray());
        Assert.Equal("P1 szivattyú", point.GetProperty("nodeName").GetString());
        Assert.Equal(35.5, point.GetProperty("yawDeg").GetDouble());
        Assert.Equal("manual", point.GetProperty("positionSource").GetString());
        Assert.Single(onLevel.EnumerateArray());
        // FR-G4: the grid shows that the record has an info point now.
        Assert.Equal(["panorama_point"], grid.GetProperty("rows")[0].GetProperty("anchorKinds").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task InfoPoint_WithoutFloorplan_UsesPanoramaFallback_UntilMovedByHand()
    {
        var s = await LevelWithPanoramasAsync();
        var created = await TestApi.ReadJson(await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors",
            InfoPoint(s.Panoramas[0], s.Level, source: "panorama_fallback")));
        var id = created.GetProperty("id").GetInt64();

        var moved = await TestApi.ReadJson(await s.Org.Http.PatchJsonAsync($"/api/v1/anchors/{id}",
            new { position = new { type = "Point", coordinates = new[] { Lon + 0.00001, Lat } } }));

        Assert.Equal("panorama_fallback", created.GetProperty("positionSource").GetString());
        Assert.Equal("manual", moved.GetProperty("positionSource").GetString());
    }

    [Fact]
    public async Task InfoPoint_Validation_AndDeleteWithNode()
    {
        var s = await LevelWithPanoramasAsync();
        var other = await Api.NewOrganizationAsync();
        var otherSite = await other.Http.CreateSiteAsync();
        var otherNode = (await other.Http.CreateNodeAsync(otherSite, null, await other.Http.TypeIdAsync("equipment"), "X")).GetProperty("id").GetInt64();

        var noYaw = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", new
        {
            kind = "panorama_point", panoramaId = s.Panoramas[0], position = new { type = "Point", coordinates = new[] { Lon, Lat } },
        });
        var prism = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors",
            new { kind = "region", regionShape = "prism", position = new { type = "Point", coordinates = new[] { Lon, Lat } } });
        var foreignPanorama = await other.Http.PostAsJsonAsync($"/api/v1/nodes/{otherNode}/anchors", InfoPoint(s.Panoramas[0], s.Level));
        var created = await TestApi.ReadJson(await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", InfoPoint(s.Panoramas[0], s.Level)));
        var deleteNode = await s.Org.Http.DeleteAsync($"/api/v1/nodes/{s.Pump}");

        Assert.Equal("anchor.panorama_fields", await TestApi.ErrorCode(noYaw));
        Assert.Equal("anchor.kind_not_supported", await TestApi.ErrorCode(prism));
        // Refused already on the level of the other organization (not visible to it).
        Assert.Equal("level.invalid", await TestApi.ErrorCode(foreignPanorama));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.DeleteAsync($"/api/v1/anchors/{created.GetProperty("id").GetInt64()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteNode.StatusCode);
        Assert.Empty((await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{s.Site}/anchors?levelId={s.Level}")).EnumerateArray());
    }

    [Fact]
    public async Task Viewer_DoesNotSeeInfoPointsOfUnapprovedPanoramas()
    {
        var s = await LevelWithPanoramasAsync(fifthStatus: "review");
        await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", InfoPoint(s.Panoramas[0], s.Level));
        await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", InfoPoint(s.Panoramas[4], s.Level));
        var viewer = await Api.NewUserAsync(s.Org.OrganizationId, s.Org.OrganizationCode, Roles.Viewer);

        var seen = await viewer.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{s.Site}/anchors?levelId={s.Level}");

        Assert.Equal([s.Panoramas[0]], seen.EnumerateArray().Select(a => a.GetProperty("panoramaId").GetInt64()));
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Http.GetAsync($"/api/v1/panoramas/{s.Panoramas[4]}/anchors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", InfoPoint(s.Panoramas[0], s.Level))).StatusCode);
    }

    [Fact]
    public async Task AutoLink_LinksNearestWithinRealDistance_AndNeighboursHaveBearing()
    {
        var s = await LevelWithPanoramasAsync();

        var created = await TestApi.ReadJson(await s.Org.Http.PostAsJsonAsync($"/api/v1/sites/{s.Site}/panorama-links/auto",
            new { levelId = s.Level, maxDistanceM = 10, maxNeighbours = 4 }));
        var again = await TestApi.ReadJson(await s.Org.Http.PostAsJsonAsync($"/api/v1/sites/{s.Site}/panorama-links/auto",
            new { levelId = s.Level, maxDistanceM = 10 }));
        var middle = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/panoramas/{s.Panoramas[2]}/links");

        // 8 m apart: only direct neighbours are within 10 m (16 m is not) → a chain of 4 links; nothing new the second time.
        Assert.Equal(4, created.GetInt32());
        Assert.Equal(0, again.GetInt32());
        var neighbours = middle.EnumerateArray().ToList();
        Assert.Equal(2, neighbours.Count);
        Assert.All(neighbours, n => Assert.Equal(8, n.GetProperty("distanceM").GetDouble(), 0));
        var east = neighbours.Single(n => n.GetProperty("id").GetInt64() == s.Panoramas[3]);
        var west = neighbours.Single(n => n.GetProperty("id").GetInt64() == s.Panoramas[1]);
        Assert.Equal(90, east.GetProperty("bearingDeg").GetDouble(), 0);
        Assert.Equal(270, west.GetProperty("bearingDeg").GetDouble(), 0);
    }

    [Fact]
    public async Task ManualLinks_BothWays_AndOtherOrganizationCannotSee()
    {
        var s = await LevelWithPanoramasAsync();
        var other = await Api.NewOrganizationAsync();

        var linked = await s.Org.Http.PostAsJsonAsync($"/api/v1/panoramas/{s.Panoramas[0]}/links", new { toId = s.Panoramas[4] });
        var back = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/panoramas/{s.Panoramas[4]}/links");
        var foreign = await other.Http.GetAsync($"/api/v1/panoramas/{s.Panoramas[0]}/links");
        var unlinked = await s.Org.Http.DeleteAsync($"/api/v1/panoramas/{s.Panoramas[4]}/links/{s.Panoramas[0]}");
        var after = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/panoramas/{s.Panoramas[0]}/links");

        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        Assert.Equal(s.Panoramas[0], Assert.Single(back.EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(32, back[0].GetProperty("distanceM").GetDouble(), 0);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unlinked.StatusCode);
        Assert.Empty(after.EnumerateArray());
    }
}
