namespace Api.Model;

/// <summary>
/// Every organization-owned row carries its organization (denormalized). The global query filter of
/// <see cref="Data.AppDbContext"/> restricts these rows to the signed-in user's organization, and composite foreign
/// keys (parent id + organization id) guarantee in the database that a row never points into another organization.
/// </summary>
public interface IOrganizationOwned
{
    long OrganizationId { get; set; }
}

/// <summary>Tenant (spec 3.1). Users, sites and everything below them belong to exactly one organization.</summary>
public class Organization
{
    public long Id { get; set; }
    /// <summary>Organization code entered at sign-in (lowercase).</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    /// <summary>MapManager base URL (spec 6.3).</summary>
    public string? MapmanagerUrl { get; set; }
    /// <summary>Reference of the service token in the secret store – never the token itself.</summary>
    public string? MapmanagerTokenRef { get; set; }
    /// <summary>
    /// Keeping the unblurred panorama originals (FR-P8): null = deleted after approval (default), otherwise kept in
    /// the restricted store for this many days.
    /// </summary>
    public int? PanoramaOriginalRetentionDays { get; set; }
    public DateTime CreatedAt { get; set; }
}
