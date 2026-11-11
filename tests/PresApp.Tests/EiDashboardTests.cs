using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>
/// The E&amp;I dashboard (E1–E8 steps, Not Applicable left out of the %) and the separate E&amp;I category catalogue
/// (codes CatE1…), which never mixes with the mechanical one.
/// </summary>
public sealed class EiDashboardTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public EiDashboardTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task Each_catalogue_has_its_own_codes_and_options_and_never_accepts_the_other_type()
    {
        var unitId = await UnitIdAsync("Unit 31");
        var mechanical = await SaveAsync(unitId, null, new { label = "Under inspection", section = "Hold" });
        var ei = await SaveAsync(unitId, "ei",
            new { label = "Under inspection", section = "Hold" },          // same description: allowed in the other list
            new { label = "Motor removed", section = "Hold" },
            new { label = "CatE 5: Cable disconnected", section = "NotApplicable" });

        Assert.Equal("Cat1", Code(mechanical.Single()));
        Assert.Equal("mechanical", mechanical.Single().GetProperty("scope").GetString());
        Assert.Equal("CatE1,CatE2", string.Join(",", ei.Where(r => Section(r) == "Hold").Select(Code)));
        Assert.Equal("CatE5", Code(ei.Single(r => Section(r) == "NotApplicable")));
        Assert.All(ei, r => Assert.Equal("ei", r.GetProperty("scope").GetString()));

        // Saving one list never touches the other.
        Assert.Single(await admin.GetFromJsonAsync<List<JsonElement>>($"/api/units/{unitId}/categories") ?? []);
        Assert.Equal(3, (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/units/{unitId}/category-options?scope=ei"))!.Count);
        Assert.Single((await admin.GetFromJsonAsync<List<JsonElement>>($"/api/units/{unitId}/category-options"))!);

        var motor = await CreateEiAsync(unitId, "31-EM-1");
        var vessel = await CreateEquipmentAsync(unitId, "31-V-1");
        var motorRemoved = ei.Single(r => Label(r) == "Motor removed").GetProperty("id").GetInt32();
        var mechanicalId = mechanical.Single().GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.BadRequest, (await SetCategoryAsync(motor, mechanicalId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetCategoryAsync(vessel, motorRemoved)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetCategoryAsync(motor, motorRemoved)).StatusCode);

        // Bulk: the vessel is skipped, the motor already has it.
        var bulk = await admin.PostAsJsonAsync("/api/bulk/preview", new { field = "DashboardCategory", value = motorRemoved.ToString(), assetIds = new[] { motor, vessel } });
        Assert.True(bulk.IsSuccessStatusCode, await bulk.Content.ReadAsStringAsync());
        var rows = (await bulk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("outcome").GetString() == "Skipped" && r.GetProperty("reason").GetString()!.Contains("E&I category"));

        // The E&I dashboard counts it; the mechanical one does not.
        var eiDashboard = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard?scope=ei");
        Assert.Equal(1, eiDashboard.GetProperty("totalHold").GetInt32());
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard")).GetProperty("totalHold").GetInt32());
        var card = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard/cards/category-{motorRemoved}");
        Assert.Equal(motor, card.GetProperty("assetIds")[0].GetGuid());
    }

    [Fact]
    public async Task The_ei_dashboard_counts_steps_leaves_not_applicable_out_and_preserves_when_every_step_is_closed()
    {
        var unitId = await UnitIdAsync("Unit 32");
        var done = await CreateEiAsync(unitId, "32-EM-1");
        var partial = await CreateEiAsync(unitId, "32-EM-2");
        var instrument = await CreateEiAsync(unitId, "32-PT-1", "E&I");
        await CreateEquipmentAsync(unitId, "32-V-1");

        // 32-EM-1: E1–E6 and E8 completed, E7 not applicable → preserved.
        await PatchAsync(done, [.. Enumerable.Range(1, 8).Select(g => ($"G{g}Status", g == 7 ? "N/A" : "Completed"))]);
        // 32-EM-2: E1 completed, E2 ongoing, E3 hold.
        await PatchAsync(partial, [("G1Status", "Completed"), ("G2Status", "Ongoing"), ("G3Status", "Hold")]);
        // 32-PT-1: no motor steps apply except E1, E4 and E8, all three completed → preserved.
        await PatchAsync(instrument, [.. Enumerable.Range(1, 8).Select(g => ($"G{g}Status", g is 1 or 4 or 8 ? "Completed" : "N/A"))]);

        var d = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard?scope=ei");
        Assert.Equal("ei", d.GetProperty("scope").GetString());
        Assert.Equal(3, d.GetProperty("totalTags").GetInt32());
        Assert.Equal(2, d.GetProperty("preserved").GetInt32());

        var activities = d.GetProperty("activities").EnumerateArray().ToList();
        Assert.Equal("Walkdown,E1,E2,E3,E4,E5,E6,E7,E8", string.Join(",", activities.Select(a => a.GetProperty("code").GetString())));
        Assert.Equal("E1 - Tag & Nameplate Check", activities[1].GetProperty("label").GetString());
        Assert.Equal("E7 - IR / Winding Test", activities[7].GetProperty("label").GetString());

        // E1: all three completed → 100 %.
        Assert.Equal(3, activities[1].GetProperty("completed").GetInt32());
        Assert.Equal(100m, activities[1].GetProperty("completionPct").GetDecimal());
        // E2: done, ongoing, N/A → 1 of 2 applicable = 50 %.
        Assert.Equal(1, activities[2].GetProperty("pending").GetInt32());
        Assert.Equal(1, activities[2].GetProperty("notApplicable").GetInt32());
        Assert.Equal(50m, activities[2].GetProperty("completionPct").GetDecimal());
        // E3: done, hold, N/A.
        Assert.Equal(1, activities[3].GetProperty("hold").GetInt32());
        // E7: two N/A, one not started → 0 % of 1 applicable.
        Assert.Equal(2, activities[7].GetProperty("notApplicable").GetInt32());
        Assert.Equal(1, activities[7].GetProperty("other").GetInt32());

        var disciplines = d.GetProperty("disciplines").EnumerateArray().ToList();
        Assert.Equal("Electrical,Instrumentation,E&I", string.Join(",", disciplines.Select(r => r.GetProperty("discipline").GetString())));
        var instrumentation = disciplines[2];   // the E&I discipline
        Assert.Equal(1, instrumentation.GetProperty("total").GetInt32());
        // Walkdown not started + E1, E4, E8 done; five steps N/A → 3 of 4 applicable.
        Assert.Equal(75m, instrumentation.GetProperty("progressPct").GetDecimal());
        Assert.Equal(9, instrumentation.GetProperty("done").GetArrayLength());

        // The cards open exactly those items in the E&I register.
        var preserved = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard/cards/ei-preserved");
        Assert.Equal(new[] { done, instrument }.OrderBy(x => x), preserved.GetProperty("assetIds").EnumerateArray().Select(x => x.GetGuid()).OrderBy(x => x));

        // The mechanical dashboard never counts E&I items.
        var mechanical = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard");
        Assert.Equal("mechanical", mechanical.GetProperty("scope").GetString());
        Assert.Equal(1, mechanical.GetProperty("totalTags").GetInt32());
        Assert.Equal(5, mechanical.GetProperty("activities").GetArrayLength());
        Assert.Equal(3, mechanical.GetProperty("electricalInstrumentCount").GetInt32());
    }

    [Fact]
    public async Task The_ei_register_says_which_items_have_a_complete_e7_test()
    {
        var unitId = await UnitIdAsync("Unit 41");
        var tested = await CreateEiAsync(unitId, "41-EM-1");
        var partial = await CreateEiAsync(unitId, "41-EM-2");
        var untested = await CreateEiAsync(unitId, "41-EM-3");
        var notDone = await CreateEiAsync(unitId, "41-EM-4");
        // A complete test sheet on 41-EM-1 and 41-EM-4, one value only on 41-EM-2, nothing on 41-EM-3.
        var full = ElectricalTestSpec.Measures.Where(m => m.Required).ToDictionary(m => m.Key, _ => (decimal?)1.5m);
        foreach (var (id, values) in new[] { (tested, full), (partial, new Dictionary<string, decimal?> { ["WrU1U2"] = 0.21m }), (notDone, full) })
        {
            var mark = await admin.PutAsJsonAsync($"/api/assets/{id}/electrical-test", new { required = true, testedOn = (string?)null, values, expected = (DateTime?)null });
            Assert.True(mark.IsSuccessStatusCode, await mark.Content.ReadAsStringAsync());
        }
        // E7 completed on the first three; 41-EM-4 has its test but E7 is still open.
        foreach (var id in new[] { tested, partial, untested }) await PatchAsync(id, [("G7Status", "Completed")]);

        // The register filter "E7 completed, test missing" works on these flags (+ E7 Completed + discipline Electrical).
        var rows = (await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=ElectricalInstrument"))
            .EnumerateArray().ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("e7Tested").GetBoolean());
        Assert.True(rows[tested]);
        Assert.False(rows[partial]);
        Assert.False(rows[untested]);
        Assert.True(rows[notDone]);
        var equipment = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Equipment");
        Assert.All(equipment.EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("e7Tested").ValueKind));
    }

    // ------------------------------------------------------------------ helpers

    private Task<int> UnitIdAsync(string unit) => app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());

    private async Task<List<JsonElement>> SaveAsync(int unitId, string? scope, params object[] categories)
    {
        var response = await admin.PutAsJsonAsync($"/api/units/{unitId}/categories{(scope is null ? "" : $"?scope={scope}")}", categories);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private Task<HttpResponseMessage> SetCategoryAsync(Guid asset, int categoryId) =>
        admin.PutAsJsonAsync($"/api/assets/{asset}/dashboard-category", new { categoryId, expected = (int?)null });

    private async Task PatchAsync(Guid id, (string Field, string To)[] changes)
    {
        var response = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = changes.Select(c => new { field = c.Field, from = (string?)null, to = c.To }).ToArray() });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private Task<Guid> CreateEiAsync(int unitId, string tag, string discipline = "Electrical") => CreateAsync(unitId, tag, "ElectricalInstrument", discipline);

    private Task<Guid> CreateEquipmentAsync(int unitId, string tag) => CreateAsync(unitId, tag, "Equipment", "Static");

    private async Task<Guid> CreateAsync(int unitId, string tag, string type, string discipline)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = "Item " + tag });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static string? Code(JsonElement row) => row.GetProperty("code").GetString();
    private static string? Label(JsonElement row) => row.GetProperty("label").GetString();
    private static string? Section(JsonElement row) => row.GetProperty("section").GetString();
}
