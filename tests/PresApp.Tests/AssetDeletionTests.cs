using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class AssetDeletionTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public AssetDeletionTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<(Guid Id, string Code)> CreateAsync(string tag, string unit = "Unit 32")
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Loaded by mistake" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("code").GetString()!);
    }

    [Fact]
    public async Task Admin_deletes_an_asset_with_the_typed_word_and_its_id_is_given_again()
    {
        var (id, code) = await CreateAsync("32-V-DEL1");
        // Gate documentation rows and a group membership go with it.
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field = "G1Status", from = (string?)null, to = "Completed" } } });
        patch.EnsureSuccessStatusCode();

        var preview = await (await admin.PostAsJsonAsync("/api/assets/delete/preview", new { assetIds = new[] { id } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, preview.GetProperty("deletable").GetInt32());
        Assert.Equal("DELETE", preview.GetProperty("confirmationWord").GetString());

        var unconfirmed = await admin.PostAsJsonAsync("/api/assets/delete", new { assetIds = new[] { id }, confirmation = "delete it" });
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.True(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Id == id)));

        var deleted = await admin.PostAsJsonAsync("/api/assets/delete", new { assetIds = new[] { id }, confirmation = "DELETE" });
        deleted.EnsureSuccessStatusCode();
        Assert.Equal(1, (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32());

        Assert.False(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Id == id)));
        Assert.False(await app.WithDbAsync(db => db.GateDocuments.AnyAsync(g => g.AssetId == id)));
        Assert.True(await app.WithDbAsync(db => db.ChangeLogs.AnyAsync(c => c.AssetId == id && c.Field == "(deleted)" && c.OldValue!.StartsWith(code))));

        // The deleted ID was the last one given: the next asset gets it again.
        var (_, next) = await CreateAsync("32-V-DEL2");
        Assert.Equal(code, next);
    }

    [Fact]
    public async Task An_asset_already_in_Simpro_is_not_deleted()
    {
        var (kept, _) = await CreateAsync("32-V-DEL3");
        var (gone, _) = await CreateAsync("32-V-DEL4");
        await app.WithDbAsync(db => db.Assets.Where(a => a.Id == kept).ExecuteUpdateAsync(s => s.SetProperty(a => a.SimproFlangeId, a => a.Id)));

        var response = await admin.PostAsJsonAsync("/api/assets/delete", new { assetIds = new[] { kept, gone }, confirmation = "DELETE" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("deleted").GetInt32());
        var notDeleted = Assert.Single(body.GetProperty("notDeleted").EnumerateArray());
        Assert.Equal(kept, notDeleted.GetProperty("id").GetGuid());
        Assert.Contains("Simpro", notDeleted.GetProperty("blocked").GetString());
        Assert.True(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Id == kept)));
        Assert.False(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Id == gone)));
    }

    [Fact]
    public async Task Only_a_role_with_the_delete_permission_can_delete()
    {
        var (id, _) = await CreateAsync("32-V-DEL5");
        const string email = "engineer.delete@enermech.com";
        await app.AddUserAsync(email, RoleNames.Engineer);

        var response = await app.ClientFor(email).PostAsJsonAsync("/api/assets/delete", new { assetIds = new[] { id }, confirmation = "DELETE" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Id == id)));
    }
}
