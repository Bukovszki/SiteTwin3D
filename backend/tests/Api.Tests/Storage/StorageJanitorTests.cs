using System.Net;
using System.Net.Http.Json;
using System.Text;
using Api.Data;
using Api.Services.Storage;
using Api.Tests.Database;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Api.Tests.Storage;

/// <summary>M9: object store housekeeping – orphans after the grace period, expired panorama originals, dry run.</summary>
public sealed class StorageJanitorTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private IObjectStorage Store => Factory.Services.GetRequiredService<IObjectStorage>();

    private async Task<CleanupReport> RunAsync(int graceHours, bool dryRun)
    {
        using var scope = Factory.Services.CreateScope();
        var janitor = new StorageJanitor(scope.ServiceProvider.GetRequiredService<AppDbContext>(), Store,
            Options.Create(new CleanupOptions { OrphanGraceHours = graceHours }), NullLogger<StorageJanitor>.Instance);
        return await janitor.RunAsync(dryRun, CancellationToken.None);
    }

    private async Task PutAsync(string key) => await Store.PutAsync(key, new MemoryStream(Encoding.ASCII.GetBytes(key)), "application/octet-stream", CancellationToken.None);

    private async Task<bool> ExistsAsync(string key) => await Store.SizeAsync(key, CancellationToken.None) is not null;

    [Fact]
    public async Task Orphans_AreDeletedAfterTheGracePeriod_ReferencedFilesStay()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var level = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        var panorama = await sql.PanoramaAsync(org.OrganizationId, level, 19.08, 47.43, null);
        var display = $"org/{org.OrganizationId}/panoramas/{panorama}/display.jpg";
        await sql.ExecuteAsync("UPDATE panorama SET storage_key = @k, display_key = @d WHERE id = @id",
            ("k", $"org/{org.OrganizationId}/uploads/{Guid.NewGuid():N}.jpg"), ("d", display), ("id", panorama));
        var original = await sql.ScalarAsync<string>("SELECT storage_key FROM panorama WHERE id = @id", ("id", panorama));

        // A registered model package (its folder), and leftovers no row refers to.
        var modelFolder = $"org/{org.OrganizationId}/models/{Guid.NewGuid():N}/";
        await sql.ExecuteAsync("""
            INSERT INTO model_asset (organization_id, site_id, kind, storage_key, attrs) VALUES (@org, @site, '3dtiles', @key, '{}')
            """, ("org", org.OrganizationId), ("site", site), ("key", modelFolder + "tileset.json"));
        var orphanUpload = $"org/{org.OrganizationId}/uploads/{Guid.NewGuid():N}.jpg";
        var orphanModel = $"org/{org.OrganizationId}/models/{Guid.NewGuid():N}/tileset.json";
        foreach (var key in new[] { original, display, modelFolder + "tileset.json", modelFolder + "tiles/0.glb", orphanUpload, orphanModel })
            await PutAsync(key);

        var young = await RunAsync(graceHours: 1, dryRun: false);
        Assert.True(young.Young >= 2);
        Assert.True(await ExistsAsync(orphanUpload)); // still within the grace period (a running upload)

        var dry = await RunAsync(graceHours: 0, dryRun: true);
        Assert.True(dry.DryRun);
        Assert.True(dry.Orphans >= 2);
        Assert.Equal(0, dry.Deleted);
        Assert.True(await ExistsAsync(orphanUpload)); // dry run deletes nothing

        var report = await RunAsync(graceHours: 0, dryRun: false);
        Assert.True(report.Deleted >= 2);
        Assert.False(await ExistsAsync(orphanUpload));
        Assert.False(await ExistsAsync(orphanModel));
        foreach (var key in new[] { original, display, modelFolder + "tileset.json", modelFolder + "tiles/0.glb" })
            Assert.True(await ExistsAsync(key), key);
    }

    [Fact]
    public async Task ExpiredOriginal_IsDeleted_DisplayVersionStays()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var node = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        var expired = await sql.PanoramaAsync(org.OrganizationId, node, 19.08, 47.43, null);
        var kept = await sql.PanoramaAsync(org.OrganizationId, node, 19.08, 47.43, null);
        var keys = new Dictionary<long, (string Original, string Display)>();
        foreach (var (id, until) in new[] { (expired, "now() - interval '1 day'"), (kept, "now() + interval '30 days'") })
        {
            var original = $"org/{org.OrganizationId}/uploads/{Guid.NewGuid():N}.jpg";
            var display = $"org/{org.OrganizationId}/panoramas/{id}/display.jpg";
            await sql.ExecuteAsync($"UPDATE panorama SET storage_key = @k, display_key = @d, original_retained_until = {until} WHERE id = @id",
                ("k", original), ("d", display), ("id", id));
            await PutAsync(original);
            await PutAsync(display);
            keys[id] = (original, display);
        }

        var report = await RunAsync(graceHours: 24, dryRun: false);

        Assert.True(report.ExpiredOriginals >= 1);
        Assert.False(await ExistsAsync(keys[expired].Original));
        Assert.True(await ExistsAsync(keys[expired].Display));
        Assert.True(await ExistsAsync(keys[kept].Original));
        Assert.Equal(DBNull.Value, await sql.ScalarAsync<object>("SELECT original_retained_until FROM panorama WHERE id = @id", ("id", expired)));
    }

    [Fact]
    public async Task CleanupEndpoint_PlatformAdminOnly_DryRunByDefault()
    {
        var org = await Api.NewOrganizationAsync();
        var platform = await Api.NewPlatformAdminAsync();

        var forbidden = await org.Http.PostAsJsonAsync("/api/v1/platform/storage/cleanup", new { });
        var report = await TestApi.ReadJson(await platform.PostAsJsonAsync("/api/v1/platform/storage/cleanup", new { }));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, report.GetProperty("deleted").GetInt32());
    }

    [Fact]
    public async Task Requests_GetARequestId()
    {
        var org = await Api.NewOrganizationAsync();
        var response = await org.Http.GetAsync("/api/v1/sites");
        Assert.False(string.IsNullOrEmpty(response.Headers.GetValues("X-Request-Id").Single()));
    }
}
