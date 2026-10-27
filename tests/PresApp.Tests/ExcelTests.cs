using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Common;
using PresApp.Domain.Imports;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class ExcelTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public ExcelTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    // ------------------------------------------------------------------ helpers

    private Task<int> UnitIdAsync(string unit) => app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());

    private async Task<int> PrepareUnitAsync(string unit, params (string Tag, string Discipline)[] assets)
    {
        var unitId = await UnitIdAsync(unit);
        if (!await app.WithDbAsync(db => db.ExcelTemplates.AnyAsync(t => t.UnitId == unitId)))
        {
            var upload = await admin.PutAsync($"/api/excel-templates/{unitId}", FileContent(TestWorkbooks.Template(), "Template.xlsx"));
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }
        await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Rotating" && d.Prefix == null)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.Prefix, "RT")));
        foreach (var (tag, discipline) in assets)
        {
            var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
            var created = await admin.PostAsJsonAsync("/api/assets", new { type = discipline is "Electrical" ? "ElectricalInstrument" : "Equipment", unitId, disciplineId, tag, description = "Item " + tag });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        return unitId;
    }

    private static MultipartFormDataContent FileContent(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }

    private async Task<byte[]> ExportAsync(int unitId)
    {
        var response = await admin.GetAsync($"/api/units/{unitId}/excel/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<JsonElement> PreviewAsync(int unitId, byte[] file)
    {
        var response = await admin.PostAsync($"/api/units/{unitId}/excel/import", FileContent(file, "edited.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ApplyAsync(JsonElement preview)
    {
        var id = preview.GetProperty("run").GetProperty("id").GetInt64();
        var response = await admin.PostAsync($"/api/imports/{id}/apply", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Status(JsonElement detail) => detail.GetProperty("run").GetProperty("status").GetString()!;
    private static string? Message(JsonElement detail) => detail.GetProperty("run").GetProperty("message").GetString();

    private static List<(string Tag, string Field, string Outcome, string? NewValue)> Changes(JsonElement detail) =>
        detail.GetProperty("changes").EnumerateArray().Select(c => (
            c.GetProperty("assetTag").GetString() ?? "", c.GetProperty("field").GetString()!, c.GetProperty("outcome").GetString()!,
            c.GetProperty("newValue").GetString())).ToList();

    private Task<JsonElement> ProfileAsync(string tag) => app.WithDbAsync(async db =>
    {
        var id = await db.Assets.Where(a => a.Tag == tag).Select(a => a.Id).FirstAsync();
        return await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
    });

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_fills_the_template_hides_the_key_and_records_the_session()
    {
        var unitId = await PrepareUnitAsync("Unit 31", ("31-V-1", "Static"), ("31-P-2", "Rotating"), ("31-JB-3", "Electrical"));

        var bytes = await ExportAsync(unitId);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var table = TestWorkbooks.Master(wb);
        Assert.Equal("Master_List__31", table.Worksheet.Name);
        Assert.Contains("Unit-31", table.Worksheet.Cell("B2").GetString());
        Assert.DoesNotContain("{DATE}", table.Worksheet.Cell("B2").GetString());
        Assert.Equal(["31-V-1", "31-P-2", "31-JB-3"], table.DataRange.Rows().Select(r => r.Field("EQUIP NO").GetString()));
        Assert.Equal("Key", table.HeadersRow().LastCell().GetString());
        Assert.True(table.Worksheet.Column(table.HeadersRow().LastCell().Address.ColumnNumber).IsHidden);
        Assert.True(table.DataRange.Rows().Last().WorksheetRow().IsHidden);   // Electrical rows hidden by the filter
        Assert.True(table.DataRange.Rows().First().Field("Category").HasFormula);
        Assert.Equal(XLWorksheetVisibility.VeryHidden, wb.Worksheet("_meta").Visibility);
        Assert.Equal(XLWorksheetVisibility.VeryHidden, wb.Worksheet("_lists").Visibility);
        Assert.DoesNotContain(wb.Worksheets, w => w.IsProtected);
        Assert.Equal([table.Worksheet.Name], wb.Worksheets.Where(w => w.TabSelected).Select(w => w.Name));
        Assert.NotEmpty(table.Worksheet.DataValidations);

        var sessionId = Guid.Parse(wb.Worksheet("_meta").Cell("B1").GetString());
        var session = await app.WithDbAsync(db => db.ExcelSessions.FirstAsync(s => s.Id == sessionId));
        Assert.Equal(ExcelSessionStatus.Open, session.Status);
        Assert.Equal(3, session.RowCount);
        Assert.Contains("31-P-2", session.BaseJson);
    }

    [Fact]
    public async Task Template_categories_become_the_unit_categories_and_feed_the_dashboard()
    {
        var unitId = await PrepareUnitAsync("Unit 32", ("32-V-1", "Static"), ("32-V-2", "Static"));
        var categories = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/categories");
        var labels = categories.EnumerateArray().Select(c => c.GetProperty("label").GetString()).ToList();
        Assert.Equal(["Cat1 :Conflict Between DNV and EnerMech", "Cat 4 Under integrity Testing /Hydro Test", "Cat1 :Removed from Site"], labels);
        var cat4 = categories.EnumerateArray().First(c => c.GetProperty("label").GetString()!.StartsWith("Cat 4"));
        Assert.Equal(2, cat4.GetProperty("keywords").GetArrayLength());
        Assert.Equal("CPY Ops/PF", cat4.GetProperty("actionBy").GetString());

        var bytes = await ExportAsync(unitId);
        var edited = TestWorkbooks.Edit(bytes, wb => TestWorkbooks.Row(wb, "32-V-1").Field("Preservation ").SetValue("Under  HYDRO test, integrity testing"));
        await ApplyAsync(await PreviewAsync(unitId, edited));

        var dashboard = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard");
        var hold = dashboard.GetProperty("hold").EnumerateArray().ToList();
        Assert.Equal(1, hold.First(c => c.GetProperty("label").GetString()!.StartsWith("Cat 4")).GetProperty("count").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("totalHold").GetInt32());
        Assert.Equal(2, dashboard.GetProperty("totalTags").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("availableForPreservation").GetInt32());
    }

    [Fact]
    public async Task Dashboard_block_is_rebuilt_from_the_categories_saved_in_the_app()
    {
        var unitId = await PrepareUnitAsync("Unit 41", ("41-V-1", "Static"));
        var save = await admin.PutAsJsonAsync($"/api/units/{unitId}/categories", new object[]
        {
            new { label = "Cat 1: Under inspection", section = "Hold", priority = 2, keywords = new[] { "inspection" }, actionBy = "PF" },
            new { label = "Cat 2: Refurbishment", section = "Hold", priority = 3, keywords = new[] { "refurb" } },
            new { label = "Cat 3: Live", section = "NotApplicable", priority = 1, keywords = new[] { "live condition" }, note = "Info only" },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var wb = new XLWorkbook(new MemoryStream(await ExportAsync(unitId)));
        var d = wb.Worksheet("Dashboard");
        Assert.Equal("Cat 1: Under inspection", d.Cell("B19").GetString());
        Assert.Contains("TRIM(B19)", d.Cell("C19").FormulaA1);
        Assert.Equal("PF", d.Cell("D19").GetString());
        Assert.Equal("Cat 2: Refurbishment", d.Cell("B20").GetString());
        Assert.Equal("Total -1", d.Cell("B21").GetString());
        Assert.Equal("SUM(C19:C20)", d.Cell("C21").FormulaA1);
        Assert.StartsWith("N/A Category", d.Cell("B22").GetString());
        Assert.Equal("Cat 3: Live", d.Cell("B23").GetString());
        Assert.Equal("Info only", d.Cell("F23").GetString());
        Assert.Equal("G5", d.Cell("C26").FormulaA1);
        Assert.Equal("C26-C25", d.Cell("C27").FormulaA1);
        Assert.True(d.Cell("B18").Style.Font.Bold);
        var categories = WorkbookCategories(wb);
        Assert.Equal([(1, "Cat 3: Live", "live condition", "N/A"), (2, "Cat 1: Under inspection", "inspection", "Hold"), (3, "Cat 2: Refurbishment", "refurb", "Hold")], categories);
    }

    private static List<(int, string, string, string)> WorkbookCategories(XLWorkbook wb) =>
        wb.Worksheets.SelectMany(w => w.Tables).First(t => t.Name == "Categories").DataRange.Rows()
            .Select(r => ((int)r.Cell(1).GetDouble(), r.Cell(2).GetString(), r.Cell(3).GetString(), r.Cell(4).GetString())).ToList();

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Edited_cells_are_applied_with_derived_rules_and_the_file_cannot_be_used_twice()
    {
        var unitId = await PrepareUnitAsync("Unit 42", ("42-E-10", "Static"), ("42-E-11", "Static"));
        var bytes = await ExportAsync(unitId);
        var edited = TestWorkbooks.Edit(bytes, wb =>
        {
            var row = TestWorkbooks.Row(wb, "42-E-10");
            row.Field("G1-Status").SetValue("completed");
            row.Field("G2-Status").SetValue("Completed");
            row.Field("Remark").SetValue("  checked   on site ");
            row.Field("Action By").SetValue("Not a company");
        });

        var preview = await PreviewAsync(unitId, edited);
        Assert.Equal("Previewed", Status(preview));
        var changes = Changes(preview);
        Assert.Contains(("42-E-10", "G1Status", "Applied", "Completed"), changes);
        Assert.Contains(("42-E-10", "RemarksAdnoc", "Applied", "checked on site"), changes);
        Assert.Contains(changes, c => c is { Field: "ActionBy", Outcome: "Invalid" });
        Assert.Contains(changes, c => c is { Field: "CurrentGate", Outcome: "Derived", NewValue: "G2" });
        Assert.Contains(changes, c => c is { Field: "G2Status", Outcome: "Warning" });   // no dew point reading
        Assert.DoesNotContain(changes, c => c.Tag == "42-E-11");
        Assert.Equal("New", (await ProfileAsync("42-E-10")).GetProperty("currentGate").GetString());   // preview writes nothing

        var applied = await ApplyAsync(preview);
        Assert.Equal("AppliedWithIssues", Status(applied));
        var profile = await ProfileAsync("42-E-10");
        Assert.Equal("G2", profile.GetProperty("currentGate").GetString());
        Assert.Equal("Completed", profile.GetProperty("values").GetProperty("G1Status").GetString());
        Assert.NotNull(profile.GetProperty("values").GetProperty("LastFieldActionDate").GetString());
        Assert.True(await app.WithDbAsync(db => db.ChangeLogs.AnyAsync(l => l.Field == "G1Status" && l.Source == ChangeSource.Excel)));

        var issues = await admin.GetFromJsonAsync<JsonElement>($"/api/import-issues?unitId={unitId}");
        Assert.Contains(issues.EnumerateArray(), i => i.GetProperty("change").GetProperty("field").GetString() == "ActionBy");

        var again = await PreviewAsync(unitId, edited);
        Assert.Equal("Rejected", Status(again));
        Assert.Contains("already imported", Message(again));
    }

    [Fact]
    public async Task A_field_changed_in_the_app_after_the_export_is_a_conflict()
    {
        var unitId = await PrepareUnitAsync("Unit 541", ("541-V-1", "Static"));
        var bytes = await ExportAsync(unitId);
        var profile = await ProfileAsync("541-V-1");
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{profile.GetProperty("id").GetString()}",
            new { changes = new[] { new { field = "G3Status", from = (string?)null, to = "Hold" } } });
        patch.EnsureSuccessStatusCode();

        var edited = TestWorkbooks.Edit(bytes, wb => TestWorkbooks.Row(wb, "541-V-1").Field("G3-Status").SetValue("Ongoing"));
        var preview = await PreviewAsync(unitId, edited);

        Assert.Contains(Changes(preview), c => c is { Field: "G3Status", Outcome: "Conflict" });
        await ApplyAsync(preview);
        Assert.Equal("Hold", (await ProfileAsync("541-V-1")).GetProperty("values").GetProperty("G3Status").GetString());
    }

    [Fact]
    public async Task Structure_changes_and_an_edited_key_stop_the_import_but_hidden_columns_do_not()
    {
        var unitId = await PrepareUnitAsync("Unit 39", ("39-X-1", "Static"), ("39-X-2", "Static"));
        var bytes = await ExportAsync(unitId);

        var inserted = TestWorkbooks.Edit(bytes, wb => TestWorkbooks.Master(wb).Worksheet.Column(8).InsertColumnsBefore(1));
        Assert.Equal("Rejected", Status(await PreviewAsync(unitId, inserted)));

        var renamed = TestWorkbooks.Edit(bytes, wb => TestWorkbooks.Master(wb).HeadersRow().Cell(14).Value = "G1 Status (new)");
        var rejected = await PreviewAsync(unitId, renamed);
        Assert.Equal("Rejected", Status(rejected));
        Assert.Contains("renamed", Message(rejected));

        var key = TestWorkbooks.Edit(bytes, wb =>
        {
            var rows = TestWorkbooks.Master(wb).DataRange.Rows().ToList();
            rows[0].Field("Key").SetValue(rows[1].Field("Key").GetString());
        });
        var keyRejected = await PreviewAsync(unitId, key);
        Assert.Equal("Rejected", Status(keyRejected));
        Assert.Contains("Key", Message(keyRejected));

        var hidden = TestWorkbooks.Edit(bytes, wb =>
        {
            TestWorkbooks.Master(wb).Worksheet.Column(8).Hide();
            TestWorkbooks.Row(wb, "39-X-2").Field("Response").SetValue("Waiting for scaffolding");
        });
        var ok = await PreviewAsync(unitId, hidden);
        Assert.Equal("Previewed", Status(ok));
        Assert.Contains(Changes(ok), c => c is { Tag: "39-X-2", Field: "Response", Outcome: "Applied" });
    }

    [Fact]
    public async Task Files_older_than_the_limit_are_refused()
    {
        var unitId = await PrepareUnitAsync("Unit 39", ("39-OLD-1", "Static"));
        var bytes = await ExportAsync(unitId);
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var id = Guid.Parse(wb.Worksheet("_meta").Cell("B1").GetString());
            await app.WithDbAsync(db => db.ExcelSessions.Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExportedAtUtc, DateTime.UtcNow.AddHours(-49))));
        }
        var preview = await PreviewAsync(unitId, bytes);
        Assert.Equal("Rejected", Status(preview));
        Assert.Contains("48 hours", Message(preview));
    }

    [Fact]
    public async Task A_file_not_exported_by_the_app_is_refused()
    {
        var unitId = await PrepareUnitAsync("Unit 39");
        var preview = await PreviewAsync(unitId, TestWorkbooks.Template());
        Assert.Equal("Rejected", Status(preview));
        Assert.Contains("not downloaded from K Data", Message(preview));
    }

    // ------------------------------------------------------------------ onboarding

    [Fact]
    public async Task Onboarding_creates_new_tags_and_fills_existing_ones()
    {
        var unitId = await PrepareUnitAsync("Unit 41", ("41-K-1", "Static"));
        var list = TestWorkbooks.Edit(TestWorkbooks.Template(), wb =>
        {
            var t = TestWorkbooks.Master(wb);
            t.DataRange.LastRow().InsertRowsBelow(2);
            var rows = t.DataRange.Rows().ToList();
            void Fill(IXLTableRow r, string tag, string discipline, string g1, string remarks)
            {
                r.Field("EQUIP NO").SetValue(tag); r.Field("Discipline").SetValue(discipline);
                r.Field("G1-Status").SetValue(g1); r.Field("Preservation ").SetValue(remarks);
            }
            Fill(rows[0], "41-K-1", "Static", "Completed", "Done");
            Fill(rows[1], "41-K-2", "Static / Mechanical", "N/A", "Live condition");
            Fill(rows[2], "41-K-3", "Unknown discipline", "Completed", "x");
        });

        var response = await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", FileContent(list, "client list.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        var changes = Changes(preview);
        Assert.Contains(changes, c => c is { Tag: "41-K-2", Field: "(asset)", Outcome: "Created" });
        Assert.Contains(changes, c => c is { Tag: "41-K-3", Outcome: "Invalid" });
        Assert.Contains(("41-K-1", "G1Status", "Applied", "Completed"), changes);

        await ApplyAsync(preview);
        var created = await ProfileAsync("41-K-2");
        Assert.Matches(@"^ST-EMP-041-\d{3}$", created.GetProperty("code").GetString());
        Assert.Equal("Not Applicable", created.GetProperty("values").GetProperty("G1Status").GetString());
        Assert.Equal("G1", (await ProfileAsync("41-K-1")).GetProperty("currentGate").GetString());
        Assert.False(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Tag == "41-K-3")));
    }

    [Fact]
    public async Task A_client_line_list_creates_lines_with_their_data_and_fills_existing_ones()
    {
        var unitId = await PrepareUnitAsync("Unit 42");
        var pipingId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.AssetType == PresApp.Domain.Assets.AssetType.Pipeline).Select(d => d.Id).FirstAsync());
        (await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId = pipingId, tag = "42-0001-HCX-6\"-11010-S/FG" })).EnsureSuccessStatusCode();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Line list");
        ws.Cell("A1").SetValue("CLIENT LINE LIST");
        string[] headers = ["Sr No", "Line No.", "Length (m)", "G1-Status", "Preservation Remarks", "P&ID No."];
        for (var c = 0; c < headers.Length; c++) ws.Cell(3, c + 1).SetValue(headers[c]);
        object[][] rows =
        [
            [1, "42-0001-HCX-6\"-11010-S/FG", 35.5, "Completed", "Drained", "42-PID-001"],
            [2, "42-0002-LS-2\"-61055-P", 12, "", "New line", "y"],
            [3, "42-0003-LS-2-61055", "", "", "", ""],
            [4, "39-0004-LS-2\"-61055-P", "", "", "", ""],
        ];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++) ws.Cell(4 + r, c + 1).SetValue(XLCellValue.FromObject(rows[r][c]));
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var response = await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", FileContent(ms.ToArray(), "line list.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        var changes = Changes(preview);
        Assert.Contains(changes, c => c is { Tag: "42-0002-LS-2\"-61055-P", Field: "(asset)", Outcome: "Created" });
        Assert.Contains(changes, c => c is { Tag: "42-0003-LS-2-61055", Outcome: "Invalid" });
        Assert.Contains(changes, c => c is { Tag: "39-0004-LS-2\"-61055-P", Outcome: "Invalid" });
        Assert.Contains(("42-0001-HCX-6\"-11010-S/FG", "LengthM", "Applied", "35.5"), changes);

        await ApplyAsync(preview);
        var created = await ProfileAsync("42-0002-LS-2\"-61055-P");
        Assert.Matches(@"^PL-EMP-042-\d{3}$", created.GetProperty("code").GetString());
        Assert.Equal("12", created.GetProperty("values").GetProperty("LengthM").GetString());
        Assert.Equal("LS", created.GetProperty("values").GetProperty("Service").GetString());
        Assert.Equal("New line", created.GetProperty("values").GetProperty("PreservationRemarks").GetString());
        var existing = await ProfileAsync("42-0001-HCX-6\"-11010-S/FG");
        Assert.Equal("42-PID-001", existing.GetProperty("values").GetProperty("PidNumber").GetString());
        Assert.Equal("G1", existing.GetProperty("currentGate").GetString());
    }
}
