using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PresApp.Api.Assets;
using PresApp.Domain.Assets;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>Asset Availability = Removed from Site drives Current Gate (and back), everywhere it can change.</summary>
public sealed class RemovedFromSiteTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public RemovedFromSiteTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<string> CreateAsync(string tag)
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Test vessel" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> PatchAsync(string id, params (string Field, string? From, string? To)[] changes)
    {
        var response = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = changes.Select(c => new { field = c.Field, from = c.From, to = c.To }) });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Gate(JsonElement patch) => patch.GetProperty("asset").GetProperty("currentGate").GetString()!;

    [Fact]
    public async Task Removed_from_site_moves_the_gate_even_with_completed_gates_and_back_to_the_highest_gate()
    {
        var id = await CreateAsync("39-RS-001");
        await PatchAsync(id, ("G1Status", null, "Completed"), ("G3Status", null, "Completed"));
        (await admin.PostAsJsonAsync($"/api/assets/{id}/current-gate", new { value = "G3", expected = "New" })).EnsureSuccessStatusCode();

        var removed = await PatchAsync(id, ("AssetAvailability", null, "removed from site"));
        Assert.Equal("Removed from Site", Gate(removed));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("proposals").GetProperty("currentGate").ValueKind);

        // A gate change while removed never takes it out of Removed from Site.
        var gateWhileRemoved = await PatchAsync(id, ("G5Status", null, "Completed"));
        Assert.Equal("Removed from Site", Gate(gateWhileRemoved));
        Assert.Equal(JsonValueKind.Null, gateWhileRemoved.GetProperty("proposals").GetProperty("currentGate").ValueKind);

        var back = await PatchAsync(id, ("AssetAvailability", "Removed from Site", "Available"));
        Assert.Equal("G5", Gate(back));

        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("field").GetString() == "CurrentGate" && h.GetProperty("newValue").GetString() == "Removed from Site");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("field").GetString() == "CurrentGate" && h.GetProperty("newValue").GetString() == "G5");
    }

    [Fact]
    public async Task Coming_back_without_completed_gates_goes_to_New_and_TBC_leaves_the_gate_alone()
    {
        var id = await CreateAsync("39-RS-002");
        Assert.Equal("Removed from Site", Gate(await PatchAsync(id, ("AssetAvailability", null, "Removed from Site"))));
        Assert.Equal("New", Gate(await PatchAsync(id, ("AssetAvailability", "Removed from Site", "TBC"))));
        Assert.Equal("New", Gate(await PatchAsync(id, ("AssetAvailability", "TBC", "Available"))));
    }

    [Fact]
    public async Task Current_gate_can_only_be_set_to_the_suggested_value()
    {
        var id = await CreateAsync("39-RS-003");
        await PatchAsync(id, ("G2Status", null, "Completed"));

        var wrong = await admin.PostAsJsonAsync($"/api/assets/{id}/current-gate", new { value = "G7", expected = "New" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var removed = await admin.PostAsJsonAsync($"/api/assets/{id}/current-gate", new { value = "Removed from Site", expected = "New" });
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);

        (await admin.PostAsJsonAsync($"/api/assets/{id}/current-gate", new { value = "G2", expected = "New" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bulk_edit_of_asset_availability_moves_the_gate_and_says_so_in_the_preview()
    {
        var a = await CreateAsync("39-RS-010");
        var b = await CreateAsync("39-RS-011");
        await PatchAsync(b, ("G4Status", null, "Completed"));
        (await admin.PostAsJsonAsync($"/api/assets/{b}/current-gate", new { value = "G4", expected = "New" })).EnsureSuccessStatusCode();

        var request = new { assetIds = new[] { a, b }, field = "AssetAvailability", value = "Removed from Site" };
        var preview = await (await admin.PostAsJsonAsync("/api/bulk/preview", request)).Content.ReadFromJsonAsync<JsonElement>();
        var reasons = preview.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("reason").GetString()).ToList();
        Assert.Contains("Current Gate New → Removed from Site", reasons);
        Assert.Contains("Current Gate G4 → Removed from Site", reasons);

        (await admin.PostAsJsonAsync("/api/bulk/apply", request)).EnsureSuccessStatusCode();
        foreach (var id in new[] { a, b })
        {
            var profile = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
            Assert.Equal("Removed from Site", profile.GetProperty("currentGate").GetString());
        }

        var back = new { assetIds = new[] { a, b }, field = "AssetAvailability", value = "Available" };
        (await admin.PostAsJsonAsync("/api/bulk/apply", back)).EnsureSuccessStatusCode();
        Assert.Equal("New", (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}")).GetProperty("currentGate").GetString());
        Assert.Equal("G4", (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{b}")).GetProperty("currentGate").GetString());
    }

    [Fact]
    public async Task Alignment_tool_previews_then_fixes_cases_A_and_B_and_only_lists_C()
    {
        var caseA = Guid.Parse(await CreateAsync("39-RS-020"));
        var caseB = Guid.Parse(await CreateAsync("39-RS-021"));
        var caseC = Guid.Parse(await CreateAsync("39-RS-022"));
        var aligned = Guid.Parse(await CreateAsync("39-RS-023"));
        await app.WithDbAsync(async db =>
        {
            var ps = await db.Set<AssetPreservation>().Where(p => new[] { caseA, caseB, caseC, aligned }.Contains(p.AssetId)).ToListAsync();
            var p = ps.ToDictionary(x => x.AssetId);
            p[caseA].AssetAvailability = "Removed from Site"; p[caseA].CurrentGate = CurrentGate.G2; p[caseA].G2Status = "Completed";
            p[caseB].AssetAvailability = null; p[caseB].CurrentGate = CurrentGate.RemovedFromSite;
            p[caseC].AssetAvailability = "Available"; p[caseC].CurrentGate = CurrentGate.RemovedFromSite;
            p[aligned].AssetAvailability = "Removed from Site"; p[aligned].CurrentGate = CurrentGate.RemovedFromSite;
            return await db.SaveChangesAsync();
        });

        async Task<List<AlignmentRow>> RunAsync(bool apply)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<RemovedFromSiteAlignment>().RunAsync(apply);
        }

        var preview = (await RunAsync(false)).Where(r => r.Tag!.StartsWith("39-RS-02")).ToList();
        Assert.Equal(["A", "B", "C"], preview.Select(r => r.Case));
        Assert.All(preview, r => Assert.NotEqual("Changed", r.Result));
        Assert.Equal("G2", preview[0].HighestCompletedGate);
        Assert.Contains("39-RS-020", RemovedFromSiteAlignment.ToCsv(preview));

        var applied = (await RunAsync(true)).Where(r => r.Tag!.StartsWith("39-RS-02")).ToList();
        Assert.Equal(["Changed", "Changed", "Not changed"], applied.Select(r => r.Result));

        var after = await app.WithDbAsync(db => db.Set<AssetPreservation>().Where(p => new[] { caseA, caseB, caseC }.Contains(p.AssetId)).ToListAsync());
        Assert.Equal(CurrentGate.RemovedFromSite, after.Single(p => p.AssetId == caseA).CurrentGate);
        Assert.Equal("Removed from Site", after.Single(p => p.AssetId == caseB).AssetAvailability);
        Assert.Equal("Available", after.Single(p => p.AssetAvailability == "Available").AssetAvailability);

        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{caseA}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("field").GetString() == "CurrentGate" && h.GetProperty("source").GetString() == "System");

        Assert.DoesNotContain(await RunAsync(false), r => r.Tag is "39-RS-020" or "39-RS-021");
    }
}
