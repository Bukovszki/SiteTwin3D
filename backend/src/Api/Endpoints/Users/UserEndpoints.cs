using System.Text.Json;
using Api.Data;
using Api.Model;
using Api.Services;
using Api.Services.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Api.Endpoints.Users;

public record SiteRoleDto(long SiteId, string Role);

public record UserDto(long Id, string Username, string DisplayName, string Role, bool Active, bool MustChangePassword, DateTime? LastLogin,
    List<SiteRoleDto> SiteRoles);

public record UserCreateRequest(string Username, string? DisplayName, string Role, string Password);

public record PasswordResetRequest(string Password);

public record SiteRolesRequest(List<SiteRoleDto> Roles);

/// <summary>
/// Users of the organization (spec 3.2; organization admin only): create with a temporary password, name, role,
/// active state, password reset, site-level roles. Role and state changes end the user's sessions (security stamp).
/// The organization keeps at least one active admin, and admins cannot lock themselves out.
/// </summary>
public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/users").WithTags("Users").RequireAuthorization(Policies.OrgAdmin);
        group.MapGet("/", List).WithSummary("Users of the organization with their site roles");
        group.MapPost("/", Create).WithSummary("New user (OrgAdmin | Editor | Viewer) with a temporary password");
        group.MapPatch("/{id:long}", Update).WithSummary("Display name, role, active; role or state change ends the user's sessions");
        group.MapPost("/{id:long}/password", ResetPassword).WithSummary("Sets a temporary password (to be changed at the next sign-in)");
        group.MapPut("/{id:long}/site-roles", SetSiteRoles)
            .WithSummary("Site roles (Editor | Viewer per site); empty: every site of the organization with the user's role");
        return api;
    }

    private static async Task<Ok<List<UserDto>>> List(AppDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
        var roles = await db.UserSiteRoles.AsNoTracking().ToListAsync(ct);
        return TypedResults.Ok(users.Select(u => ToDto(u, roles)).ToList());
    }

    private static async Task<Created<UserDto>> Create(UserCreateRequest request, AppDbContext db, CurrentUser current, CancellationToken ct)
    {
        var username = (request.Username ?? "").Trim();
        var user = new AppUser
        {
            OrganizationId = current.OrganizationId,
            Username = username,
            UsernameLower = AuthService.Normalize(username),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(),
            Role = ValidRole(request.Role),
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            AuthService.ValidateUsername(username);
            AuthService.SetPassword(user, request.Password, temporary: true);
        }
        catch (AuthException ex)
        {
            throw AppException.BadRequest(ex.Code, ex.Message);
        }
        if (await db.Users.AnyAsync(u => u.UsernameLower == user.UsernameLower, ct))
            throw AppException.Conflict("user.exists", $"A user named '{username}' already exists.");
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/users/{user.Id}", ToDto(user, []));
    }

    private static async Task<Ok<UserDto>> Update(long id, JsonElement body, IOptions<JsonOptions> json, AppDbContext db, CurrentUser current,
        AuthService auth, IMemoryCache cache, CancellationToken ct)
    {
        var patch = PatchBody.From(body, json);
        var user = await FindAsync(db, id, ct);
        var role = patch.Get<string>("role") is { } r ? ValidRole(r) : user.Role;
        var active = patch.Get<bool?>("active") ?? user.Active;
        if (user.Id == current.UserId && (role != user.Role || !active))
            throw AppException.BadRequest("user.self_lockout", "You cannot change your own role or disable yourself.");
        if (user.Role == Roles.OrgAdmin && user.Active && (role != Roles.OrgAdmin || !active)
            && !await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == Roles.OrgAdmin && u.Active, ct))
            throw AppException.BadRequest("user.last_admin", "The organization must keep at least one active admin.");

        if (patch.Get<string>("displayName") is { } name && name.Trim().Length > 0) user.DisplayName = name.Trim();
        if (role != user.Role || active != user.Active)
        {
            user.Role = role;
            user.Active = active;
            // Sessions end: the cookie still carries the old role.
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }
        await db.SaveChangesAsync(ct);
        auth.Invalidate(user.Id);
        cache.Remove(SiteRolesTransformation.CacheKey(user.Id));
        return TypedResults.Ok(ToDto(user, await db.UserSiteRoles.AsNoTracking().Where(s => s.UserId == id).ToListAsync(ct)));
    }

    private static async Task<NoContent> ResetPassword(long id, PasswordResetRequest request, AppDbContext db, AuthService auth, CancellationToken ct)
    {
        var user = await FindAsync(db, id, ct);
        try
        {
            AuthService.SetPassword(user, request.Password, temporary: true);
        }
        catch (AuthException ex)
        {
            throw AppException.BadRequest(ex.Code, ex.Message);
        }
        await db.SaveChangesAsync(ct);
        auth.Invalidate(user.Id);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<UserDto>> SetSiteRoles(long id, SiteRolesRequest request, AppDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        var user = await FindAsync(db, id, ct);
        var roles = request.Roles ?? [];
        if (roles.Any(r => r.Role is not (Roles.Editor or Roles.Viewer)))
            throw AppException.BadRequest("user.invalid_site_role", "Site role: Editor or Viewer.");
        var siteIds = roles.Select(r => r.SiteId).Distinct().ToList();
        if (siteIds.Count != roles.Count) throw AppException.BadRequest("user.duplicate_site", "A site may appear only once.");
        // Organization-filtered: another organization's site is not found.
        if (await db.Sites.CountAsync(s => siteIds.Contains(s.Id), ct) != siteIds.Count)
            throw AppException.NotFound("site.not_found", "Unknown site.");

        await db.UserSiteRoles.Where(r => r.UserId == id).ExecuteDeleteAsync(ct);
        db.UserSiteRoles.AddRange(roles.Select(r => new UserSiteRole { UserId = id, SiteId = r.SiteId, Role = r.Role }));
        await db.SaveChangesAsync(ct);
        cache.Remove(SiteRolesTransformation.CacheKey(id));
        return TypedResults.Ok(ToDto(user, await db.UserSiteRoles.AsNoTracking().Where(s => s.UserId == id).ToListAsync(ct)));
    }

    private static async Task<AppUser> FindAsync(AppDbContext db, long id, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw AppException.NotFound("user.not_found", $"User {id} not found.");

    private static string ValidRole(string? role) => Roles.OrganizationRoles.Contains(role)
        ? role! : throw AppException.BadRequest("user.invalid_role", "Role: OrgAdmin, Editor or Viewer.");

    private static UserDto ToDto(AppUser u, List<UserSiteRole> roles) =>
        new(u.Id, u.Username, u.DisplayName, u.Role, u.Active, u.MustChangePassword, u.LastLogin,
            roles.Where(r => r.UserId == u.Id).OrderBy(r => r.SiteId).Select(r => new SiteRoleDto(r.SiteId, r.Role)).ToList());
}
