using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Endpoints;
using PresApp.Domain.Common;
using PresApp.Domain.Rules;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>
/// Daily report: preservation of each unit at the end of a site day rebuilt from the change log, tags completed on the
/// day, unit highlights and the summary served to EPCS with the shared key.
/// </summary>
public sealed class DailyReportTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;
    private static readonly DateOnly Today = SiteClock.Today(TimeProvider.System);
    private static readonly DateOnly Day = Today.AddDays(-1);

    public DailyReportTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task The_report_of_a_day_counts_the_statuses_at_its_end_and_the_tags_completed_that_day()
    {
        var unitId = await UnitIdAsync("Unit 39");
        var before = await CreateAsync(unitId, "39-V-901", "Equipment", "Static");          // preserved the day before
        var pump = await CreateAsync(unitId, "39-P-902", "Equipment", "Rotating");          // preserved on the day, undone today
        var motor = await CreateAsync(unitId, "39-EM-903", "ElectricalInstrument", "Electrical"); // every step closed on the day
        var migrated = await CreateAsync(unitId, "39-V-904", "Equipment", "Static");        // G4 written by the migration on the day
        await CreateAsync(unitId, "39-V-905", "Equipment", "Static");                       // never preserved

        await PatchAsync(before, ("G4Status", "Completed"));
        await PatchAsync(pump, ("G4Status", "Completed"));
        await PatchAsync(motor, [.. Enumerable.Range(1, 8).Select(g => ($"G{g}Status", g == 5 ? "N/A" : "Completed"))]);
        await PatchAsync(migrated, ("G4Status", "Completed"));

        // Move the history: everything created two days ago, then each change on its day.
        await app.WithDbAsync(db => db.ChangeLogs.ExecuteUpdateAsync(s => s.SetProperty(c => c.ChangedAtUtc, At(Day.AddDays(-2)))));
        await MoveAsync(before, "G4Status", At(Day.AddDays(-1)));
        await MoveAsync(pump, "G4Status", At(Day));
        await MoveAsync(motor, null, At(Day), field => field.StartsWith('G'));
        await MoveAsync(migrated, "G4Status", At(Day), source: ChangeSource.Migration);
        var undo = await admin.PatchAsJsonAsync($"/api/assets/{pump}", new { changes = new[] { new { field = "G4Status", from = "Completed", to = "Ongoing" } } });
        Assert.True(undo.IsSuccessStatusCode, await undo.Content.ReadAsStringAsync());   // today: not part of the day's report

        var unit = await UnitAsync(Day, unitId);
        Assert.Equal(5, unit.GetProperty("tags").GetInt32());
        Assert.Equal(4, unit.GetProperty("preserved").GetInt32());
        Assert.Equal(2, unit.GetProperty("preservedDayBefore").GetInt32());   // the migrated value counts as always there
        Assert.Equal(80m, unit.GetProperty("preservedPct").GetDecimal());
        Assert.Equal(["39-EM-903", "39-P-902"], Strings(unit.GetProperty("completedTags")));
        var disciplines = unit.GetProperty("disciplines").EnumerateArray().ToDictionary(d => d.GetProperty("discipline").GetString()!);
        Assert.Equal(3, disciplines["Static"].GetProperty("tags").GetInt32());
        Assert.Equal(2, disciplines["Static"].GetProperty("preserved").GetInt32());
        Assert.Equal(1, disciplines["Rotating"].GetProperty("preserved").GetInt32());
        Assert.Equal(1, disciplines["E&I"].GetProperty("preserved").GetInt32());
        Assert.False(disciplines.ContainsKey("Package Mechanical"));

        // The day before: only 39-V-901 was completed then.
        Assert.Equal(["39-V-901"], Strings((await UnitAsync(Day.AddDays(-1), unitId)).GetProperty("completedTags")));

        // Today: the pump is no longer preserved and nothing was completed.
        var today = await UnitAsync(Today, unitId);
        Assert.Equal(3, today.GetProperty("preserved").GetInt32());
        Assert.Empty(today.GetProperty("completedTags").EnumerateArray());

        // A day that has not started cannot be asked.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/reports/daily?date={Today.AddDays(1):yyyy-MM-dd}")).StatusCode);
    }

    [Fact]
    public async Task Highlights_are_written_per_unit_and_day_and_appear_in_the_report()
    {
        var unitId = await UnitIdAsync("Unit 541");
        await CreateAsync(unitId, "541-E-901", "Equipment", "Static");
        var url = $"/api/units/{unitId}/highlights";

        var first = await PostAsync(url, new { date = Day, text = "  Cold box blanketed at 0.8 bar  " });
        var list = await PostAsync(url, new { date = Day, text = "Fin fan oil draining" });
        Assert.Equal(["Cold box blanketed at 0.8 bar", "Fin fan oil draining"], list.Select(h => h.GetProperty("text").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(url, new { date = Today.AddDays(1), text = "Tomorrow" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(url, new { date = Day, text = "  " })).StatusCode);

        var id = first[0].GetProperty("id").GetInt32();
        var stale = await admin.PutAsJsonAsync($"{url}/{id}", new { text = "Changed", expected = "Something else" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var changed = await admin.PutAsJsonAsync($"{url}/{id}", new { text = "Cold box 541-E-113 blanketed", expected = "Cold box blanketed at 0.8 bar" });
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        var removed = await admin.DeleteAsync($"{url}/{list[1].GetProperty("id").GetInt32()}");
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());

        var unit = await UnitAsync(Day, unitId);
        Assert.Equal(["Cold box 541-E-113 blanketed"], unit.GetProperty("highlights").EnumerateArray().Select(h => h.GetProperty("text").GetString()));
        Assert.Empty((await UnitAsync(Day.AddDays(-1), unitId)).GetProperty("highlights").EnumerateArray());

        // A viewer reads them but cannot write.
        await app.AddUserAsync("viewer.daily@enermech.com", "Viewer");
        var viewer = app.ClientFor("viewer.daily@enermech.com");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{url}?date={Day:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(url, new { date = Day, text = "No" })).StatusCode);
    }

    [Fact]
    public async Task Epcs_reads_the_summary_with_the_shared_key_only()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/api/integration/daily-summary")).StatusCode);

        using var withKey = app.WithWebHostBuilder(b => b.UseSetting("Integration:ApiKey", "test-shared-key"));
        var client = withKey.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/integration/daily-summary")).StatusCode);

        using var ok = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/daily-summary?date={Day:yyyy-MM-dd}");
        ok.Headers.Add(ReportEndpoints.KeyHeader, "test-shared-key");
        var response = await client.SendAsync(ok);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(Day.ToString("yyyy-MM-dd"), (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("date").GetString());

        using var tunnel = new HttpRequestMessage(HttpMethod.Get, "/api/integration/daily-summary");
        tunnel.Headers.Add(ReportEndpoints.KeyHeader, "test-shared-key");
        tunnel.Headers.Add("Cf-Connecting-Ip", "203.0.113.5");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(tunnel)).StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime At(DateOnly day) => SiteClock.StartOfDayUtc(day).AddHours(12);

    private static List<string?> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToList();

    private Task<int> MoveAsync(Guid asset, string? field, DateTime at, Func<string, bool>? match = null, ChangeSource? source = null) =>
        app.WithDbAsync(async db =>
        {
            // The changes that set the field(s) to Completed.
            var rows = await db.ChangeLogs.Where(c => c.AssetId == asset && c.NewValue == "Completed").ToListAsync();
            foreach (var r in rows.Where(r => field is null ? match!(r.Field) : r.Field == field))
            {
                r.ChangedAtUtc = at;
                if (source is { } s) r.Source = s;
            }
            return await db.SaveChangesAsync();
        });

    private async Task<JsonElement> UnitAsync(DateOnly day, int unitId)
    {
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/reports/daily?date={day:yyyy-MM-dd}");
        return report.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("unitId").GetInt32() == unitId);
    }

    private async Task<List<JsonElement>> PostAsync(string url, object body)
    {
        var response = await admin.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private Task<int> UnitIdAsync(string unit) => app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());

    private Task PatchAsync(Guid id, (string Field, string To) change) => PatchAsync(id, [change]);

    private async Task PatchAsync(Guid id, (string Field, string To)[] changes)
    {
        var response = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = changes.Select(c => new { field = c.Field, from = (string?)null, to = c.To }).ToArray() });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<Guid> CreateAsync(int unitId, string tag, string type, string discipline)
    {
        await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Rotating" && d.Prefix == null)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.Prefix, "RT")));
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = "Item " + tag });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
