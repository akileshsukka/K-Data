using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class PipelineTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public PipelineTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Theory]
    [InlineData("39-0218-LS-16\"-61055-P", 39, "0218", "LS", 16, "61055")]
    [InlineData("39-0301-PG-3/4\"-61055-", 39, "0301", "PG", 0.75, "61055")]
    [InlineData("39-0302-FG-1-1/2\"-1C1", 39, "0302", "FG", 1.5, "1C1")]
    [InlineData("541-1001-HC-2.5\" -A1A-H", 541, "1001", "HC", 2.5, "A1A")]
    [InlineData("000-2222-HCX-6\"-XXXXX-S/FG", 0, "2222", "HCX", 6, "XXXXX")]
    public void Line_number_gives_size_spec_and_service(string tag, int unit, string seq, string service, double size, string spec)
    {
        var p = LineNumber.Parse(tag);
        Assert.NotNull(p);
        Assert.Equal(unit, p.Unit);
        Assert.Equal(seq, p.Sequence);
        Assert.Equal(service, p.Service);
        Assert.Equal((decimal)size, p.SizeIn);
        Assert.Equal(spec, p.Spec);
    }

    [Theory]
    [InlineData("39-V-101")]
    [InlineData("39-0218-LS-16-61055-P")]
    [InlineData("")]
    public void Anything_else_is_not_a_line_number(string tag) => Assert.Null(LineNumber.Parse(tag));

    [Fact]
    public async Task Line_codes_come_from_the_client_standard_and_flag_unknown_ones()
    {
        var codes = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/line-codes");
        Assert.Contains(codes.EnumerateArray(), c => c.GetProperty("code").GetString() == "HCX" && c.GetProperty("description").GetString() == "Hydrocarbon, sour");
        Assert.DoesNotContain(codes.EnumerateArray(), c => c.GetProperty("kind").GetString() != "Service");

        var (unitId, _) = await IdsAsync();
        var preview = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text = "39-0900-QQ-2\"-ZZ9-H\n39-0901-HCX-2\"-1C1-S/FG" })).Content.ReadFromJsonAsync<JsonElement>();
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Contains("Service QQ is not in the line codes.", rows[0].GetProperty("warning").GetString());
        Assert.Contains("Pipe spec ZZ9 is not in the pipe spec list", rows[0].GetProperty("warning").GetString());

        var added = await admin.PostAsJsonAsync("/api/line-codes", new { kind = "Service", code = "qq", description = "Test fluid" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/line-codes", new { kind = "Service", code = "QQ", description = "Again" })).StatusCode);
    }

    [Fact]
    public async Task Lines_take_class_and_flange_type_from_the_pipe_spec_list()
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Piping classes");
        ws.Cell("A1").SetValue("PIPING CLASS INDEX");
        // Same headers as the client's pipe class register.
        ws.Cell("A3").SetValue("Pipe Class"); ws.Cell("B3").SetValue("Rating (#)"); ws.Cell("C3").SetValue("Face (exacta PDF)"); ws.Cell("D3").SetValue("Grupo face");
        ws.Cell("E3").SetValue("Gasket (PDF)"); ws.Cell("F3").SetValue("Material pipe");
        ws.Cell("A4").SetValue("77010"); ws.Cell("B4").SetValue(150); ws.Cell("C4").SetValue("RFD"); ws.Cell("D4").SetValue("RF*"); ws.Cell("E4").SetValue("Graphite"); ws.Cell("F4").SetValue("Carbon Steel");
        ws.Cell("A5").SetValue("77020"); ws.Cell("B5").SetValue(600); ws.Cell("C5").SetValue("RTJ"); ws.Cell("E5").SetValue("Soft Iron");
        ws.Cell("A6").SetValue("77010"); ws.Cell("B6").SetValue(600);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        MultipartFormDataContent File() { var f = new ByteArrayContent(ms.ToArray()); return new MultipartFormDataContent { { f, "file", "specs.xlsx" } }; }

        var preview = await (await admin.PostAsync("/api/pipe-specs/import", File())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, preview.GetProperty("new").GetInt32());
        Assert.Equal(1, preview.GetProperty("invalid").GetInt32());
        Assert.Equal(0, await app.WithDbAsync(db => db.PipeSpecs.CountAsync(s => s.Code == "77010")));
        await admin.PostAsync("/api/pipe-specs/import?apply=true", File());

        var (unitId, disciplineId) = await IdsAsync();
        (await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "39-0777-HC-4\"-77010-P" })).EnsureSuccessStatusCode();
        var register = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Pipeline");
        var line = register.EnumerateArray().Single(r => r.GetProperty("tag").GetString() == "39-0777-HC-4\"-77010-P").GetProperty("line");
        Assert.Equal("150#", line.GetProperty("pressureClass").GetString());
        Assert.Equal("RF", line.GetProperty("flangeType").GetString());
        Assert.Equal("Graphite", line.GetProperty("gasket").GetString());

        // Correcting the spec updates every line that uses it.
        var specs = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/pipe-specs");
        var s77 = specs.EnumerateArray().Single(s => s.GetProperty("code").GetString() == "77010");
        Assert.Equal(1, s77.GetProperty("lines").GetInt32());
        (await admin.PutAsJsonAsync($"/api/pipe-specs/{s77.GetProperty("id").GetInt32()}", new { code = "77010", pressureClass = "150", flangeType = "RTJ", gasket = "Soft Iron" })).EnsureSuccessStatusCode();
        register = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Pipeline");
        Assert.Equal("RTJ", register.EnumerateArray().Single(r => r.GetProperty("tag").GetString() == "39-0777-HC-4\"-77010-P").GetProperty("line").GetProperty("flangeType").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/pipe-specs", new { code = "77020" })).StatusCode);
        var rfc = await (await admin.PostAsJsonAsync("/api/pipe-specs", new { code = "77030", pressureClass = "300", flangeType = "RFC", gasket = "Spiral Wound" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RF", rfc.GetProperty("flangeType").GetString());
        Assert.Equal("300#", rfc.GetProperty("pressureClass").GetString());
    }

    private async Task<(int Unit, int Discipline)> IdsAsync() => await app.WithDbAsync(async db => (
        await db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync(),
        await db.Disciplines.Where(d => d.AssetType == AssetType.Pipeline).Select(d => d.Id).FirstAsync()));

    [Fact]
    public async Task New_line_reads_size_and_spec_from_its_number_and_keeps_the_length()
    {
        var (unitId, disciplineId) = await IdsAsync();
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "39-0218-LS-16\"-61055-P", lengthM = 42.5 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var a = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches(@"^PL-EMP-039-\d{3}$", a.GetProperty("code").GetString());
        var v = a.GetProperty("values");
        Assert.Equal("16", v.GetProperty("SizeIn").GetString());
        Assert.Equal("61055", v.GetProperty("PipeSpec").GetString());
        Assert.Equal("LS", v.GetProperty("Service").GetString());
        Assert.False(v.TryGetProperty("Insulation", out _));
        Assert.Equal("42.5", v.GetProperty("LengthM").GetString());

        // A new line number re-reads the parts.
        var id = a.GetProperty("id").GetString();
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field = "Tag", from = "39-0218-LS-16\"-61055-P", to = "39-0218-LS-8\"-1C1" } } });
        patch.EnsureSuccessStatusCode();
        var after = (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("asset").GetProperty("values");
        Assert.Equal("8", after.GetProperty("SizeIn").GetString());
        Assert.Equal("1C1", after.GetProperty("PipeSpec").GetString());
        Assert.Equal("LS", after.GetProperty("Service").GetString());

        var register = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Pipeline");
        var row = register.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        Assert.Equal(8, row.GetProperty("line").GetProperty("sizeIn").GetDecimal());
    }

    [Fact]
    public async Task Line_from_another_unit_is_refused()
    {
        var (unitId, disciplineId) = await IdsAsync();
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "41-0218-LS-16\"-61055-P" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pasted_lines_preview_then_create()
    {
        var (unitId, _) = await IdsAsync();
        var text = "Line No.\tLength\n39-0500-LS-6\"-61055-P\t12\n39-0501-PG-2\"-1C1\n39-0500-LS-6\"-61055-P\t3\n41-0001-LS-2\"-1C1\n39-V-101\n39-0502-FG-4\"-1C1\tabc";

        var preview = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, preview.GetProperty("toCreate").GetInt32());
        Assert.Equal(4, preview.GetProperty("invalid").GetInt32());
        Assert.Equal(0, await app.WithDbAsync(db => db.Assets.CountAsync(a => a.Tag == "39-0500-LS-6\"-61055-P")));

        var applied = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk?apply=true", new { text })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, applied.GetProperty("toCreate").GetInt32());
        var line = await app.WithDbAsync(db => db.Assets.Include(a => a.Pipeline).SingleAsync(a => a.Tag == "39-0500-LS-6\"-61055-P"));
        Assert.Equal(6, line.Pipeline!.SizeIn);
        Assert.Equal(12, line.Pipeline.LengthM);
        Assert.StartsWith("PL-EMP-039-", line.Code);

        var again = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("toCreate").GetInt32());
        Assert.Equal(2, again.GetProperty("existing").GetInt32());
    }

    [Fact]
    public async Task A_line_written_differently_is_flagged_instead_of_created_twice()
    {
        Assert.Equal(LineNumber.IdentityKey(LineNumber.Parse("39-0218-LS-1-1/2\"-61055-P")!), LineNumber.IdentityKey(LineNumber.Parse("39-218-LS-1.5\"-61055")!));
        var (unitId, disciplineId) = await IdsAsync();
        (await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "39-0880-HC-6\"-11010-P", pidNumber = "39-PID-0007" })).EnsureSuccessStatusCode();

        // New line page: refused unless confirmed.
        var dup = await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "39-0880-HC-6\"-11010" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("39-PID-0007", await dup.Content.ReadAsStringAsync());

        // Paste: possible match, not created.
        var paste = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk?apply=true", new { text = "39-0880-HC-6\"-11010-H" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Possible match", paste.GetProperty("rows")[0].GetProperty("outcome").GetString());
        Assert.Equal(1, await app.WithDbAsync(db => db.Assets.CountAsync(a => a.Tag!.StartsWith("39-0880-"))));

        // Client list: held for review; applied with a decision.
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Lines");
        ws.Cell("A1").SetValue("Line No."); ws.Cell("B1").SetValue("G1-Status");
        ws.Cell("A2").SetValue("39-0880-HC-6\"-11010"); ws.Cell("B2").SetValue("Completed");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        MultipartFormDataContent File() { var f = new ByteArrayContent(ms.ToArray()); return new MultipartFormDataContent { { f, "file", "l.xlsx" } }; }

        var preview = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File())).Content.ReadFromJsonAsync<JsonElement>();
        var match = preview.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == "(possible match)");
        Assert.Equal("Conflict", match.GetProperty("outcome").GetString());
        Assert.Contains("39-PID-0007", match.GetProperty("reason").GetString());

        var runId = preview.GetProperty("run").GetProperty("id").GetInt64();
        (await admin.PostAsJsonAsync($"/api/imports/{runId}/apply", new { decisions = new Dictionary<int, string> { [match.GetProperty("rowNumber").GetInt32()] = "rename" } })).EnsureSuccessStatusCode();
        var line = await app.WithDbAsync(db => db.Assets.Include(a => a.Preservation).SingleAsync(a => a.Tag!.StartsWith("39-0880-")));
        Assert.Equal("39-0880-HC-6\"-11010", line.Tag);
        Assert.Equal("Completed", line.Preservation!.G1Status);

        // Without a decision the row stays as an open issue for review.
        var p2 = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(p2.GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "(possible match)");
    }

    [Fact]
    public async Task Dropdown_lists_get_new_values_and_deactivate_old_ones_but_the_gate_list_is_fixed()
    {
        var sets = await admin.GetFromJsonAsync<JsonElement>("/api/option-sets");
        var activity = sets.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "CurrentActivity");
        var values = activity.GetProperty("values").EnumerateArray().Select(v => new { value = v.GetProperty("value").GetString(), isActive = true }).ToList();
        values.Add(new { value = (string?)"Purging", isActive = true });
        values[0] = new { value = values[0].value, isActive = false };
        (await admin.PutAsJsonAsync("/api/option-sets/CurrentActivity", new { values })).EnsureSuccessStatusCode();
        var options = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/options");
        var list = options.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "CurrentActivity").GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("Purging", list);
        Assert.DoesNotContain(values[0].value, list);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/option-sets/CurrentActivity", new { values = values.Take(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/option-sets/GateStatus", new { values = new[] { new { value = "Done", isActive = true } } })).StatusCode);
    }
}
