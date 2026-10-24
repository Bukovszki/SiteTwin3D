using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Services.Models;
using Api.Services.Storage;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Models;

/// <summary>
/// Spec 9.2: IFC, mesh formats and point clouds are uploaded as they are and converted by the converter service in the
/// background; the model shows when ready. An IFC becomes segmented (GlobalId) and places itself when georeferenced.
/// </summary>
public sealed class ModelConversionTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const double Lon = 19.08, Lat = 47.43;
    private readonly HttpClient _s3 = new();

    private async Task<(OrgClient Org, long Site)> SiteAsync()
    {
        var org = await Api.NewOrganizationAsync();
        return (org, await org.Http.CreateSiteAsync()); // base height 150 m
    }

    private async Task<HttpResponseMessage> TicketAsync(OrgClient org, long site, params string[] paths) =>
        await org.Http.PostAsJsonAsync("/api/v1/uploads/model", new { siteId = site, files = paths.Select(p => new { path = p, size = 10 }) });

    private async Task<string> UploadAsync(OrgClient org, long site, params string[] paths)
    {
        var ticket = await TestApi.ReadJson(await TicketAsync(org, site, paths));
        foreach (var file in ticket.GetProperty("files").EnumerateArray())
        {
            var put = await _s3.SendAsync(new HttpRequestMessage(HttpMethod.Put, file.GetProperty("url").GetString())
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("sample")) { Headers = { ContentType = new MediaTypeHeaderValue(file.GetProperty("contentType").GetString()!) } },
            });
            Assert.True(put.IsSuccessStatusCode);
        }
        return ticket.GetProperty("prefix").GetString()!;
    }

    private async Task<bool> ConvertAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModelConversionService>().ProcessNextAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ifc_IsConverted_Segmented_AndPlacesItselfFromItsGeoreference()
    {
        var (org, site) = await SiteAsync();
        var prefix = await UploadAsync(org, site, "gephaz.ifc");
        Factory.Converter.Answer = job => new JsonObject
        {
            ["kind"] = "gltf", ["key"] = job.GetProperty("outputPrefix").GetString() + "model.glb", ["segmented"] = true,
            ["elementIdProperty"] = "element_id", ["elements"] = 42,
            ["placement"] = new JsonObject { ["lon"] = 19.05, ["lat"] = 47.5, ["absoluteHeightM"] = 152.5, ["headingDeg"] = 330, ["crs"] = "EPSG:23700" },
            ["warnings"] = new JsonArray(),
        };

        var created = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "Gépház BIM" }));
        var id = created.GetProperty("id").GetInt64();
        var notReady = await org.Http.GetAsync($"/api/v1/models/{id}/tileset.json");
        Assert.True(await ConvertAsync());
        var model = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{id}");
        var job = Assert.Single(Factory.Converter.Jobs);

        Assert.Equal("converting", created.GetProperty("status").GetString());
        Assert.Equal("ifc", created.GetProperty("sourceFormat").GetString());
        Assert.Equal("model.not_ready", await TestApi.ErrorCode(notReady));
        Assert.Equal("ifc", job.GetProperty("format").GetString());
        Assert.Equal(prefix + "gephaz.ifc", job.GetProperty("main").GetString());
        Assert.Equal(prefix + "converted/", job.GetProperty("outputPrefix").GetString());
        Assert.Equal("ready", model.GetProperty("status").GetString());
        Assert.True(model.GetProperty("segmented").GetBoolean());
        Assert.Equal("element_id", model.GetProperty("elementIdProperty").GetString());
        Assert.Equal(2.5, model.GetProperty("placement").GetProperty("heightM").GetDouble(), 3); // 152.5 − 150 base
        Assert.Equal(330, model.GetProperty("placement").GetProperty("headingDeg").GetDouble());
        Assert.Equal(42, model.GetProperty("conversion").GetProperty("elements").GetInt32());
        Assert.False(await ConvertAsync()); // nothing left
    }

    [Fact]
    public async Task Mesh_WithMaterialAndTexture_NeedsAPlacement_FailureIsReported()
    {
        var (org, site) = await SiteAsync();
        var prefix = await UploadAsync(org, site, "szivattyu.obj", "szivattyu.mtl", "textures/festek.png");
        var noPlacement = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "Szivattyú" });
        var created = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models",
            new { prefix, name = "Szivattyú", placement = new { lon = Lon, lat = Lat, heightM = 0, headingDeg = 0 } });
        var id = (await TestApi.ReadJson(created)).GetProperty("id").GetInt64();
        Factory.Converter.Answer = _ => null; // 422
        await ConvertAsync();
        var model = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{id}");
        var job = Assert.Single(Factory.Converter.Jobs);

        Assert.Equal("model.placement_required", await TestApi.ErrorCode(noPlacement));
        Assert.Equal("mesh", job.GetProperty("format").GetString());
        Assert.Equal(3, job.GetProperty("keys").GetArrayLength()); // the MTL and the texture go with it
        Assert.Equal("failed", model.GetProperty("status").GetString());
        Assert.Equal("The model could not be read.", model.GetProperty("processingError").GetString());
    }

    [Fact]
    public async Task PointCloud_WithItsCoordinateSystem_IsGeoreferenced_WithoutIt_PlacedAtTheSite()
    {
        var (org, site) = await SiteAsync();
        var store = Factory.Services.GetRequiredService<IObjectStorage>();
        Factory.Converter.Answer = job =>
        {
            var georeferenced = job.GetProperty("srs").ValueKind == JsonValueKind.String;
            var key = job.GetProperty("outputPrefix").GetString() + "tileset.json";
            var tileset = """{"asset":{"version":"1.0"},"geometricError":10,"root":{"geometricError":0,"boundingVolume":{"box":[0,0,0,5,0,0,0,5,0,0,0,5]},"content":{"uri":"tiles/0.pnts"}}}""";
            store.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes(tileset)), "application/json", CancellationToken.None).GetAwaiter().GetResult();
            return new JsonObject { ["kind"] = "3dtiles", ["key"] = key, ["segmented"] = false, ["points"] = 1000, ["georeferenced"] = georeferenced };
        };
        var eov = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models",
            new { prefix = await UploadAsync(org, site, "felmeres.laz"), name = "Felmérés EOV", srs = "epsg:23700" }));
        var local = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models",
            new { prefix = await UploadAsync(org, site, "belso.e57"), name = "Belső szken" }));
        var badSrs = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models",
            new { prefix = await UploadAsync(org, site, "x.las"), name = "X", srs = "EOV" });
        await ConvertAsync();
        await ConvertAsync();

        var eovModel = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{eov.GetProperty("id").GetInt64()}");
        var localModel = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{local.GetProperty("id").GetInt64()}");
        var localTileset = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{local.GetProperty("id").GetInt64()}/tileset.json");

        Assert.Equal("EPSG:23700", Factory.Converter.Jobs[0].GetProperty("srs").GetString());
        Assert.Equal("model.invalid_srs", await TestApi.ErrorCode(badSrs));
        Assert.Equal("ready", eovModel.GetProperty("status").GetString());
        Assert.Equal("3dtiles", eovModel.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, eovModel.GetProperty("placement").ValueKind);
        var point = (await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/sites")).EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == site)
            .GetProperty("refPoint").GetProperty("coordinates");
        var (siteLon, siteLat) = (point[0].GetDouble(), point[1].GetDouble());
        Assert.Equal(siteLon, localModel.GetProperty("placement").GetProperty("lon").GetDouble(), 6);
        Assert.Single(localModel.GetProperty("conversion").GetProperty("warnings").EnumerateArray());
        // Local coordinates: the served tileset carries the placement frame (ENU at the site point, 150 m base).
        var origin = Geodesy.ToEcef(siteLon, siteLat, 150);
        Assert.Equal(origin.X, localTileset.GetProperty("root").GetProperty("transform")[12].GetDouble(), 2);
    }

    [Fact]
    public async Task Uploads_OfUnknownCombinations_AreRefused_ConverterDown_FailsAfterRetries()
    {
        var (org, site) = await SiteAsync();
        Assert.Equal("model.unknown_format", await TestApi.ErrorCode(await TicketAsync(org, site, "a.ifc", "b.ifc")));
        Assert.Equal("model.unknown_format", await TestApi.ErrorCode(await TicketAsync(org, site, "a.obj", "run.exe")));
        Assert.Equal("model.unknown_format", await TestApi.ErrorCode(await TicketAsync(org, site, "scan.las", "scan.mtl")));
        Assert.Equal(HttpStatusCode.OK, (await TicketAsync(org, site, "model.gltf", "model.bin", "tex.jpg")).StatusCode);

        var id = (await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models",
            new { prefix = await UploadAsync(org, site, "gephaz.ifc"), name = "B" }))).GetProperty("id").GetInt64();
        Factory.Converter.Down = true;
        for (var i = 0; i < 5; i++) await ConvertAsync();
        var model = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{id}");
        Assert.Equal("failed", model.GetProperty("status").GetString());
    }
}
