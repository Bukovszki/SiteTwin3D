using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;

namespace Api.Tests.Auth;

/// <summary>Sign-in (spec 3.3): fallback policy, temporary password, lockout, platform organization creation.</summary>
public sealed class AuthTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    [Fact]
    public async Task Endpoints_RequireSignIn()
    {
        var anonymous = Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/node-types")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task WrongPassword_IsUnauthorized_AndLocksAfterFiveAttempts()
    {
        var org = await Api.NewOrganizationAsync();
        var me = await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        var username = me.GetProperty("username").GetString();
        var client = Factory.CreateClient();

        HttpResponseMessage response = null!;
        for (var i = 0; i < 5; i++)
            response = await client.PostAsJsonAsync("/api/v1/auth/login", new { organization = org.OrganizationCode, username, password = "wrong-password" });
        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new { organization = org.OrganizationCode, username, password = TestApi.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid_credentials", await TestApi.ErrorCode(response));
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("auth.locked", await TestApi.ErrorCode(locked));
    }

    [Fact]
    public async Task PlatformAdmin_CreatesOrganization_WhoseAdminMustChangePassword()
    {
        var platform = await Api.NewPlatformAdminAsync();
        var code = TestApi.UniqueCode();

        var created = await platform.PostAsJsonAsync("/api/v1/platform/organizations", new
        {
            code, name = "Kitalált Vízmű Zrt.", adminUsername = "admin", adminPassword = "Temporary-1",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var admin = await Api.SignInAsync(code, "admin", "Temporary-1");
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(Roles.OrgAdmin, me.GetProperty("role").GetString());
        // Until the password is changed, only the sign-in endpoints work.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/sites")).StatusCode);

        var changed = await admin.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "Temporary-1", newPassword = "Final-Password-2" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/sites")).StatusCode);
    }

    [Fact]
    public async Task OrganizationUser_CannotManageOrganizations()
    {
        var org = await Api.NewOrganizationAsync();

        var response = await org.Http.PostAsJsonAsync("/api/v1/platform/organizations", new
        {
            code = TestApi.UniqueCode(), name = "X", adminUsername = "admin", adminPassword = "Temporary-1",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_EndsOtherSessions()
    {
        var org = await Api.NewOrganizationAsync();
        var username = (await org.Http.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("username").GetString()!;
        var other = await Api.SignInAsync(org.OrganizationCode, username, TestApi.Password);

        var changed = await org.Http.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = TestApi.Password, newPassword = "Another-Password-3" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await org.Http.GetAsync("/api/v1/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/sites")).StatusCode);
    }
}
