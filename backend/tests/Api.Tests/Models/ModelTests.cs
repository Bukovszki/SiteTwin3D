using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Api.Model;
using Api.Services.Models;
using Api.Tests.Infrastructure;

namespace Api.Tests.Models;

/// <summary>
/// 3D models with the real object store (spec 9, FR-I1, NFR-3): files go up with signed URLs, the API serves only the
/// tileset.json with signed tile URLs; a GLB becomes a one-tile tileset carrying its placement.
/// </summary>
public sealed class ModelTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const double Lon = 19.08, Lat = 47.43;
    private readonly HttpClient _s3 = new();

    private static readonly byte[] Glb = Encoding.ASCII.GetBytes("glTF-sample-bytes");

    private async Task<(OrgClient Org, long Site)> SiteAsync()
    {
        var org = await Api.NewOrganizationAsync();
        return (org, await org.Http.CreateSiteAsync()); // base height 150 m
    }

    /// <summary>Like the browser: ticket for every file, PUT each to its signed URL; returns the prefix.</summary>
    private async Task<string> UploadAsync(OrgClient org, long site, Dictionary<string, byte[]> files)
    {
        var ticket = await TestApi.ReadJson(await org.Http.PostAsJsonAsync("/api/v1/uploads/model",
            new { siteId = site, files = files.Select(f => new { path = f.Key, size = f.Value.Length }) }));
        foreach (var file in ticket.GetProperty("files").EnumerateArray())
        {
            var put = new HttpRequestMessage(HttpMethod.Put, file.GetProperty("url").GetString())
            {
                Content = new ByteArrayContent(files[file.GetProperty("path").GetString()!])
                {
                    Headers = { ContentType = new MediaTypeHeaderValue(file.GetProperty("contentType").GetString()!) },
                },
            };
            var uploaded = await _s3.SendAsync(put);
            Assert.True(uploaded.IsSuccessStatusCode, $"signed PUT failed: {(int)uploaded.StatusCode}");
        }
        return ticket.GetProperty("prefix").GetString()!;
    }

    private static object Placement(double heading = 90) => new { lon = Lon, lat = Lat, heightM = 2.5, headingDeg = heading };

    [Fact]
    public async Task Glb_IsServedAsOneTileTileset_WithPlacementAndSignedContent()
    {
        var (org, site) = await SiteAsync();
        var prefix = await UploadAsync(org, site, new() { ["epulet.glb"] = Glb });

        var created = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "Gépház", placement = Placement() });
        var model = await TestApi.ReadJson(created);
        var list = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{site}/models");
        var tilesetResponse = await org.Http.GetAsync(model.GetProperty("tilesetUrl").GetString());
        var tileset = await TestApi.ReadJson(tilesetResponse);
        var root = tileset.GetProperty("root");
        var content = await _s3.GetByteArrayAsync(root.GetProperty("content").GetProperty("uri").GetString());

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("gltf", model.GetProperty("kind").GetString());
        Assert.Equal(90, model.GetProperty("placement").GetProperty("headingDeg").GetDouble());
        Assert.Single(list.EnumerateArray());
        Assert.Contains("no-store", tilesetResponse.Headers.CacheControl?.ToString());
        Assert.Equal(Glb, content);
        // Root transform: ENU at the point, 150 m base + 2.5 m, turned 90° – local +y (forward) points east.
        var t = root.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var origin = Geodesy.ToEcef(Lon, Lat, 152.5);
        Assert.Equal(origin.X, t[12], 3);
        Assert.Equal(origin.Z, t[14], 3);
        var east = new[] { -Math.Sin(Lon * Math.PI / 180), Math.Cos(Lon * Math.PI / 180), 0 };
        Assert.Equal(east[0], t[4], 6);
        Assert.Equal(east[1], t[5], 6);
    }

    [Fact]
    public async Task TilesPackage_NestedTilesetsComeThroughTheApi_ContentsAreSigned()
    {
        var (org, site) = await SiteAsync();
        var rootJson = """
            {"asset":{"version":"1.1"},"geometricError":100,"root":{"geometricError":50,"refine":"REPLACE",
             "boundingVolume":{"sphere":[0,0,0,100]},"content":{"uri":"tiles/0.glb"},
             "children":[{"geometricError":0,"boundingVolume":{"sphere":[0,0,0,50]},"content":{"uri":"sub/tileset.json"}}]}}
            """;
        var subJson = """
            {"asset":{"version":"1.1"},"geometricError":10,"root":{"geometricError":0,"boundingVolume":{"sphere":[0,0,0,50]},
             "contents":[{"uri":"../tiles/a%20b.glb"},{"uri":"https://example.org/x.glb"}]}}
            """;
        var prefix = await UploadAsync(org, site, new()
        {
            ["tileset.json"] = Encoding.UTF8.GetBytes(rootJson),
            ["sub/tileset.json"] = Encoding.UTF8.GetBytes(subJson),
            ["tiles/0.glb"] = Glb,
            ["tiles/a b.glb"] = [1, 2, 3],
        });
        var model = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "Felmérés 2026" }));

        var root = await org.Http.GetFromJsonAsync<JsonElement>(model.GetProperty("tilesetUrl").GetString());
        var nestedUrl = root.GetProperty("root").GetProperty("children")[0].GetProperty("content").GetProperty("uri").GetString()!;
        var nested = await org.Http.GetFromJsonAsync<JsonElement>(nestedUrl);
        var contents = nested.GetProperty("root").GetProperty("contents");

        Assert.Equal("3dtiles", model.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, model.GetProperty("placement").ValueKind);
        Assert.Equal(Glb, await _s3.GetByteArrayAsync(root.GetProperty("root").GetProperty("content").GetProperty("uri").GetString()));
        Assert.StartsWith($"/api/v1/models/{model.GetProperty("id").GetInt64()}/tileset.json?path=sub%2Ftileset.json", nestedUrl);
        Assert.Equal(new byte[] { 1, 2, 3 }, await _s3.GetByteArrayAsync(contents[0].GetProperty("uri").GetString()));
        Assert.Equal("https://example.org/x.glb", contents[1].GetProperty("uri").GetString());
    }

    [Fact]
    public async Task Upload_Validation()
    {
        var (org, site) = await SiteAsync();
        var traversal = await org.Http.PostAsJsonAsync("/api/v1/uploads/model", new { siteId = site, files = new[] { new { path = "../x.glb", size = 1 } } });
        var unknown = await org.Http.PostAsJsonAsync("/api/v1/uploads/model", new { siteId = site, files = new[] { new { path = "model.obj", size = 1 } } });
        var tooBig = await org.Http.PostAsJsonAsync("/api/v1/uploads/model", new { siteId = site, files = new[] { new { path = "m.glb", size = 1L << 40 } } });
        var prefix = await UploadAsync(org, site, new() { ["m.glb"] = Glb });
        var noPlacement = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "M" });
        var foreignPrefix = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models",
            new { prefix = $"org/999999/models/{Guid.NewGuid():N}/", name = "M", placement = Placement() });
        var empty = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models",
            new { prefix = $"org/{org.OrganizationId}/models/{Guid.NewGuid():N}/", name = "M", placement = Placement() });
        var ok = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "M", placement = Placement() });
        var again = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "M", placement = Placement() });
        var badTileset = await UploadAsync(org, site, new() { ["tileset.json"] = Encoding.UTF8.GetBytes("""{"asset":{"version":"1.1"},"root":{"implicitTiling":{}}}""") });
        var implicitTiling = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix = badTileset, name = "T" });

        Assert.Equal("model.invalid_files", await TestApi.ErrorCode(traversal));
        Assert.Equal("model.unknown_format", await TestApi.ErrorCode(unknown));
        Assert.Equal("model.too_large", await TestApi.ErrorCode(tooBig));
        Assert.Equal("model.placement_required", await TestApi.ErrorCode(noPlacement));
        Assert.Equal("model.invalid_upload", await TestApi.ErrorCode(foreignPrefix));
        Assert.Equal("model.upload_missing", await TestApi.ErrorCode(empty));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("model.upload_used", await TestApi.ErrorCode(again));
        Assert.Equal("model.implicit_tiling", await TestApi.ErrorCode(implicitTiling));
    }

    [Fact]
    public async Task OtherOrganization_GetsNotFound_DeleteRemovesTheFiles()
    {
        var (org, site) = await SiteAsync();
        var prefix = await UploadAsync(org, site, new() { ["m.glb"] = Glb });
        var model = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/models", new { prefix, name = "M", placement = Placement() }));
        var id = model.GetProperty("id").GetInt64();
        var signed = (await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/models/{id}/tileset.json")).GetProperty("root").GetProperty("content")
            .GetProperty("uri").GetString();
        var other = await Api.NewOrganizationAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/models/{id}/tileset.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/sites/{site}/models")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.DeleteAsync($"/api/v1/models/{id}")).StatusCode);

        var renamed = await TestApi.ReadJson(await org.Http.PatchJsonAsync($"/api/v1/models/{id}", new { name = "Új név", placement = Placement(180) }));
        Assert.Equal("Új név", renamed.GetProperty("name").GetString());
        Assert.Equal(180, renamed.GetProperty("placement").GetProperty("headingDeg").GetDouble());

        Assert.Equal(HttpStatusCode.NoContent, (await org.Http.DeleteAsync($"/api/v1/models/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.Http.GetAsync($"/api/v1/models/{id}")).StatusCode);
        Assert.False((await _s3.GetAsync(signed)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Viewer_ReadsModels_ButCannotUpload()
    {
        var (org, site) = await SiteAsync();
        var viewer = await Api.NewUserAsync(org.OrganizationId, org.OrganizationCode, Roles.Viewer);

        var upload = await viewer.Http.PostAsJsonAsync("/api/v1/uploads/model", new { siteId = site, files = new[] { new { path = "m.glb", size = 1 } } });
        var list = await viewer.Http.GetAsync($"/api/v1/sites/{site}/models");

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }
}
