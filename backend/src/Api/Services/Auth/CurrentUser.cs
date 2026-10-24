using System.Security.Claims;
using System.Text.Json;
using Api.Model;

namespace Api.Services.Auth;

/// <summary>The signed-in user, from the claims of the request.</summary>
public class CurrentUser(IHttpContextAccessor accessor)
{
    public const string OrganizationIdClaim = "org_id";
    public const string OrganizationCodeClaim = "org_code";
    public const string DisplayNameClaim = "display_name";
    public const string StampClaim = "stamp";
    /// <summary>Present while the password must be changed: only the sign-in endpoints are usable then.</summary>
    public const string MustChangePasswordClaim = "must_change_password";

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public long? UserId => long.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>The user's organization; null when signed out or for the platform admin.</summary>
    public long? OrganizationId => long.TryParse(Principal?.FindFirstValue(OrganizationIdClaim), out var id) ? id : null;

    public string? Role => Principal?.FindFirstValue(ClaimTypes.Role);

    public string? DisplayName => Principal?.FindFirstValue(DisplayNameClaim) ?? Principal?.FindFirstValue(ClaimTypes.Name);

    /// <summary>Site roles of the user (site id → Editor | Viewer); empty: no site restriction.</summary>
    public IReadOnlyDictionary<long, string> SiteRoles =>
        _siteRoles ??= Principal?.FindFirstValue(SiteRolesTransformation.Claim) is { } json
            ? JsonSerializer.Deserialize<Dictionary<long, string>>(json) ?? [] : [];
    private IReadOnlyDictionary<long, string>? _siteRoles;

    /// <summary>Sites the user may see; null: every site of the organization (spec 3.2).</summary>
    public IReadOnlyList<long>? ReadableSiteIds => Role == Roles.OrgAdmin || SiteRoles.Count == 0 ? null : SiteRoles.Keys.ToList();

    /// <summary>
    /// Sites the user may edit; null: every site (also for background work without a user, which selects its rows
    /// deliberately); empty: none.
    /// </summary>
    public IReadOnlyList<long>? EditableSiteIds => Role switch
    {
        _ when !IsAuthenticated => null,
        Roles.OrgAdmin => null,
        Roles.Editor when SiteRoles.Count == 0 => null,
        Roles.Editor => SiteRoles.Where(r => r.Value == Roles.Editor).Select(r => r.Key).ToList(),
        _ => [],
    };

    public bool CanEditSite(long siteId) => EditableSiteIds is not { } ids || ids.Contains(siteId);

    /// <summary>403 (site.read_only) when the user may not change data of the site.</summary>
    public void RequireEditableSite(long siteId)
    {
        if (!CanEditSite(siteId))
            throw new AppException(StatusCodes.Status403Forbidden, "site.read_only", "You have read-only access to this site.");
    }
}
