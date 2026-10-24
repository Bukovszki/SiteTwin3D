using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Hierarchy;

/// <summary>Deleting a site removes everything on it, including rows that refer to its levels without a cascade.</summary>
public sealed class SiteDeletionTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    [Fact]
    public async Task Site_WithAnchorsOnLevels_PanoramasAndFinalizedRecords_IsDeleted()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var level = (await org.Http.CreateNodeAsync(site, building, await org.Http.TypeIdAsync("level"), "Földszint", new { elevation_m = 0 }))
            .GetProperty("id").GetInt64();
        var pump = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("equipment"), "P1")).GetProperty("id").GetInt64();
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors",
            new { kind = "point", levelId = level, position = new { type = "Point", coordinates = new[] { 19.08, 47.43 } } });
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        await sql.PanoramaAsync(org.OrganizationId, level, 19.08, 47.43, level);
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/finalize", new { });

        var deleted = await org.Http.DeleteAsync($"/api/v1/sites/{site}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.Http.GetAsync($"/api/v1/nodes/{pump}")).StatusCode);
        Assert.Empty((await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites")).EnumerateArray());
    }
}
