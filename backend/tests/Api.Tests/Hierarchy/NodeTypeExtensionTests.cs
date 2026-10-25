using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;

namespace Api.Tests.Hierarchy;

/// <summary>
/// An organization's own fields on a shared base type (user decision): the organization admin adds them, its records
/// of that type are checked, listed, filtered and imported with them; other organizations do not see them.
/// </summary>
public sealed class NodeTypeExtensionTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private static object RoomFields(bool withFinish = true) => new
    {
        type = "object",
        properties = withFinish
            ? (object)new
            {
                floor_area_m2 = new { type = "number", title = "Alapterület (m²)", minimum = 0 },
                finish = new { type = "string", title = "Burkolat", @enum = new[] { "beton", "csempe", "epoxi" } },
            }
            : new { floor_area_m2 = new { type = "number", title = "Alapterület (m²)", minimum = 0 } },
        required = new[] { "floor_area_m2" },
    };

    private static Task<HttpResponseMessage> SetFieldsAsync(OrgClient org, long typeId, object? schema) =>
        org.Http.PutAsJsonAsync($"/api/v1/node-types/{typeId}/extension", new { attrSchema = schema });

    private static async Task<JsonElement> TypeAsync(OrgClient org, string code) =>
        (await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/node-types"))!.EnumerateArray().Single(t => t.GetProperty("code").GetString() == code);

    private static List<string> Fields(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object ? schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList() : [];

    [Fact]
    public async Task FieldsOnABaseType_ApplyToTheOrganizationsRecords()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var room = await org.Http.TypeIdAsync("room");

        var set = await SetFieldsAsync(org, room, RoomFields());
        var type = await TypeAsync(org, "room");
        var valid = await org.Http.PostNodeAsync(site, null, room, "Gépterem", new { floor_area_m2 = 42.5, finish = "epoxi" });
        var missing = await org.Http.PostNodeAsync(site, null, room, "Raktár", new { finish = "beton" });
        var wrong = await org.Http.PostNodeAsync(site, null, room, "Iroda", new { floor_area_m2 = 12, finish = "parketta" });
        var filter = "&filter=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { new { field = "attrs.floor_area_m2", op = "gte", value = 40 } }));
        var grid = await org.Http.GetAsync($"/api/v1/nodes?siteId={site}&typeId={room}{filter}");
        var imported = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/import/attributes", new
        {
            typeId = room,
            rows = new[] { new { code = "R1", name = "Kapcsolótér", attrs = new Dictionary<string, object> { ["floor_area_m2"] = "18,5" } } },
        });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.True(type.GetProperty("shared").GetBoolean());
        Assert.Equal(JsonValueKind.Null, type.GetProperty("baseSchema").ValueKind);
        Assert.Equal(["floor_area_m2", "finish"], Fields(type.GetProperty("extensionSchema")));
        Assert.Equal(["floor_area_m2", "finish"], Fields(type.GetProperty("attrSchema")));
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        Assert.Equal("node.invalid_attrs", await TestApi.ErrorCode(missing));
        Assert.Equal("node.invalid_attrs", await TestApi.ErrorCode(wrong));
        Assert.True(grid.StatusCode == HttpStatusCode.OK, await grid.Content.ReadAsStringAsync());
        Assert.Equal(1, (await TestApi.ReadJson(grid)).GetProperty("total").GetInt32());
        Assert.True(imported.StatusCode == HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BaseFieldsComeFirst_NamesMustNotClash_OwnTypesAndEditorsRefused()
    {
        var org = await Api.NewOrganizationAsync();
        var equipment = await org.Http.TypeIdAsync("equipment");
        var editor = await Api.NewUserAsync(org.OrganizationId, org.OrganizationCode, Roles.Editor);
        var own = await TestApi.ReadJson(await org.Http.PostAsJsonAsync("/api/v1/node-types",
            new { code = "valve", name = "Szelep", aspect = "function" }));

        var added = await SetFieldsAsync(org, equipment, new { type = "object", properties = new { serial = new { type = "string" } } });
        var clash = await SetFieldsAsync(org, equipment, new { type = "object", properties = new { manufacturer = new { type = "string" } } });
        var onOwnType = await SetFieldsAsync(org, own.GetProperty("id").GetInt64(), new { type = "object", properties = new { dn = new { type = "integer" } } });
        var byEditor = await SetFieldsAsync(editor, equipment, new { type = "object", properties = new { serial = new { type = "string" } } });

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(["manufacturer", "model", "power_kw", "install_year", "serial"], Fields((await TypeAsync(org, "equipment")).GetProperty("attrSchema")));
        Assert.Equal("node_type.field_exists", await TestApi.ErrorCode(clash));
        Assert.Equal("node_type.own_type", await TestApi.ErrorCode(onOwnType));
        Assert.Equal(HttpStatusCode.Forbidden, byEditor.StatusCode);
    }

    [Fact]
    public async Task OtherOrganizations_DoNotSeeTheFields_AndRemovingKeepsTheValues()
    {
        var org = await Api.NewOrganizationAsync();
        var other = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var room = await org.Http.TypeIdAsync("room");
        await SetFieldsAsync(org, room, RoomFields(withFinish: false));
        var node = await org.Http.CreateNodeAsync(site, null, room, "Gépterem", new { floor_area_m2 = 30 });

        var othersRoom = await TypeAsync(other, "room");
        var removed = await SetFieldsAsync(org, room, null);
        var afterRemoval = await TypeAsync(org, "room");
        var stored = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{node.GetProperty("id").GetInt64()}");

        Assert.Equal(JsonValueKind.Null, othersRoom.GetProperty("attrSchema").ValueKind);
        Assert.Equal(JsonValueKind.Null, othersRoom.GetProperty("extensionSchema").ValueKind);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(JsonValueKind.Null, afterRemoval.GetProperty("attrSchema").ValueKind);
        Assert.Equal(30, stored.GetProperty("attrs").GetProperty("floor_area_m2").GetInt32());
    }
}
