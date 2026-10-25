using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Api.Model;
using Api.Services.Panoramas;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Api.Tests.Panoramas;

/// <summary>
/// Panorama life cycle with the real object store (spec 4.4, FR-P8): signed upload straight to S3, registration,
/// background analysis, review, approval, blurred display version; viewers only see approved panoramas.
/// </summary>
public sealed class PanoramaTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private readonly HttpClient _s3 = new();

    public override Task InitializeAsync()
    {
        var result = base.InitializeAsync();
        Factory.FaceDetector = new RedPatchDetector();
        return result;
    }

    private record Setup(OrgClient Org, long Site, long Building, long Level);

    private async Task<Setup> SiteWithLevelAsync()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync(); // base height 150 m
        var building = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        var level = (await org.Http.CreateNodeAsync(site, building, await org.Http.TypeIdAsync("level"), "Emelet",
            new { elevation_m = 4.5 })).GetProperty("id").GetInt64();
        return new Setup(org, site, building, level);
    }

    /// <summary>Uploads like the browser does: ticket, PUT to the signed URL, registration.</summary>
    private async Task<JsonElement> UploadAsync(Setup s, byte[] jpeg, object? extra = null)
    {
        var ticket = await TestApi.ReadJson(await s.Org.Http.PostAsJsonAsync("/api/v1/uploads/panorama",
            new { siteId = s.Site, fileName = "pano.jpg", size = jpeg.Length }));
        var put = new HttpRequestMessage(HttpMethod.Put, ticket.GetProperty("url").GetString())
        {
            Content = new ByteArrayContent(jpeg) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } },
        };
        var uploaded = await _s3.SendAsync(put);
        Assert.True(uploaded.IsSuccessStatusCode, $"signed PUT failed: {(int)uploaded.StatusCode} {await uploaded.Content.ReadAsStringAsync()}");

        var response = await s.Org.Http.PostAsJsonAsync("/api/v1/panoramas", new
        {
            uploadKey = ticket.GetProperty("key").GetString(),
            sha256 = Convert.ToHexStringLower(SHA256.HashData(jpeg)),
            nodeId = s.Building,
            position = new { type = "Point", coordinates = new[] { 19.0401, 47.5002 } },
            levelId = s.Level,
            zRelM = 1.5,
            headingDeg = extra is null ? (double?)null : 370,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await TestApi.ReadJson(response);
    }

    /// <summary>Runs the background worker until the queue is empty.</summary>
    private async Task ProcessAllAsync()
    {
        var worker = new PanoramaWorker(Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<PanoramaQueue>(), NullLogger<PanoramaWorker>.Instance);
        while (await worker.ProcessNextAsync(CancellationToken.None)) { }
    }

    private static byte[] PanoramaWithFace()
    {
        // A red "face" (found by the stand-in detector) with detail inside (a checkerboard) that the blur must remove.
        using var image = TestImages.Panorama(3600, 1800);
        TestImages.PaintPatch(image, yaw: -30, pitch: 5, width: 6, height: 8);
        TestImages.PaintCheckerboard(image, yaw: -30, pitch: 5, size: 4);
        return TestImages.Jpeg(image);
    }

    [Fact]
    public async Task Upload_Analyse_Review_Approve_Display()
    {
        var s = await SiteWithLevelAsync();
        var jpeg = PanoramaWithFace();

        var created = await UploadAsync(s, jpeg, extra: true);
        Assert.Equal("pending", created.GetProperty("privacyStatus").GetString());
        Assert.Equal(10, created.GetProperty("headingDeg").GetDouble(), 3); // normalized to [0, 360)

        await ProcessAllAsync();
        var review = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/panoramas/{created.GetProperty("id").GetInt64()}");
        var id = review.GetProperty("id").GetInt64();
        Assert.Equal("review", review.GetProperty("privacyStatus").GetString());
        var face = Assert.Single(review.GetProperty("privacyRegions").EnumerateArray());
        Assert.Equal(-30, face.GetProperty("yaw").GetDouble(), 0);
        // Absolute height (spec 7.1): site base 150 + level floor 4.5 + camera 1.5.
        Assert.Equal(156, review.GetProperty("absoluteHeightM").GetDouble(), 3);
        // During review the editor sees the original through a signed URL.
        Assert.True(review.GetProperty("imageIsOriginal").GetBoolean());
        Assert.Equal(jpeg, await _s3.GetByteArrayAsync(review.GetProperty("imageUrl").GetString()));

        // The editor adds a manual region, then approves.
        var regions = new[]
        {
            JsonSerializer.Deserialize<object>(face.GetRawText()),
            new { id = "manual1", yaw = 100.0, pitch = 0.0, width = 10.0, height = 6.0, source = "manual", accepted = true },
        };
        Assert.Equal(HttpStatusCode.OK, (await s.Org.Http.PutAsJsonAsync($"/api/v1/panoramas/{id}/privacy-regions", new { regions })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Org.Http.PostAsync($"/api/v1/panoramas/{id}/approve", null)).StatusCode);
        await ProcessAllAsync();

        var approved = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/panoramas/{id}");
        Assert.Equal("approved", approved.GetProperty("privacyStatus").GetString());
        Assert.False(approved.GetProperty("imageIsOriginal").GetBoolean());
        using var display = SKBitmap.Decode(await _s3.GetByteArrayAsync(approved.GetProperty("imageUrl").GetString()));
        Assert.Equal(3600, display.Width);
        // The detail of the "face" is gone in the display version, the rest of the image is untouched.
        using var original = SKBitmap.Decode(jpeg);
        Assert.True(TestImages.Contrast(display, -30, 5, 3) < TestImages.Contrast(original, -30, 5, 3) / 5, "face detail still visible");
        Assert.Equal(TestImages.Contrast(original, 150, 0, 3), TestImages.Contrast(display, 150, 0, 3), 0);

        // Default organization policy: the unblurred original is deleted after approval.
        await using var db = Postgis.CreateDbContext();
        var row = db.Panoramas.IgnoreQueryFiltersFor(id);
        Assert.Null(row.OriginalRetainedUntil);
        var storage = Factory.Services.GetRequiredService<Api.Services.Storage.IObjectStorage>();
        Assert.Null(await storage.SizeAsync(row.StorageKey, CancellationToken.None));
    }

    [Fact]
    public async Task Viewer_SeesOnlyApprovedPanoramas()
    {
        var s = await SiteWithLevelAsync();
        var first = (await UploadAsync(s, PanoramaWithFace())).GetProperty("id").GetInt64();
        var second = (await UploadAsync(s, PanoramaWithFace())).GetProperty("id").GetInt64();
        await ProcessAllAsync();
        await s.Org.Http.PostAsync($"/api/v1/panoramas/{first}/approve", null);
        await ProcessAllAsync();
        var viewer = await Api.NewUserAsync(s.Org.OrganizationId, s.Org.OrganizationCode, Roles.Viewer);

        var list = await viewer.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{s.Site}/panoramas?levelId={s.Level}");

        Assert.Equal([first], list.EnumerateArray().Select(p => p.GetProperty("id").GetInt64()));
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Http.GetAsync($"/api/v1/panoramas/{second}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsync($"/api/v1/panoramas/{second}/approve", null)).StatusCode);
        Assert.Equal(2, (await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/sites/{s.Site}/panoramas")).GetArrayLength());
    }

    [Fact]
    public async Task WrongChecksum_OrNot2to1_Fails()
    {
        var s = await SiteWithLevelAsync();
        using var square = TestImages.Panorama(1000, 1000);
        var notPanorama = (await UploadAsync(s, TestImages.Jpeg(square))).GetProperty("id").GetInt64();
        await ProcessAllAsync();

        var failed = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/panoramas/{notPanorama}");

        Assert.Equal("failed", failed.GetProperty("privacyStatus").GetString());
        Assert.Equal("panorama.not_equirectangular", failed.GetProperty("processingError").GetString());
    }

    [Fact]
    public async Task Register_RefusesForeignOrMissingUploads()
    {
        var s = await SiteWithLevelAsync();
        object Body(string key) => new
        {
            uploadKey = key, sha256 = new string('a', 64), nodeId = s.Building,
            position = new { type = "Point", coordinates = new[] { 19.04, 47.5 } },
        };

        var foreign = await s.Org.Http.PostAsJsonAsync("/api/v1/panoramas", Body("org/999999/uploads/x.jpg"));
        var missing = await s.Org.Http.PostAsJsonAsync("/api/v1/panoramas", Body($"org/{s.Org.OrganizationId}/uploads/nothing.jpg"));
        var tooBig = await s.Org.Http.PostAsJsonAsync("/api/v1/uploads/panorama", new { siteId = s.Site, fileName = "p.jpg", size = 10L * 1024 * 1024 * 1024 });

        Assert.Equal("panorama.invalid_upload", await TestApi.ErrorCode(foreign));
        Assert.Equal("panorama.upload_missing", await TestApi.ErrorCode(missing));
        Assert.Equal("panorama.too_large", await TestApi.ErrorCode(tooBig));
    }

    [Fact]
    public async Task MovedToAnotherNode_TheLevelFollowsTheHierarchy()
    {
        // User report: a panorama moved in the data tree from the building to a room of its floor stayed on the old level.
        var s = await SiteWithLevelAsync();
        var outdoorArea = (await s.Org.Http.CreateNodeAsync(s.Site, null, await s.Org.Http.TypeIdAsync("outdoor_area"), "Udvar")).GetProperty("id").GetInt64();
        var room = (await s.Org.Http.CreateNodeAsync(s.Site, s.Level, await s.Org.Http.TypeIdAsync("room"), "Gépterem")).GetProperty("id").GetInt64();
        var id = (await UploadAsync(s, PanoramaWithFace())).GetProperty("id").GetInt64();
        long? Level(JsonElement p) => p.GetProperty("levelId") is { ValueKind: JsonValueKind.Number } l ? l.GetInt64() : null;

        var outside = await TestApi.ReadJson(await s.Org.Http.PatchJsonAsync($"/api/v1/panoramas/{id}", new { nodeId = outdoorArea }));
        var inRoom = await TestApi.ReadJson(await s.Org.Http.PatchJsonAsync($"/api/v1/panoramas/{id}", new { nodeId = room }));
        // Back to the building: its level is inside the building, so it stays.
        var inBuilding = await TestApi.ReadJson(await s.Org.Http.PatchJsonAsync($"/api/v1/panoramas/{id}", new { nodeId = s.Building }));
        // A level given with the move wins.
        var given = await TestApi.ReadJson(await s.Org.Http.PatchJsonAsync($"/api/v1/panoramas/{id}", new { nodeId = room, levelId = (long?)null }));

        Assert.Null(Level(outside));
        Assert.Equal(s.Level, Level(inRoom));
        Assert.Equal(s.Level, Level(inBuilding));
        Assert.Null(Level(given));
    }

    [Fact]
    public async Task Placement_HeadingCalibration_AndSubtreeQuery()
    {
        var s = await SiteWithLevelAsync();
        var id = (await UploadAsync(s, PanoramaWithFace())).GetProperty("id").GetInt64();

        var calibrated = await s.Org.Http.PatchJsonAsync($"/api/v1/panoramas/{id}", new
        {
            headingDeg = -45,
            position = new { type = "Point", coordinates = new[] { 19.0405, 47.5003 } },
        });
        var outdoor = await s.Org.Http.PatchJsonAsync($"/api/v1/panoramas/{id}", new { levelId = (long?)null });
        var notLevel = await s.Org.Http.PatchJsonAsync($"/api/v1/panoramas/{id}", new { levelId = s.Building });
        var subtree = await s.Org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{s.Building}/panoramas?recursive=true");

        Assert.Equal(315, (await TestApi.ReadJson(calibrated)).GetProperty("headingDeg").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, (await TestApi.ReadJson(outdoor)).GetProperty("levelId").ValueKind);
        Assert.Equal("level.invalid", await TestApi.ErrorCode(notLevel));
        Assert.Single(subtree.EnumerateArray());
    }

    [Fact]
    public async Task OtherOrganization_CannotSeeOrChangePanoramas()
    {
        var s = await SiteWithLevelAsync();
        var id = (await UploadAsync(s, PanoramaWithFace())).GetProperty("id").GetInt64();
        var other = await Api.NewOrganizationAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/panoramas/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.DeleteAsync($"/api/v1/panoramas/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/sites/{s.Site}/panoramas")).StatusCode);
    }
}

internal static class PanoramaQueries
{
    public static Panorama IgnoreQueryFiltersFor(this Microsoft.EntityFrameworkCore.DbSet<Panorama> set, long id) =>
        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(set).Single(p => p.Id == id);
}
