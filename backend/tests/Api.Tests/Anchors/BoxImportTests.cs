using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;

namespace Api.Tests.Anchors;

/// <summary>
/// Box import from a table (user decision): a record's rows (by code) replace its boxes, centres transformed from the
/// source system (EOV) to WGS84, existing boxes updated in place, all or nothing, history of finalized records.
/// </summary>
public sealed class BoxImportTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    // The test site's point (19.04, 47.50) in EOV (EPSG:23700): Y (east), X (north).
    private const double East = 649439.112, North = 239562.881;

    private async Task<(OrgClient Org, long Site, long Level, long Pump, long Pipe)> SetupAsync()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var level = await CodedAsync(org, site, building, "level", "Földszint", "F0", new { elevation_m = 0 });
        var pump = await CodedAsync(org, site, null, "equipment", "Szivattyú", "P1");
        var pipe = await CodedAsync(org, site, null, "equipment", "Nyomócső", "L1");
        return (org, site, level, pump, pipe);
    }

    private static async Task<long> CodedAsync(OrgClient org, long site, long? parent, string type, string name, string code, object? attrs = null)
    {
        var id = (await org.Http.CreateNodeAsync(site, parent, await org.Http.TypeIdAsync(type), name, attrs)).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await org.Http.PatchJsonAsync($"/api/v1/nodes/{id}", new { code })).StatusCode);
        return id;
    }

    private static object Row(string code, double east, double north, string? level = "F0", object? bottom = null, object? sizeX = null,
        object? heading = null) => new
    {
        code, level, east, north, bottom, sizeX = sizeX ?? 2, sizeY = "1,5", sizeZ = 1, heading,
    };

    private static Task<HttpResponseMessage> ImportAsync(OrgClient org, long site, object[] rows, string srs = "EPSG:23700", string? reason = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sites/{site}/import/boxes") { Content = JsonContent.Create(new { srs, rows }) };
        if (reason is not null) request.Headers.Add("X-Change-Reason", Uri.EscapeDataString(reason));
        return org.Http.SendAsync(request);
    }

    private static async Task<List<JsonElement>> BoxesAsync(OrgClient org, long node) =>
        (await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{node}/anchors")).EnumerateArray()
            .Where(a => a.GetProperty("kind").GetString() == "region").OrderBy(a => a.GetProperty("id").GetInt64()).ToList();

    [Fact]
    public async Task Import_EovRows_ReplaceTheRecordsBoxes()
    {
        var (org, site, level, pump, pipe) = await SetupAsync();
        var drawn = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", new
        {
            kind = "region", regionShape = "box", sizeX = 1, sizeY = 1, sizeZ = 1, levelId = level, zRelM = 0.5,
            position = new { type = "Point", coordinates = new[] { 19.041, 47.501 } },
        }));

        var first = await ImportAsync(org, site,
        [
            Row("P1", East, North, bottom: "0,5", heading: 30),
            Row("L1", East + 5, North, level: "Földszint"),
            Row("L1", East + 10, North, sizeX: "4"),
        ]);
        var result = await TestApi.ReadJson(first);
        Assert.True(first.StatusCode == HttpStatusCode.OK, result.ToString());
        var pumpBox = Assert.Single(await BoxesAsync(org, pump));
        var pipeBoxes = await BoxesAsync(org, pipe);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((2, 1, 0, 2), (result.GetProperty("created").GetInt32(), result.GetProperty("updated").GetInt32(),
            result.GetProperty("removed").GetInt32(), result.GetProperty("records").GetInt32()));
        // The drawn box is updated in place (its history stays with it).
        Assert.Equal(drawn.GetProperty("id").GetInt64(), pumpBox.GetProperty("id").GetInt64());
        var position = pumpBox.GetProperty("position").GetProperty("coordinates");
        Assert.Equal(19.04, position[0].GetDouble(), 6);
        Assert.Equal(47.50, position[1].GetDouble(), 6);
        Assert.Equal(level, pumpBox.GetProperty("levelId").GetInt64());
        Assert.Equal(1.0, pumpBox.GetProperty("zRelM").GetDouble(), 3); // underside 0.5 + half of 1 m
        Assert.Equal((2.0, 1.5, 1.0), (pumpBox.GetProperty("sizeX").GetDouble(), pumpBox.GetProperty("sizeY").GetDouble(), pumpBox.GetProperty("sizeZ").GetDouble()));
        Assert.Equal(30, pumpBox.GetProperty("headingDeg").GetDouble(), 1); // EOV grid north ≈ true north at Budapest
        Assert.Equal("import", pumpBox.GetProperty("positionSource").GetString());
        Assert.Equal(2, pipeBoxes.Count);
        Assert.Equal(4, pipeBoxes[1].GetProperty("sizeX").GetDouble(), 3);

        // Again with one row for the pipe: its second box goes.
        var second = await TestApi.ReadJson(await ImportAsync(org, site, [Row("L1", East + 5, North)]));
        Assert.Equal((0, 1, 1), (second.GetProperty("created").GetInt32(), second.GetProperty("updated").GetInt32(), second.GetProperty("removed").GetInt32()));
        Assert.Equal(pipeBoxes[0].GetProperty("id").GetInt64(), Assert.Single(await BoxesAsync(org, pipe)).GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Import_Errors_AllAtOnce_NothingSaved()
    {
        var (org, site, _, pump, _) = await SetupAsync();
        var response = await ImportAsync(org, site,
        [
            Row("P1", East, North),                 // fine, but the rest fails
            Row("NINCS", East, North),              // unknown code
            Row("P1", East, North, level: "F9"),    // unknown level
            Row("L1", North, East),                 // EOV X and Y swapped: far away
            Row("L1", East, North, sizeX: "sok"),   // not a number
        ]);
        var body = await TestApi.ReadJson(response);
        var errors = body.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("code").GetString())).ToList();
        var badSrs = await ImportAsync(org, site, [Row("P1", East, North)], srs: "EPSG:4269x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("import.invalid", body.GetProperty("code").GetString());
        Assert.Contains((2, "import.unknown_code"), errors);
        Assert.Contains((3, "import.unknown_level"), errors);
        Assert.Contains((4, "import.far_from_site"), errors);
        Assert.Contains((5, "import.invalid_value"), errors);
        Assert.DoesNotContain(errors, e => e.Item1 == 1);
        Assert.Empty(await BoxesAsync(org, pump));
        Assert.Equal("import.invalid_srs", await TestApi.ErrorCode(badSrs));
    }

    [Fact]
    public async Task Import_FinalizedRecord_NeedsAReason_AndIsInItsHistory()
    {
        var (org, site, _, pump, _) = await SetupAsync();
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/finalize", new { validFrom = "2025-01-01" });

        var withoutReason = await ImportAsync(org, site, [Row("P1", East, North)]);
        var withReason = await ImportAsync(org, site, [Row("P1", East, North)], reason: "Felmérés 2026");
        var history = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/history");

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutReason.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withReason.StatusCode);
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("reason").GetString() == "Felmérés 2026");
    }

    [Fact]
    public async Task Import_OtherOrganization_And_Viewer_Refused()
    {
        var (org, site, _, _, _) = await SetupAsync();
        var other = await Api.NewOrganizationAsync();
        var viewer = await Api.NewUserAsync(org.OrganizationId, org.OrganizationCode, Roles.Viewer);

        Assert.Equal(HttpStatusCode.NotFound, (await ImportAsync(other, site, [Row("P1", East, North)])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ImportAsync(viewer, site, [Row("P1", East, North)])).StatusCode);
    }
}
