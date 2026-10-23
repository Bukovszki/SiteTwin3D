using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Endpoints;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Hosting;

/// <summary>M0 acceptance: the API starts, migrations run, /health answers.</summary>
[Collection(PostgisCollection.Name)]
public sealed class StartupTests(PostgisFixture postgis) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(postgis);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Health_IsHealthy_WhenDatabaseIsReachable()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Startup_AppliesAllMigrations_AndEnablesExtensions()
    {
        _ = _factory.CreateClient(); // starts the host, which migrates

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var extensions = await db.Database
            .SqlQuery<string>($"SELECT extname AS \"Value\" FROM pg_extension")
            .ToListAsync();
        Assert.Contains("postgis", extensions);
        Assert.Contains("ltree", extensions);
    }

    [Fact]
    public async Task Version_ReturnsAssemblyVersion()
    {
        var body = await _factory.CreateClient()
            .GetFromJsonAsync<EndpointRegistration.VersionResponse>("/api/v1/version");

        Assert.Equal(EndpointRegistration.AppVersion, body?.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", body!.Version);
    }
}
