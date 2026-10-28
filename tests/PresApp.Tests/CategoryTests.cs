using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>Dashboard categories (template v4): codes, deactivation, assignment to equipment and dashboard counts.</summary>
public sealed class CategoryTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public CategoryTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task Saved_categories_get_codes_per_section_from_their_label_or_the_next_free_number()
    {
        var unitId = await UnitIdAsync("Unit 31");
        var rows = await SaveAsync(unitId,
            new { label = "Under maintenance", section = "Hold" },
            new { label = "Cat 3: Tube bundle removed", section = "Hold" },
            new { label = "Missing drawing", section = "Hold", recommendation = "Ask Petrofac", note = "Info" },
            new { label = "Removed from site", section = "NotApplicable" });

        Assert.Equal("Cat1,Cat3,Cat2", string.Join(",", rows.Where(r => Section(r) == "Hold").Select(Code)));
        Assert.Equal("Cat1", Code(rows.Single(r => Section(r) == "NotApplicable")));
        var missing = rows.Single(r => Label(r) == "Missing drawing");
        Assert.Equal("Ask Petrofac", missing.GetProperty("recommendation").GetString());
        Assert.Empty(missing.GetProperty("keywords").EnumerateArray());
    }

    [Fact]
    public async Task Equipment_category_drives_the_dashboard_and_a_category_in_use_cannot_be_deactivated()
    {
        var unitId = await UnitIdAsync("Unit 32");
        var rows = await SaveAsync(unitId,
            new { label = "Clarification required", section = "Hold" },
            new { label = "Removed from site", section = "NotApplicable" });
        var hold = rows.Single(r => Section(r) == "Hold");
        var holdId = hold.GetProperty("id").GetInt32();
        var naId = rows.Single(r => Section(r) == "NotApplicable").GetProperty("id").GetInt32();

        var a = await CreateEquipmentAsync(unitId, "32-V-1");
        var b = await CreateEquipmentAsync(unitId, "32-V-2");
        await CreateEquipmentAsync(unitId, "32-V-3");

        var options = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/category-options");
        Assert.Equal(2, options.GetArrayLength());

        var set = await admin.PutAsJsonAsync($"/api/assets/{a}/dashboard-category", new { categoryId = holdId, expected = (int?)null });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var profile = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cat1", profile.GetProperty("dashboardCategory").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/assets/{b}/dashboard-category", new { categoryId = naId, expected = (int?)null })).StatusCode);

        // Someone else changed it meanwhile.
        var stale = await admin.PutAsJsonAsync($"/api/assets/{a}/dashboard-category", new { categoryId = (int?)null, expected = (int?)null });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var dashboard = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard");
        Assert.Equal("Assigned", dashboard.GetProperty("categorySource").GetString());
        Assert.Equal(1, dashboard.GetProperty("totalHold").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("totalNotApplicable").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("availableForPreservation").GetInt32());

        // In use: cannot be deactivated nor moved.
        var deactivate = await admin.PutAsJsonAsync($"/api/units/{unitId}/categories", new object[]
        {
            new { id = holdId, label = "Clarification required", section = "Hold", isActive = false },
            new { id = naId, label = "Removed from site", section = "NotApplicable" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, deactivate.StatusCode);
        Assert.Contains("assigned to 1 equipment", await deactivate.Content.ReadAsStringAsync());

        // Free it, deactivate it, add a new Hold category: the old code is not reused.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/assets/{a}/dashboard-category", new { categoryId = (int?)null, expected = holdId })).StatusCode);
        rows = await SaveAsync(unitId,
            new { id = holdId, label = "Clarification required", section = "Hold", isActive = false },
            new { id = naId, label = "Removed from site", section = "NotApplicable" },
            new { label = "Missing drawing", section = "Hold" });
        Assert.False(rows.Single(r => r.GetProperty("id").GetInt32() == holdId).GetProperty("isActive").GetBoolean());
        Assert.Equal("Cat2", Code(rows.Single(r => Label(r) == "Missing drawing")));
        Assert.Equal(1, (await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/category-options")).EnumerateArray().Count(o => o.GetProperty("section").GetString() == "Hold"));

        // An inactive category cannot be assigned.
        var inactive = await admin.PutAsJsonAsync($"/api/assets/{a}/dashboard-category", new { categoryId = holdId, expected = (int?)null });
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);

        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("label").GetString() == "Dashboard category"
            && h.GetProperty("newValue").GetString() == "Hold Cat1 – Clarification required");
    }

    [Fact]
    public async Task A_category_of_another_unit_is_refused()
    {
        var other = await UnitIdAsync("Unit 41");
        var rows = await SaveAsync(other, new { label = "Under maintenance", section = "Hold" });
        var unitId = await UnitIdAsync("Unit 42");
        var asset = await CreateEquipmentAsync(unitId, "42-V-1");

        var response = await admin.PutAsJsonAsync($"/api/assets/{asset}/dashboard-category",
            new { categoryId = rows[0].GetProperty("id").GetInt32(), expected = (int?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private Task<int> UnitIdAsync(string unit) => app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());

    private async Task<List<JsonElement>> SaveAsync(int unitId, params object[] categories)
    {
        var response = await admin.PutAsJsonAsync($"/api/units/{unitId}/categories", categories);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task<Guid> CreateEquipmentAsync(int unitId, string tag)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Item " + tag });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static string? Code(JsonElement row) => row.GetProperty("code").GetString();
    private static string? Label(JsonElement row) => row.GetProperty("label").GetString();
    private static string? Section(JsonElement row) => row.GetProperty("section").GetString();
}
