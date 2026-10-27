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

    [Fact]
    public async Task Only_roles_with_the_permission_see_the_Excel_page_and_where_changes_came_from()
    {
        const string email = "engineer-src@enermech.com";
        await app.AddUserAsync(email, RoleNames.Engineer);
        var engineer = app.ClientFor(email);
        var admin = app.ClientFor(TestApp.AdminEmail);
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var created = await (await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag = "39-SRC-1" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var forAdmin = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history");
        Assert.All(forAdmin.EnumerateArray(), h => Assert.Equal(JsonValueKind.String, h.GetProperty("source").ValueKind));
        var forEngineer = await engineer.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history");
        Assert.NotEmpty(forEngineer.EnumerateArray());
        Assert.All(forEngineer.EnumerateArray(), h => Assert.Equal(JsonValueKind.Null, h.GetProperty("source").ValueKind));

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync($"/api/units/{unitId}/excel")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/units/{unitId}/excel")).StatusCode);
    }
}
