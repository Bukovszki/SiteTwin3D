using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Users;

/// <summary>
/// Site-level roles (spec 3.2): a restricted user sees only the assigned sites (others: 404) and edits only where the
/// site role is Editor (else 403 site.read_only); bulk operations are checked too.
/// </summary>
public sealed class SiteAccessTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private record World(OrgClient Admin, long EditSite, long ViewSite, long HiddenSite, Dictionary<long, long> Nodes, Dictionary<long, long> Panoramas);

    /// <summary>Three sites, each with a building, an equipment node and an approved panorama.</summary>
    private async Task<World> WorldAsync()
    {
        var admin = await Api.NewOrganizationAsync();
        var sites = new[] { await admin.Http.CreateSiteAsync("A"), await admin.Http.CreateSiteAsync("B"), await admin.Http.CreateSiteAsync("C") };
        var nodes = new Dictionary<long, long>();
        var panoramas = new Dictionary<long, long>();
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        foreach (var site in sites)
        {
            nodes[site] = (await admin.Http.CreateNodeAsync(site, null, await admin.Http.TypeIdAsync("equipment"), $"Szivattyú {site}")).GetProperty("id").GetInt64();
            panoramas[site] = await sql.PanoramaAsync(admin.OrganizationId, nodes[site], 19.08, 47.43, null);
        }
        return new World(admin, sites[0], sites[1], sites[2], nodes, panoramas);
    }

    /// <summary>A user of the organization with site roles (set by the admin through the API).</summary>
    private async Task<OrgClient> UserWithSitesAsync(World w, string role, params (long Site, string Role)[] siteRoles)
    {
        var user = await Api.NewUserAsync(w.Admin.OrganizationId, w.Admin.OrganizationCode, role);
        var me = await user.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        var set = await w.Admin.Http.PutAsJsonAsync($"/api/v1/users/{me.GetProperty("id").GetInt64()}/site-roles",
            new { roles = siteRoles.Select(r => new { siteId = r.Site, role = r.Role }) });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        return user;
    }

    [Fact]
    public async Task RestrictedUser_SeesOnlyAssignedSites()
    {
        var w = await WorldAsync();
        var user = await UserWithSitesAsync(w, Roles.Editor, (w.EditSite, Roles.Editor), (w.ViewSite, Roles.Viewer));

        var sites = await user.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites");
        var me = await user.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");

        Assert.Equal([w.EditSite, w.ViewSite], sites.EnumerateArray().Select(s => s.GetProperty("id").GetInt64()).Order());
        Assert.Equal([w.EditSite], me.GetProperty("editableSiteIds").EnumerateArray().Select(s => s.GetInt64()));
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync($"/api/v1/sites/{w.HiddenSite}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync($"/api/v1/nodes/{w.Nodes[w.HiddenSite]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync($"/api/v1/panoramas/{w.Panoramas[w.HiddenSite]}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync($"/api/v1/panoramas/{w.Panoramas[w.ViewSite]}")).StatusCode);
    }

    [Fact]
    public async Task SiteViewer_CannotWrite_SiteEditorCan()
    {
        var w = await WorldAsync();
        var user = await UserWithSitesAsync(w, Roles.Editor, (w.EditSite, Roles.Editor), (w.ViewSite, Roles.Viewer));
        var type = await user.Http.TypeIdAsync("equipment");

        var editOk = await user.Http.PatchJsonAsync($"/api/v1/nodes/{w.Nodes[w.EditSite]}", new { name = "Átnevezve" });
        var editRo = await user.Http.PatchJsonAsync($"/api/v1/nodes/{w.Nodes[w.ViewSite]}", new { name = "Tilos" });
        var createRo = await user.Http.PostNodeAsync(w.ViewSite, null, type, "Tilos");
        var deleteRo = await user.Http.DeleteAsync($"/api/v1/nodes/{w.Nodes[w.ViewSite]}");
        var anchorRo = await user.Http.PostAsJsonAsync($"/api/v1/nodes/{w.Nodes[w.ViewSite]}/anchors",
            new { kind = "point", position = new { type = "Point", coordinates = new[] { 19.08, 47.43 } } });
        var panoramaRo = await user.Http.PatchJsonAsync($"/api/v1/panoramas/{w.Panoramas[w.ViewSite]}", new { headingDeg = 10 });

        Assert.Equal(HttpStatusCode.OK, editOk.StatusCode);
        foreach (var response in new[] { editRo, createRo, deleteRo, anchorRo, panoramaRo })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("site.read_only", await TestApi.ErrorCode(response));
        }
        Assert.Equal("Szivattyú " + w.ViewSite, (await w.Admin.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{w.Nodes[w.ViewSite]}")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task ViewerUser_NeverEdits_EvenWithSiteEditorRole()
    {
        var w = await WorldAsync();
        var viewer = await UserWithSitesAsync(w, Roles.Viewer, (w.EditSite, Roles.Editor));

        var me = await viewer.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        var edit = await viewer.Http.PatchJsonAsync($"/api/v1/nodes/{w.Nodes[w.EditSite]}", new { name = "X" });

        Assert.Empty(me.GetProperty("editableSiteIds").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
    }

    [Fact]
    public async Task UnrestrictedUser_SeesEverySite_AndSiteRolesApplyWithoutNewSignIn()
    {
        var w = await WorldAsync();
        var user = await Api.NewUserAsync(w.Admin.OrganizationId, w.Admin.OrganizationCode, Roles.Editor);
        Assert.Equal(3, (await user.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites")).GetArrayLength());

        var me = await user.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        await w.Admin.Http.PutAsJsonAsync($"/api/v1/users/{me.GetProperty("id").GetInt64()}/site-roles",
            new { roles = new[] { new { siteId = w.EditSite, role = "Viewer" } } });

        Assert.Single((await user.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites")).EnumerateArray());
    }

    [Fact]
    public async Task BulkOperations_AreCheckedToo()
    {
        var w = await WorldAsync();
        await using (var sql = await SqlTestData.OpenAsync(Postgis))
            await sql.ExecuteAsync(
                "INSERT INTO anchor (organization_id, node_id, position, kind) VALUES (@org, @node, ST_SetSRID(ST_MakePoint(19.08, 47.43), 4326), 'point')",
                ("org", w.Admin.OrganizationId), ("node", w.Nodes[w.ViewSite]));
        var anchor = (await w.Admin.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{w.Nodes[w.ViewSite]}/anchors"))[0].GetProperty("id").GetInt64();
        var layer = (await TestApi.ReadJson(await w.Admin.Http.PostAsJsonAsync("/api/v1/draw-layers",
            new { siteId = w.ViewSite, name = "Zónák", geometryType = "MultiPolygon" }))).GetProperty("id").GetInt64();
        var user = await UserWithSitesAsync(w, Roles.Editor, (w.EditSite, Roles.Editor), (w.ViewSite, Roles.Viewer));

        Assert.Equal(HttpStatusCode.Forbidden, (await user.Http.DeleteAsync($"/api/v1/anchors/{anchor}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Http.DeleteAsync($"/api/v1/draw-layers/{layer}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Http.PostAsJsonAsync($"/api/v1/draw-layers/{layer}/transaction", new { deletes = Array.Empty<object>() })).StatusCode);
        Assert.Single(await w.Admin.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{w.Nodes[w.ViewSite]}/anchors") is var a ? a.EnumerateArray() : default);
    }
}
