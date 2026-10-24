using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Services.Locations;
using Api.Tests.Infrastructure;

namespace Api.Tests.Hierarchy;

/// <summary>
/// M8: a process line in the functional tree crosses levels; where its elements are comes from located_in, else from
/// the smallest location box containing their anchor (3D height included).
/// </summary>
public sealed class LocationTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const double Lon = 19.08, Lat = 47.43;
    // 1 m east / north in degrees at 47.43° N.
    private const double E = 1 / 75470.0, N = 1 / 111190.0;

    private static object Point(double east, double north, long level, double z) =>
        new { kind = "point", levelId = level, zRelM = z, position = new { type = "Point", coordinates = new[] { Lon + east * E, Lat + north * N } } };

    private static object Box(double east, double north, long level, double sx, double sy, double sz, double heading = 0) => new
    {
        kind = "region", regionShape = "box", sizeX = sx, sizeY = sy, sizeZ = sz, headingDeg = heading, levelId = level, zRelM = sz / 2,
        position = new { type = "Point", coordinates = new[] { Lon + east * E, Lat + north * N } },
    };

    [Fact]
    public void Containment_UndoesTheTurn_AndChecksHeight()
    {
        // 10 m × 2 m turned 90°: long along north–south.
        var box = new BoxShape(Lon, Lat, 1.5, 10, 2, 3, 90);
        Assert.True(BoxContainment.Contains(box, Lon, Lat + 4.5 * N, 1));
        Assert.False(BoxContainment.Contains(box, Lon + 4.5 * E, Lat, 1));
        Assert.False(BoxContainment.Contains(box, Lon, Lat, 3.2));
    }

    [Fact]
    public async Task Line_ElementsAcrossLevels_ByRelationOrSmallestBox()
    {
        var org = await Api.NewOrganizationAsync();
        var h = org.Http;
        var site = await h.CreateSiteAsync();
        async Task<long> NodeAsync(long? parent, string type, string name, object? attrs = null) =>
            (await h.CreateNodeAsync(site, parent, await h.TypeIdAsync(type), name, attrs)).GetProperty("id").GetInt64();
        var building = await NodeAsync(null, "building", "Gépház");
        var ground = await NodeAsync(building, "level", "Földszint", new { elevation_m = 0 });
        var first = await NodeAsync(building, "level", "Emelet", new { elevation_m = 4.5 });
        var hall = await NodeAsync(ground, "room", "Szivattyútér");
        var store = await NodeAsync(ground, "room", "Raktár");
        var control = await NodeAsync(first, "room", "Vezérlőterem");
        var line = await NodeAsync(null, "process_line", "Előkezelő vonal");
        var pump = await NodeAsync(line, "equipment", "P1");
        var screen = await NodeAsync(line, "equipment", "Szita");
        var cabinet = await NodeAsync(line, "equipment", "Vezérlőszekrény");
        var spare = await NodeAsync(line, "equipment", "Tartalék");

        // The building box holds everything; the hall and the control room (first floor, same ground plan) inside it.
        await h.PostAsJsonAsync($"/api/v1/nodes/{building}/anchors", Box(0, 0, ground, 40, 24, 8));
        await h.PostAsJsonAsync($"/api/v1/nodes/{hall}/anchors", Box(-7, 0, ground, 25, 23, 4.2));
        await h.PostAsJsonAsync($"/api/v1/nodes/{control}/anchors", Box(-7, 0, first, 25, 23, 3.5));
        await h.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Point(1.5, -3.7, ground, 0.5));
        await h.PostAsJsonAsync($"/api/v1/nodes/{cabinet}/anchors", Point(1.5, -3.7, first, 1.0));
        await h.PostAsJsonAsync($"/api/v1/nodes/{screen}/anchors", Point(-17, -1, ground, 1));
        var set = await h.PutAsJsonAsync($"/api/v1/nodes/{screen}/location", new { locationId = store });

        var result = await h.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{line}/members");
        var members = result.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);
        string? Place(string name) => members[name].GetProperty("location") is { ValueKind: JsonValueKind.Object } l ? l.GetProperty("name").GetString() : null;

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal("Előkezelő vonal", result.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal(["P1", "Szita", "Tartalék", "Vezérlőszekrény"], members.Keys.Order());
        Assert.Equal("Szivattyútér", Place("P1"));
        Assert.Equal("box", members["P1"].GetProperty("location").GetProperty("source").GetString());
        Assert.Equal(["Gépház", "Földszint", "Szivattyútér"], members["P1"].GetProperty("location").GetProperty("path").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("Vezérlőterem", Place("Vezérlőszekrény")); // same ground plan, other level: the height decides
        Assert.Equal("Emelet", members["Vezérlőszekrény"].GetProperty("levelName").GetString());
        Assert.Equal("Raktár", Place("Szita"));
        Assert.Equal("relation", members["Szita"].GetProperty("location").GetProperty("source").GetString());
        Assert.Null(Place("Tartalék"));
        Assert.Equal(1, members["P1"].GetProperty("depth").GetInt32());

        // Clearing the relation: back to the box (the screen stands in the hall box).
        await h.PutAsJsonAsync($"/api/v1/nodes/{screen}/location", new { locationId = (long?)null });
        var cleared = await h.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{screen}/members");
        Assert.Equal("Szivattyútér", cleared.GetProperty("node").GetProperty("location").GetProperty("name").GetString());

        // Only a place of the location tree on the same site; other organizations see nothing.
        Assert.Equal("location.invalid", await TestApi.ErrorCode(await h.PutAsJsonAsync($"/api/v1/nodes/{screen}/location", new { locationId = pump })));
        var other = await Api.NewOrganizationAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/nodes/{line}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.PutAsJsonAsync($"/api/v1/nodes/{screen}/location", new { locationId = store })).StatusCode);
    }
}
