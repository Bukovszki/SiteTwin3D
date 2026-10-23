using System.Reflection;
using Api.Endpoints.Anchors;
using Api.Endpoints.Auth;
using Api.Endpoints.Drawing;
using Api.Endpoints.Maps;
using Api.Endpoints.Models;
using Api.Endpoints.Nodes;
using Api.Endpoints.Panoramas;
using Api.Endpoints.NodeTypes;
using Api.Endpoints.Platform;
using Api.Endpoints.Sites;
using Api.Endpoints.Users;

namespace Api.Endpoints;

/// <summary>
/// Registers every API endpoint. For a new feature area: an Endpoints/&lt;Area&gt;/ folder with a
/// Map&lt;Area&gt;Endpoints extension method, called from here.
/// </summary>
public static class EndpointRegistration
{
    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapAuthEndpoints();
        api.MapPlatformEndpoints();
        api.MapUserEndpoints();
        api.MapSiteEndpoints();
        api.MapNodeTypeEndpoints();
        api.MapNodeEndpoints();
        api.MapMapEndpoints();
        api.MapPanoramaEndpoints();
        api.MapAnchorEndpoints();
        api.MapDrawEndpoints();
        api.MapModelEndpoints();

        // Running version (anonymous): the frontend uses it to notice a stale build in the browser.
        api.MapGet("/version", () => TypedResults.Ok(new VersionResponse(AppVersion))).AllowAnonymous().WithTags("Info")
            .WithSummary("Application (API) version, e.g. 0.1.0");

        // Liveness and database readiness for compose / load balancers.
        app.MapHealthChecks("/health").AllowAnonymous();

        return app;
    }

    public record VersionResponse(string Version);

    /// <summary>The &lt;Version&gt; of Api.csproj (without the commit hash appended at build time).</summary>
    public static readonly string AppVersion =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
