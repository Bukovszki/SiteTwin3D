using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;

namespace Api.Tests.Users;

/// <summary>User management by the organization admin and organization management by the platform admin (spec 3).</summary>
public sealed class UserManagementTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    [Fact]
    public async Task Admin_CreatesUser_WithTemporaryPassword()
    {
        var admin = await Api.NewOrganizationAsync();
        var username = TestApi.UniqueCode("k");

        var created = await admin.Http.PostAsJsonAsync("/api/v1/users", new { username, displayName = "Kiss Anna", role = "Editor", password = "Ideiglenes-1" });
        var duplicate = await admin.Http.PostAsJsonAsync("/api/v1/users", new { username, role = "Viewer", password = "Ideiglenes-1" });
        var signedIn = await Api.SignInAsync(admin.OrganizationCode, username, "Ideiglenes-1");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.True((await signedIn.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.GetAsync("/api/v1/sites")).StatusCode);
    }

    [Fact]
    public async Task RoleChangeAndDisabling_EndSessions()
    {
        var admin = await Api.NewOrganizationAsync();
        var user = await Api.NewUserAsync(admin.OrganizationId, admin.OrganizationCode, Roles.Editor);
        var id = (await user.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetInt64();

        var demoted = await admin.Http.PatchJsonAsync($"/api/v1/users/{id}", new { role = "Viewer" });

        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/v1/sites")).StatusCode);
    }

    [Fact]
    public async Task Admin_CannotLockOutThemselves_OrRemoveTheLastAdmin()
    {
        var admin = await Api.NewOrganizationAsync();
        var me = (await admin.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetInt64();
        var second = await Api.NewUserAsync(admin.OrganizationId, admin.OrganizationCode, Roles.OrgAdmin);
        var secondId = (await second.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetInt64();

        var self = await admin.Http.PatchJsonAsync($"/api/v1/users/{me}", new { active = false });
        var demoteSecond = await admin.Http.PatchJsonAsync($"/api/v1/users/{secondId}", new { role = "Editor" });
        var demoteLast = await second.Http.PatchJsonAsync($"/api/v1/users/{me}", new { role = "Editor" });

        Assert.Equal("user.self_lockout", await TestApi.ErrorCode(self));
        Assert.Equal(HttpStatusCode.OK, demoteSecond.StatusCode);
        // The second admin's session ended with the demotion.
        Assert.Equal(HttpStatusCode.Unauthorized, demoteLast.StatusCode);
    }

    [Fact]
    public async Task OnlyOrgAdmin_ManagesUsers_AndOnlyOwnOrganization()
    {
        var admin = await Api.NewOrganizationAsync();
        var editor = await Api.NewUserAsync(admin.OrganizationId, admin.OrganizationCode, Roles.Editor);
        var other = await Api.NewOrganizationAsync();
        var editorId = (await editor.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.Http.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.PatchJsonAsync($"/api/v1/users/{editorId}", new { active = false })).StatusCode);
        Assert.DoesNotContain((await other.Http.GetFromJsonAsync<JsonElement>("/api/v1/users")).EnumerateArray(), u => u.GetProperty("id").GetInt64() == editorId);
        var otherSite = await other.Http.CreateSiteAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.PutAsJsonAsync($"/api/v1/users/{editorId}/site-roles",
            new { roles = new[] { new { siteId = otherSite, role = "Editor" } } })).StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_DisablesOrganization_UsersCannotSignIn()
    {
        var admin = await Api.NewOrganizationAsync();
        var username = (await admin.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("username").GetString();
        var platform = await Api.NewPlatformAdminAsync();

        var disabled = await platform.PatchJsonAsync($"/api/v1/platform/organizations/{admin.OrganizationId}", new { active = false });
        var login = await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { organization = admin.OrganizationCode, username, password = TestApi.Password });

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.False((await TestApi.ReadJson(disabled)).GetProperty("active").GetBoolean());
        Assert.Equal("auth.disabled", await TestApi.ErrorCode(login));
    }
}
