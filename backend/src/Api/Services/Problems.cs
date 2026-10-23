using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Services;

/// <summary>
/// A business rule violation. <see cref="Code"/> is a stable, machine-readable key (e.g. "node.cycle"); the UI shows
/// the Hungarian text for it from its message resource. The message is English, for logs and developers.
/// </summary>
public class AppException(int status, string code, string message, object? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public object? Errors { get; } = errors;

    public static AppException NotFound(string code, string message) => new(StatusCodes.Status404NotFound, code, message);
    public static AppException BadRequest(string code, string message, object? errors = null) => new(StatusCodes.Status400BadRequest, code, message, errors);
    public static AppException Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);
}

/// <summary>
/// Turns <see cref="AppException"/> and database constraint violations into problem details with a "code" extension.
/// The database is the last line of defence (cycle, same-site parent, organization consistency, CHECKs); services
/// check what they can earlier with clearer codes.
/// </summary>
public class ProblemExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, detail, errors) = exception switch
        {
            AppException app => (app.Status, app.Code, app.Message, app.Errors),
            DbUpdateException { InnerException: PostgresException pg } => Map(pg),
            PostgresException pg => Map(pg),
            _ => (0, "", "", null),
        };
        if (status == 0) return false;

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Detail = detail,
                Extensions = errors is null ? new Dictionary<string, object?> { ["code"] = code }
                    : new Dictionary<string, object?> { ["code"] = code, ["errors"] = errors },
            },
        });
    }

    private static (int, string, string, object?) Map(PostgresException pg) => (pg.SqlState, pg.ConstraintName) switch
    {
        (_, "node_no_cycle") => (StatusCodes.Status409Conflict, "node.cycle", "A node cannot be moved under its own subtree.", null),
        (_, "node_parent_exists") => (StatusCodes.Status400BadRequest, "node.parent_not_found", "The parent node does not exist.", null),
        (_, "fk_node_parent_same_site") => (StatusCodes.Status400BadRequest, "node.parent_other_site", "The parent node is on another site.", null),
        (PostgresErrorCodes.ForeignKeyViolation, _) => (StatusCodes.Status409Conflict, "db.referenced",
            $"The operation violates a reference ({pg.ConstraintName}).", null),
        (PostgresErrorCodes.UniqueViolation, _) => (StatusCodes.Status409Conflict, "db.duplicate", $"Duplicate value ({pg.ConstraintName}).", null),
        (PostgresErrorCodes.CheckViolation, _) => (StatusCodes.Status400BadRequest, "db.check_failed", $"Invalid data ({pg.ConstraintName}).", null),
        _ => (0, "", "", null),
    };
}
