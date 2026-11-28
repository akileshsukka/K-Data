using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>Readings entered with a wrong date or value, corrected or removed by an admin (readings.correct).</summary>
public sealed class ReadingCorrectionTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public ReadingCorrectionTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<(int UnitId, Guid Id)> CreateAsync(string tag)
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 31").Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var r = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (unitId, Guid.Parse((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!));
    }

    private Task<int> TypeAsync(string key) => app.WithDbAsync(db => db.MeasurementTypes.Where(t => t.Key == key).Select(t => t.Id).FirstAsync());

    private async Task<List<JsonElement>> ReadingsAsync(Guid id) =>
        (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/readings")).EnumerateArray().ToList();

    private async Task<JsonElement> MonitoringRowAsync(int unitId, Guid id) =>
        (await admin.GetFromJsonAsync<JsonElement>($"/api/monitoring?unitId={unitId}")).GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);

    private static DateTime Utc(JsonElement e, string name) => DateTime.SpecifyKind(e.GetProperty(name).GetDateTime(), DateTimeKind.Utc);

    [Fact]
    public async Task A_round_pressure_moved_past_the_preservation_date_becomes_post_pressure_and_the_history_keeps_both()
    {
        var (unitId, id) = await CreateAsync("31-COR-1");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));
        (await admin.PostAsJsonAsync($"/api/assets/{id}/preserved", new { preservedOn = today.AddDays(-5) })).EnsureSuccessStatusCode();

        // Entered with the wrong date (20 days ago, before it was preserved): saved as G4 - N2 Blanket, DURING.
        var wrong = DateTime.UtcNow.AddDays(-20);
        (await admin.PostAsJsonAsync("/api/monitoring/rounds", new { measuredAtUtc = wrong, rows = new[] { new { assetId = id, pressure = 0.9m } } })).EnsureSuccessStatusCode();
        var reading = Assert.Single(await ReadingsAsync(id));
        Assert.Equal("G4N2BlanketBarg", reading.GetProperty("measurement").GetString());
        Assert.Equal("No reading since preserved", (await MonitoringRowAsync(unitId, id)).GetProperty("status").GetString());

        // Without a reason nothing changes.
        var right = DateTime.UtcNow.AddDays(-1);
        var noReason = await admin.PutAsJsonAsync($"/api/assets/{id}/readings/{reading.GetProperty("id").GetInt64()}", new { measuredAtUtc = right, value = 0.9m, reason = " " });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var r = await admin.PutAsJsonAsync($"/api/assets/{id}/readings/{reading.GetProperty("id").GetInt64()}", new
        {
            measuredAtUtc = right, value = 1.1m, reason = "Typed with the wrong date",
            expected = new { measuredAtUtc = Utc(reading, "measuredAtUtc"), value = 0.9m }
        });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());

        var corrected = Assert.Single(await ReadingsAsync(id));
        Assert.Equal("PressureBarg", corrected.GetProperty("measurement").GetString());
        Assert.Equal("Post", corrected.GetProperty("phase").GetString());
        Assert.Equal(1.1m, corrected.GetProperty("value").GetDecimal());
        var row = await MonitoringRowAsync(unitId, id);
        Assert.Equal("OK", row.GetProperty("status").GetString());
        Assert.Equal(1.1m, row.GetProperty("pressureBarg").GetDecimal());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("n2Barg").ValueKind);

        var history = (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history")).EnumerateArray()
            .Single(h => h.GetProperty("field").GetString()!.StartsWith("Reading corrected", StringComparison.Ordinal));
        Assert.StartsWith("0.9 barg measured", history.GetProperty("oldValue").GetString());
        Assert.Contains("Reason: Typed with the wrong date", history.GetProperty("newValue").GetString());

        // A second admin working on what they saw before is stopped.
        var stale = await admin.PutAsJsonAsync($"/api/assets/{id}/readings/{corrected.GetProperty("id").GetInt64()}", new
        {
            measuredAtUtc = right, value = 1.3m, reason = "Other", expected = new { measuredAtUtc = Utc(reading, "measuredAtUtc"), value = 0.9m }
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Only_the_latest_reading_changes_the_asset_value_and_a_removed_one_gives_way_to_the_previous()
    {
        var (unitId, id) = await CreateAsync("31-COR-2");
        var o2 = await TypeAsync("G3O2Pct");
        foreach (var (days, value) in new[] { (6, 4.0m), (3, 3.0m) })
            (await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = DateTime.UtcNow.AddDays(-days), values = new[] { new { measurementTypeId = o2, value } } })).EnsureSuccessStatusCode();
        var readings = await ReadingsAsync(id);
        var latest = readings[0];
        var older = readings[1];
        Assert.Equal(3.0m, (await MonitoringRowAsync(unitId, id)).GetProperty("o2Pct").GetDecimal());

        // The older one corrected: the asset keeps the latest value.
        (await admin.PutAsJsonAsync($"/api/assets/{id}/readings/{older.GetProperty("id").GetInt64()}", new { measuredAtUtc = Utc(older, "measuredAtUtc"), value = 4.5m, reason = "Value typo" })).EnsureSuccessStatusCode();
        Assert.Equal(3.0m, (await MonitoringRowAsync(unitId, id)).GetProperty("o2Pct").GetDecimal());

        // The latest moved before the other one: the other one becomes the latest.
        (await admin.PutAsJsonAsync($"/api/assets/{id}/readings/{latest.GetProperty("id").GetInt64()}", new { measuredAtUtc = DateTime.UtcNow.AddDays(-9), value = 3.0m, reason = "Wrong day" })).EnsureSuccessStatusCode();
        var row = await MonitoringRowAsync(unitId, id);
        Assert.Equal(4.5m, row.GetProperty("o2Pct").GetDecimal());
        Assert.Equal(Utc(older, "measuredAtUtc"), Utc(row, "lastReadingAtUtc"), TimeSpan.FromSeconds(1));

        // Removed: the remaining one is the latest; with none left the value is empty.
        (await admin.PostAsJsonAsync($"/api/assets/{id}/readings/{older.GetProperty("id").GetInt64()}/remove", new { reason = "Entered twice" })).EnsureSuccessStatusCode();
        Assert.Equal(3.0m, (await MonitoringRowAsync(unitId, id)).GetProperty("o2Pct").GetDecimal());
        (await admin.PostAsJsonAsync($"/api/assets/{id}/readings/{latest.GetProperty("id").GetInt64()}/remove", new { reason = "Wrong asset" })).EnsureSuccessStatusCode();
        row = await MonitoringRowAsync(unitId, id);
        Assert.Empty(await ReadingsAsync(id));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("o2Pct").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastReadingAtUtc").ValueKind);
        var removed = (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history")).EnumerateArray()
            .Count(h => h.GetProperty("field").GetString()!.StartsWith("Reading removed", StringComparison.Ordinal));
        Assert.Equal(2, removed);
    }

    [Fact]
    public async Task A_correction_leaves_no_trace_outside_the_roles_that_record_readings()
    {
        var (unitId, id) = await CreateAsync("31-COR-4");
        var o2 = await TypeAsync("G3O2Pct");
        (await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = DateTime.UtcNow.AddDays(-2), values = new[] { new { measurementTypeId = o2, value = 2m } } })).EnsureSuccessStatusCode();
        var reading = (await ReadingsAsync(id))[0];
        var readingId = reading.GetProperty("id").GetInt64();
        var logsBefore = await app.WithDbAsync(db => db.ChangeLogs.CountAsync(c => c.AssetId == id));

        // It cannot be dated after it was entered (it would show it was moved).
        var later = await admin.PutAsJsonAsync($"/api/assets/{id}/readings/{readingId}", new { measuredAtUtc = Utc(reading, "recordedAtUtc").AddHours(1), value = 2m, reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, later.StatusCode);

        (await admin.PutAsJsonAsync($"/api/assets/{id}/readings/{readingId}", new { measuredAtUtc = DateTime.UtcNow.AddDays(-3), value = 2.5m, reason = "Wrong value" })).EnsureSuccessStatusCode();
        Assert.Equal(2.5m, (await MonitoringRowAsync(unitId, id)).GetProperty("o2Pct").GetDecimal());

        // Only the internal reading row is written: no "G3 - O2 2 → 2.5" with no new reading.
        var added = await app.WithDbAsync(db => db.ChangeLogs.Where(c => c.AssetId == id).OrderBy(c => c.Id).Skip(logsBefore).Select(c => c.EntityType).ToListAsync());
        Assert.Equal(["Reading"], added);

        // A role that sees the history but does not record readings does not see it (asset and unit history).
        await app.WithDbAsync(async db =>
        {
            db.Roles.Add(new Role { Name = "History reader", Permissions = [new() { Permission = Permissions.AssetsView }, new() { Permission = Permissions.HistoryView }] });
            return await db.SaveChangesAsync();
        });
        const string email = "history.reader@enermech.com";
        await app.AddUserAsync(email, "History reader");
        var reader = app.ClientFor(email);
        static bool Correction(JsonElement h) => h.GetProperty("field").GetString()!.StartsWith("Reading ", StringComparison.Ordinal);
        Assert.DoesNotContain((await reader.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history")).EnumerateArray(), Correction);
        Assert.DoesNotContain((await reader.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/history?take=1000")).EnumerateArray(), Correction);
        Assert.Contains((await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history")).EnumerateArray(), Correction);

        // Field Technician records readings but does not see corrections; Engineer does.
        const string tech = "tech.cor@enermech.com", engineer = "engineer.view.cor@enermech.com";
        await app.AddUserAsync(tech, RoleNames.FieldTechnician);
        await app.AddUserAsync(engineer, RoleNames.Engineer);
        Assert.DoesNotContain((await app.ClientFor(tech).GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history")).EnumerateArray(), Correction);
        Assert.Contains((await app.ClientFor(engineer).GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history")).EnumerateArray(), Correction);
        var viewers = await app.WithDbAsync(db => db.Roles.Where(r => r.Permissions.Any(p => p.Permission == Permissions.ReadingsCorrectionsView)).Select(r => r.Name).OrderBy(n => n).ToListAsync());
        Assert.Equal([RoleNames.Admin, RoleNames.Engineer], viewers);

        // The reading itself keeps who entered it and when, as any other.
        var after = Assert.Single(await reader.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/readings").ContinueWith(t => t.Result.EnumerateArray().ToList()));
        Assert.Equal(Utc(reading, "recordedAtUtc"), Utc(after, "recordedAtUtc"));
        Assert.Equal(reading.GetProperty("recordedBy").GetString(), after.GetProperty("recordedBy").GetString());
        Assert.Equal(reading.GetProperty("source").GetString(), after.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task Only_roles_with_readings_correct_can_correct_and_by_default_only_admin_has_it()
    {
        var roles = await app.WithDbAsync(db => db.Roles.Where(r => r.Permissions.Any(p => p.Permission == Permissions.ReadingsCorrect)).Select(r => r.Name).ToListAsync());
        Assert.Equal([RoleNames.Admin], roles);

        var (_, id) = await CreateAsync("31-COR-3");
        var o2 = await TypeAsync("G3O2Pct");
        (await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = DateTime.UtcNow.AddDays(-1), values = new[] { new { measurementTypeId = o2, value = 2m } } })).EnsureSuccessStatusCode();
        var readingId = (await ReadingsAsync(id))[0].GetProperty("id").GetInt64();

        const string email = "engineer.cor@enermech.com";
        await app.AddUserAsync(email, RoleNames.Engineer);
        var engineer = app.ClientFor(email);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PutAsJsonAsync($"/api/assets/{id}/readings/{readingId}", new { measuredAtUtc = DateTime.UtcNow.AddDays(-2), value = 2m, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsJsonAsync($"/api/assets/{id}/readings/{readingId}/remove", new { reason = "x" })).StatusCode);
        Assert.Single(await ReadingsAsync(id));
    }
}
