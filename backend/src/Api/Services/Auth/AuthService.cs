using System.Security.Claims;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Services.Auth;

/// <summary>A user-facing error; <see cref="Code"/> is the machine-readable key the UI translates.</summary>
public class AuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public record LoginResult(AppUser? User, string? ErrorCode);

/// <summary>
/// Sign-in (organization + username + password), password handling and session validity (spec 3.3, following
/// MapManager). Passwords are hashed with the ASP.NET Core Identity hasher (PBKDF2). Locks for 15 minutes after
/// 5 failed attempts.
/// </summary>
public partial class AuthService(AppDbContext db, IMemoryCache cache, ILogger<AuthService> logger)
{
    /// <summary>The platform admin signs in with this organization code (no organization may have it).</summary>
    public const string PlatformOrganizationCode = "platform";
    public const int MinPasswordLength = 8;
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);
    /// <summary>Session validation (user and organization active, stamp unchanged) is cached this long.</summary>
    private static readonly TimeSpan ValidationCacheTime = TimeSpan.FromSeconds(30);

    private static readonly PasswordHasher<AppUser> Hasher = new();

    [GeneratedRegex(@"^[A-Za-z0-9._@\-]{2,64}$")]
    private static partial Regex UsernameRegex();

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    public static void ValidateUsername(string username)
    {
        if (!UsernameRegex().IsMatch(username ?? ""))
            throw new AuthException("auth.invalid_username", "Username: 2–64 characters of letters, digits and . _ - @");
    }

    public static void SetPassword(AppUser user, string password, bool temporary)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new AuthException("auth.password_too_short", $"The password must be at least {MinPasswordLength} characters.");
        user.PasswordHash = Hasher.HashPassword(user, password);
        user.MustChangePassword = temporary;
        user.FailedLogins = 0;
        user.LockedUntil = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public async Task<LoginResult> LoginAsync(string organizationCode, string username, string password, CancellationToken ct)
    {
        var code = Normalize(organizationCode ?? "");
        var name = Normalize(username ?? "");

        // Sign-in happens before the organization is known: the filter is bypassed deliberately.
        var users = db.Users.IgnoreQueryFilters().Include(u => u.Organization);
        var user = code == PlatformOrganizationCode
            ? await users.FirstOrDefaultAsync(u => u.OrganizationId == null && u.UsernameLower == name, ct)
            : await users.FirstOrDefaultAsync(u => u.Organization != null && u.Organization.Code == code && u.UsernameLower == name, ct);

        if (user is null)
        {
            // Hash anyway so the response time does not reveal whether the user exists.
            Hasher.HashPassword(new AppUser(), password ?? "");
            return new LoginResult(null, "auth.invalid_credentials");
        }
        if (user.LockedUntil is { } locked && locked > DateTime.UtcNow)
            return new LoginResult(null, "auth.locked");

        var verified = Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (verified == PasswordVerificationResult.Failed)
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailedLogins)
            {
                user.LockedUntil = DateTime.UtcNow + LockoutTime;
                user.FailedLogins = 0;
                logger.LogWarning("Locked: {Organization}/{User} (failed sign-ins)", code, name);
            }
            await db.SaveChangesAsync(ct);
            return new LoginResult(null, "auth.invalid_credentials");
        }
        if (!user.Active || user.Organization is { Active: false })
            return new LoginResult(null, "auth.disabled");

        if (verified == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, password!);
        user.FailedLogins = 0;
        user.LockedUntil = null;
        user.LastLogin = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Sign-in: {Organization}/{User} ({Role})", code, user.Username, user.Role);
        return new LoginResult(user, null);
    }

    public async Task ChangePasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
            throw new AuthException("auth.wrong_password", "The current password is wrong.");
        if (currentPassword == newPassword)
            throw new AuthException("auth.same_password", "The new password must differ from the old one.");
        SetPassword(user, newPassword, temporary: false);
        await db.SaveChangesAsync(ct);
        Invalidate(userId);
    }

    /// <summary>Cookie content: id, name, role, organization, security stamp, password change obligation.</summary>
    public static ClaimsPrincipal CreatePrincipal(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(CurrentUser.DisplayNameClaim, user.DisplayName),
            new(CurrentUser.StampClaim, user.SecurityStamp),
        };
        if (user.Organization is { } organization)
        {
            claims.Add(new(CurrentUser.OrganizationIdClaim, organization.Id.ToString()));
            claims.Add(new(CurrentUser.OrganizationCodeClaim, organization.Code));
        }
        if (user.MustChangePassword) claims.Add(new(CurrentUser.MustChangePasswordClaim, "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>
    /// Whether the cookie is still valid: user and organization active, security stamp unchanged (password change,
    /// role change or deactivation ends the old sessions). Cached briefly, because the map sends many requests.
    /// </summary>
    public async Task<bool> IsSessionValidAsync(long userId, string? stamp)
    {
        var current = await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ValidationCacheTime;
            return await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == userId && u.Active && (u.Organization == null || u.Organization.Active))
                .Select(u => u.SecurityStamp).FirstOrDefaultAsync();
        });
        return current != null && current == stamp;
    }

    /// <summary>After a change of the user's data: the next request validates the session again.</summary>
    public void Invalidate(long userId) => cache.Remove(CacheKey(userId));

    private static string CacheKey(long userId) => $"session:{userId}";
}
