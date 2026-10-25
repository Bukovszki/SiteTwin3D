using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;

namespace Api.Tests.Hierarchy;

/// <summary>M1 acceptance through the API: subtree, ancestor and move behaviour; cycles refused (spec 12, FR-H).</summary>
public sealed class HierarchyApiTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    /// <summary>Building ─ Level ─ Room on a site, plus a second building.</summary>
    private async Task<(OrgClient Org, long Site, long Building, long Level, long Room, long Building2)> LocationTreeAsync()
    {
        var org = await Api.NewOrganizationAsync(Roles.Editor);
        var admin = await Api.NewUserAsync(org.OrganizationId, org.OrganizationCode, Roles.OrgAdmin);
        var site = await admin.Http.CreateSiteAsync();
        var http = org.Http;
        var building = (await http.CreateNodeAsync(site, null, await http.TypeIdAsync("building"), "A épület")).GetProperty("id").GetInt64();
        var level = (await http.CreateNodeAsync(site, building, await http.TypeIdAsync("level"), "Földszint",
            new { elevation_m = 0, height_m = 4 })).GetProperty("id").GetInt64();
        var room = (await http.CreateNodeAsync(site, level, await http.TypeIdAsync("room"), "Gépház")).GetProperty("id").GetInt64();
        var building2 = (await http.CreateNodeAsync(site, null, await http.TypeIdAsync("building"), "B épület", sortOrder: 1))
            .GetProperty("id").GetInt64();
        return (org, site, building, level, room, building2);
    }

    /// <summary>A chain of an own type allowed anywhere (the base types' parent rules would refuse these moves first).</summary>
    private async Task<(OrgClient Org, long Outer, long Inner)> ZoneChainAsync()
    {
        var org = await Api.NewOrganizationAsync(Roles.Editor);
        var admin = await Api.NewUserAsync(org.OrganizationId, org.OrganizationCode, Roles.OrgAdmin);
        var site = await admin.Http.CreateSiteAsync();
        var zone = (await TestApi.ReadJson(await admin.Http.PostAsJsonAsync("/api/v1/node-types",
            new { code = "zone", name = "Zóna", aspect = "location" }))).GetProperty("id").GetInt64();
        var outer = (await org.Http.CreateNodeAsync(site, null, zone, "Külső")).GetProperty("id").GetInt64();
        var inner = (await org.Http.CreateNodeAsync(site, outer, zone, "Belső")).GetProperty("id").GetInt64();
        return (org, outer, inner);
    }

    [Fact]
    public async Task Create_ReturnsPathAndDepth()
    {
        var t = await LocationTreeAsync();

        var room = await t.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{t.Room}");

        Assert.Equal($"{t.Building}.{t.Level}.{t.Room}", room.GetProperty("path").GetString());
        Assert.Equal(3, room.GetProperty("depth").GetInt32());
        Assert.Equal("room", room.GetProperty("typeCode").GetString());
        Assert.Equal("location", room.GetProperty("aspect").GetString());
    }

    [Fact]
    public async Task Tree_ReturnsSubtreeWithDepthLimit()
    {
        var t = await LocationTreeAsync();
        var http = t.Org.Http;

        var all = await http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/nodes");
        var roots = await http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/nodes?depth=0");
        var subtree = await http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/nodes?root={t.Building}");
        var shallow = await http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/nodes?root={t.Building}&depth=1");
        var function = await http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/nodes?aspect=function");

        Assert.Equal([t.Building, t.Building2, t.Level, t.Room], Ids(all));
        Assert.Equal([t.Building, t.Building2], Ids(roots));
        Assert.Equal([t.Building, t.Level, t.Room], Ids(subtree));
        Assert.Equal([t.Building, t.Level], Ids(shallow));
        Assert.Empty(Ids(function));
    }

    [Fact]
    public async Task Ancestors_ReturnBreadcrumbFromRoot()
    {
        var t = await LocationTreeAsync();

        var ancestors = await t.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{t.Room}/ancestors");

        Assert.Equal([t.Building, t.Level], Ids(ancestors));
    }

    [Fact]
    public async Task Move_MovesWholeSubtree()
    {
        var t = await LocationTreeAsync();
        var http = t.Org.Http;

        var moved = await http.PatchJsonAsync($"/api/v1/nodes/{t.Level}", new { parentId = t.Building2 });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var room = await http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{t.Room}");
        Assert.Equal($"{t.Building2}.{t.Level}.{t.Room}", room.GetProperty("path").GetString());
        var ancestors = await http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{t.Room}/ancestors");
        Assert.Equal([t.Building2, t.Level], Ids(ancestors));
        Assert.Equal([t.Building], Ids(await http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/nodes?root={t.Building}")));
    }

    [Fact]
    public async Task Move_ToRoot_WithNullParent()
    {
        var t = await ZoneChainAsync();

        var moved = await t.Org.Http.PatchJsonAsync($"/api/v1/nodes/{t.Inner}", new { parentId = (long?)null });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var inner = await TestApi.ReadJson(moved);
        Assert.Equal($"{t.Inner}", inner.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, inner.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task Move_UnderOwnSubtree_IsConflict()
    {
        var t = await ZoneChainAsync();

        var response = await t.Org.Http.PatchJsonAsync($"/api/v1/nodes/{t.Outer}", new { parentId = t.Inner });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("node.cycle", await TestApi.ErrorCode(response));
        var inner = await t.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{t.Inner}");
        Assert.Equal($"{t.Outer}.{t.Inner}", inner.GetProperty("path").GetString());
    }

    [Fact]
    public async Task OtherPatchFields_DoNotMoveTheNode()
    {
        var t = await LocationTreeAsync();

        var response = await t.Org.Http.PatchJsonAsync($"/api/v1/nodes/{t.Room}", new { name = "Fúvóház", code = "=A1+R01", sortOrder = 5 });

        var room = await TestApi.ReadJson(response);
        Assert.Equal("Fúvóház", room.GetProperty("name").GetString());
        Assert.Equal("=A1+R01", room.GetProperty("code").GetString());
        Assert.Equal(5, room.GetProperty("sortOrder").GetInt32());
        Assert.Equal(t.Level, room.GetProperty("parentId").GetInt64());
    }

    [Fact]
    public async Task ParentFromOtherTree_IsRejected()
    {
        var t = await LocationTreeAsync();
        var http = t.Org.Http;

        var response = await http.PostNodeAsync(t.Site, t.Room, await http.TypeIdAsync("equipment"), "Szivattyú");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("node.aspect_mismatch", await TestApi.ErrorCode(response));
    }

    [Fact]
    public async Task ParentOnOtherSite_IsRejected()
    {
        var t = await LocationTreeAsync();
        var admin = await Api.NewUserAsync(t.Org.OrganizationId, t.Org.OrganizationCode, Roles.OrgAdmin);
        var otherSite = await admin.Http.CreateSiteAsync("Másik");

        var response = await t.Org.Http.PostNodeAsync(otherSite, t.Building, await t.Org.Http.TypeIdAsync("level"), "X",
            new { elevation_m = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("node.parent_other_site", await TestApi.ErrorCode(response));
    }

    [Fact]
    public async Task Attrs_AreValidatedAgainstTypeSchema()
    {
        var t = await LocationTreeAsync();
        var http = t.Org.Http;
        var level = await http.TypeIdAsync("level");

        var missing = await http.PostNodeAsync(t.Site, t.Building, level, "Emelet", new { height_m = 3 });
        var wrongType = await http.PostNodeAsync(t.Site, t.Building, level, "Emelet", new { elevation_m = "magas" });
        var update = await http.PatchJsonAsync($"/api/v1/nodes/{t.Level}", new { attrs = new { elevation_m = 0, height_m = -1 } });

        Assert.Equal("node.invalid_attrs", await TestApi.ErrorCode(missing));
        Assert.Equal("node.invalid_attrs", await TestApi.ErrorCode(update));
        var problem = await TestApi.ReadJson(wrongType);
        Assert.Equal("node.invalid_attrs", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        Assert.Contains(errors.EnumerateArray(), e => e.GetProperty("path").GetString() == "/elevation_m");
    }

    [Fact]
    public async Task NewAttributeField_NeedsOnlyASchemaChange()
    {
        var t = await LocationTreeAsync();
        var admin = await Api.NewUserAsync(t.Org.OrganizationId, t.Org.OrganizationCode, Roles.OrgAdmin);
        var created = await admin.Http.PostAsJsonAsync("/api/v1/node-types", new
        {
            code = "blower", name = "Fúvó", aspect = "function",
            attrSchema = new { type = "object", properties = new { airflow_m3h = new { type = "number" } } },
        });
        var typeId = (await TestApi.ReadJson(created)).GetProperty("id").GetInt64();

        var node = await t.Org.Http.CreateNodeAsync(t.Site, null, typeId, "F1", new { airflow_m3h = 1200 });
        var updated = await admin.Http.PatchJsonAsync($"/api/v1/node-types/{typeId}", new
        {
            attrSchema = new { type = "object", properties = new { airflow_m3h = new { type = "number" }, noise_db = new { type = "number" } } },
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(1200, node.GetProperty("attrs").GetProperty("airflow_m3h").GetInt32());
        var patched = await t.Org.Http.PatchJsonAsync($"/api/v1/nodes/{node.GetProperty("id").GetInt64()}",
            new { attrs = new { airflow_m3h = 1200, noise_db = 78 } });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
    }

    [Fact]
    public async Task NodeTypeSchema_KeepsPropertyOrder()
    {
        var org = await Api.NewOrganizationAsync();
        var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"zz_last_alpha":{"type":"string"},"dn":{"type":"integer"},"a":{"type":"string"}}}""").RootElement;
        var created = await org.Http.PostAsJsonAsync("/api/v1/node-types", new { code = "valve", name = "Szelep", aspect = "function", attrSchema = schema });
        var types = await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/node-types");

        static List<string> Order(JsonElement type) =>
            type.GetProperty("attrSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        // jsonb would reorder the keys (shortest first); the order is the order of form fields and grid columns.
        Assert.Equal(["zz_last_alpha", "dn", "a"], Order(await TestApi.ReadJson(created)));
        Assert.Equal(["manufacturer", "model", "power_kw", "install_year"],
            Order(types.EnumerateArray().Single(t => t.GetProperty("code").GetString() == "equipment")));
    }

    [Fact]
    public async Task Delete_WithChildren_NeedsRecursive()
    {
        var t = await LocationTreeAsync();
        var http = t.Org.Http;

        var refused = await http.DeleteAsync($"/api/v1/nodes/{t.Building}");
        var deleted = await http.DeleteAsync($"/api/v1/nodes/{t.Building}?recursive=true");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("node.has_children", await TestApi.ErrorCode(refused));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal([t.Building2], Ids(await http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{t.Site}/nodes")));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/nodes/{t.Room}")).StatusCode);
    }

    [Fact]
    public async Task Relations_LinkTreesAcrossAspects()
    {
        var t = await LocationTreeAsync();
        var http = t.Org.Http;
        var line = (await http.CreateNodeAsync(t.Site, null, await http.TypeIdAsync("process_line"), "Eleveniszapos vonal"))
            .GetProperty("id").GetInt64();
        var pump = (await http.CreateNodeAsync(t.Site, line, await http.TypeIdAsync("equipment"), "Szivattyú P1",
            new { manufacturer = "Grundfos", power_kw = 7.5 })).GetProperty("id").GetInt64();

        var added = await http.PostAsJsonAsync($"/api/v1/nodes/{pump}/relations", new { toId = t.Room, kind = "located_in" });
        var duplicate = await http.PostAsJsonAsync($"/api/v1/nodes/{pump}/relations", new { toId = t.Room, kind = "located_in" });
        var ofRoom = await http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{t.Room}/relations");
        var removed = await http.DeleteAsync($"/api/v1/nodes/{pump}/relations?toId={t.Room}&kind=located_in");

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var relation = Assert.Single(ofRoom.EnumerateArray());
        Assert.Equal(pump, relation.GetProperty("fromId").GetInt64());
        Assert.Equal("located_in", relation.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/relations")).EnumerateArray());
    }

    [Fact]
    public async Task Viewer_CanReadButNotWrite()
    {
        var t = await LocationTreeAsync();
        var viewer = await Api.NewUserAsync(t.Org.OrganizationId, t.Org.OrganizationCode, Roles.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.Http.GetAsync($"/api/v1/sites/{t.Site}/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PatchJsonAsync($"/api/v1/nodes/{t.Room}", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.DeleteAsync($"/api/v1/nodes/{t.Room}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Org.Http.PostAsJsonAsync("/api/v1/sites", new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Sites_GeoJsonAndBboxFilter()
    {
        var org = await Api.NewOrganizationAsync();
        var budapest = await org.Http.CreateSiteAsync("Budapest");
        var debrecen = await org.Http.PostAsJsonAsync("/api/v1/sites", new
        {
            name = "Debrecen",
            refPoint = new { type = "Point", coordinates = new[] { 21.62, 47.53 } },
            extent = new { type = "Polygon", coordinates = new[] { new[] { new[] { 21.6, 47.5 }, new[] { 21.7, 47.5 }, new[] { 21.7, 47.6 }, new[] { 21.6, 47.5 } } } },
        });
        var debrecenId = (await TestApi.ReadJson(debrecen)).GetProperty("id").GetInt64();

        var west = await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites?bbox=18.5,47,19.5,48");
        var east = await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites?bbox=21.65,47.55,22,48");
        var site = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{budapest}");

        Assert.Equal([budapest], Ids(west));
        Assert.Equal([debrecenId], Ids(east));
        Assert.Equal("Point", site.GetProperty("refPoint").GetProperty("type").GetString());
        Assert.Equal(19.04, site.GetProperty("refPoint").GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(150.0, site.GetProperty("baseHeightM").GetDouble());
    }

    private static List<long> Ids(JsonElement array) => array.EnumerateArray().Select(n => n.GetProperty("id").GetInt64()).ToList();
}
