using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class MonitoringTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public MonitoringTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<(int UnitId, Guid Id)> CreateAsync(string tag, string unit = "Unit 31")
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var r = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (unitId, Guid.Parse((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!));
    }

    private Task<int> N2Async() => app.WithDbAsync(db => db.MeasurementTypes.Where(t => t.Key == "G4N2BlanketBarg").Select(t => t.Id).FirstAsync());

    private static JsonElement Row(JsonElement view, Guid id) => view.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Preserved_assets_are_due_when_the_last_N2_reading_is_older_than_the_interval()
    {
        var (unitId, a) = await CreateAsync("31-MON-1");
        var (_, b) = await CreateAsync("31-MON-2");
        var (_, c) = await CreateAsync("31-MON-3");
        var n2 = await N2Async();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var id in new[] { a, b })
            (await admin.PostAsJsonAsync($"/api/assets/{id}/preserved", new { preservedOn = today.AddDays(-20) })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/assets/{a}/readings", new { measuredAtUtc = DateTime.UtcNow.AddDays(-10), values = new[] { new { measurementTypeId = n2, value = 0.8m } } })).EnsureSuccessStatusCode();

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/monitoring?unitId={unitId}");
        Assert.Equal("Overdue", Row(view, a).GetProperty("status").GetString());
        Assert.Equal("Post", Row(view, a).GetProperty("phase").GetString());
        Assert.Equal("No reading since preserved", Row(view, b).GetProperty("status").GetString());
        Assert.Equal("During preservation", Row(view, c).GetProperty("status").GetString());

        // A round saves both at once; with a minimum of 1 barg the 0.5 reading is flagged.
        (await admin.PutAsJsonAsync("/api/settings/monitoring", new { intervalDays = 7, minN2Barg = 1.0m })).EnsureSuccessStatusCode();
        var round = await admin.PostAsJsonAsync("/api/monitoring/rounds", new
        {
            measuredAtUtc = DateTime.UtcNow.AddMinutes(-5),
            rows = new[] { new { assetId = a, values = new[] { new { measurementTypeId = n2, value = 1.2m } } }, new { assetId = b, values = new[] { new { measurementTypeId = n2, value = 0.5m } } } }
        });
        round.EnsureSuccessStatusCode();
        Assert.Equal(2, (await round.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("readings").GetInt32());
        view = await admin.GetFromJsonAsync<JsonElement>($"/api/monitoring?unitId={unitId}");
        Assert.Equal("OK", Row(view, a).GetProperty("status").GetString());
        Assert.False(Row(view, a).GetProperty("lowN2").GetBoolean());
        Assert.True(Row(view, b).GetProperty("lowN2").GetBoolean());
        (await admin.PutAsJsonAsync("/api/settings/monitoring", new { intervalDays = 7, minN2Barg = (decimal?)null })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_round_with_one_bad_value_saves_nothing()
    {
        var (_, a) = await CreateAsync("31-MON-4");
        var (_, b) = await CreateAsync("31-MON-5");
        var o2 = await app.WithDbAsync(db => db.MeasurementTypes.Where(t => t.Key == "G3O2Pct").Select(t => t.Id).FirstAsync());
        var round = await admin.PostAsJsonAsync("/api/monitoring/rounds", new
        {
            measuredAtUtc = DateTime.UtcNow.AddMinutes(-5),
            rows = new[] { new { assetId = a, values = new[] { new { measurementTypeId = o2, value = 2m } } }, new { assetId = b, values = new[] { new { measurementTypeId = o2, value = 150m } } } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, round.StatusCode);
        Assert.Contains("31-MON-5", await round.Content.ReadAsStringAsync());
        Assert.False(await app.WithDbAsync(db => db.Readings.AnyAsync(r => r.AssetId == a)));
    }

    [Fact]
    public async Task Workpacks_get_a_code_group_assets_of_the_unit_and_show_progress()
    {
        var (unitId, a) = await CreateAsync("32-WP-1", "Unit 32");
        var (_, b) = await CreateAsync("32-WP-2", "Unit 32");
        var (_, other) = await CreateAsync("41-WP-9", "Unit 41");
        var types = await admin.GetFromJsonAsync<JsonElement>("/api/group-types");
        var wp = types.EnumerateArray().First(t => t.GetProperty("name").GetString() == "Workpack");
        Assert.Equal("WP", wp.GetProperty("prefix").GetString());

        var created = await admin.PostAsJsonAsync("/api/groups", new { typeId = wp.GetProperty("id").GetInt32(), name = "Air coolers", unitId });
        created.EnsureSuccessStatusCode();
        var group = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("group");
        Assert.Equal("WP-032-001", group.GetProperty("code").GetString());
        var id = group.GetProperty("id").GetInt32();

        (await admin.PatchAsJsonAsync($"/api/assets/{a}", new { changes = new[] { new { field = "G1Status", from = (string?)null, to = "Completed" } } })).EnsureSuccessStatusCode();
        var added = await (await admin.PostAsJsonAsync($"/api/groups/{id}/members", new { assetIds = new[] { a, b, a } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, added.GetProperty("added").GetInt32());
        var progress = added.GetProperty("detail").GetProperty("group").GetProperty("progress");
        Assert.Equal(2, progress.GetProperty("members").GetInt32());
        Assert.Equal(1, progress.GetProperty("gatesCompleted")[0].GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/groups/{id}/members", new { assetIds = new[] { other } })).StatusCode);

        var register = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Equipment");
        Assert.Contains("WP-032-001", register.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == a).GetProperty("groups").EnumerateArray().Select(x => x.GetString()));
        var monitoring = await admin.GetFromJsonAsync<JsonElement>($"/api/monitoring?groupId={id}");
        Assert.Equal(2, monitoring.GetProperty("rows").GetArrayLength());

        var removed = await (await admin.PostAsJsonAsync($"/api/groups/{id}/members/remove", new { assetIds = new[] { b } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, removed.GetProperty("removed").GetInt32());
        var second = await (await admin.PostAsJsonAsync("/api/groups", new { typeId = wp.GetProperty("id").GetInt32(), name = "Pumps", unitId })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("WP-032-002", second.GetProperty("group").GetProperty("code").GetString());
    }
}
