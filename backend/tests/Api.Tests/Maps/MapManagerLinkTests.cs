using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Maps;

/// <summary>
/// Descriptive data from a MapManager layer as a live reference (spec 6.3): skeleton records, live attributes,
/// layer grid, sync. MapManager is replaced by <see cref="FakeGeoServer"/>.
/// </summary>
public sealed class MapManagerLinkTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private async Task<(OrgClient Org, long Site)> ConnectedAsync()
    {
        var org = await Api.NewOrganizationAsync();
        await ConnectAsync(org.OrganizationId);
        return (org, await org.Http.CreateSiteAsync());
    }

    private async Task ConnectAsync(long organizationId)
    {
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        await sql.ExecuteAsync("UPDATE organization SET mapmanager_url = 'http://mapmanager.test', mapmanager_token_ref = @ref WHERE id = @id",
            ("ref", ApiFactory.TokenRef), ("id", organizationId));
    }

    private static async Task<HttpResponseMessage> LinkAsync(OrgClient org, long site) =>
        await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/link-layer", new
        {
            layer = FakeGeoServer.Layer, typeId = await org.Http.TypeIdAsync("equipment"), nameField = "nev",
        });

    [Fact]
    public async Task LinkLayer_CreatesSkeletonRecords_WithoutCopyingAttributes()
    {
        var (org, site) = await ConnectedAsync();

        var first = await TestApi.ReadJson(await LinkAsync(org, site));
        var second = await TestApi.ReadJson(await LinkAsync(org, site));
        var nodes = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/nodes?aspect=function");

        Assert.Equal(3, first.GetProperty("created").GetInt32());
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(3, second.GetProperty("alreadyLinked").GetInt32());
        var p1 = nodes.EnumerateArray().Single(n => n.GetProperty("name").GetString() == "P1 szivattyú");
        var attrs = p1.GetProperty("attrs");
        Assert.Equal(FakeGeoServer.Layer, attrs.GetProperty("mapmanager_ref").GetProperty("layer").GetString());
        Assert.Equal("szivattyuk.1", attrs.GetProperty("mapmanager_ref").GetProperty("featureId").GetString());
        Assert.False(attrs.TryGetProperty("gyarto", out _));
        // Every request carried the service token; the browser never sees it.
        Assert.All(Factory.GeoServer.Requests, r => Assert.Equal($"Bearer {ApiFactory.Token}", r.Headers.Authorization?.ToString()));
        Assert.All(Factory.GeoServer.Requests, r => Assert.StartsWith("http://mapmanager.test/geoserver/wfs", r.RequestUri!.ToString()));
    }

    [Fact]
    public async Task LinkedLayers_ListsReferencedLayersWithCounts()
    {
        var (org, site) = await ConnectedAsync();
        await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("equipment"), "Saját berendezés");
        await LinkAsync(org, site);

        var layers = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/linked-layers");

        var layer = Assert.Single(layers.EnumerateArray());
        Assert.Equal(FakeGeoServer.Layer, layer.GetProperty("layer").GetString());
        Assert.Equal(3, layer.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task LinkedAttributes_AreReadLive()
    {
        var (org, site) = await ConnectedAsync();
        await LinkAsync(org, site);
        var p1 = (await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/nodes?aspect=function"))
            .EnumerateArray().Single(n => n.GetProperty("name").GetString() == "P1 szivattyú").GetProperty("id").GetInt64();

        Factory.GeoServer.Features["szivattyuk.1"]["gyarto"] = "Wilo"; // changed in MapManager
        var attributes = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{p1}/linked-attributes");

        Assert.Equal("Wilo", attributes.GetProperty("gyarto").GetString());
        Assert.Equal("szivattyuk.1", Factory.GeoServer.Queries.Last()["FEATUREID"]);
    }

    [Fact]
    public async Task LinkedRecord_TechnicalData_IsReadOnly()
    {
        var (org, site) = await ConnectedAsync();
        await LinkAsync(org, site);
        var p1 = (await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/nodes?aspect=function"))[0].GetProperty("id").GetInt64();

        var edit = await org.Http.PatchJsonAsync($"/api/v1/nodes/{p1}", new { attrs = new { manufacturer = "X" } });
        var rename = await org.Http.PatchJsonAsync($"/api/v1/nodes/{p1}", new { name = "Átnevezett" });
        var forged = await org.Http.PostNodeAsync(site, null, await org.Http.TypeIdAsync("equipment"), "Hamis",
            new { mapmanager_ref = new { layer = "masik:reteg", featureId = "x.1" } });

        Assert.Equal("node.linked_read_only", await TestApi.ErrorCode(edit));
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode); // the hierarchy side is SiteTwin3D's own
        Assert.Equal("node.reserved_attr", await TestApi.ErrorCode(forged));
    }

    [Fact]
    public async Task LayerGrid_RowsFromMapManager_WithTheirRecords_AndSafeCql()
    {
        var (org, site) = await ConnectedAsync();
        await LinkAsync(org, site);
        var filter = JsonSerializer.Serialize(new object[]
        {
            new { field = "gyarto", op = "contains", value = "O'Brien%" },
            new { field = "teljesitmeny", op = "gte", value = 5 },
        });

        var page = await org.Http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/sites/{site}/linked-layers/features?layer={FakeGeoServer.Layer}&sort=teljesitmeny&dir=desc&rows=2&filter={Uri.EscapeDataString(filter)}");
        var injected = await org.Http.GetAsync(
            $"/api/v1/sites/{site}/linked-layers/features?layer={FakeGeoServer.Layer}&filter=" +
            Uri.EscapeDataString(JsonSerializer.Serialize(new[] { new { field = "1=1 OR nev", op = "eq", value = "x" } })));

        var request = Factory.GeoServer.Queries.Last(q => q.GetValueOrDefault("REQUEST") == "GetFeature" && q.ContainsKey("SORTBY"));
        Assert.Equal("gyarto ILIKE '%O''Brien\\%%' AND teljesitmeny >= 5", request["CQL_FILTER"]);
        Assert.Equal("teljesitmeny D", request["SORTBY"]);
        Assert.Equal("nev,gyarto,teljesitmeny", request["PROPERTYNAME"]); // never the geometry
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("rows").GetArrayLength());
        Assert.All(page.GetProperty("rows").EnumerateArray(), r => Assert.Equal(JsonValueKind.Number, r.GetProperty("nodeId").ValueKind));
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
    }

    [Fact]
    public async Task Sync_ReportsNewFeaturesAndOrphans()
    {
        var (org, site) = await ConnectedAsync();
        await LinkAsync(org, site);
        Factory.GeoServer.Features.Remove("szivattyuk.2");
        Factory.GeoServer.Features["szivattyuk.4"] = new() { ["nev"] = "P4 szivattyú" };

        var result = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/sync-links", new { layer = FakeGeoServer.Layer }));

        Assert.Equal(3, result.GetProperty("features").GetInt32());
        Assert.Equal(2, result.GetProperty("linked").GetInt32());
        Assert.Equal(1, result.GetProperty("unlinked").GetInt32());
        var orphan = Assert.Single(result.GetProperty("orphans").EnumerateArray());
        Assert.Equal("szivattyuk.2", orphan.GetProperty("featureId").GetString());
        Assert.Equal("P2 szivattyú", orphan.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UnlinkedLayer_CannotBeRead()
    {
        var (org, site) = await ConnectedAsync();

        var response = await org.Http.GetAsync($"/api/v1/sites/{site}/linked-layers/features?layer=ceg:titkos");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("maps.layer_not_linked", await TestApi.ErrorCode(response));
        Assert.Empty(Factory.GeoServer.Requests);
    }

    [Fact]
    public async Task WithoutConnection_IsServiceUnavailable()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();

        var response = await LinkAsync(org, site);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("maps.mapmanager_not_configured", await TestApi.ErrorCode(response));
    }

    [Fact]
    public async Task OtherOrganization_CannotReadLinkedData()
    {
        var (org, site) = await ConnectedAsync();
        await LinkAsync(org, site);
        var p1 = (await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/nodes?aspect=function"))[0].GetProperty("id").GetInt64();
        var other = await Api.NewOrganizationAsync();
        await ConnectAsync(other.OrganizationId);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/nodes/{p1}/linked-attributes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Http.GetAsync($"/api/v1/sites/{site}/linked-layers/features?layer={FakeGeoServer.Layer}")).StatusCode);
    }
}
