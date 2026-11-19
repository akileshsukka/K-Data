using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>The parts of a split item are one row of the client Excel, and a changed cell of that row waits for a decision.</summary>
public sealed class GroupedExcelTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public GroupedExcelTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private static MultipartFormDataContent File(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }

    private async Task<int> PrepareUnitAsync(string unit)
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());
        if (!await app.WithDbAsync(db => db.ExcelTemplates.AnyAsync(t => t.UnitId == unitId)))
            (await admin.PutAsync($"/api/excel-templates/{unitId}", File(TestWorkbooks.Template(), "Template.xlsx"))).EnsureSuccessStatusCode();
        return unitId;
    }

    private async Task<Guid> CreateAsync(int unitId, string tag)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Pumps" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task PatchAsync(Guid id, string field, string? to)
    {
        var profile = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
        var from = profile.GetProperty("values").GetProperty(field).GetString();
        (await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field, from, to } } })).EnsureSuccessStatusCode();
    }

    /// <summary>Splits the asset in two; <paramref name="b"/> changes the gates of the second part (gate, current status → status).</summary>
    private async Task<Guid> SplitAsync(Guid id, string tagA, string tagB, Func<int, string?, string?> b)
    {
        var preview = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        object[] Gates(Func<int, string?, string?> f) => preview.GetProperty("gates").EnumerateArray().Select(g =>
        {
            var gate = g.GetProperty("gate").GetInt32();
            return (object)new { gate, status = f(gate, g.GetProperty("status").GetString()), comments = g.GetProperty("comments").GetString() };
        }).ToArray();
        var response = await admin.PostAsJsonAsync($"/api/assets/{id}/split", new
        {
            stamp = preview.GetProperty("stamp").GetString(),
            parts = new object[] { new { tag = tagA, gates = Gates((_, s) => s) }, new { tag = tagB, gates = Gates(b) } },
            readings = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("parts")[1].GetProperty("id").GetGuid();
    }

    private async Task<byte[]> ExportAsync(int unitId)
    {
        var response = await admin.GetAsync($"/api/units/{unitId}/excel/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static List<IXLTableRow> Rows(XLWorkbook wb) => TestWorkbooks.Master(wb).DataRange.Rows().ToList();

    [Fact]
    public async Task The_parts_of_a_split_item_are_one_row_with_the_most_advanced_status()
    {
        var unitId = await PrepareUnitAsync("Unit 31");
        var a = await CreateAsync(unitId, "31-P-501A/B");
        await CreateAsync(unitId, "31-V-502");
        await PatchAsync(a, "G1Status", "Completed");
        await PatchAsync(a, "G2Status", "Completed");
        // B: G2 not finished, G3 on hold; G4 Not Applicable on A only.
        var b = await SplitAsync(a, "31-P-501A", "31-P-501B", (g, s) => g == 2 ? "Ongoing" : g == 3 ? "Hold" : s);
        await PatchAsync(a, "G4Status", "Not Applicable");
        await PatchAsync(b, "G4Status", "Ongoing");
        await PatchAsync(a, "PreservationRemarks", "Motor ok");
        await PatchAsync(b, "PreservationRemarks", "Seal leak");

        using var wb = new XLWorkbook(new MemoryStream(await ExportAsync(unitId)));
        var rows = Rows(wb);
        Assert.DoesNotContain(rows, r => r.Field("EQUIP NO").GetString() is "31-P-501A" or "31-P-501B");
        var row = Assert.Single(rows, r => r.Field("EQUIP NO").GetString() == "31-P-501A/B");
        Assert.Contains(rows, r => r.Field("EQUIP NO").GetString() == "31-V-502");

        Assert.Equal($"{a},{b}", row.Field("Key").GetString());
        Assert.Equal("Completed", row.Field("G1-Status").GetString());
        Assert.Equal("Completed", row.Field("G2-Status").GetString());   // A finished it
        Assert.Equal("Hold", row.Field("G3-Status").GetString());        // B on hold, A empty
        Assert.Equal("Ongoing", row.Field("G4-Status").GetString());     // N/A on A does not count
        Assert.Equal("31-P-501A: Motor ok / 31-P-501B: Seal leak", row.Field("Preservation ").GetString());
        Assert.Equal("Pumps", row.Field("EQUIP DESCRIPTION").GetString());
    }

    [Fact]
    public async Task The_unit_dashboard_counts_a_split_item_once_as_the_client()
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());
        var a = await CreateAsync(unitId, "39-P-801A/B");
        await CreateAsync(unitId, "39-V-802");
        await PatchAsync(a, "G1Status", "Completed");
        await PatchAsync(a, "ReadyForPreservation", "No");
        var b = await SplitAsync(a, "39-P-801A", "39-P-801B", (g, s) => g == 1 ? "Ongoing" : s);
        await PatchAsync(b, "ReadyForPreservation", "Yes");

        // The unit has one split item and opens "By equipment" until someone changes the default.
        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/client-view");
        Assert.False(view.GetProperty("grouped").GetBoolean());
        Assert.Equal(1, view.GetProperty("splitItems").GetInt32());

        static int G1(JsonElement d, string what) => d.GetProperty("activities").EnumerateArray().First(x => x.GetProperty("code").GetString() == "G1").GetProperty(what).GetInt32();
        var byEquipment = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard");
        Assert.False(byEquipment.GetProperty("grouped").GetBoolean());
        var asClient = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard?grouped=true");
        Assert.True(asClient.GetProperty("grouped").GetBoolean());

        // One tag less; its G1 is the most advanced of the parts (Completed), its Ready is Yes (B).
        Assert.Equal(byEquipment.GetProperty("totalTags").GetInt32() - 1, asClient.GetProperty("totalTags").GetInt32());
        Assert.Equal(G1(byEquipment, "completed"), G1(asClient, "completed"));
        Assert.Equal(G1(byEquipment, "pending") - 1, G1(asClient, "pending"));
        Assert.Equal(byEquipment.GetProperty("readyForPreservation").GetInt32(), asClient.GetProperty("readyForPreservation").GetInt32());
        Assert.Equal(byEquipment.GetProperty("byCurrentGate").EnumerateObject().Sum(p => p.Value.GetInt32()) - 1,
            asClient.GetProperty("byCurrentGate").EnumerateObject().Sum(p => p.Value.GetInt32()));

        // The figure counts the tag once and lists both parts, so the register can show them.
        var tags = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard/cards/tags?grouped=true");
        Assert.Equal(asClient.GetProperty("totalTags").GetInt32(), tags.GetProperty("count").GetInt32());
        var ids = tags.GetProperty("assetIds").EnumerateArray().Select(x => x.GetGuid()).ToList();
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);

        // As the unit default, the dashboard (and the PDF, which asks without a view) is grouped.
        (await admin.PutAsJsonAsync($"/api/units/{unitId}/client-view", new { grouped = true })).EnsureSuccessStatusCode();
        Assert.True((await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard")).GetProperty("grouped").GetBoolean());
        Assert.Equal("39-P-801A/B", (await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Equipment"))
            .EnumerateArray().First(r => r.GetProperty("id").GetGuid() == b).GetProperty("clientTag").GetString());
    }

    [Fact]
    public async Task A_changed_cell_of_a_grouped_row_is_not_applied_and_waits_for_a_decision()
    {
        var unitId = await PrepareUnitAsync("Unit 41");
        var a = await CreateAsync(unitId, "41-P-601A/B");
        await CreateAsync(unitId, "41-V-602");
        await PatchAsync(a, "G1Status", "Completed");
        var b = await SplitAsync(a, "41-P-601A", "41-P-601B", (g, s) => g == 1 ? "Ongoing" : s);
        await PatchAsync(a, "PreservationRemarks", "Motor ok");
        await PatchAsync(b, "PreservationRemarks", "Seal leak");

        var edited = TestWorkbooks.Edit(await ExportAsync(unitId), wb =>
        {
            var row = TestWorkbooks.Row(wb, "41-P-601A/B");
            row.Field("G2-Status").SetValue("Completed");
            row.Field("G1-Status").SetValue("Ongoing");     // shown Completed (A): changed, A gets a decision, B already has it
            TestWorkbooks.Row(wb, "41-V-602").Field("G1-Status").SetValue("Completed");
        });
        var response = await admin.PostAsync($"/api/units/{unitId}/excel/import", File(edited, "edited.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        var changes = preview.GetProperty("changes").EnumerateArray()
            .Select(c => (Tag: c.GetProperty("assetTag").GetString(), Field: c.GetProperty("field").GetString(), Outcome: c.GetProperty("outcome").GetString(), New: c.GetProperty("newValue").GetString()))
            .ToList();

        Assert.Contains(("41-P-601A", "G2Status", "Grouped", "Completed"), changes);
        Assert.Contains(("41-P-601B", "G2Status", "Grouped", "Completed"), changes);
        Assert.Contains(("41-P-601A", "G1Status", "Grouped", "Ongoing"), changes);
        Assert.Contains(("41-P-601B", "G1Status", "No change", "Ongoing"), changes);
        Assert.Contains(("41-V-602", "G1Status", "Applied", "Completed"), changes);
        // Cells left as the row showed them (the joined remarks, the tag) are not changes.
        Assert.DoesNotContain(changes, c => c.Tag!.StartsWith("41-P-601") && c.Field is "PreservationRemarks" or "Tag");
        Assert.Equal(3, preview.GetProperty("run").GetProperty("counts").GetProperty("grouped").GetInt32());

        var id = preview.GetProperty("run").GetProperty("id").GetInt64();
        (await admin.PostAsync($"/api/imports/{id}/apply", null)).EnsureSuccessStatusCode();

        // Nothing written to the parts; the other row was applied; the decisions are open issues.
        var pa = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}");
        var pb = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{b}");
        Assert.Equal("Completed", pa.GetProperty("values").GetProperty("G1Status").GetString());
        Assert.Equal(JsonValueKind.Null, pa.GetProperty("values").GetProperty("G2Status").ValueKind);
        Assert.Equal(JsonValueKind.Null, pb.GetProperty("values").GetProperty("G2Status").ValueKind);
        Assert.Equal(3, await app.WithDbAsync(db => db.ImportChanges.CountAsync(c => c.ImportRunId == id
            && c.Outcome == PresApp.Domain.Imports.ImportOutcome.Grouped && c.Resolution == PresApp.Domain.Imports.IssueResolution.Open)));

        // The decision: G2 Completed only for A. Applied like an Excel cell: Current Gate follows the completed gates.
        long Issue(Guid asset, string field) => app.WithDbAsync(db => db.ImportChanges.Where(c => c.ImportRunId == id && c.AssetId == asset && c.Field == field)
            .Select(c => c.Id).FirstAsync()).GetAwaiter().GetResult();
        var applied = await admin.PostAsync($"/api/import-issues/{Issue(a, "G2Status")}/apply", null);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{a}");
        Assert.Equal("Completed", after.GetProperty("values").GetProperty("G2Status").GetString());
        Assert.Equal("G2", after.GetProperty("currentGate").GetString());
        Assert.True(await app.WithDbAsync(db => db.ChangeLogs.AnyAsync(c => c.AssetId == a && c.Field == "G2Status" && c.Source == PresApp.Domain.Common.ChangeSource.Excel)));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/import-issues/{Issue(a, "G2Status")}/apply", null)).StatusCode);

        // B changed in the app after the import: applying is refused and nothing is written.
        await PatchAsync(b, "G2Status", "Hold");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/import-issues/{Issue(b, "G2Status")}/apply", null)).StatusCode);
        Assert.Equal("Hold", (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{b}")).GetProperty("values").GetProperty("G2Status").GetString());

        // A change that is not of a grouped row cannot be applied from Import issues.
        var other = await app.WithDbAsync(db => db.ImportChanges.Where(c => c.ImportRunId == id && c.Outcome != PresApp.Domain.Imports.ImportOutcome.Grouped).Select(c => c.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/import-issues/{other}/apply", null)).StatusCode);
    }
}
