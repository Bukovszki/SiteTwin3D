using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Infrastructure;

namespace Api.Tests.Hierarchy;

/// <summary>
/// Where a type may be (user decision): building and outdoor area at the top of the location tree, a level under a
/// building, a room under a level; a process line at the top or under a line; equipment anywhere. Own types: set by
/// the organization admin, anywhere by default.
/// </summary>
public sealed class ParentRuleTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private static async Task<long> IdAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await TestApi.ReadJson(response)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task BaseTypes_OnlyWhereTheyBelong()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var (building, level, room, outdoor, line, equipment) = (await org.Http.TypeIdAsync("building"), await org.Http.TypeIdAsync("level"),
            await org.Http.TypeIdAsync("room"), await org.Http.TypeIdAsync("outdoor_area"), await org.Http.TypeIdAsync("process_line"),
            await org.Http.TypeIdAsync("equipment"));

        var b = await IdAsync(await org.Http.PostNodeAsync(site, null, building, "Épület"));
        var l = await IdAsync(await org.Http.PostNodeAsync(site, b, level, "Földszint", new { elevation_m = 0 }));
        var r = await IdAsync(await org.Http.PostNodeAsync(site, l, room, "Gépterem"));
        var top = await IdAsync(await org.Http.PostNodeAsync(site, null, line, "Vonal"));
        var sub = await IdAsync(await org.Http.PostNodeAsync(site, top, line, "Alvonal"));
        var e = await IdAsync(await org.Http.PostNodeAsync(site, top, equipment, "Szivattyú"));
        await IdAsync(await org.Http.PostNodeAsync(site, null, equipment, "Szivattyú 2"));

        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(await org.Http.PostNodeAsync(site, null, room, "Gyökérhelyiség")));
        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(await org.Http.PostNodeAsync(site, b, room, "Épülethelyiség")));
        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(await org.Http.PostNodeAsync(site, r, outdoor, "Udvar")));
        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(await org.Http.PostNodeAsync(site, null, level, "Szint", new { elevation_m = 0 })));
        // A move is checked the same way: a room to the top, a building under a room.
        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(await org.Http.PatchAsJsonAsync($"/api/v1/nodes/{r}", new { parentId = (long?)null })));
        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(await org.Http.PatchAsJsonAsync($"/api/v1/nodes/{b}", new { parentId = r })));
        Assert.Equal(HttpStatusCode.OK, (await org.Http.PatchAsJsonAsync($"/api/v1/nodes/{e}", new { parentId = sub })).StatusCode);
        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(await org.Http.PatchAsJsonAsync($"/api/v1/nodes/{sub}", new { parentId = e })));
    }

    [Fact]
    public async Task OwnTypes_AnywhereByDefault_RulesSetByTheAdmin()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var created = await TestApi.ReadJson(await org.Http.PostAsJsonAsync("/api/v1/node-types", new { code = "cabinet", name = "Szekrény", aspect = "location" }));
        var cabinet = created.GetProperty("id").GetInt64();
        var room = await org.Http.TypeIdAsync("room");

        var anywhere = await org.Http.PostNodeAsync(site, null, cabinet, "Szekrény 1");
        var set = await org.Http.PatchAsJsonAsync($"/api/v1/node-types/{cabinet}", new { allowedParents = new[] { "room" } });
        var unknown = await org.Http.PatchAsJsonAsync($"/api/v1/node-types/{cabinet}", new { allowedParents = new[] { "process_line" } });
        var atTop = await org.Http.PostNodeAsync(site, null, cabinet, "Szekrény 2");
        var types = await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/node-types");

        Assert.Equal(JsonValueKind.Null, created.GetProperty("allowedParents").ValueKind);
        Assert.Equal(HttpStatusCode.Created, anywhere.StatusCode);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("node_type.invalid_parents", await TestApi.ErrorCode(unknown));
        Assert.Equal("node.parent_type_not_allowed", await TestApi.ErrorCode(atTop));
        Assert.Equal(["room"], types.EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == cabinet)
            .GetProperty("allowedParents").EnumerateArray().Select(p => p.GetString()).ToList());
        Assert.Equal(["level"], types.EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == room)
            .GetProperty("allowedParents").EnumerateArray().Select(p => p.GetString()).ToList());
    }
}
