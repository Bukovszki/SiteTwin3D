using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests.Organizations;

/// <summary>
/// Organization isolation (spec 3.1): another organization's data is not reachable (404), not even by id; writes
/// into another organization are refused. Plus a model check that every organization-owned table is filtered.
/// </summary>
public sealed class OrganizationIsolationTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    [Fact]
    public void EveryOrganizationOwnedEntity_HasTheOrganizationFilter()
    {
        using var db = Postgis.CreateDbContext();

        var unfiltered = db.Model.GetEntityTypes()
            .Where(t => typeof(IOrganizationOwned).IsAssignableFrom(t.ClrType))
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name)
            .ToList();

        Assert.Empty(unfiltered);
    }

    [Fact]
    public async Task OtherOrganizationsSiteAndNodes_AreNotFound()
    {
        var a = await Api.NewOrganizationAsync();
        var b = await Api.NewOrganizationAsync();
        var site = await a.Http.CreateSiteAsync();
        var building = await a.Http.CreateNodeAsync(site, null, await a.Http.TypeIdAsync("building"), "Building");
        var nodeId = building.GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/v1/sites/{site}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/v1/sites/{site}/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/v1/nodes/{nodeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/v1/nodes/{nodeId}/ancestors")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/v1/nodes/{nodeId}/relations")).StatusCode);

        var sitesOfB = await b.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites");
        Assert.DoesNotContain(sitesOfB.EnumerateArray(), s => s.GetProperty("id").GetInt64() == site);
    }

    [Fact]
    public async Task WritesIntoOtherOrganization_AreRefused()
    {
        var a = await Api.NewOrganizationAsync();
        var b = await Api.NewOrganizationAsync();
        var site = await a.Http.CreateSiteAsync();
        var building = await a.Http.CreateNodeAsync(site, null, await a.Http.TypeIdAsync("building"), "Building");
        var nodeId = building.GetProperty("id").GetInt64();
        var bSite = await b.Http.CreateSiteAsync();
        var bNode = (await b.Http.CreateNodeAsync(bSite, null, await b.Http.TypeIdAsync("building"), "B")).GetProperty("id").GetInt64();

        // Node on A's site, child of A's node, update and delete of A's node and site.
        Assert.Equal(HttpStatusCode.NotFound,
            (await b.Http.PostNodeAsync(site, null, await b.Http.TypeIdAsync("building"), "Intruder")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await b.Http.PostNodeAsync(bSite, nodeId, await b.Http.TypeIdAsync("level"), "Intruder", new { elevation_m = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.PatchJsonAsync($"/api/v1/nodes/{nodeId}", new { name = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.DeleteAsync($"/api/v1/nodes/{nodeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.PatchJsonAsync($"/api/v1/sites/{site}", new { name = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.DeleteAsync($"/api/v1/sites/{site}")).StatusCode);
        // Move B's node under A's node; relation from B's node to A's node.
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Http.PatchJsonAsync($"/api/v1/nodes/{bNode}", new { parentId = nodeId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await b.Http.PostAsJsonAsync($"/api/v1/nodes/{bNode}/relations", new { toId = nodeId, kind = "located_in" })).StatusCode);

        var unchanged = await a.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{nodeId}");
        Assert.Equal("Building", unchanged.GetProperty("name").GetString());
    }

    [Fact]
    public async Task OwnNodeTypes_AreInvisibleToOtherOrganizations()
    {
        var a = await Api.NewOrganizationAsync();
        var b = await Api.NewOrganizationAsync();
        var created = await a.Http.PostAsJsonAsync("/api/v1/node-types", new { code = "pump", name = "Szivattyú", aspect = "function" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var typeId = (await TestApi.ReadJson(created)).GetProperty("id").GetInt64();

        var typesOfB = await b.Http.GetFromJsonAsync<JsonElement>("/api/v1/node-types");
        Assert.DoesNotContain(typesOfB.EnumerateArray(), t => t.GetProperty("id").GetInt64() == typeId);
        Assert.Contains(typesOfB.EnumerateArray(), t => t.GetProperty("code").GetString() == "building");
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.PatchJsonAsync($"/api/v1/node-types/{typeId}", new { name = "X" })).StatusCode);

        // B can use the same code for its own type; A's type cannot be used by B.
        Assert.Equal(HttpStatusCode.Created,
            (await b.Http.PostAsJsonAsync("/api/v1/node-types", new { code = "pump", name = "Pumpa", aspect = "function" })).StatusCode);
        var bSite = await b.Http.CreateSiteAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Http.PostNodeAsync(bSite, null, typeId, "X")).StatusCode);
    }

    [Fact]
    public async Task FilteredContext_WithoutOrganization_SeesNoOrganizationData()
    {
        var a = await Api.NewOrganizationAsync();
        await a.Http.CreateSiteAsync();

        await using var db = Postgis.CreateDbContext();

        Assert.False(await db.Sites.AnyAsync());
        Assert.False(await db.Nodes.AnyAsync());
        Assert.True(await db.Sites.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == a.OrganizationId));
    }

    [Fact]
    public async Task PlatformAdmin_CannotReadOrganizationData()
    {
        var a = await Api.NewOrganizationAsync();
        var site = await a.Http.CreateSiteAsync();
        var platform = await Api.NewPlatformAdminAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync("/api/v1/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await platform.GetAsync($"/api/v1/sites/{site}")).StatusCode);
    }
}
