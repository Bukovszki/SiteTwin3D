using Api.Data;
using Api.Model;
using Api.Services;
using Api.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints.Auth;

public record LoginRequest(string Organization, string Username, string Password);

public record PasswordChangeRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// The signed-in user for the UI. <see cref="ReadableSiteIds"/> / <see cref="EditableSiteIds"/>: null = every site of
/// the organization (spec 3.2); the UI offers the editing modes only where editing is allowed.
/// </summary>
public record MeDto(long Id, string Username, string DisplayName, string Role, long? OrganizationId, string? OrganizationCode,
    string? OrganizationName, bool MustChangePassword, IReadOnlyList<long>? ReadableSiteIds = null, IReadOnlyList<long>? EditableSiteIds = null);

/// <summary>Sign-in, sign-out, current user and password change (spec 3.3).</summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", Login).AllowAnonymous()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithSummary("Sign-in with organization code, username and password (platform admin: organization = \"platform\"); sets the cookie");
        group.MapPost("/logout", Logout).AllowAnonymous().WithSummary("Sign-out (clears the cookie)");
        group.MapGet("/me", Me).RequireAuthorization(Policies.SignedIn).WithSummary("The signed-in user (401 if none)");
        group.MapPost("/password", ChangePassword).RequireAuthorization(Policies.SignedIn)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Change own password (mandatory after a temporary one); other sessions end");
        return group;
    }

    private static async Task<Ok<MeDto>> Login(LoginRequest request, HttpContext context, AuthService auth, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request.Organization, request.Username, request.Password, ct);
        if (result.User is null)
            throw new AppException(StatusCodes.Status401Unauthorized, result.ErrorCode!, "Sign-in failed.");
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthService.CreatePrincipal(result.User));
        return TypedResults.Ok(ToDto(result.User));
    }

    private static async Task<NoContent> Logout(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MeDto>, UnauthorizedHttpResult>> Me(CurrentUser current, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Id == current.UserId, ct);
        return user is null ? TypedResults.Unauthorized()
            : TypedResults.Ok(ToDto(user) with { ReadableSiteIds = current.ReadableSiteIds, EditableSiteIds = current.EditableSiteIds });
    }

    private static async Task<Ok<MeDto>> ChangePassword(
        PasswordChangeRequest request, HttpContext context, CurrentUser current, AuthService auth, AppDbContext db, CancellationToken ct)
    {
        try
        {
            await auth.ChangePasswordAsync(current.UserId!.Value, request.CurrentPassword, request.NewPassword, ct);
        }
        catch (AuthException ex)
        {
            throw AppException.BadRequest(ex.Code, ex.Message);
        }
        // New cookie with the new security stamp (ends the old sessions), without the password change flag.
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Include(u => u.Organization).FirstAsync(u => u.Id == current.UserId, ct);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthService.CreatePrincipal(user));
        return TypedResults.Ok(ToDto(user));
    }

    private static MeDto ToDto(AppUser u) =>
        new(u.Id, u.Username, u.DisplayName, u.Role, u.OrganizationId, u.Organization?.Code, u.Organization?.Name, u.MustChangePassword);
}
