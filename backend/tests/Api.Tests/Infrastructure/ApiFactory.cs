using Api.Services.Maps;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Infrastructure;

/// <summary>
/// The API in memory, against the test container database; migrations run on startup as in production.
/// Outbound map requests (MapManager, external WMS) go to <see cref="GeoServer"/> instead of the network.
/// </summary>
public sealed class ApiFactory(PostgisFixture postgis) : WebApplicationFactory<Program>
{
    /// <summary>Token reference configured for tests (organization.mapmanager_token_ref).</summary>
    public const string TokenRef = "test";
    public const string Token = "mmst_test_token";

    public FakeGeoServer GeoServer { get; } = new();

    /// <summary>Stands in for the converter service (ModelConversionTests).</summary>
    public FakeConverter Converter { get; } = new();

    /// <summary>Replaces the ONNX face detector when set before the first request.</summary>
    public Api.Services.Panoramas.IFaceDetector? FaceDetector { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Database", postgis.ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting($"MapManager:Tokens:{TokenRef}", Token);
        builder.UseSetting("Storage:Endpoint", postgis.StorageUrl);
        builder.UseSetting("Storage:PublicUrl", postgis.StorageUrl);
        builder.UseSetting("Storage:AccessKey", PostgisFixture.StorageAccessKey);
        builder.UseSetting("Storage:SecretKey", PostgisFixture.StorageSecretKey);
        // Tests drive the panorama worker step by step (PanoramaTests.ProcessAllAsync).
        builder.UseSetting("Privacy:RunWorker", "false");
        builder.UseSetting("Cleanup:RunWorker", "false");
        builder.UseSetting("Converter:RunWorker", "false");
        builder.ConfigureTestServices(services =>
        {
            if (FaceDetector is { } detector) services.AddSingleton(detector);
            services.AddHttpClient(OutboundHttp.MapManagerClientName).ConfigurePrimaryHttpMessageHandler(() => GeoServer);
            services.AddHttpClient(OutboundHttp.ExternalWmsClientName).ConfigurePrimaryHttpMessageHandler(() => GeoServer);
            services.AddHttpClient(Api.Services.Models.ConverterOptions.ClientName).ConfigurePrimaryHttpMessageHandler(() => Converter);
        });
    }
}
