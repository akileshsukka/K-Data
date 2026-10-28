using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>The engineers' piping line list: onboarding, piping lists per asset type, export and import of the K Data file.</summary>
public sealed class PipingListTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public PipingListTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private static readonly string[] Headers =
        ["Sr. No.", "Tag No", "P&ID Number", "Process Code", "Process Description", "Current Activity", "EM Walkdown Status", "Bulk liquid drained", "Inspection Done ?", "Cleaning Required ?", "Cleaned?"];

    /// <summary>Same layout as "U39 ALL PIPING LINES 27 OCT": table Table1 from B2, plus a Summary sheet.</summary>
    private static byte[] EngineerList(params object?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Line List");
        for (var c = 0; c < Headers.Length; c++) ws.Cell(2, c + 2).SetValue(Headers[c]);
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++) ws.Cell(3 + r, c + 2).SetValue(XLCellValue.FromObject(rows[r][c]));
        ws.Range(2, 2, 2 + rows.Length, 1 + Headers.Length).CreateTable("Table1");
        wb.AddWorksheet("Summary").Cell("B2").SetValue("UNIT 39 PIPING STATUS SUMMARY");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static MultipartFormDataContent File(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }

    private Task<int> UnitIdAsync(string unit) => app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());

    private async Task<JsonElement> PostAsync(string url, byte[] bytes)
    {
        var response = await admin.PostAsync(url, File(bytes, "list.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ApplyAsync(JsonElement preview)
    {
        var response = await admin.PostAsync($"/api/imports/{preview.GetProperty("run").GetProperty("id").GetInt64()}/apply", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(string Tag, string Field, string Outcome, string? NewValue, string? Reason)> Changes(JsonElement detail) =>
        detail.GetProperty("changes").EnumerateArray().Select(c => (
            c.GetProperty("assetTag").GetString() ?? "", c.GetProperty("field").GetString()!, c.GetProperty("outcome").GetString()!,
            c.GetProperty("newValue").GetString(), c.GetProperty("reason").GetString())).ToList();

    private async Task<JsonElement> RowAsync(int unitId, string tag) =>
        (await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Pipeline")).EnumerateArray()
        .Single(r => r.GetProperty("tag").GetString() == tag);

    private const string Two = "39-0061-RSX-16\"-13030-H\n39-0061-RSX-12\"-13030-H";

    [Fact]
    public async Task The_engineers_line_list_creates_lines_and_a_second_load_only_replaces_new_values()
    {
        var unitId = await UnitIdAsync("Unit 39");
        var list = EngineerList(
            [1, "39-0001-HCX-18\"-61055-N", "39-00-30001\n39-00-30002", "HCX", "HYDROCARBON, SOUR", "Line cut\ndebris inside; jetting required.\n", "Completed", "Yes", "Yes", "Hydromilling", null],
            [2, Two, "39-00-30010", "RSX", "X", "Line Cut", "Completed", "Yes", "No", "TBC", null],
            [3, "39-0204-SCH-2\"-61011-N", "39-00-30020", "SC", "STEAM CONDENSATE", null, "Completed", "Yes", "No", "TBC", null],
            [4, "39-0300-HC-2\"-61055-N", null, "HC", "HYDROCARBON", "Draining", "Completed", "Ongoing", "Maybe", "TBC", null]);

        var preview = await PostAsync($"/api/units/{unitId}/excel/onboarding", list);
        var changes = Changes(preview);
        Assert.Equal("Previewed", preview.GetProperty("run").GetProperty("status").GetString());
        Assert.Contains(changes, c => c is { Tag: Two, Field: "(asset)", Outcome: "Created" });
        Assert.Contains(changes, c => c is { Tag: "39-0204-SCH-2\"-61011-N", Field: "Process Code", Outcome: "Warning" } && c.Reason!.Contains("K Data keeps SCH"));
        Assert.Contains(changes, c => c is { Tag: "39-0300-HC-2\"-61055-N", Field: "CurrentActivity", Outcome: "Invalid" } && c.Reason!.Contains("for lines"));
        Assert.Contains(changes, c => c is { Tag: "39-0300-HC-2\"-61055-N", Field: "G1BulkLiquidDrained", Outcome: "Invalid" });
        Assert.Contains(changes, c => c is { Tag: "39-0300-HC-2\"-61055-N", Field: "Inspection Done", Outcome: "Invalid" });
        Assert.Null(preview.GetProperty("run").GetProperty("message").GetString());   // Process Code/Description are not "not loaded"

        await ApplyAsync(preview);
        var first = await RowAsync(unitId, "39-0001-HCX-18\"-61055-N");
        Assert.Equal("Line cut\nDebris inside; jetting required", first.GetProperty("currentActivity").GetString());
        Assert.Equal("Completed", first.GetProperty("bulkLiquidDrained").GetString());
        Assert.Equal("Yes", first.GetProperty("custom").GetProperty("InspectionDone").GetString());
        Assert.Equal("Hydromilling", first.GetProperty("custom").GetProperty("CleaningRequired").GetString());
        var two = await RowAsync(unitId, Two);
        Assert.Equal(16m, two.GetProperty("line").GetProperty("sizeIn").GetDecimal());
        Assert.Equal("RSX", two.GetProperty("line").GetProperty("service").GetString());
        Assert.Equal("Line cut", two.GetProperty("currentActivity").GetString());
        Assert.Equal("SCH", (await RowAsync(unitId, "39-0204-SCH-2\"-61011-N")).GetProperty("line").GetProperty("service").GetString());

        // Second load of the same list (one value changed): nothing is created again, only the new value is applied.
        var again = EngineerList(
            [1, "39-0001-HCX-18\"-61055-N", "39-00-30001\n39-00-30002", "HCX", "HYDROCARBON, SOUR", "Line cut\ndebris inside; jetting required.\n", "Completed", "Yes", "Yes", "Hydromilling", "Yes"],
            [2, Two, "39-00-30010", "RSX", "X", "Line Cut", "Completed", "Yes", "No", "TBC", null]);
        var second = Changes(await ApplyAsync(await PostAsync($"/api/units/{unitId}/excel/onboarding", again)));
        Assert.DoesNotContain(second, c => c.Outcome == "Created");
        Assert.Single(second, c => c.Outcome == "Applied");
        Assert.Contains(second, c => c is { Tag: "39-0001-HCX-18\"-61055-N", Field: "Cleaned", Outcome: "Applied", NewValue: "Yes" });
        Assert.Equal(1, await app.WithDbAsync(db => db.Assets.CountAsync(a => a.Tag == Two)));
    }

    [Fact]
    public async Task Lines_and_equipment_use_their_own_current_activity_list()
    {
        var unitId = await UnitIdAsync("Unit 32");
        var piping = await app.WithDbAsync(db => db.Disciplines.Where(d => d.AssetType == AssetType.Pipeline).Select(d => d.Id).FirstAsync());
        var line = await (await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId = piping, tag = "32-0001-HC-2\"-61055-N" })).Content.ReadFromJsonAsync<JsonElement>();
        var lineId = line.GetProperty("id").GetString();

        var bad = await admin.PatchAsJsonAsync($"/api/assets/{lineId}", new { changes = new[] { new { field = "CurrentActivity", from = (string?)null, to = "Draining" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var good = await admin.PatchAsJsonAsync($"/api/assets/{lineId}", new { changes = new[] { new { field = "CurrentActivity", from = (string?)null, to = "spool dropped\nLine Cut" } } });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var drained = await admin.PatchAsJsonAsync($"/api/assets/{lineId}", new { changes = new[] { new { field = "G1BulkLiquidDrained", from = (string?)null, to = "Yes" } } });
        Assert.Equal(HttpStatusCode.OK, drained.StatusCode);
        var values = (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{lineId}")).GetProperty("values");
        Assert.Equal("Spool dropped\nLine cut", values.GetProperty("CurrentActivity").GetString());
        Assert.Equal("Completed", values.GetProperty("G1BulkLiquidDrained").GetString());

        var staticId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var equipment = await (await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId = staticId, tag = "32-V-900" })).Content.ReadFromJsonAsync<JsonElement>();
        var equipmentId = equipment.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/assets/{equipmentId}", new { changes = new[] { new { field = "CurrentActivity", from = (string?)null, to = "Line cut" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/assets/{equipmentId}", new { changes = new[] { new { field = "CurrentActivity", from = (string?)null, to = "Draining" } } })).StatusCode);

        var fields = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/fields");
        var activity = fields.EnumerateArray().Single(f => f.GetProperty("key").GetString() == "CurrentActivity");
        Assert.Equal("PipingCurrentActivity", activity.GetProperty("pipelineOptionSet").GetString());
        Assert.True(activity.GetProperty("pipelineMultiple").GetBoolean());
    }

    [Fact]
    public async Task The_piping_file_keeps_the_engineers_layout_and_imports_only_what_changed()
    {
        var unitId = await UnitIdAsync("Unit 41");
        var list = EngineerList(
            [1, "41-0001-HCX-6\"-61055-N", "41-00-1", "HCX", "", "Line cut", "Completed", "Yes", "Yes", "Hydromilling", null],
            [2, "41-0002-LS-2\"-61055-N", "41-00-2", "LS", "", null, "Completed", "Yes", "No", "TBC", null],
            [3, "41-0003-LS-3\"-61055-N", "41-00-2", "LS", "", null, "Completed", null, null, "TBC", null]);
        await ApplyAsync(await PostAsync($"/api/units/{unitId}/excel/onboarding", list));

        var response = await admin.GetAsync($"/api/units/{unitId}/excel/piping/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var table = wb.Worksheet("Line List").Table("Table1");
            Assert.Equal(Headers.Append("Key"), table.HeadersRow().Cells().Select(c => c.GetString()));
            Assert.Equal(2, table.HeadersRow().RowNumber());
            Assert.Equal(2, table.HeadersRow().FirstCell().Address.ColumnNumber);
            Assert.True(wb.Worksheet("Line List").Column(13).IsHidden);
            var rows = table.DataRange.Rows().ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal("LEAN SOLVENT (LEAN AMINE)", rows[1].Field("Process Description").GetString());
            Assert.Equal("Yes", rows[0].Field("Bulk liquid drained").GetString());
            Assert.Equal("", rows[2].Field("Bulk liquid drained").GetString());
            Assert.Equal("No", rows[2].Field("Inspection Done ?").GetString());   // empty Inspection Done is No

            var summary = wb.Worksheet("Summary");
            Assert.Equal("UNIT 41 PIPING STATUS SUMMARY", summary.Cell("B2").GetString());
            Assert.Equal("All lines", summary.Cell("B5").GetString());
            Assert.Equal("HCX", summary.Cell("B6").GetString());
            Assert.Equal("LS", summary.Cell("B7").GetString());
            Assert.Equal("COUNTIFS(Table1[Tag No],\"<>\")", summary.Cell("D5").FormulaA1);
            Assert.Contains("Table1[Bulk liquid drained],G$4", summary.Cell("G7").FormulaA1);   // the criteria is the header cell ("Yes")
            Assert.Equal("Hydromilling", summary.Cell("K4").GetString());
            Assert.Equal("TBC", summary.Cell("L4").GetString());
            Assert.Equal("Cleaning Performed", summary.Cell("M3").GetString());
            Assert.Equal(XLWorksheetVisibility.VeryHidden, wb.Worksheet("_meta").Visibility);
            Assert.Equal("Piping", wb.Worksheet("_meta").Cell("B3").GetString());

            // The engineer: inspection done on line 2, line 3 drained, cleaning done on line 1; someone else changes line 2 P&ID meanwhile.
            rows[1].Field("Inspection Done ?").SetValue("Yes");
            rows[1].Field("P&ID Number").SetValue("41-00-9");
            rows[2].Field("Bulk liquid drained").SetValue("yes");
            rows[0].Field("Cleaned?").SetValue("Yes");
            rows[0].Field("Current Activity").SetValue("Line cut\nSpool dropped");
            rows[2].Field("Cleaning Required ?").SetValue("Sandblasting");
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            bytes = ms.ToArray();
        }
        var line2 = await app.WithDbAsync(db => db.Assets.Where(a => a.Tag == "41-0002-LS-2\"-61055-N").Select(a => a.Id).FirstAsync());
        (await admin.PatchAsJsonAsync($"/api/assets/{line2}", new { changes = new[] { new { field = "PidNumber", from = "41-00-2", to = "41-00-7" } } })).EnsureSuccessStatusCode();

        var preview = await PostAsync($"/api/units/{unitId}/excel/import", bytes);
        Assert.Equal("Previewed", preview.GetProperty("run").GetProperty("status").GetString());
        var changes = Changes(preview);
        Assert.Contains(changes, c => c is { Tag: "41-0002-LS-2\"-61055-N", Field: "PidNumber", Outcome: "Conflict" });
        Assert.Contains(changes, c => c is { Tag: "41-0003-LS-3\"-61055-N", Field: "Cleaning Required", Outcome: "Invalid" });
        Assert.Equal(4, changes.Count(c => c.Outcome == "Applied"));

        await ApplyAsync(preview);
        var r1 = await RowAsync(unitId, "41-0001-HCX-6\"-61055-N");
        Assert.Equal("Yes", r1.GetProperty("custom").GetProperty("Cleaned").GetString());
        Assert.Equal("Line cut\nSpool dropped", r1.GetProperty("currentActivity").GetString());
        Assert.Equal("Yes", (await RowAsync(unitId, "41-0002-LS-2\"-61055-N")).GetProperty("custom").GetProperty("InspectionDone").GetString());
        var r3 = await RowAsync(unitId, "41-0003-LS-3\"-61055-N");
        Assert.Equal("Completed", r3.GetProperty("bulkLiquidDrained").GetString());
        Assert.Equal("TBC", r3.GetProperty("custom").GetProperty("CleaningRequired").GetString());
        Assert.Equal("41-00-7", (await RowAsync(unitId, "41-0002-LS-2\"-61055-N")).GetProperty("line").GetProperty("pidNumber").GetString());
    }

    [Fact]
    public async Task A_pasted_list_of_line_numbers_goes_into_a_circuit_and_leaves_the_old_one()
    {
        var unitId = await UnitIdAsync("Unit 42");
        var list = EngineerList(
            [1, "42-0001-HC-2\"-61055-N", null, "HC", "", null, "Completed", "Yes", "No", "TBC", null],
            [2, "42-0002-HC-1-1/2\"-61055-N\n42-0002-HC-3/4\"-61055-N", null, "HC", "", null, "Completed", "Yes", "No", "TBC", null],
            [3, "42-0003-HC-2\"-61055-N", null, "HC", "", null, "Completed", "Yes", "No", "TBC", null]);
        await ApplyAsync(await PostAsync($"/api/units/{unitId}/excel/onboarding", list));

        var types = await admin.GetFromJsonAsync<JsonElement>("/api/group-types");
        var circuitType = types.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Circuit").GetProperty("id").GetInt32();
        async Task<int> CircuitAsync(string name)
        {
            var created = await (await admin.PostAsJsonAsync("/api/groups", new { typeId = circuitType, name, unitId })).Content.ReadFromJsonAsync<JsonElement>();
            return created.GetProperty("group").GetProperty("id").GetInt32();
        }
        var c1 = await CircuitAsync("Circuit one");
        var c2 = await CircuitAsync("Circuit two");

        // Copied from Excel: a multi-number cell comes quoted with doubled quotes; the size can be written 1.5".
        var text = "Tag No\n42-0001-HC-2\"-61055-N\n42-0002-HC-1.5\"-61055-N\n42-0099-HC-2\"-61055-N\n";
        var check = await (await admin.PostAsJsonAsync($"/api/groups/{c1}/members/paste", new { text, apply = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, check.GetProperty("toAdd").GetInt32());
        Assert.Equal(1, check.GetProperty("notFound").GetInt32());
        Assert.Equal(0, await app.WithDbAsync(db => db.GroupMembers.CountAsync(m => m.GroupId == c1)));

        await admin.PostAsJsonAsync($"/api/groups/{c1}/members/paste", new { text, apply = true });
        Assert.Equal(2, await app.WithDbAsync(db => db.GroupMembers.CountAsync(m => m.GroupId == c1)));

        var moved = await (await admin.PostAsJsonAsync($"/api/groups/{c2}/members/paste", new { text = "\"42-0002-HC-1-1/2\"\"-61055-N\n42-0002-HC-3/4\"\"-61055-N\"", apply = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, moved.GetProperty("toAdd").GetInt32());
        Assert.Contains(moved.GetProperty("rows").EnumerateArray(), r => r.GetProperty("note").GetString()?.StartsWith("Moved from") == true);
        Assert.Equal(1, await app.WithDbAsync(db => db.GroupMembers.CountAsync(m => m.GroupId == c1)));
        Assert.Equal(await app.WithDbAsync(db => db.Groups.Where(g => g.Id == c2).Select(g => g.Code).FirstAsync()), (await RowAsync(unitId, "42-0002-HC-1-1/2\"-61055-N\n42-0002-HC-3/4\"-61055-N")).GetProperty("circuit").GetString());
    }

    [Fact]
    public async Task Summary_notes_come_from_the_list_go_to_the_file_and_come_back_by_date()
    {
        var unitId = await UnitIdAsync("Unit 31");
        byte[] list;
        using (var wb = new XLWorkbook(new MemoryStream(EngineerList(
                   [1, "31-0001-HC-2\"-61055-N", null, "HC", "", null, "Completed", "Yes", "Yes", "Hydromilling", "Yes"],
                   [2, "31-0002-HC-2\"-61055-N", null, "HC", "", null, "Completed", "Yes", "No", "Hydromilling", null],
                   [3, "31-0003-HC-2\"-61055-N", null, "HC", "", null, "Completed", "Yes", "No", "TBC", null]))))
        {
            // Same layout as the engineers' Summary: table, an empty row, then the notes.
            var s = wb.Worksheet("Summary");
            s.Cell("B5").SetValue("All lines"); s.Cell("B6").SetValue("HC");
            s.Cell("B8").SetValue("Estimated date of Cleaning completion : 16 Nov 2026");
            s.Cell("B10").SetValue("26 Oct:"); s.Cell("B11").SetValue("Petrofac will raise FRA and PTW on 27 Oct to clean identified pipes.");
            s.Cell("B12").SetValue("27 Oct:"); s.Cell("B13").SetValue("FRA preparation started by PA"); s.Cell("B14").SetValue("Camera : CICPA sticker in progress.");
            using var ms = new MemoryStream(); wb.SaveAs(ms); list = ms.ToArray();
        }
        var onboarding = Changes(await ApplyAsync(await PostAsync($"/api/units/{unitId}/excel/onboarding", list)));
        Assert.Equal(3, onboarding.Count(c => c.Field.StartsWith("Summary note") && c.Outcome == "Applied"));

        var notes = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/piping-notes");
        Assert.Equal("Estimated date of Cleaning completion : 16 Nov 2026", notes[0].GetProperty("text").GetString());
        Assert.Equal("FRA preparation started by PA\nCamera : CICPA sticker in progress.", notes.EnumerateArray().Single(n => n.GetProperty("date").GetString()?.EndsWith("-10-27") == true).GetProperty("text").GetString());

        var bytes = await (await admin.GetAsync($"/api/units/{unitId}/excel/piping/export")).Content.ReadAsByteArrayAsync();
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var s = wb.Worksheet("Summary");
            Assert.Equal("% of scope", s.Cell("N4").GetString());
            Assert.Equal("IF(SUM(K5)>0,M5/SUM(K5),\"\")", s.Cell("N5").FormulaA1);   // cleaned / Hydromilling (TBC is not scope)
            var texts = Enumerable.Range(7, 12).Select(r => s.Cell(r, 2).GetString()).ToList();
            Assert.Contains("Estimated date of Cleaning completion : 16 Nov 2026", texts);
            Assert.Contains("27 Oct:", texts);

            // The engineer: changes the 27 Oct note and adds 28 Oct; someone changes 26 Oct in the app meanwhile.
            var row27 = texts.IndexOf("27 Oct:") + 7;
            s.Cell(row27 + 2, 2).SetValue("Camera : sticker received.");
            s.Cell(row27 + 3, 2).SetValue("28 Oct:");
            s.Cell(row27 + 4, 2).SetValue("Cleaning of 39-0001 started.");
            var row26 = texts.IndexOf("26 Oct:") + 7;
            s.Cell(row26 + 1, 2).SetValue("Edited in the file.");
            using var ms = new MemoryStream(); wb.SaveAs(ms); bytes = ms.ToArray();
        }
        var date26 = notes.EnumerateArray().Single(n => n.GetProperty("date").GetString()?.EndsWith("-10-26") == true);
        (await admin.PutAsJsonAsync($"/api/units/{unitId}/piping-notes", new { date = date26.GetProperty("date").GetString(), text = "Edited in the app.", expected = date26.GetProperty("text").GetString() })).EnsureSuccessStatusCode();

        var preview = await PostAsync($"/api/units/{unitId}/excel/import", bytes);
        Assert.Equal(0, await app.WithDbAsync(db => db.PipingNotes.CountAsync(n => n.Text.Contains("39-0001"))));   // preview writes nothing
        var changes = Changes(await ApplyAsync(preview));
        Assert.Contains(changes, c => c is { Field: "Summary note 27 Oct 2026", Outcome: "Applied" });
        Assert.Contains(changes, c => c is { Field: "Summary note 28 Oct 2026", Outcome: "Applied", NewValue: "Cleaning of 39-0001 started." });
        Assert.Contains(changes, c => c is { Field: "Summary note 26 Oct 2026", Outcome: "Conflict" });
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/piping-notes");
        Assert.Equal(4, after.GetArrayLength());
        Assert.Contains(after.EnumerateArray(), n => n.GetProperty("text").GetString() == "Edited in the app.");
    }

    [Fact]
    public async Task One_piping_template_for_the_project_can_be_replaced_with_its_own_formulas()
    {
        var unitId = await UnitIdAsync("Unit 541");
        await ApplyAsync(await PostAsync($"/api/units/{unitId}/excel/onboarding", EngineerList(
            [1, "541-0001-HC-2\"-61055-N", null, "HC", "", null, "Completed", "Yes", "Yes", "Hydromilling", "Yes"],
            [2, "541-0002-LS-2\"-61055-N", null, "LS", "", null, null, null, "No", "TBC", null])));

        var sum = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/piping-summary");
        Assert.Equal(2, sum.GetProperty("lines").GetInt32());
        Assert.Equal(1, sum.GetProperty("walkdown").GetProperty("done").GetInt32());
        Assert.Equal(1, sum.GetProperty("inspected").GetProperty("done").GetInt32());
        Assert.Equal(1, sum.GetProperty("cleaned").GetProperty("total").GetInt32());   // scope = Hydromilling only, TBC is not scope
        Assert.Contains(sum.GetProperty("cleaning").EnumerateArray(), c => c.GetProperty("value").GetString() == "TBC" && !c.GetProperty("inScope").GetBoolean());

        var info = await admin.GetFromJsonAsync<JsonElement>("/api/excel-templates/piping");
        Assert.True(info.GetProperty("isDefault").GetBoolean());

        // Start from the default, change it: a column of our own, another sheet name, a different % formula.
        var bytes = await (await admin.GetAsync("/api/excel-templates/piping/file")).Content.ReadAsByteArrayAsync();
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var table = wb.Worksheet("Line List").Table("Table1");
            table.InsertColumnsAfter(1);
            table.HeadersRow().LastCell().SetValue("Engineer remark");
            wb.Worksheet("Line List").Name = "Lines U{UNIT}";
            var summary = wb.Worksheet("Summary");
            Assert.Equal("{CODE}", summary.Cell("B6").GetString());
            summary.Cell("N6").FormulaA1 = "IF(D6>0,M6/D6,\"\")";
            using var ms = new MemoryStream(); wb.SaveAs(ms); bytes = ms.ToArray();
        }
        var file = new ByteArrayContent(bytes);
        var uploaded = await (await admin.PutAsync("/api/excel-templates/piping", new MultipartFormDataContent { { file, "file", "Piping v2.xlsx" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(uploaded.GetProperty("isDefault").GetBoolean());
        Assert.Contains(uploaded.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("Engineer remark"));

        var export = await (await admin.GetAsync($"/api/units/{unitId}/excel/piping/export")).Content.ReadAsByteArrayAsync();
        using (var wb = new XLWorkbook(new MemoryStream(export)))
        {
            Assert.True(wb.TryGetWorksheet("Lines U541", out _));
            var s = wb.Worksheet("Summary");
            Assert.Equal("UNIT 541 PIPING STATUS SUMMARY", s.Cell("B2").GetString());
            Assert.Equal("HC", s.Cell("B6").GetString());
            Assert.Equal("LS", s.Cell("B7").GetString());
            Assert.Equal("IF(D7>0,M7/D7,\"\")", s.Cell("N7").FormulaA1);   // the template's own formula, moved to each code row
            Assert.Equal(XLDataType.DateTime, s.Cell("B3").DataType);
        }
        // Editing and uploading back still works with the new layout.
        var preview = await PostAsync($"/api/units/{unitId}/excel/import", export);
        Assert.Equal("Previewed", preview.GetProperty("run").GetProperty("status").GetString());

        Assert.True((await (await admin.DeleteAsync("/api/excel-templates/piping")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isDefault").GetBoolean());
    }
}
