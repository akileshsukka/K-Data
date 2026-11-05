using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>
/// Line numbers outside the standard (trimmed numbers of demolished plants, Unit 41, Nov 2026): refused with the format check on,
/// loaded and marked with it off, read only where it is certain.
/// </summary>
public sealed class NonStandardLineTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public NonStandardLineTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private static readonly HashSet<string> Specs = new(StringComparer.OrdinalIgnoreCase) { "61055" };
    private static readonly HashSet<string> Insulation = new(StringComparer.OrdinalIgnoreCase) { "IA", "IC", "IH", "IS" };
    private static readonly HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase) { "A", "F", "FD", "LS", "ADS" };
    private static LineNumberReading Read(string tag) => LineNumber.Read(tag, Specs.Contains, Insulation.Contains, Services.Contains);

    [Theory]
    // unit, sequence, service, size, spec, not read, ignored
    [InlineData("41-100-FD-8\"-IC", 41, "100", "FD", 8.0, null, "pipe spec", "")]
    [InlineData("41-101-F-2\"", 41, "101", "F", 2.0, null, "pipe spec", "")]
    [InlineData("41-102-FD-2\"-ST(1x1/2)", 41, "102", "FD", 2.0, null, "pipe spec", "ST(1x1/2)")]
    [InlineData("41-1009-A", 41, "1009", "A", null, null, "size,pipe spec", "")]
    [InlineData("41-3250-ADS", 41, "3250", "ADS", null, null, "size,pipe spec", "")]
    [InlineData("41-1043-FD-16\"-IC-61055", 41, "1043", "FD", 16.0, "61055", "", "")]
    [InlineData("41-1001-F-1-1/2\"", 41, "1001", "F", 1.5, null, "pipe spec", "")]
    [InlineData("41-1009-X-Y-2\"", 41, "1009", null, 2.0, null, "service,pipe spec", "X,Y")]
    [InlineData("41-1009-LS-Y-2\"", 41, "1009", "LS", 2.0, null, "pipe spec", "Y")]
    [InlineData("41-1009", 41, "1009", null, null, null, "service,size,pipe spec", "")]
    public void A_non_standard_number_keeps_only_what_is_certain(string tag, int unit, string seq, string? service, double? size, string? spec, string missing, string ignored)
    {
        var r = Read(tag);
        Assert.False(r.IsStandard);
        Assert.Equal(unit, r.Unit);
        Assert.Equal(seq, r.Sequence);
        Assert.Equal(service, r.Service);
        Assert.Equal(size is null ? null : (decimal)size, r.SizeIn);
        Assert.Equal(spec, r.Spec);
        Assert.Equal(missing, string.Join(",", r.Missing));
        Assert.Equal(ignored, string.Join(",", r.Ignored));
        Assert.NotNull(r.Note);
    }

    [Theory]
    [InlineData("39-0218-LS-16\"-61055-P", "61055")]
    [InlineData("41-1000-F-18\"-99999", "99999")] // a spec not loaded yet is still the spec of a standard number
    public void A_standard_number_is_read_as_always(string tag, string spec)
    {
        var r = Read(tag);
        Assert.True(r.IsStandard);
        Assert.Equal(spec, r.Spec);
        Assert.Null(r.Note);
    }

    [Theory]
    [InlineData("41-1009-A", "41-1009-A-2\"-61055", true)]
    [InlineData("41-0350-A", "41-350-A", true)]
    [InlineData("41-1009-A", "41-1009-F", false)]
    [InlineData("41-1009-A", "41-1009-FDS-3\"", false)]
    [InlineData("41-1043-FD-16\"-IC", "41-1043-FD-20\"-IC", false)]
    [InlineData("41-1009-A", "41-1010-A", false)]
    public void Same_line_when_unit_and_sequence_match_and_nothing_contradicts(string a, string b, bool same) =>
        Assert.Equal(same, LineNumber.MayBeSameLine(Read(a), Read(b)));

    [Fact]
    public async Task The_format_check_refuses_by_default_and_turned_off_loads_and_marks_the_lines()
    {
        var unitId = await UnitAsync();
        const string list = "39-7100-FD-8\"-IC\t12\n39-7101-F-2\"\n39-7102-FD-2\"-ST(1x1/2)\n39-7103-A\n41-7104-A";

        // Check on (default): nothing non-standard gets in; an insulation code is not taken for a spec.
        var strict = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text = list })).Content.ReadFromJsonAsync<JsonElement>();
        var rows = strict.GetProperty("rows").EnumerateArray().ToList();
        Assert.All(rows, r => Assert.Equal("Invalid", r.GetProperty("outcome").GetString()));
        Assert.Contains("IC, an insulation code", rows.Single(r => r.GetProperty("lineNumber").GetString() == "39-7100-FD-8\"-IC").GetProperty("reason").GetString());

        // Check off: created and marked; another unit is still refused.
        (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk?apply=true", new { text = list, checkFormat = false })).EnsureSuccessStatusCode();
        var lines = await app.WithDbAsync(db => db.Assets.Include(a => a.Pipeline).Where(a => a.Tag!.StartsWith("39-71")).ToListAsync());
        Assert.Equal(4, lines.Count);
        Assert.All(lines, a => Assert.True(a.Pipeline!.NonStandardNumber));
        var ic = lines.Single(a => a.Tag == "39-7100-FD-8\"-IC").Pipeline!;
        Assert.Equal(8, ic.SizeIn);
        Assert.Equal("FD", ic.Service);
        Assert.Null(ic.PipeSpec);
        Assert.Equal(12, ic.LengthM);
        Assert.Equal("Not read: pipe spec · Ignored: ST(1x1/2)", lines.Single(a => a.Tag == "39-7102-FD-2\"-ST(1x1/2)").Pipeline!.NumberNote);
        Assert.Equal(0, await app.WithDbAsync(db => db.Assets.CountAsync(a => a.Tag == "41-7104-A")));

        // The exact same text is the same line, check or not.
        var again = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text = "39-7103-A", checkFormat = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Exists", again.GetProperty("rows")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_full_number_of_a_trimmed_line_is_held_as_a_possible_match()
    {
        var unitId = await UnitAsync();
        (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk?apply=true", new { text = "39-7200-A", checkFormat = false })).EnsureSuccessStatusCode();

        var paste = await (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text = "39-7200-A-2\"-61055-P\n39-7200-F" , checkFormat = false })).Content.ReadFromJsonAsync<JsonElement>();
        var rows = paste.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Possible match", rows[0].GetProperty("outcome").GetString());
        Assert.Contains("May be the same line as 39-7200-A", rows[0].GetProperty("reason").GetString());
        Assert.Equal("New", rows[1].GetProperty("outcome").GetString());

        // New line asks before creating it; the user can still say it is a different line.
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.AssetType == AssetType.Pipeline).Select(d => d.Id).FirstAsync());
        var create = await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "39-7200-A-2\"-61055-P" });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "39-7200-A-2\"-61055-P", allowSimilar = true })).StatusCode);
    }

    [Fact]
    public async Task Rereading_fills_specs_added_later_without_touching_hand_entered_values()
    {
        var unitId = await UnitAsync();
        (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk?apply=true", new { text = "39-7300-F-3\"-IC-88001\n39-7301-F-3\"-IC-88001", checkFormat = false })).EnsureSuccessStatusCode();
        var hand = await app.WithDbAsync(db => db.Assets.Where(a => a.Tag == "39-7301-F-3\"-IC-88001").Select(a => a.Id).FirstAsync());
        (await admin.PatchAsJsonAsync($"/api/assets/{hand}", new { changes = new[] { new { field = "PipeSpec", from = (string?)null, to = "HAND1" } } })).EnsureSuccessStatusCode();
        Assert.Null(await app.WithDbAsync(db => db.PipelineDetails.Where(p => p.Asset.Tag == "39-7300-F-3\"-IC-88001").Select(p => p.PipeSpec).FirstAsync()));

        (await admin.PostAsJsonAsync("/api/pipe-specs", new { code = "88001", pressureClass = "150", flangeType = "RF" })).EnsureSuccessStatusCode();
        var result = await (await admin.PostAsync($"/api/units/{unitId}/lines/reread", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("updated").GetInt32() >= 1);

        Assert.Equal("88001", await app.WithDbAsync(db => db.PipelineDetails.Where(p => p.Asset.Tag == "39-7300-F-3\"-IC-88001").Select(p => p.PipeSpec).FirstAsync()));
        Assert.Equal("HAND1", await app.WithDbAsync(db => db.PipelineDetails.Where(p => p.AssetId == hand).Select(p => p.PipeSpec).FirstAsync()));
    }

    [Fact]
    public async Task Only_roles_with_the_permission_turn_the_check_off_or_see_the_mark()
    {
        var unitId = await UnitAsync();
        (await admin.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk?apply=true", new { text = "39-7400-A", checkFormat = false })).EnsureSuccessStatusCode();
        var id = await app.WithDbAsync(db => db.Assets.Where(a => a.Tag == "39-7400-A").Select(a => a.Id).FirstAsync());

        // A role that can create lines but lacks lines.nonStandard.
        await app.WithDbAsync(async db =>
        {
            db.Roles.Add(new Role
            {
                Name = "Line loader", Description = "test",
                Permissions = new[] { Permissions.AssetsView, Permissions.HistoryView, Permissions.AssetsManage, Permissions.AssetsEdit }
                    .Select(p => new RolePermission { Permission = p }).ToList()
            });
            return await db.SaveChangesAsync();
        });
        await app.AddUserAsync("loader@enermech.com", "Line loader");
        await app.AddUserAsync("engineer.lines@enermech.com", RoleNames.Engineer);
        var loader = app.ClientFor("loader@enermech.com");
        var engineer = app.ClientFor("engineer.lines@enermech.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await loader.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text = "39-7401-A", checkFormat = false })).StatusCode);
        var strictRow = (await (await loader.PostAsJsonAsync($"/api/units/{unitId}/lines/bulk", new { text = "39-7401-A" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows")[0];
        Assert.DoesNotContain("turn off", strictRow.GetProperty("reason").GetString());
        var patch = await loader.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field = "Tag", from = "39-7400-A", to = "39-7400-B" } } });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

        JsonElement Row(JsonElement register) => register.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id.ToString()).GetProperty("line");
        Assert.True(Hidden(Row(await loader.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Pipeline")), "numberCheck"));
        Assert.True(Hidden(await loader.GetFromJsonAsync<JsonElement>($"/api/assets/{id}"), "lineNumberCheck"));
        Assert.DoesNotContain((await loader.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history")).EnumerateArray(),
            h => h.GetProperty("field").GetString() is "NonStandardNumber" or "NumberNote");

        Assert.True(Row(await engineer.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Pipeline")).GetProperty("numberCheck").GetProperty("nonStandard").GetBoolean());
        Assert.Equal("Not read: size, pipe spec", (await engineer.GetFromJsonAsync<JsonElement>($"/api/assets/{id}")).GetProperty("lineNumberCheck").GetProperty("note").GetString());

        // Writing the full number clears the mark and reads the parts.
        (await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field = "Tag", from = "39-7400-A", to = "39-7400-A-4\"-61055-P" } } })).EnsureSuccessStatusCode();
        var line = await app.WithDbAsync(db => db.PipelineDetails.FirstAsync(p => p.AssetId == id));
        Assert.False(line.NonStandardNumber);
        Assert.Null(line.NumberNote);
        Assert.Equal(4, line.SizeIn);
        Assert.Equal("61055", line.PipeSpec);
    }

    [Fact]
    public async Task A_client_line_list_keeps_the_mode_of_its_preview()
    {
        var unitId = await UnitAsync();
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Lines");
        ws.Cell("A1").SetValue("Line No."); ws.Cell("B1").SetValue("Length");
        ws.Cell("A2").SetValue("39-7500-FD-3\"-IC"); ws.Cell("B2").SetValue(7);
        ws.Cell("A3").SetValue("39-7501-ADS");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        MultipartFormDataContent File() { var f = new ByteArrayContent(ms.ToArray()); return new MultipartFormDataContent { { f, "file", "lines.xlsx" } }; }

        var strict = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, strict.GetProperty("changes").EnumerateArray().Count(c => c.GetProperty("outcome").GetString() == "Invalid"));

        var open = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding?checkLineFormat=false", File())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(open.GetProperty("run").GetProperty("lineFormatCheckOff").GetBoolean());
        Assert.Equal(2, open.GetProperty("changes").EnumerateArray().Count(c => c.GetProperty("outcome").GetString() == "Created"));
        (await admin.PostAsync($"/api/imports/{open.GetProperty("run").GetProperty("id").GetInt64()}/apply", null)).EnsureSuccessStatusCode();

        var lines = await app.WithDbAsync(db => db.PipelineDetails.Where(p => p.Asset.Tag!.StartsWith("39-750")).ToListAsync());
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.NonStandardNumber));
    }

    [Fact]
    public async Task An_engineers_tag_no_list_of_trimmed_numbers_is_read_as_lines_with_the_check_off()
    {
        var unitId = await UnitAsync();
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Line List");
        ws.Cell("A1").SetValue("Sr. No."); ws.Cell("B1").SetValue("Tag No"); ws.Cell("C1").SetValue("P&ID Number");
        string[] tags = ["39-7600-A", "39-7601-F-2\"", "39-7602-FD-3\"-IC", "39-7603-ADS"];
        for (var i = 0; i < tags.Length; i++) { ws.Cell(i + 2, 1).SetValue(i + 1); ws.Cell(i + 2, 2).SetValue(tags[i]); ws.Cell(i + 2, 3).SetValue("39-PID-76"); }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        MultipartFormDataContent File() { var f = new ByteArrayContent(ms.ToArray()); return new MultipartFormDataContent { { f, "file", "U39 ALL PIPING LINES.xlsx" } }; }

        // Check on: not a line list (most numbers are not standard), so nothing becomes a line.
        var strict = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(strict.GetProperty("changes").EnumerateArray(), c => c.GetProperty("outcome").GetString() == "Created");

        var open = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding?checkLineFormat=false", File())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, open.GetProperty("changes").EnumerateArray().Count(c => c.GetProperty("outcome").GetString() == "Created"));
        (await admin.PostAsync($"/api/imports/{open.GetProperty("run").GetProperty("id").GetInt64()}/apply", null)).EnsureSuccessStatusCode();

        var lines = await app.WithDbAsync(db => db.Assets.Include(a => a.Pipeline).Where(a => a.Tag!.StartsWith("39-760")).ToListAsync());
        Assert.Equal(4, lines.Count);
        Assert.All(lines, a => Assert.Equal(AssetType.Pipeline, a.Type));
        Assert.All(lines, a => Assert.True(a.Pipeline!.NonStandardNumber));
        Assert.All(lines, a => Assert.Equal("39-PID-76", a.Pipeline!.PidNumber));
    }

    private static bool Hidden(JsonElement e, string property) => !e.TryGetProperty(property, out var v) || v.ValueKind == JsonValueKind.Null;

    private Task<int> UnitAsync() => app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());
}
