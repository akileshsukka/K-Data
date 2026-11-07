using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>E7 electrical test of motors: entry in the app, the client sheet as report and upload, the E7 warning, Nameplate Data.</summary>
public sealed class ElectricalTestTests : IClassFixture<TestApp>
{
    private const string Nameplate = "SERIAL NO- 3386870\nType- M2BA280 6EXnAIIT3V3\nTemp-54°C , Efficiency - 93.2%, Voltage- 415V, RPM- 992";

    private readonly TestApp app;
    private readonly HttpClient admin;

    public ElectricalTestTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private static readonly Dictionary<string, decimal?> FullTest = new()
    {
        ["WrU1U2"] = 0.21m, ["WrV1V2"] = 0.22m, ["WrW1W2"] = 0.2m,
        ["IrUV"] = 21.1m, ["IrUW"] = 19.49m, ["IrVW"] = 14.39m, ["IrUG"] = 23.5m, ["IrVG"] = 17.9m, ["IrWG"] = 14.45m,
        ["DarUVwe30"] = 10.71m, ["DarUVwe60"] = 16.21m, ["DarVUwe30"] = 9.09m, ["DarVUwe60"] = 13.79m,
        ["DarWUve30"] = 10.07m, ["DarWUve60"] = 16.57m, ["DarUvwToE30"] = 13.12m, ["DarUvwToE60"] = 21.7m,
    };

    [Fact]
    public async Task Test_is_entered_in_the_app_with_calculated_ratios_and_optional_PI()
    {
        var unitId = await UnitAsync("Unit 39");
        var motor = await CreateAsync(unitId, "39-EM-102-01A");

        // Activated with part of the values: saved, with the missing required values listed.
        var partial = await SaveAsync(motor, true, new() { ["WrU1U2"] = 0.21m }, expected: null);
        var test = partial.GetProperty("electricalTest");
        Assert.True(test.GetProperty("required").GetBoolean());
        Assert.False(test.GetProperty("complete").GetBoolean());
        Assert.Equal(16, test.GetProperty("missing").GetArrayLength());

        // All required values (no PI): complete; DAR = 60 s ÷ 30 s.
        var full = await SaveAsync(motor, true, FullTest, test.GetProperty("updatedAtUtc").GetDateTime(), new DateOnly(2026, 11, 3));
        test = full.GetProperty("electricalTest");
        Assert.True(test.GetProperty("complete").GetBoolean());
        Assert.Equal(1.51m, test.GetProperty("ratios").GetProperty("DarU").GetDecimal());
        Assert.Equal(1.65m, test.GetProperty("ratios").GetProperty("DarUvw").GetDecimal());
        Assert.Equal(JsonValueKind.Null, test.GetProperty("ratios").GetProperty("PiUV").ValueKind);
        Assert.Equal("2026-11-03", test.GetProperty("testedOn").GetString());

        // A stale save is a conflict; negative values are refused.
        var stale = await admin.PutAsJsonAsync($"/api/assets/{motor}/electrical-test", new { required = true, testedOn = (string?)null, values = new Dictionary<string, decimal?>(), expected = (DateTime?)null });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var negative = await admin.PutAsJsonAsync($"/api/assets/{motor}/electrical-test", new { required = true, testedOn = (string?)null, values = new Dictionary<string, decimal?> { ["PiUV1"] = -1 }, expected = test.GetProperty("updatedAtUtc").GetDateTime() });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        // History reads like the sheet.
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{motor}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("label").GetString() == "E-UVW 60 s" && h.GetProperty("newValue").GetString() == "21.7");

        // Nameplate Data is its own field, edited like the others.
        var profile = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{motor}");
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{motor}", new { changes = new[] { new { field = "NameplateData", from = (string?)null, to = Nameplate } } });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
        Assert.Equal(Nameplate, (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{motor}")).GetProperty("values").GetProperty("NameplateData").GetString());
        Assert.Equal("Item 39-EM-102-01A", profile.GetProperty("values").GetProperty("Description").GetString());
    }

    [Fact]
    public async Task Report_and_upload_use_the_client_sheet()
    {
        var unitId = await UnitAsync("Unit 41");
        var reported = await CreateAsync(unitId, "41-EM-201");
        var notRequired = await CreateAsync(unitId, "41-EM-202");
        var loadedLater = await CreateAsync(unitId, "41-EM-203");
        await SaveAsync(reported, true, FullTest, expected: null);
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{reported}", new { changes = new[] { new { field = "NameplateData", from = (string?)null, to = Nameplate } } });
        Assert.True(patch.IsSuccessStatusCode);

        // ---- report: only the motors that require the test, in the client layout
        var response = await admin.GetAsync($"/api/units/{unitId}/electrical-tests/export");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("ITEM TAG", ws.Cell("A2").GetString());
            Assert.Equal("E-UVW", ws.Cell("U2").GetString());
            Assert.Equal("W-G", ws.Cell("AM2").GetString());
            Assert.Equal("41-EM-201", ws.Cell("A4").GetString());
            Assert.Equal(Nameplate, ws.Cell("B4").GetString());
            Assert.Equal(0.21, ws.Cell("C4").GetDouble());
            Assert.Equal(1.51, ws.Cell("N4").GetDouble());   // DAR ratio U-VWE
            Assert.Equal(21.7, ws.Cell("V4").GetDouble());
            Assert.True(ws.Cell("X4").IsEmpty());             // PI not done
            Assert.True(ws.Cell("A5").IsEmpty());             // 41-EM-202 does not require the test
            Assert.Equal(4, ws.LastRowUsed(XLCellsUsedOptions.All)!.RowNumber());   // no formatted empty rows after the printed equipment
            Assert.Equal("DAR – DIELECTRIC ABSORPTION RATIO (GΩ)", ws.Cell("L1").GetString());
        }
        // Opens at A1 with ITEM TAG / DESCRIPTION and the header rows frozen (not scrolled to where the template was saved).
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = reader.ReadToEnd();
            var view = System.Text.RegularExpressions.Regex.Match(xml, "<(?:x:)?sheetView [^>]*>").Value;
            Assert.DoesNotContain("topLeftCell=\"E1\"", view);
            Assert.Matches("xSplit=\"2\"", xml);
            Assert.Matches("ySplit=\"3\"", xml);
            Assert.Matches("state=\"frozen(Split)?\"", xml);
        }

        // ---- upload: PI added to the first motor (an empty cell clears nothing), a new motor, an unknown tag, a bad value
        var edited = TestWorkbooks.Edit(bytes, wb =>
        {
            var ws = wb.Worksheet(1);
            ws.Cell("C4").Value = Blank.Value;
            ws.Cell("X4").Value = 1.2; ws.Cell("Y4").Value = 3.6;
            ws.Cell("AA4").Value = 2; ws.Cell("AB4").Value = 5.0999;   // PI V-W
            ws.Cell("A5").Value = "41-em-203"; ws.Cell("B5").Value = "SERIAL NO- 1"; ws.Cell("C5").Value = 0.3; ws.Cell("D5").Value = "abc";
            ws.Cell("A6").Value = "41-EM-999"; ws.Cell("C6").Value = 0.3;
        });
        var preview = await PostFileAsync($"/api/units/{unitId}/electrical-tests/import", edited);
        var changes = Changes(preview);
        Assert.Contains(("41-EM-201", "PiUV1", "Applied"), changes);
        Assert.Contains(("41-EM-201", "PiVW10", "Applied"), changes);
        Assert.Contains(("41-EM-201", "WrV1V2", "No change"), changes);
        Assert.DoesNotContain(changes, c => c is ("41-EM-201", "WrU1U2", _));
        Assert.Contains(("41-EM-203", "Required", "Derived"), changes);
        Assert.Contains(("41-EM-203", "NameplateData", "Applied"), changes);
        Assert.Contains(("41-EM-203", "WrV1V2", "Invalid"), changes);
        Assert.Contains(("41-EM-999", "(row)", "Rejected"), changes);

        var applied = await admin.PostAsync($"/api/imports/{preview.GetProperty("run").GetProperty("id").GetInt64()}/apply", null);
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        var first = (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{reported}")).GetProperty("electricalTest");
        Assert.Equal(0.21m, first.GetProperty("values").GetProperty("WrU1U2").GetDecimal());
        Assert.Equal(3m, first.GetProperty("ratios").GetProperty("PiUV").GetDecimal());
        Assert.Equal(2.54m, first.GetProperty("ratios").GetProperty("PiVW").GetDecimal());   // 2.54995 cut, not 2.55
        var later = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{loadedLater}");
        Assert.True(later.GetProperty("electricalTest").GetProperty("required").GetBoolean());
        Assert.Equal(0.3m, later.GetProperty("electricalTest").GetProperty("values").GetProperty("WrU1U2").GetDecimal());
        Assert.Equal("SERIAL NO- 1", later.GetProperty("values").GetProperty("NameplateData").GetString());
        Assert.Equal(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{notRequired}")).GetProperty("electricalTest").ValueKind);

        // A sheet with other columns is refused.
        var wrong = TestWorkbooks.Edit(bytes, wb => wb.Worksheet(1).Cell("C3").Value = "Something else");
        var rejected = await PostFileAsync($"/api/units/{unitId}/electrical-tests/import", wrong);
        Assert.Equal("Rejected", rejected.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ratios_are_cut_after_two_decimals_and_the_report_takes_the_selected_equipment()
    {
        var unitId = await UnitAsync("Unit 32");
        var first = await CreateAsync(unitId, "32-EM-501");
        var second = await CreateAsync(unitId, "32-EM-502");
        var third = await CreateAsync(unitId, "32-EM-503");
        // 1.519 / 1 → 1.51 (rounding would give 1.52); 10 min ÷ 1 min = 6.27 / 3 = 2.09 exactly.
        var values = new Dictionary<string, decimal?>(FullTest) { ["DarUVwe30"] = 1m, ["DarUVwe60"] = 1.519m, ["PiUV1"] = 3m, ["PiUV10"] = 6.27m };
        var saved = (await SaveAsync(first, true, values, expected: null)).GetProperty("electricalTest");
        Assert.Equal(1.51m, saved.GetProperty("ratios").GetProperty("DarU").GetDecimal());
        Assert.Equal(2.09m, saved.GetProperty("ratios").GetProperty("PiUV").GetDecimal());
        await SaveAsync(third, true, FullTest, expected: null);

        // Chosen in the register: the first and the second (which has no test yet), not the third.
        var response = await admin.PostAsJsonAsync($"/api/units/{unitId}/electrical-tests/export", new { assetIds = new[] { second, first } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var wb = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet(1);
        Assert.Equal("32-EM-501", ws.Cell("A4").GetString());
        Assert.Equal(1.51, ws.Cell("N4").GetDouble());
        Assert.Equal("32-EM-502", ws.Cell("A5").GetString());
        Assert.True(ws.Cell("C5").IsEmpty());
        Assert.True(ws.Cell("A6").IsEmpty());

        var none = await admin.PostAsJsonAsync($"/api/units/{unitId}/electrical-tests/export", new { assetIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task A_sheet_with_the_single_PI_block_of_before_still_loads_into_PI_U_V()
    {
        var unitId = await UnitAsync("Unit 541");
        var motor = await CreateAsync(unitId, "541-EM-601");
        await SaveAsync(motor, true, FullTest, expected: null);
        var report = await admin.PostAsJsonAsync($"/api/units/{unitId}/electrical-tests/export", new { assetIds = new[] { motor } });
        // Old layout: PI 1 Min / 10 Min / PI Ratio in X:Z and nothing after.
        var old = TestWorkbooks.Edit(await report.Content.ReadAsByteArrayAsync(), wb =>
        {
            var ws = wb.Worksheet(1);
            ws.Range("AA2:AO4").Clear();
            ws.Cell("X4").Value = 2; ws.Cell("Y4").Value = 4;
        });
        var preview = await PostFileAsync($"/api/units/{unitId}/electrical-tests/import", old);
        Assert.Equal("Previewed", preview.GetProperty("run").GetProperty("status").GetString());
        Assert.Contains(("541-EM-601", "PiUV10", "Applied"), Changes(preview));

        // Any other change of the columns is still refused.
        var wrong = TestWorkbooks.Edit(await report.Content.ReadAsByteArrayAsync(), wb => wb.Worksheet(1).Cell("AB3").Value = "Something");
        Assert.Equal("Rejected", (await PostFileAsync($"/api/units/{unitId}/electrical-tests/import", wrong)).GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Changing_a_complete_test_from_the_sheet_needs_a_yes_or_no_per_motor()
    {
        var unitId = await UnitAsync("Unit 32");
        var replaced = await CreateAsync(unitId, "32-EM-601");
        var kept = await CreateAsync(unitId, "32-EM-602");
        var unanswered = await CreateAsync(unitId, "32-EM-603");
        var piAdded = await CreateAsync(unitId, "32-EM-604");
        foreach (var id in new[] { replaced, kept, unanswered, piAdded }) await SaveAsync(id, true, FullTest, expected: null);

        var export = await admin.PostAsJsonAsync($"/api/units/{unitId}/electrical-tests/export", new { assetIds = new[] { replaced, kept, unanswered, piAdded } });
        var edited = TestWorkbooks.Edit(await export.Content.ReadAsByteArrayAsync(), wb =>
        {
            var ws = wb.Worksheet(1);
            ws.Cell("C4").Value = 0.5; ws.Cell("C5").Value = 0.5; ws.Cell("C6").Value = 0.5;   // winding U1-U2 changed on rows 1–3
            ws.Cell("X7").Value = 1.2; ws.Cell("Y7").Value = 3.6;                                // only PI added (empty before) on row 4
        });
        var preview = await PostFileAsync($"/api/units/{unitId}/electrical-tests/import", edited);
        var changes = Changes(preview);
        Assert.Contains(("32-EM-601", "(replace test)", "Conflict"), changes);
        Assert.Contains(("32-EM-602", "(replace test)", "Conflict"), changes);
        Assert.Contains(("32-EM-603", "(replace test)", "Conflict"), changes);
        Assert.DoesNotContain(changes, c => c is ("32-EM-604", "(replace test)", _));
        Assert.Contains(("32-EM-604", "PiUV1", "Applied"), changes);

        var runId = preview.GetProperty("run").GetProperty("id").GetInt64();
        var applied = await admin.PostAsJsonAsync($"/api/imports/{runId}/apply", new { decisions = new Dictionary<int, string> { [1] = "replace", [2] = "keep" } });
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        var result = Changes(await applied.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Contains(("32-EM-601", "(replace test)", "Applied"), result);
        Assert.Contains(("32-EM-602", "(replace test)", "No change"), result);
        Assert.Contains(("32-EM-603", "(replace test)", "Conflict"), result);

        async Task<decimal> Winding(Guid id) => (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}")).GetProperty("electricalTest").GetProperty("values").GetProperty("WrU1U2").GetDecimal();
        Assert.Equal(0.5m, await Winding(replaced));
        Assert.Equal(0.21m, await Winding(kept));
        Assert.Equal(0.21m, await Winding(unanswered));
        var pi = (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{piAdded}")).GetProperty("electricalTest");
        Assert.Equal(3m, pi.GetProperty("ratios").GetProperty("PiUV").GetDecimal());
    }

    [Fact]
    public async Task E7_completed_in_the_unit_file_without_a_complete_test_is_a_warning()
    {
        var unitId = await UnitAsync("Unit 42");
        var template = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Support", "Template_Unit_39_v5.xlsx"));
        Assert.True((await admin.PutAsync($"/api/excel-templates/{unitId}", FileContent(template, "Template_Unit_42.xlsx"))).IsSuccessStatusCode);
        var incomplete = await CreateAsync(unitId, "42-EM-301");
        var complete = await CreateAsync(unitId, "42-EM-302");
        await SaveAsync(incomplete, true, new() { ["WrU1U2"] = 0.2m }, expected: null);
        await SaveAsync(complete, true, FullTest, expected: null);

        var export = await admin.GetAsync($"/api/units/{unitId}/excel/export");
        var edited = TestWorkbooks.Edit(await export.Content.ReadAsByteArrayAsync(), wb =>
        {
            var ei = wb.Worksheets.SelectMany(w => w.Tables).First(t => t.Name == "EIList");
            foreach (var row in ei.DataRange.Rows()) row.Field("E7-Status").SetValue("Completed");
        });
        var preview = await PostFileAsync($"/api/units/{unitId}/excel/import", edited);
        var changes = Changes(preview);
        Assert.Contains(("42-EM-301", "G7Status", "Warning"), changes);
        Assert.DoesNotContain(("42-EM-302", "G7Status", "Warning"), changes);
        Assert.Contains(("42-EM-302", "G7Status", "Applied"), changes);
    }

    [Fact]
    public async Task Onboarding_loads_nameplate_data()
    {
        var unitId = await UnitAsync("Unit 31");
        var list = TestWorkbooks.Edit(TestWorkbooks.Template(), wb =>
        {
            var t = TestWorkbooks.Master(wb);
            t.InsertColumnsAfter(1);
            t.HeadersRow().LastCell().Value = "Nameplate Data";
            var row = t.DataRange.Row(1);
            row.Field("EQUIP NO").SetValue("31-EM-401"); row.Field("Discipline").SetValue("Electrical");
            row.Field("Nameplate Data").SetValue(Nameplate);
        });
        var preview = await PostFileAsync($"/api/units/{unitId}/excel/onboarding", list);
        Assert.True((await admin.PostAsync($"/api/imports/{preview.GetProperty("run").GetProperty("id").GetInt64()}/apply", null)).IsSuccessStatusCode);
        Assert.Equal(Nameplate, await app.WithDbAsync(db => db.Assets.Where(a => a.Tag == "31-EM-401").Select(a => a.NameplateData).FirstAsync()));
    }

    // ------------------------------------------------------------------ helpers

    private Task<int> UnitAsync(string name) => app.WithDbAsync(db => db.Units.Where(u => u.Name == name).Select(u => u.Id).FirstAsync());

    private async Task<Guid> CreateAsync(int unitId, string tag)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Electrical").Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type = "ElectricalInstrument", unitId, disciplineId, tag, description = "Item " + tag });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> SaveAsync(Guid id, bool required, Dictionary<string, decimal?> values, DateTime? expected, DateOnly? testedOn = null)
    {
        var response = await admin.PutAsJsonAsync($"/api/assets/{id}/electrical-test", new { required, testedOn, values, expected });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> PostFileAsync(string url, byte[] bytes)
    {
        var response = await admin.PostAsync(url, FileContent(bytes, "sheet.xlsx"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(string? Tag, string? Field, string? Outcome)> Changes(JsonElement detail) =>
        detail.GetProperty("changes").EnumerateArray()
            .Select(x => (x.GetProperty("assetTag").GetString(), x.GetProperty("field").GetString(), x.GetProperty("outcome").GetString())).ToList();

    private static MultipartFormDataContent FileContent(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }
}
