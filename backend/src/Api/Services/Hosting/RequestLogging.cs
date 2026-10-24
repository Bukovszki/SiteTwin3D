using System.Diagnostics;
using Api.Services.Auth;

namespace Api.Services.Hosting;

/// <summary>
/// One log line per API request (M9): method, path (without the query – it may carry filters or signatures), status,
/// duration, user and organization. The request id goes back in <c>X-Request-Id</c>; ASP.NET Core already puts the
/// same id (RequestId) in the log scope of every line of the request, so a user's report leads to all of them.
/// Health checks are not logged.
/// </summary>
public class RequestLogging(RequestDelegate next, ILogger<RequestLogging> logger)
{
    public const string Header = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }
        var requestId = context.TraceIdentifier;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = requestId;
            return Task.CompletedTask;
        });
        var watch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            var status = context.Response.StatusCode;
            var user = context.User;
            var level = status >= 500 ? LogLevel.Error : status is 401 or 403 or 429 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "{Method} {Path} {Status} in {Elapsed} ms (user {UserId}, organization {OrganizationId})",
                context.Request.Method, context.Request.Path.Value, status, watch.ElapsedMilliseconds,
                user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "-",
                user.FindFirst(CurrentUser.OrganizationIdClaim)?.Value ?? "-");
        }
    }
}
