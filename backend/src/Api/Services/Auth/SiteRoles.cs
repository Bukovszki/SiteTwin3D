using System.Security.Claims;
using System.Text.Json;
using Api.Data;
using Api.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Services.Auth;

/// <summary>
/// Site-level roles (spec 3.2, user_site_role) added to the signed-in principal on every request, from a short cache –
/// so a change by the organization admin takes effect without a new sign-in (after at most the cache time, or at once
/// when the cache is invalidated).
/// <list type="bullet">
/// <item>Organization admin: every site, editing.</item>
/// <item>No site roles: every site of the organization with the user's own role (Editor / Viewer).</item>
/// <item>Site roles: only those sites, each with its role; a Viewer user never edits, whatever the site role says.</item>
/// </list>
/// </summary>
public class SiteRolesTransformation(IServiceProvider services, IMemoryCache cache) : IClaimsTransformation
{
    public const string Claim = "site_roles";
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);

    public static string CacheKey(long userId) => $"site-roles:{userId}";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == Claim)) return principal;
        if (!long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return principal;
        if (principal.FindFirstValue(CurrentUser.OrganizationIdClaim) is null || principal.IsInRole(Roles.OrgAdmin)) return principal;

        var roles = await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTime;
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.UserSiteRoles.IgnoreQueryFilters().AsNoTracking().Where(r => r.UserId == userId)
                .ToDictionaryAsync(r => r.SiteId, r => r.Role);
        });
        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(Claim, JsonSerializer.Serialize(roles)));
        var transformed = principal.Clone();
        transformed.AddIdentity(identity);
        return transformed;
    }
}
