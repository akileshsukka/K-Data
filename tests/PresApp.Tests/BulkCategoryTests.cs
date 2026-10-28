using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>Bulk edit of the dashboard category: preview, apply, skips and audit.</summary>
public sealed class BulkCategoryTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public BulkCategoryTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task Bulk_edit_assigns_and_clears_the_dashboard_category()
    {
        var unitId = await UnitIdAsync("Unit 31");
        var otherUnitId = await UnitIdAsync("Unit 32");
        var hold = (await SaveAsync(unitId, new { label = "Clarification required", section = "Hold" }))[0].GetProperty("id").GetInt32();
        var otherHold = (await SaveAsync(otherUnitId, new { label = "Under maintenance", section = "Hold" }))[0].GetProperty("id").GetInt32();

        var a = await CreateAsync(unitId, "31-V-501");
        var b = await CreateAsync(unitId, "31-V-502");
        var c = await CreateAsync(unitId, "31-V-503");
        var pipingId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.AssetType == PresApp.Domain.Assets.AssetType.Pipeline).Select(d => d.Id).FirstAsync());
        var line = await CreateAsync(unitId, "31-0001-HC-4\"-11010-P", "Pipeline", pipingId);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/assets/{c}/dashboard-category", new { categoryId = hold, expected = (int?)null })).StatusCode);

        // ---- preview: nothing is saved yet
        var request = new { assetIds = new[] { a, b, c, line }, field = "DashboardCategory", value = hold.ToString() };
        var preview = await PostAsync("/api/bulk/preview", request);
        Assert.Equal(2, preview.GetProperty("changed").GetInt32());
        Assert.Equal(1, preview.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, preview.GetProperty("skipped").GetInt32());
        var rowA = preview.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("tag").GetString() == "31-V-501");
        Assert.Equal("Hold Cat1 – Clarification required", rowA.GetProperty("new").GetString());
        Assert.Equal(JsonValueKind.Null, (await Profile(a)).GetProperty("dashboardCategory").ValueKind);

        // ---- apply
        var applied = await PostAsync("/api/bulk/apply", request);
        Assert.Equal(2, applied.GetProperty("changed").GetInt32());
        Assert.Equal("Cat1", (await Profile(a)).GetProperty("dashboardCategory").GetProperty("code").GetString());
        Assert.Equal("Cat1", (await Profile(b)).GetProperty("dashboardCategory").GetProperty("code").GetString());
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("label").GetString() == "Dashboard category"
            && h.GetProperty("newValue").GetString() == "Hold Cat1 – Clarification required" && h.GetProperty("source").GetString() == "Bulk");
        var dashboard = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard");
        Assert.Equal(3, dashboard.GetProperty("totalHold").GetInt32());

        // ---- a category of another unit is skipped on every asset
        var foreign = await PostAsync("/api/bulk/preview", new { assetIds = new[] { a, b }, field = "DashboardCategory", value = otherHold.ToString() });
        Assert.Equal(0, foreign.GetProperty("changed").GetInt32());
        Assert.All(foreign.GetProperty("rows").EnumerateArray(), r => Assert.Equal("The category belongs to another unit", r.GetProperty("reason").GetString()));

        // ---- empty value clears it
        var cleared = await PostAsync("/api/bulk/apply", new { assetIds = new[] { a, b }, field = "DashboardCategory", value = (string?)null });
        Assert.Equal(2, cleared.GetProperty("changed").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await Profile(b)).GetProperty("dashboardCategory").ValueKind);

        // ---- an unknown or inactive category is refused
        var bad = await admin.PostAsJsonAsync("/api/bulk/preview", new { assetIds = new[] { a }, field = "DashboardCategory", value = "999999" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private Task<int> UnitIdAsync(string unit) => app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());

    private async Task<List<JsonElement>> SaveAsync(int unitId, params object[] categories)
    {
        var response = await admin.PutAsJsonAsync($"/api/units/{unitId}/categories", categories);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task<Guid> CreateAsync(int unitId, string tag, string type = "Equipment", int? disciplineId = null)
    {
        disciplineId ??= await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = "Item " + tag });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> PostAsync(string url, object body)
    {
        var response = await admin.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> Profile(Guid id) => admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
}
