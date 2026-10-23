using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Services.Auth;

namespace Api.Tests.Infrastructure;

/// <summary>A signed-in API client of a test organization.</summary>
public sealed record OrgClient(long OrganizationId, string OrganizationCode, HttpClient Http);

/// <summary>
/// Test data and signed-in clients. Every test creates its own organizations (unique codes), so tests sharing the
/// database do not see each other's data – which is exactly what the organization filter guarantees.
/// </summary>
public sealed class TestApi(ApiFactory factory, PostgisFixture postgis)
{
    public const string Password = "Test-Password-1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string UniqueCode(string prefix = "o") => prefix + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A new organization with a user of the given role (password already changed), signed in.</summary>
    public async Task<OrgClient> NewOrganizationAsync(string role = Roles.OrgAdmin)
    {
        var code = UniqueCode();
        await using var db = postgis.CreateDbContext();
        var organization = new Organization { Code = code, Name = $"Test {code}", CreatedAt = DateTime.UtcNow };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync();
        var client = await NewUserAsync(organization.Id, code, role);
        return client;
    }

    /// <summary>A further user in an existing organization, signed in.</summary>
    public async Task<OrgClient> NewUserAsync(long organizationId, string organizationCode, string role)
    {
        var username = UniqueCode("u");
        await using (var db = postgis.CreateDbContext())
        {
            var user = new AppUser
            {
                OrganizationId = organizationId,
                Username = username,
                UsernameLower = username,
                DisplayName = username,
                Role = role,
                CreatedAt = DateTime.UtcNow,
            };
            AuthService.SetPassword(user, Password, temporary: false);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        return new OrgClient(organizationId, organizationCode, await SignInAsync(organizationCode, username, Password));
    }

    /// <summary>A further platform admin (password already changed), signed in with organization "platform".</summary>
    public async Task<HttpClient> NewPlatformAdminAsync()
    {
        var username = UniqueCode("p");
        await using (var db = postgis.CreateDbContext())
        {
            var user = new AppUser
            {
                Username = username,
                UsernameLower = username,
                DisplayName = username,
                Role = Roles.PlatformAdmin,
                CreatedAt = DateTime.UtcNow,
            };
            AuthService.SetPassword(user, Password, temporary: false);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        return await SignInAsync(AuthService.PlatformOrganizationCode, username, Password);
    }

    public async Task<HttpClient> SignInAsync(string organizationCode, string username, string password)
    {
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/auth/login", new { organization = organizationCode, username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return http;
    }

    public static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).Clone();

    /// <summary>The machine-readable error code of a problem response.</summary>
    public static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await ReadJson(response)).TryGetProperty("code", out var code) ? code.GetString() : null;
}

/// <summary>Typed shortcuts for the hierarchy API.</summary>
public static class HierarchyApi
{
    public static async Task<long> CreateSiteAsync(this HttpClient http, string name = "Site")
    {
        var response = await http.PostAsJsonAsync("/api/v1/sites", new
        {
            name,
            refPoint = new { type = "Point", coordinates = new[] { 19.04, 47.50 } },
            baseHeightM = 150.0,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await TestApi.ReadJson(response)).GetProperty("id").GetInt64();
    }

    public static async Task<long> TypeIdAsync(this HttpClient http, string code)
    {
        var types = await http.GetFromJsonAsync<JsonElement>("/api/v1/node-types");
        return types.EnumerateArray().First(t => t.GetProperty("code").GetString() == code).GetProperty("id").GetInt64();
    }

    public static Task<HttpResponseMessage> PostNodeAsync(this HttpClient http, long siteId, long? parentId, long typeId, string name,
        object? attrs = null, int sortOrder = 0) =>
        http.PostAsJsonAsync("/api/v1/nodes", new { siteId, parentId, typeId, name, sortOrder, attrs });

    public static async Task<JsonElement> CreateNodeAsync(this HttpClient http, long siteId, long? parentId, long typeId, string name,
        object? attrs = null, int sortOrder = 0)
    {
        var response = await http.PostNodeAsync(siteId, parentId, typeId, name, attrs, sortOrder);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await TestApi.ReadJson(response);
    }

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient http, string url, object body) =>
        http.PatchAsJsonAsync(url, body);
}
