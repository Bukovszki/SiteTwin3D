using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Api.Services;
using Api.Services.Auth;
using Api.Services.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints.Platform;

public record OrganizationDto(long Id, string Code, string Name, bool Active, DateTime CreatedAt, int UserCount,
    string? MapmanagerUrl = null, string? MapmanagerTokenRef = null);

public record OrganizationUpdateRequest(string? Name, bool? Active);

/// <summary>MapManager link of an organization: base URL and the REFERENCE of its service token (spec 6.3).</summary>
public record OrganizationMapManagerRequest(string? MapmanagerUrl, string? MapmanagerTokenRef);

public record OrganizationCreateRequest(string Code, string Name, string AdminUsername, string? AdminDisplayName, string AdminPassword);

/// <summary>
/// Organizations (platform admin only, spec 3.1): created together with their first admin (temporary password).
/// The platform admin never sees organization data. Further management (disable, MapManager link) comes in M5.
/// </summary>
public static partial class PlatformEndpoints
{
    [GeneratedRegex("^[a-z][a-z0-9]{1,19}$")]
    private static partial Regex CodeRegex();

    public static RouteGroupBuilder MapPlatformEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/platform/organizations").WithTags("Platform").RequireAuthorization(Policies.Platform);

        group.MapGet("/", List).WithSummary("Organizations with user count");
        group.MapPost("/", Create).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("New organization with its first admin (temporary password). The code (2–20 lowercase letters/digits) is entered at sign-in");
        group.MapPatch("/{id:long}", Update).ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Name and active state; disabling ends the sessions of the organization's users (within 30 s)");
        group.MapPut("/{id:long}/mapmanager", SetMapManager).ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("MapManager base URL and service token reference (the token itself is in the secret store: MapManager:Tokens:<ref>)");
        api.MapPost("/platform/storage/cleanup", Cleanup).WithTags("Platform").RequireAuthorization(Policies.Platform)
            .WithSummary("Object store housekeeping now: unreferenced files after the grace period, expired panorama originals; dryRun (default true) only reports");
        return group;
    }

    public record CleanupRequest(bool? DryRun);

    private static async Task<Ok<CleanupReport>> Cleanup(CleanupRequest request, StorageJanitor janitor, CancellationToken ct) =>
        TypedResults.Ok(await janitor.RunAsync(request.DryRun ?? true, ct));

    private static async Task<Ok<List<OrganizationDto>>> List(AppDbContext db, CancellationToken ct)
    {
        var organizations = await db.Organizations.AsNoTracking().OrderBy(o => o.Code).ToListAsync(ct);
        var counts = await db.Users.IgnoreQueryFilters().Where(u => u.OrganizationId != null)
            .GroupBy(u => u.OrganizationId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return TypedResults.Ok(organizations.Select(o => new OrganizationDto(o.Id, o.Code, o.Name, o.Active, o.CreatedAt,
            counts.FirstOrDefault(c => c.Key == o.Id)?.Count ?? 0, o.MapmanagerUrl, o.MapmanagerTokenRef)).ToList());
    }

    private static async Task<Ok<OrganizationDto>> Update(long id, OrganizationUpdateRequest request, AppDbContext db, CancellationToken ct)
    {
        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw AppException.NotFound("organization.not_found", $"Organization {id} not found.");
        if (request.Name is { } name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw AppException.BadRequest("organization.name_required", "The organization name is required.");
            organization.Name = name.Trim();
        }
        if (request.Active is { } active) organization.Active = active;
        await db.SaveChangesAsync(ct);
        var users = await db.Users.IgnoreQueryFilters().CountAsync(u => u.OrganizationId == id, ct);
        return TypedResults.Ok(new OrganizationDto(organization.Id, organization.Code, organization.Name, organization.Active, organization.CreatedAt, users,
            organization.MapmanagerUrl, organization.MapmanagerTokenRef));
    }

    private static async Task<NoContent> SetMapManager(long id, OrganizationMapManagerRequest request, AppDbContext db, CancellationToken ct)
    {
        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw AppException.NotFound("organization.not_found", $"Organization {id} not found.");
        string? url = null;
        if (!string.IsNullOrWhiteSpace(request.MapmanagerUrl))
        {
            if (!Uri.TryCreate(request.MapmanagerUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw AppException.BadRequest("organization.invalid_mapmanager_url", "The MapManager URL must be an absolute http(s) URL.");
            url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        var tokenRef = string.IsNullOrWhiteSpace(request.MapmanagerTokenRef) ? null : request.MapmanagerTokenRef.Trim();
        if (tokenRef is not null && !tokenRef.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw AppException.BadRequest("organization.invalid_token_ref", "Token reference: letters, digits, _ and - (a key, not the token).");
        organization.MapmanagerUrl = url;
        organization.MapmanagerTokenRef = tokenRef;
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Created<OrganizationDto>> Create(OrganizationCreateRequest request, AppDbContext db, CancellationToken ct)
    {
        var code = (request.Code ?? "").Trim();
        if (!CodeRegex().IsMatch(code) || code == AuthService.PlatformOrganizationCode)
            throw AppException.BadRequest("organization.invalid_code",
                "Code: 2–20 characters, lowercase letter first, then lowercase letters or digits; \"platform\" is reserved.");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw AppException.BadRequest("organization.name_required", "The organization name is required.");
        if (await db.Organizations.AnyAsync(o => o.Code == code, ct))
            throw AppException.Conflict("organization.code_exists", $"An organization with code '{code}' already exists.");

        var organization = new Organization { Code = code, Name = request.Name.Trim(), CreatedAt = DateTime.UtcNow };
        var admin = new AppUser
        {
            Organization = organization,
            Username = (request.AdminUsername ?? "").Trim(),
            UsernameLower = AuthService.Normalize(request.AdminUsername ?? ""),
            DisplayName = string.IsNullOrWhiteSpace(request.AdminDisplayName) ? (request.AdminUsername ?? "").Trim() : request.AdminDisplayName.Trim(),
            Role = Roles.OrgAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            AuthService.ValidateUsername(admin.Username);
            AuthService.SetPassword(admin, request.AdminPassword, temporary: true);
        }
        catch (AuthException ex)
        {
            throw AppException.BadRequest(ex.Code, ex.Message);
        }

        db.Organizations.Add(organization);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/platform/organizations/{organization.Id}",
            new OrganizationDto(organization.Id, organization.Code, organization.Name, organization.Active, organization.CreatedAt, 1));
    }
}
