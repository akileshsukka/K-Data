using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Simpro;
using PresApp.Infrastructure.Seeding;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>The sync against an in-memory copy of the Simpro tables, with the rules of script v5.</summary>
public sealed class SimproTests : IClassFixture<TestApp>
{
    private static readonly Guid Plant0 = SeedData.Plants[0].PlantId;
    private static readonly Guid Unit42 = SeedData.Plants[0].Units.First(u => u.Number == 42).Id;

    private readonly TestApp app;
    private readonly HttpClient admin;

    public SimproTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
        app.Simpro.EnsureCreated(SeedData.Plants.SelectMany(p => p.Units.Select(u => (u.Id, u.Unit)).Prepend((p.PlantId, p.Plant))));
    }

    private async Task<(Guid Id, string Code)> CreateAsync(string tag)
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Number == 42).Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Item", priority = "Priority 3" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (Guid.Parse(body.GetProperty("id").GetString()!), body.GetProperty("code").GetString()!);
    }

    /// <summary>An equipment that is the same in K Data and Simpro, with a snapshot of the last sync.</summary>
    private async Task<(Guid Id, string Code)> InBothAsync(string tag, Dictionary<string, string>? appValues = null)
    {
        var (id, code) = await CreateAsync(tag);
        appValues ??= new() { ["G1Status"] = "Ongoing", ["RemarksAdnoc"] = "Initial" };
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = appValues.Select(kv => new { field = kv.Key, from = (string?)null, to = kv.Value }) });
        patch.EnsureSuccessStatusCode();
        app.Simpro.AddFlange(id, code, tag, "Static", Plant0, Unit42, 1, new() { ["G1S"] = "Ongoing" });
        app.Simpro.Exec("UPDATE tbl_flange SET WorkInstructions = 'Initial', flangeDescription = 'Item' WHERE flangeId = @p0", id);
        app.Simpro.Exec("UPDATE tbl_flangeChanges SET WorkInstructions = 'Initial', flangeDescription = 'Item' WHERE flangeId = @p0", id);
        return (id, code);
    }

    private async Task<JsonElement> RunAsync(string action)
    {
        var response = await admin.PostAsync($"/api/simpro/{action}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(string Code, string Field, string Outcome, string? New)> Items(JsonElement run) =>
        run.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("assetCode").GetString()!, i.GetProperty("field").GetString()!,
            i.GetProperty("outcome").GetString()!, i.GetProperty("newValue").GetString())).ToList();

    private Task Patch(Guid id, string field, string? from, string to) =>
        admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field, from, to } } }).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    [Fact]
    public async Task Changes_made_in_KData_are_written_like_Simpro_and_become_the_new_snapshot()
    {
        var (id, code) = await InBothAsync("42-SY-1");
        await Patch(id, "G1Status", "Ongoing", "Completed");
        await Patch(id, "RemarksAdnoc", "Initial", "Checked on site");

        var preview = await RunAsync("preview");
        Assert.Equal("Previewed", preview.GetProperty("run").GetProperty("status").GetString());
        var items = Items(preview).Where(i => i.Code == code).ToList();
        Assert.Contains(items, i => i is { Field: "G1S", Outcome: "To write", New: "Completed" });
        Assert.Contains(items, i => i is { Field: "REMARK", Outcome: "To write" });
        Assert.Contains(items, i => i is { Field: "GATE", Outcome: "To write", New: "G1" });   // rule: highest completed gate
        Assert.Equal("Ongoing", app.Simpro.Custom(id, "G1S"));                                 // preview writes nothing

        var applied = await RunAsync("apply");
        Assert.Equal("Applied", applied.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal("Completed", app.Simpro.Custom(id, "G1S"));
        Assert.Equal("Checked on site", app.Simpro.Header(id, "WorkInstructions"));
        Assert.Equal(9L, app.Simpro.Header(id, "flangeStatusId"));
        Assert.Equal(1, app.Simpro.Count("tbl_flangeHistory", "flangeId = @p0 AND username = 'SYNC-KDATA' AND activityId = 2 AND activitySequence = 2", id));
        Assert.Equal(1, app.Simpro.Count("tbl_flangeChanges", "flangeId = @p0 AND WorkInstructions = 'Checked on site'", id));

        var again = await RunAsync("preview");
        Assert.DoesNotContain(Items(again), i => i.Code == code && i.Outcome == "To write");
    }

    [Fact]
    public async Task An_edit_made_in_Simpro_holds_the_equipment_until_KData_wins()
    {
        var (id, code) = await InBothAsync("42-SY-2");
        await Patch(id, "RemarksAdnoc", "Initial", "From K Data");
        app.Simpro.Exec("UPDATE tbl_flangeCustomFieldValue SET value = 'Hold' WHERE ownerId = @p0", id);   // G1S edited in Simpro

        var applied = await RunAsync("apply");
        var items = Items(applied).Where(i => i.Code == code).ToList();
        Assert.Contains(items, i => i is { Field: "G1S", Outcome: "Changed in Simpro" });
        Assert.Contains(items, i => i is { Field: "REMARK", Outcome: "Waiting" });
        Assert.Equal("Initial", app.Simpro.Header(id, "WorkInstructions"));   // nothing written for a held equipment
        Assert.Equal("HeldForConflicts", applied.GetProperty("run").GetProperty("status").GetString());

        (await admin.PostAsync($"/api/simpro/force/{id}?force=true", null)).EnsureSuccessStatusCode();
        await RunAsync("apply");
        Assert.Equal("From K Data", app.Simpro.Header(id, "WorkInstructions"));
        Assert.Equal("Ongoing", app.Simpro.Custom(id, "G1S"));
        Assert.False(await app.WithDbAsync(db => db.SimproForcedAssets.AnyAsync(f => f.AssetId == id)));
    }

    [Fact]
    public async Task The_Simpro_value_can_be_copied_into_KData()
    {
        var (id, code) = await InBothAsync("42-SY-3");
        app.Simpro.Exec("UPDATE tbl_flangeCustomFieldValue SET value = 'Hold' WHERE ownerId = @p0", id);
        var preview = await RunAsync("preview");
        var item = preview.GetProperty("items").EnumerateArray().First(i => i.GetProperty("assetCode").GetString() == code && i.GetProperty("field").GetString() == "G1S");
        Assert.True(item.GetProperty("canCopy").GetBoolean());

        var copy = await admin.PostAsync($"/api/simpro/items/{item.GetProperty("id").GetInt64()}/copy-to-app", null);
        copy.EnsureSuccessStatusCode();
        var profile = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
        Assert.Equal("Hold", profile.GetProperty("values").GetProperty("G1Status").GetString());
        Assert.DoesNotContain(Items(await RunAsync("preview")), i => i.Code == code && i.Outcome is "Changed in Simpro" or "Waiting");
    }

    [Fact]
    public async Task New_equipment_is_created_with_its_workpack_item_and_linked()
    {
        var (id, code) = await CreateAsync("42-SY-NEW");
        await Patch(id, "G1Status", null, "Completed");

        var applied = await RunAsync("apply");
        Assert.Contains(Items(applied), i => i is { Field: "(equipment)", Outcome: "Created" } && i.Code == code);
        Assert.Equal(1, app.Simpro.Count("tbl_flange", "flangeId = @p0 AND tagNo = @p1 AND flangeStatusId = 9 AND flangeType = 'ANSI'", id, code));
        Assert.Equal(1, app.Simpro.Count("tbl_workpackItem", "itemId = @p0", id));
        Assert.Equal(1, app.Simpro.Count("tbl_flangeHistory", "flangeId = @p0 AND activityId = 1 AND username = 'SYNC-KDATA'", id));
        Assert.Equal("Completed", app.Simpro.Custom(id, "G1S"));
        Assert.Equal(id, await app.WithDbAsync(db => db.Assets.Where(a => a.Id == id).Select(a => a.SimproFlangeId).FirstAsync()));
    }

    [Fact]
    public async Task Not_Applicable_and_N_A_are_the_same_value()
    {
        var (id, code) = await InBothAsync("42-SY-NA", new() { ["G1Status"] = "Not Applicable", ["RemarksAdnoc"] = "Initial" });
        app.Simpro.Exec("UPDATE tbl_flangeCustomFieldValue SET value = 'N/A' WHERE ownerId = @p0", id);
        app.Simpro.Exec("UPDATE tbl_flangeCustomFieldChange SET value = 'N/A' WHERE ownerId IN (SELECT activityRecordId FROM tbl_flangeChanges WHERE flangeId = @p0)", id);

        var preview = await RunAsync("preview");
        Assert.DoesNotContain(Items(preview), i => i.Code == code && i.Field == "G1S");
    }

    [Fact]
    public async Task The_last_run_can_be_undone()
    {
        var (id, _) = await InBothAsync("42-SY-UNDO");
        await Patch(id, "RemarksAdnoc", "Initial", "To be undone");
        var events = app.Simpro.Count("tbl_flangeHistory", "flangeId = @p0", id);
        var applied = await RunAsync("apply");
        Assert.Equal("To be undone", app.Simpro.Header(id, "WorkInstructions"));

        var runId = applied.GetProperty("run").GetProperty("id").GetInt64();
        var undo = await admin.PostAsync($"/api/simpro/runs/{runId}/undo", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var undoRun = (await undo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run");
        Assert.True(undoRun.GetProperty("status").GetString() == "Applied", undoRun.GetProperty("message").GetString());
        Assert.Equal("Initial", app.Simpro.Header(id, "WorkInstructions"));
        Assert.Equal(events, app.Simpro.Count("tbl_flangeHistory", "flangeId = @p0", id));
        Assert.Equal(SyncStatus.Undone, await app.WithDbAsync(db => db.SimproSyncRuns.Where(r => r.Id == runId).Select(r => r.Status).FirstAsync()));
    }
}
