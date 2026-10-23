using Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Hosting;

/// <summary>
/// Container (production) settings. None of them is on in development; each comes from configuration:
/// <list type="bullet">
/// <item><c>ReverseProxy:TrustForwardedHeaders</c>: behind the reverse proxy (Caddy) the X-Forwarded-Proto/For headers
/// tell the app that the request came over HTTPS. Only enable it when the API is not reachable directly.</item>
/// <item><c>DataProtection:KeysPath</c>: the cookie encryption keys go to a volume, so a container restart
/// does not sign everybody out.</item>
/// <item><c>Database:MigrateOnStartup</c>: pending migrations run on startup.</item>
/// </list>
/// </summary>
public static class DeploymentSetup
{
    public static bool BehindReverseProxy(IConfiguration configuration) =>
        configuration.GetValue<bool>("ReverseProxy:TrustForwardedHeaders");

    public static IServiceCollection AddDeploymentSupport(this IServiceCollection services, IConfiguration configuration)
    {
        if (BehindReverseProxy(configuration))
        {
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                // The proxy lives on the internal compose network and its address may change (the API is not exposed).
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
            });
        }

        var keysPath = configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            services.AddDataProtection()
                .SetApplicationName("Api")
                .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }
        return services;
    }

    public static void UseDeploymentSupport(this WebApplication app)
    {
        if (BehindReverseProxy(app.Configuration)) app.UseForwardedHeaders();
    }

    /// <summary>Runs pending migrations on startup when <c>Database:MigrateOnStartup</c> is set.</summary>
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup")) return;

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The database container is slow on first start (and restarts once during init): retry a few times.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            // Only connection problems are retried; an SQL error (PostgresException) will not heal by waiting.
            catch (Exception ex) when (attempt < 20 && ex is Npgsql.NpgsqlException and not Npgsql.PostgresException or TimeoutException)
            {
                app.Logger.LogWarning("Database not reachable yet (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        app.Logger.LogInformation("Database migrations applied.");
    }

    /// <summary>Creates the object store bucket if missing (retrying while the store container starts).</summary>
    public static async Task PrepareStorageAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Storage:EnsureBucket", true)) return;
        var storage = app.Services.GetRequiredService<Storage.IObjectStorage>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await storage.EnsureBucketAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (attempt < 20 && ex is HttpRequestException or Amazon.Runtime.AmazonServiceException)
            {
                app.Logger.LogWarning("Object store not reachable yet (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}
