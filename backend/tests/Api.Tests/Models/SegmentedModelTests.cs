using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Services.Models;
using Api.Tests.Infrastructure;

namespace Api.Tests.Models;

/// <summary>
/// M7: segmented (BIM) models – the anchor stores the STABLE element id (spec 9.1); import check reports missing and
/// duplicated ids; one element belongs to one record.
/// </summary>
public sealed class SegmentedModelTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const double Lon = 19.08, Lat = 47.43;
    private readonly HttpClient _s3 = new();

    private async Task<(OrgClient Org, long Site, long Model, long Pump, long Valve)> SetupAsync(byte[] glb, string? elementIdProperty = "element_id")
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var ticket = await TestApi.ReadJson(await org.Http.PostAsJsonAsync("/api/v1/uploads/model", new { siteId = site, files = new[] { new { path = "bim.glb", size = glb.Length } } }));
        var file = ticket.GetProperty("files")[0];
        var put = await _s3.SendAsync(new HttpRequestMessage(HttpMethod.Put, file.GetProperty("url").GetString())
        {
            Content = new ByteArrayContent(glb) { Headers = { ContentType = new MediaTypeHeaderValue("model/gltf-binary") } },
        });
        Assert.True(put.IsSuccessStatusCode);
        var model = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new
        {
            prefix = ticket.GetProperty("prefix").GetString(), name = "BIM", elementIdProperty,
            placement = new { lon = Lon, lat = Lat, heightM = 0, headingDeg = 0 },
        }));
        var type = await org.Http.TypeIdAsync("equipment");
        var pump = (await org.Http.CreateNodeAsync(site, null, type, "P1")).GetProperty("id").GetInt64();
        var valve = (await org.Http.CreateNodeAsync(site, null, type, "V1")).GetProperty("id").GetInt64();
        return (org, site, model.GetProperty("id").GetInt64(), pump, valve);
    }

    private static object Element(long model, string featureRef) => new
    {
        kind = "model_feature", modelAssetId = model, featureRef, position = new { type = "Point", coordinates = new[] { Lon, Lat } }, zRelM = 0.5,
    };

    [Fact]
    public void GlbMetadata_ReadsTheStringPropertyPerTable()
    {
        var tables = GlbMetadata.ReadProperty(TestGlb.WithElementIds("element_id", "2O2Fr$t4X7Zf8NOew3FLOH", "ÁRVÍZ-1", null), "element_id");

        Assert.Equal(["2O2Fr$t4X7Zf8NOew3FLOH", "ÁRVÍZ-1", null], Assert.Single(tables!));
        Assert.Null(GlbMetadata.ReadProperty(TestGlb.WithElementIds("element_id", "A"), "GlobalId"));
    }

    [Fact]
    public async Task ElementCheck_ReportsMissingAndDuplicatedIds()
    {
        var s = await SetupAsync(TestGlb.WithElementIds("element_id", "P01", "V01", "V01", null, "W01"));

        var check = await TestApi.ReadJson(await s.Org.Http.PostAsync($"/api/v1/models/{s.Model}/element-check", null));
        var model = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{s.Model}");

        Assert.True(model.GetProperty("segmented").GetBoolean());
        Assert.Equal(1, check.GetProperty("contentsChecked").GetInt32());
        Assert.Equal(5, check.GetProperty("elements").GetInt32());
        Assert.Equal(3, check.GetProperty("distinctIds").GetInt32());
        Assert.Equal(1, check.GetProperty("missing").GetInt32());
        Assert.Equal(["V01"], check.GetProperty("duplicates").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task ModelFeature_StoresTheStableId_OneRecordPerElement()
    {
        var s = await SetupAsync(TestGlb.WithElementIds("element_id", "P01", "V01"));

        var created = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", Element(s.Model, "P01"));
        var again = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", Element(s.Model, "P01"));
        var taken = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Valve}/anchors", Element(s.Model, "P01"));
        var noRef = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Valve}/anchors", Element(s.Model, " "));
        var list = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{s.Site}/anchors?allLevels=true&kind=model_feature");
        var other = await Api.NewOrganizationAsync();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var anchor = Assert.Single(list.EnumerateArray());
        Assert.Equal("P01", anchor.GetProperty("featureRef").GetString());
        Assert.Equal(s.Model, anchor.GetProperty("modelAssetId").GetInt64());
        Assert.Equal("anchor.feature_taken", await TestApi.ErrorCode(again));
        Assert.Equal("anchor.feature_taken", await TestApi.ErrorCode(taken));
        Assert.Equal("anchor.feature_fields", await TestApi.ErrorCode(noRef));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.PostAsync($"/api/v1/models/{s.Model}/element-check", null)).StatusCode);

        // Deleting the model removes its element anchors (they point into it), the record stays.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Org.Http.DeleteAsync($"/api/v1/models/{s.Model}")).StatusCode);
        Assert.Empty((await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{s.Pump}/anchors")).EnumerateArray());
    }

    [Fact]
    public async Task NotSegmented_RefusesElements_UntilThePropertyIsSet()
    {
        var s = await SetupAsync(TestGlb.WithElementIds("GlobalId", "A"), elementIdProperty: null);

        var refused = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", Element(s.Model, "A"));
        var noCheck = await s.Org.Http.PostAsync($"/api/v1/models/{s.Model}/element-check", null);
        var patched = await TestApi.ReadJson(await s.Org.Http.PatchJsonAsync($"/api/v1/models/{s.Model}", new { elementIdProperty = "GlobalId" }));
        var check = await TestApi.ReadJson(await s.Org.Http.PostAsync($"/api/v1/models/{s.Model}/element-check", null));
        var accepted = await s.Org.Http.PostAsJsonAsync($"/api/v1/nodes/{s.Pump}/anchors", Element(s.Model, "A"));

        Assert.Equal("model.not_segmented", await TestApi.ErrorCode(refused));
        Assert.Equal("model.not_segmented", await TestApi.ErrorCode(noCheck));
        Assert.True(patched.GetProperty("segmented").GetBoolean());
        Assert.Equal(1, check.GetProperty("distinctIds").GetInt32());
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }
}
