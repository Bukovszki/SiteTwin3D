namespace Api.Model;

/// <summary>Roles (spec 3.2). The platform admin belongs to no organization and cannot see organization data.</summary>
public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";
    /// <summary>Everything inside the organization: users, sites, node types, drawing layers.</summary>
    public const string OrgAdmin = "OrgAdmin";
    /// <summary>Edits hierarchy, panoramas, anchors, drawings, models.</summary>
    public const string Editor = "Editor";
    /// <summary>Read only.</summary>
    public const string Viewer = "Viewer";

    public static readonly string[] OrganizationRoles = [OrgAdmin, Editor, Viewer];
    public static readonly string[] EditorRoles = [OrgAdmin, Editor];
    /// <summary>Roles assignable per site (user_site_role).</summary>
    public static readonly string[] SiteRoles = [Editor, Viewer];
}

public class AppUser
{
    public long Id { get; set; }
    /// <summary>Null: platform admin.</summary>
    public long? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string Username { get; set; } = "";
    /// <summary>Lowercase username for uniqueness and sign-in.</summary>
    public string UsernameLower { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public bool Active { get; set; } = true;
    /// <summary>Temporary password (after creation or reset): must be changed at the next sign-in.</summary>
    public bool MustChangePassword { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    /// <summary>Changes on password, role or status change: earlier sessions become invalid.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
}

/// <summary>Site-level role of a user (spec 3.2). Enforced from M5 on.</summary>
public class UserSiteRole : IOrganizationOwned
{
    public long UserId { get; set; }
    public long SiteId { get; set; }
    public long OrganizationId { get; set; }
    public string Role { get; set; } = Roles.Viewer;
}
