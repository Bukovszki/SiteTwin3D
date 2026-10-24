namespace Api.Services.History;

/// <summary>
/// Reason and valid-from day of the changes of the current request (headers <c>X-Change-Reason</c>, URL-encoded, and
/// <c>X-Change-Valid-From</c>, yyyy-MM-dd). Needed only for finalized nodes: the UI asks for them when the API answers
/// 428 change.reason_required, and repeats the request with them.
/// </summary>
public class ChangeContext
{
    public const string ReasonHeader = "X-Change-Reason";
    public const string ValidFromHeader = "X-Change-Valid-From";

    public string? Reason { get; set; }
    public DateOnly? ValidFrom { get; set; }
}

/// <summary>Fills <see cref="ChangeContext"/> from the request headers.</summary>
public class ChangeContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ChangeContext changes)
    {
        if (context.Request.Headers.TryGetValue(ChangeContext.ReasonHeader, out var reason) && !string.IsNullOrWhiteSpace(reason))
            changes.Reason = Uri.UnescapeDataString(reason.ToString()).Trim();
        if (context.Request.Headers.TryGetValue(ChangeContext.ValidFromHeader, out var validFrom) && !string.IsNullOrWhiteSpace(validFrom))
        {
            if (!DateOnly.TryParseExact(validFrom.ToString(), "yyyy-MM-dd", out var day))
                throw AppException.BadRequest("change.invalid_valid_from", "X-Change-Valid-From: yyyy-MM-dd.");
            changes.ValidFrom = day;
        }
        await next(context);
    }
}
