using System.Net;
using System.Net.Http.Json;
using Api.Services.Maps;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.Maps;

/// <summary>WMS proxy (spec 6.3): only configured layers, allowed requests and parameters; token only server-side.</summary>
public sealed class WmsProxyTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const string GetMap = "SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&CRS=EPSG:3857&BBOX=2100000,6000000,2200000,6100000" +
        "&WIDTH=256&HEIGHT=256&FORMAT=image/png&TRANSPARENT=true";

    private static async Task<long> CreateLayerAsync(OrgClient org, object body)
    {
        var response = await org.Http.PostAsJsonAsync("/api/v1/map-layers", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await TestApi.ReadJson(response)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ExternalWms_ForcesLayer_AndDropsDangerousParameters()
    {
        var org = await Api.NewOrganizationAsync();
        var id = await CreateLayerAsync(org, new
        {
            source = "external_wms", name = "Ortofotó", layer = "ortofoto", externalUrl = "https://wms.example.hu/service",
            wmsParams = new { STYLES = "default" },
        });

        var response = await org.Http.GetAsync($"/api/v1/maps/wms?mapLayerId={id}&{GetMap}&LAYERS=secret&SLD_BODY=%3Cx/%3E&STYLES=evil");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeGeoServer.Png, await response.Content.ReadAsByteArrayAsync());
        var sent = Factory.GeoServer.Requests.Single();
        var query = Factory.GeoServer.Queries.Single();
        Assert.StartsWith("https://wms.example.hu/service?", sent.RequestUri!.ToString());
        Assert.Equal("ortofoto", query["LAYERS"]);
        Assert.Equal("default", query["STYLES"]);
        Assert.False(query.ContainsKey("SLD_BODY"));
        Assert.False(query.ContainsKey("MAPLAYERID"));
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task ExternalWms_ErrorMessageInsteadOfAMap_IsReportedWithItsText()
    {
        var org = await Api.NewOrganizationAsync();
        var id = await CreateLayerAsync(org, new { source = "external_wms", name = "EOV", layer = "osm", externalUrl = "https://wms.example.hu/geoserver/wms" });
        Factory.GeoServer.WmsAnswer = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\"?><ServiceExceptionReport version=\"1.3.0\"><ServiceException code=\"InvalidCRS\">\n" +
                "  Error occurred decoding the espg code EPSG:3857</ServiceException></ServiceExceptionReport>",
                System.Text.Encoding.UTF8, "application/vnd.ogc.se_xml"),
        };

        var response = await org.Http.GetAsync($"/api/v1/maps/wms?mapLayerId={id}&{GetMap}");
        var problem = await TestApi.ReadJson(response);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("maps.wms_error", problem.GetProperty("code").GetString());
        Assert.Contains("Error occurred decoding the espg code EPSG:3857", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MapManagerLayer_GoesWithServiceToken()
    {
        var org = await Api.NewOrganizationAsync();
        await using (var sql = await SqlTestData.OpenAsync(Postgis))
            await sql.ExecuteAsync("UPDATE organization SET mapmanager_url = 'http://mapmanager.test/', mapmanager_token_ref = @ref WHERE id = @id",
                ("ref", ApiFactory.TokenRef), ("id", org.OrganizationId));
        var id = await CreateLayerAsync(org, new { source = "mapmanager", name = "Közmű", layer = "ceg:kozmu" });

        var response = await org.Http.GetAsync($"/api/v1/maps/wms?mapLayerId={id}&{GetMap}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Factory.GeoServer.Requests.Single();
        Assert.StartsWith("http://mapmanager.test/geoserver/wms?", sent.RequestUri!.ToString());
        Assert.Equal($"Bearer {ApiFactory.Token}", sent.Headers.Authorization?.ToString());
        Assert.Equal("ceg:kozmu", Factory.GeoServer.Queries.Single()["LAYERS"]);
    }

    [Fact]
    public async Task OtherRequests_AreRefused()
    {
        var org = await Api.NewOrganizationAsync();
        var id = await CreateLayerAsync(org, new { source = "external_wms", name = "X", layer = "x", externalUrl = "https://wms.example.hu/" });

        var capabilities = await org.Http.GetAsync($"/api/v1/maps/wms?mapLayerId={id}&SERVICE=WMS&REQUEST=GetCapabilities");

        Assert.Equal(HttpStatusCode.BadRequest, capabilities.StatusCode);
        Assert.Equal("maps.wms_request_not_allowed", await TestApi.ErrorCode(capabilities));
        Assert.Empty(Factory.GeoServer.Requests);
    }

    [Fact]
    public async Task OtherOrganizationsLayer_IsNotFound()
    {
        var org = await Api.NewOrganizationAsync();
        var other = await Api.NewOrganizationAsync();
        var id = await CreateLayerAsync(org, new { source = "external_wms", name = "X", layer = "x", externalUrl = "https://wms.example.hu/" });

        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/maps/wms?mapLayerId={id}&{GetMap}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.DeleteAsync($"/api/v1/map-layers/{id}")).StatusCode);
        Assert.Empty(Factory.GeoServer.Requests);
    }

    [Fact]
    public async Task InvalidExternalUrl_IsRejected()
    {
        var org = await Api.NewOrganizationAsync();

        var file = await org.Http.PostAsJsonAsync("/api/v1/map-layers", new { source = "external_wms", name = "X", layer = "x", externalUrl = "file:///etc/passwd" });
        var credentials = await org.Http.PostAsJsonAsync("/api/v1/map-layers", new { source = "external_wms", name = "X", layer = "x", externalUrl = "https://u:p@wms.example.hu/" });

        Assert.Equal("map_layer.invalid_url", await TestApi.ErrorCode(file));
        Assert.Equal("map_layer.invalid_url", await TestApi.ErrorCode(credentials));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("84.2.44.1", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void ExternalWms_MayOnlyConnectToPublicAddresses(string address, bool allowed) =>
        Assert.Equal(allowed, OutboundHttp.IsPublic(IPAddress.Parse(address)));
}
