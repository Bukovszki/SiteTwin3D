using Api.Data;
using Api.Services.Anchors;
using Api.Services.Drawing;
using Api.Endpoints;
using Api.Services;
using Api.Services.Auth;
using Api.Services.Hierarchy;
using Api.Services.History;
using Api.Services.Hosting;
using Api.Services.Maps;
using Api.Services.Locations;
using Api.Services.Models;
using Api.Services.Panoramas;
using Api.Services.Storage;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
// Geometries travel as GeoJSON in EPSG:4326 (spec 10).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new GeoJsonConverterFactory(new GeometryFactory(new PrecisionModel(), 4326))));
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), npgsql => npgsql.UseNetTopologySuite())
        .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
// Cookie sign-in, policies; every endpoint requires sign-in by default.
builder.Services.AddAppAuth(builder.Configuration);
builder.Services.AddScoped<SiteService>();
builder.Services.AddScoped<NodeTypeService>();
builder.Services.AddScoped<NodeService>();
builder.Services.AddScoped<NodeGridService>();
builder.Services.AddScoped<ImportService>();
// Map proxies and the MapManager link (live WFS reads with the organization's service token).
builder.Services.AddMapHttpClients(builder.Configuration);
builder.Services.AddScoped<MapManagerClient>();
builder.Services.AddScoped<MapLayerService>();
builder.Services.AddScoped<WmsProxy>();
builder.Services.AddScoped<LinkService>();
builder.Services.AddScoped<FloorplanService>();
// Object store (signed URLs; binary content never in the database) and the panorama pipeline (FR-P8).
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
builder.Services.Configure<PrivacyOptions>(builder.Configuration.GetSection(PrivacyOptions.Section));
builder.Services.AddSingleton<IFaceDetector>(sp =>
{
    var options = sp.GetRequiredService<IOptions<PrivacyOptions>>().Value;
    return new UltraFaceDetector(Path.Combine(AppContext.BaseDirectory, options.FaceModelPath), options.FaceThreshold);
});
builder.Services.AddSingleton<PrivacyDetector>();
builder.Services.AddSingleton<PanoramaQueue>();
builder.Services.AddScoped<PanoramaProcessor>();
builder.Services.AddScoped<PanoramaService>();
builder.Services.AddScoped<PanoramaLinkService>();
builder.Services.AddScoped<ModelService>();
builder.Services.AddScoped<FloorGeometryService>();
builder.Services.AddScoped<LocationService>();
builder.Services.AddScoped<ChangeContext>();
builder.Services.AddScoped<HistoryService>();
builder.Services.AddScoped<AnchorService>();
builder.Services.AddScoped<DrawService>();
if (builder.Configuration.GetValue("Privacy:RunWorker", true)) builder.Services.AddHostedService<PanoramaWorker>();
builder.Services.Configure<CleanupOptions>(builder.Configuration.GetSection(CleanupOptions.Section));
builder.Services.AddScoped<StorageJanitor>();
if (builder.Configuration.GetValue("Cleanup:RunWorker", true)) builder.Services.AddHostedService<StorageJanitorWorker>();
// Container hosting: forwarded headers behind the reverse proxy, persistent data protection keys.
builder.Services.AddDeploymentSupport(builder.Configuration);

var app = builder.Build();

app.UseDeploymentSupport();
// Outermost after the forwarded headers: logs the final status of every request, errors included.
app.UseMiddleware<RequestLogging>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}
else if (!DeploymentSetup.BehindReverseProxy(app.Configuration))
{
    // Behind the reverse proxy (Caddy) the proxy redirects to HTTPS.
    app.UseHttpsRedirection();
}

app.UseMiddleware<ChangeContextMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapApiEndpoints();

await app.PrepareDatabaseAsync();
await app.SeedPlatformAdminAsync();
await app.PrepareStorageAsync();

app.Run();
