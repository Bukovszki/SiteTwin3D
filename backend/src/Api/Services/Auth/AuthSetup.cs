using System.Security.Claims;
using Api.Data;
using Api.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Api.Services.Auth;

/// <summary>Authorization policies; endpoint groups declare with these who may use them.</summary>
public static class Policies
{
    /// <summary>Any user of an organization (read).</summary>
    public const string Member = "Member";
    /// <summary>Organization admin or editor (hierarchy, panoramas, anchors, drawings).</summary>
    public const string Editor = "Editor";
    /// <summary>Organization admin (users, sites, node types).</summary>
    public const string OrgAdmin = "OrgAdmin";
    /// <summary>Platform admin: organizations only, no organization data.</summary>
    public const string Platform = "Platform";
    /// <summary>Signed in, even before the mandatory password change (sign-in endpoints).</summary>
    public const string SignedIn = "SignedIn";
}

public class PlatformOptions
{
    public const string Section = "Platform";
    public string AdminUsername { get; set; } = "admin";
    public string? AdminPassword { get; set; }
}

public static class AuthSetup
{
    public const string CookieName = "app.auth";

    /// <summary>
    /// Cookie authentication: HttpOnly, SameSite=Strict (a cross-site request does not carry it: CSRF protection),
    /// 8-hour sliding session. As an API it answers 401/403 instead of redirecting. Every endpoint requires
    /// sign-in by default (FallbackPolicy); public ones are marked AllowAnonymous.
    /// </summary>
    public static IServiceCollection AddAppAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.Section));
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<CurrentUser>();
        services.AddScoped<AuthService>();
        // Site-level roles on the principal of every request (spec 3.2).
        services.AddSingleton<IClaimsTransformation, SiteRolesTransformation>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = async ctx =>
            {
                var id = long.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : 0;
                var auth = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
                if (id == 0 || !await auth.IsSessionValidAsync(id, ctx.Principal?.FindFirstValue(CurrentUser.StampClaim)))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });

        services.AddAuthorization(o =>
        {
            static bool PasswordOk(AuthorizationHandlerContext c) => !c.User.HasClaim(x => x.Type == CurrentUser.MustChangePasswordClaim);
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(Policies.SignedIn, p => p.RequireAuthenticatedUser());
            o.AddPolicy(Policies.Member, p => p.RequireAuthenticatedUser().RequireRole(Roles.OrganizationRoles)
                .RequireClaim(CurrentUser.OrganizationIdClaim).RequireAssertion(PasswordOk));
            o.AddPolicy(Policies.Editor, p => p.RequireAuthenticatedUser().RequireRole(Roles.EditorRoles)
                .RequireClaim(CurrentUser.OrganizationIdClaim).RequireAssertion(PasswordOk));
            o.AddPolicy(Policies.OrgAdmin, p => p.RequireAuthenticatedUser().RequireRole(Roles.OrgAdmin)
                .RequireClaim(CurrentUser.OrganizationIdClaim).RequireAssertion(PasswordOk));
            o.AddPolicy(Policies.Platform, p => p.RequireAuthenticatedUser().RequireRole(Roles.PlatformAdmin).RequireAssertion(PasswordOk));
        });
        return services;
    }

    /// <summary>
    /// Creates the first platform admin from configuration (Platform:AdminUsername / AdminPassword) if there is none.
    /// Created with a temporary password: it must be changed at the first sign-in.
    /// </summary>
    public static async Task SeedPlatformAdminAsync(this WebApplication app)
    {
        var options = app.Configuration.GetSection(PlatformOptions.Section).Get<PlatformOptions>() ?? new();
        if (string.IsNullOrEmpty(options.AdminPassword)) return;

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Role == Roles.PlatformAdmin)) return;

        var user = new AppUser
        {
            Username = options.AdminUsername,
            UsernameLower = AuthService.Normalize(options.AdminUsername),
            DisplayName = "Platform admin",
            Role = Roles.PlatformAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        AuthService.SetPassword(user, options.AdminPassword, temporary: true);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Platform admin created: {User} (sign in with organization \"{Code}\"; password change on first sign-in)",
            user.Username, AuthService.PlatformOrganizationCode);
    }
}
