using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Rules;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>
/// Effective on (25-nov-2026): the gate changes an asset had today can count in yesterday's daily report, set by hand per
/// asset or in bulk. Only today's changes move, only to yesterday; untouched changes count when they were recorded.
/// </summary>
public sealed class EffectiveOnTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;
    private static readonly DateOnly Today = SiteClock.Today(TimeProvider.System);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    public EffectiveOnTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task Todays_changes_moved_to_yesterday_count_in_yesterdays_report_and_not_again_today()
    {
        var unitId = await UnitIdAsync("Unit 39");
        var late = await CreateAsync(unitId, "39-V-801");    // done yesterday, recorded this morning
        var onTime = await CreateAsync(unitId, "39-V-802");  // done and recorded today
        var old = await CreateAsync(unitId, "39-V-803");     // recorded three days ago
        await PatchAsync(old, "G4Status", "Completed");
        await OlderAsync(unitId, Today.AddDays(-3));
        await PatchAsync(late, "G4Status", "Completed");
        await PatchAsync(onTime, "G4Status", "Completed");

        var state = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{late}/effective-on");
        Assert.Equal("Today", state.GetProperty("effectiveOn").GetString());
        Assert.Equal(1, state.GetProperty("changes").GetInt32());
        // Nothing recorded today: nothing to move, and older changes can never move.
        Assert.Equal(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{old}/effective-on")).GetProperty("effectiveOn").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(old, "Yesterday")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(late, "2026-11-20")).StatusCode);

        var moved = await SetAsync(late, "Yesterday");
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        Assert.Equal("Yesterday", (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("effectiveOn").GetString());

        var yesterday = await UnitAsync(Yesterday, unitId);
        Assert.Equal(["39-V-801"], Strings(yesterday.GetProperty("completedTags")));
        Assert.Equal(2, yesterday.GetProperty("preserved").GetInt32());
        var today = await UnitAsync(Today, unitId);
        Assert.Equal(["39-V-802"], Strings(today.GetProperty("completedTags")));
        Assert.Equal(2, today.GetProperty("preservedDayBefore").GetInt32());

        // The move is in the history, and the moment of the change is kept.
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{late}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("field").GetString() == "Effective on" && h.GetProperty("newValue").GetString() == "Yesterday");
        var recorded = await app.WithDbAsync(db => db.ChangeLogs.Where(c => c.AssetId == late && c.Field == "G4Status").Select(c => c.ChangedAtUtc).SingleAsync());
        Assert.Equal(Today, SiteClock.DateOf(recorded));

        // A later change of today is not moved with the earlier ones: it counts today until it is moved too.
        await PatchAsync(late, "G3Status", "Completed");
        state = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{late}/effective-on");
        Assert.Equal(2, state.GetProperty("changes").GetInt32());
        Assert.Equal(1, state.GetProperty("moved").GetInt32());

        // Back to today: as if nothing had been set.
        Assert.True((await SetAsync(late, "Today")).IsSuccessStatusCode);
        Assert.Empty((await UnitAsync(Yesterday, unitId)).GetProperty("completedTags").EnumerateArray());
        Assert.Equal(["39-V-801", "39-V-802"], Strings((await UnitAsync(Today, unitId)).GetProperty("completedTags")));
    }

    [Fact]
    public async Task Bulk_moves_only_assets_with_changes_today_and_needs_the_permission()
    {
        var unitId = await UnitIdAsync("Unit 41");
        var changed = await CreateAsync(unitId, "41-V-811");
        var quiet = await CreateAsync(unitId, "41-V-812");
        await OlderAsync(unitId, Today.AddDays(-3));
        await PatchAsync(changed, "G4Status", "Completed");

        var body = new { assetIds = new[] { changed, quiet }, field = "EffectiveOn", value = "Yesterday" };
        var preview = await (await admin.PostAsJsonAsync("/api/bulk/preview", body)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, preview.GetProperty("changed").GetInt32());
        Assert.Equal(1, preview.GetProperty("skipped").GetInt32());
        Assert.Equal("Today", (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{changed}/effective-on")).GetProperty("effectiveOn").GetString());

        var applied = await admin.PostAsJsonAsync("/api/bulk/apply", body);
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        Assert.Equal("Yesterday", (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{changed}/effective-on")).GetProperty("effectiveOn").GetString());
        Assert.Equal(["41-V-811"], Strings((await UnitAsync(Yesterday, unitId)).GetProperty("completedTags")));

        // Engineers do not have it unless an admin gives it to their role.
        const string engineer = "effective.engineer@enermech.com";
        await app.AddUserAsync(engineer, RoleNames.Engineer);
        var client = app.ClientFor(engineer);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/assets/{changed}/effective-on", new { day = "Today" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/bulk/apply", body with { value = "Today" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/assets/{changed}/effective-on")).StatusCode);
    }

    [Fact]
    public async Task An_asset_created_today_and_moved_to_yesterday_is_completed_yesterday()
    {
        var unitId = await UnitIdAsync("Unit 42");
        await CreateAsync(unitId, "42-V-821");                         // another tag of the unit, from before
        await OlderAsync(unitId, Today.AddDays(-3));
        var added = await CreateAsync(unitId, "42-V-822");             // missing from the register, added this morning
        await PatchAsync(added, "G4Status", "Completed");
        Assert.True((await SetAsync(added, "Yesterday")).IsSuccessStatusCode);

        var yesterday = await UnitAsync(Yesterday, unitId);
        Assert.Equal(["42-V-822"], Strings(yesterday.GetProperty("completedTags")));
        Assert.Equal(0, yesterday.GetProperty("preservedDayBefore").GetInt32());
        Assert.Equal(1, yesterday.GetProperty("preserved").GetInt32());
        var today = await UnitAsync(Today, unitId);
        Assert.Empty(today.GetProperty("completedTags").EnumerateArray());
        Assert.Equal(1, today.GetProperty("preservedDayBefore").GetInt32());
    }

    // ------------------------------------------------------------------ helpers

    private static List<string?> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToList();

    private Task<HttpResponseMessage> SetAsync(Guid id, string day) => admin.PutAsJsonAsync($"/api/assets/{id}/effective-on", new { day });

    /// <summary>Everything written so far for the unit's assets happened on an earlier day.</summary>
    private Task<int> OlderAsync(int unitId, DateOnly day)
    {
        var at = SiteClock.StartOfDayUtc(day).AddHours(12);
        return app.WithDbAsync(db => db.ChangeLogs.Where(c => c.AssetId != null && db.Assets.Any(a => a.Id == c.AssetId && a.UnitId == unitId))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ChangedAtUtc, at)));
    }

    private async Task<JsonElement> UnitAsync(DateOnly day, int unitId)
    {
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/reports/daily?date={day:yyyy-MM-dd}");
        return report.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("unitId").GetInt32() == unitId);
    }

    private Task<int> UnitIdAsync(string unit) => app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());

    private async Task PatchAsync(Guid id, string field, string to)
    {
        var response = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field, from = (string?)null, to } } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<Guid> CreateAsync(int unitId, string tag)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Item " + tag });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
