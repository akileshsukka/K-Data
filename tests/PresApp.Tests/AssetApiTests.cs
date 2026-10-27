using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class AssetApiTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public AssetApiTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<JsonElement> CreateAsync(string tag, string unit = "Unit 39", string discipline = "Static")
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Test vessel" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Change(string field, string? from, string? to) => new { field, from, to };

    [Fact]
    public async Task New_equipment_gets_the_next_id_of_its_discipline_and_unit()
    {
        var a = await CreateAsync("42-V-901", "Unit 42");
        var b = await CreateAsync("42-V-902", "Unit 42");

        Assert.Matches(@"^ST-EMP-042-\d{3}$", a.GetProperty("code").GetString());
        var na = int.Parse(a.GetProperty("code").GetString()![^3..]);
        var nb = int.Parse(b.GetProperty("code").GetString()![^3..]);
        Assert.Equal(na + 1, nb);
        Assert.Equal("New", a.GetProperty("currentGate").GetString());
    }

    [Fact]
    public async Task Duplicate_active_tag_in_the_same_unit_is_refused()
    {
        await CreateAsync("39-V-777");
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag = "39-V-777" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Completing_G4_sets_N2_date_proposes_current_gate_and_opens_documentation()
    {
        var asset = await CreateAsync("39-V-101");
        var id = asset.GetProperty("id").GetString();

        var response = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { Change("G1Status", null, "completed"), Change("G4Status", null, "Completed") } });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var values = body.GetProperty("asset").GetProperty("values");
        Assert.Equal("Completed", values.GetProperty("G1Status").GetString());
        Assert.NotNull(values.GetProperty("N2BlanketedDate").GetString());
        Assert.NotNull(values.GetProperty("LastFieldActionDate").GetString());
        Assert.Equal("G4", body.GetProperty("proposals").GetProperty("currentGate").GetString());
        Assert.Equal("New", body.GetProperty("asset").GetProperty("currentGate").GetString());
        var docs = body.GetProperty("asset").GetProperty("gateDocuments").EnumerateArray().Select(d => (d.GetProperty("gate").GetInt32(), d.GetProperty("status").GetString())).ToList();
        Assert.Equal([(1, "Pending"), (4, "Pending")], docs);

        var approve = await admin.PostAsJsonAsync($"/api/assets/{id}/current-gate", new { value = "G4", expected = "New" });
        approve.EnsureSuccessStatusCode();
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("field").GetString() == "CurrentGate" && h.GetProperty("newValue").GetString() == "G4");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("field").GetString() == "G4Status" && h.GetProperty("by").GetString() == TestApp.AdminEmail);
    }

    [Fact]
    public async Task Stale_value_is_reported_as_a_conflict_and_nothing_is_saved()
    {
        var id = (await CreateAsync("39-V-202")).GetProperty("id").GetString();
        (await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { Change("RemarksAdnoc", null, "First remark") } })).EnsureSuccessStatusCode();

        var response = await admin.PatchAsJsonAsync($"/api/assets/{id}", new
        {
            changes = new[] { Change("RemarksAdnoc", null, "Second remark"), Change("OemName", null, "Atlas") }
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var profile = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
        Assert.Equal("First remark", profile.GetProperty("values").GetProperty("RemarksAdnoc").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("values").GetProperty("OemName").ValueKind);
    }

    [Fact]
    public async Task Field_technician_can_edit_gates_but_not_client_remarks()
    {
        const string tech = "tech@enermech.com";
        await app.AddUserAsync(tech, RoleNames.FieldTechnician);
        var id = (await CreateAsync("39-V-303")).GetProperty("id").GetString();
        var client = app.ClientFor(tech);

        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { Change("G2Status", null, "Ongoing") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { Change("RemarksAdnoc", null, "x") } })).StatusCode);
    }

    [Fact]
    public async Task Values_outside_the_list_are_refused_and_NA_becomes_Not_Applicable()
    {
        var id = (await CreateAsync("39-V-404")).GetProperty("id").GetString();

        var bad = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { Change("G1Status", null, "Maybe") } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { Change("G1Status", null, "N/A") } });
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Not Applicable", body.GetProperty("asset").GetProperty("values").GetProperty("G1Status").GetString());
    }

    [Fact]
    public async Task Latest_reading_is_the_one_measured_last_not_the_one_entered_last()
    {
        var id = (await CreateAsync("39-V-505")).GetProperty("id").GetString();
        var types = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/measurements");
        var n2 = types.EnumerateArray().First(t => t.GetProperty("key").GetString() == "G4N2BlanketBarg").GetProperty("id").GetInt32();
        var now = DateTime.UtcNow;

        (await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = now.AddHours(-1), values = new[] { new { measurementTypeId = n2, value = 3.5m } } })).EnsureSuccessStatusCode();
        var late = await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = now.AddDays(-3), values = new[] { new { measurementTypeId = n2, value = 1.2m } } });
        late.EnsureSuccessStatusCode();

        var profile = (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("asset");
        Assert.Equal("3.5", profile.GetProperty("values").GetProperty("G4N2BlanketBarg").GetString());
        var readings = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/readings");
        Assert.Equal(2, readings.GetArrayLength());

        var outOfRange = await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = now, values = new[] { new { measurementTypeId = n2, value = 150m } } });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
    }

    [Fact]
    public async Task Marking_preserved_moves_later_readings_to_POST()
    {
        var id = (await CreateAsync("39-V-606")).GetProperty("id").GetString();
        var types = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/measurements");
        var p = types.EnumerateArray().First(t => t.GetProperty("key").GetString() == "PressureBarg").GetProperty("id").GetInt32();
        var now = DateTime.UtcNow;
        await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = now.AddDays(-10), values = new[] { new { measurementTypeId = p, value = 2m } } });
        await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = now.AddHours(-1), values = new[] { new { measurementTypeId = p, value = 3m } } });

        (await admin.PostAsJsonAsync($"/api/assets/{id}/preserved", new { preservedOn = DateOnly.FromDateTime(now.AddDays(-2)) })).EnsureSuccessStatusCode();

        var phases = (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/readings")).EnumerateArray().Select(r => r.GetProperty("phase").GetString()).ToList();
        Assert.Equal(["Post", "During"], phases);
    }

    [Fact]
    public async Task Bulk_edit_previews_then_applies_and_skips_inactive_assets()
    {
        var a = (await CreateAsync("39-P-701")).GetProperty("id").GetString();
        var b = (await CreateAsync("39-P-702")).GetProperty("id").GetString();
        var c = (await CreateAsync("39-P-703")).GetProperty("id").GetString();
        (await admin.PostAsync($"/api/assets/{c}/active?active=false", null)).EnsureSuccessStatusCode();
        (await admin.PatchAsJsonAsync($"/api/assets/{b}", new { changes = new[] { Change("ActionBy", null, "Petrofac") } })).EnsureSuccessStatusCode();

        var request = new { assetIds = new[] { a, b, c }, field = "ActionBy", value = "petrofac" };
        var preview = await (await admin.PostAsJsonAsync("/api/bulk/preview", request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, preview.GetProperty("changed").GetInt32());
        Assert.Equal(1, preview.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, preview.GetProperty("skipped").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}")).GetProperty("values").GetProperty("ActionBy").ValueKind);

        (await admin.PostAsJsonAsync("/api/bulk/apply", request)).EnsureSuccessStatusCode();
        var profile = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}");
        Assert.Equal("Petrofac", profile.GetProperty("values").GetProperty("ActionBy").GetString());
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("field").GetString() == "ActionBy" && h.GetProperty("source").GetString() == "Bulk");
    }

    [Fact]
    public async Task Register_and_search_find_the_asset()
    {
        var created = await CreateAsync("41-K-888", "Unit 41");
        var unitId = created.GetProperty("unit").GetProperty("id").GetInt32();

        var rows = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets");
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("tag").GetString() == "41-K-888");

        var hits = await admin.GetFromJsonAsync<JsonElement>("/api/assets/search?q=k-88");
        Assert.Contains(hits.EnumerateArray(), h => h.GetProperty("tag").GetString() == "41-K-888");
    }
}
