using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class AuthorizationTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    public AuthorizationTests(TestApp app) => this.app = app;

    [Fact]
    public async Task Viewer_cannot_manage_users_but_can_read_catalogs()
    {
        const string email = "viewer@enermech.com";
        await app.AddUserAsync(email, RoleNames.Viewer);
        var client = app.ClientFor(email);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/plants")).StatusCode);
    }

    [Fact]
    public async Task Admin_creates_user_who_can_then_sign_in()
    {
        var admin = app.ClientFor(TestApp.AdminEmail);
        var engineerRole = await app.WithDbAsync(db => db.Roles.Where(r => r.Name == RoleNames.Engineer).Select(r => r.Id).FirstAsync());

        var response = await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "  New.Engineer@EnerMech.com ", displayName = "New Engineer", roleId = engineerRole });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("new.engineer@enermech.com", body.GetProperty("user").GetProperty("email").GetString());
        Assert.False(body.GetProperty("cloudflare").GetProperty("configured").GetBoolean());

        var me = await app.ClientFor("new.engineer@enermech.com").GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(RoleNames.Engineer, me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Deactivated_user_loses_access_immediately()
    {
        const string email = "leaver@enermech.com";
        await app.AddUserAsync(email, RoleNames.FieldTechnician);
        Assert.Equal(HttpStatusCode.OK, (await app.ClientFor(email).GetAsync("/api/me")).StatusCode);
        var id = await app.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());

        var response = await app.ClientFor(TestApp.AdminEmail).PostAsync($"/api/admin/users/{id}/active?active=false", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.ClientFor(email).GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted()
    {
        var admin = await app.WithDbAsync(db => db.Users.Include(u => u.Role).FirstAsync(u => u.Email == TestApp.AdminEmail));
        var viewerRole = await app.WithDbAsync(db => db.Roles.Where(r => r.Name == RoleNames.Viewer).Select(r => r.Id).FirstAsync());
        var otherAdmins = await app.WithDbAsync(db => db.Users.CountAsync(u => u.RoleId == admin.RoleId && u.IsActive && u.Id != admin.Id));
        Assert.Equal(0, otherAdmins);

        var response = await app.ClientFor(TestApp.AdminEmail).PutAsJsonAsync($"/api/admin/users/{admin.Id}",
            new { email = admin.Email, displayName = admin.DisplayName, roleId = viewerRole });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Admin_role_permissions_cannot_be_reduced()
    {
        var adminRole = await app.WithDbAsync(db => db.Roles.Where(r => r.Name == RoleNames.Admin).Select(r => r.Id).FirstAsync());
        var response = await app.ClientFor(TestApp.AdminEmail)
            .PutAsJsonAsync($"/api/admin/roles/{adminRole}/permissions", new { permissions = new[] { Permissions.AssetsView } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
