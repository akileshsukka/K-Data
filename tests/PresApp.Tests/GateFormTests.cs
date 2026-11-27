using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.GateForms;
using PresApp.Domain.GateForms;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>Printable gate forms (Word): built-in templates, filling from the registers, saved values, uploaded templates.</summary>
public sealed class GateFormTests : IClassFixture<TestApp>
{
    private const string ViewerEmail = "gate.viewer@enermech.com";
    private readonly TestApp app;
    private readonly HttpClient admin;

    public GateFormTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Theory]
    [InlineData(GateFormKind.Mechanical, "GateForm_WorkPack.docx", new[] { "UNIT", "SOURCE", "RECORD_NO", "DATE", "SECTION", "WORKPACK" })]
    // The signed client form: Equipment Tag Number, Date, Tag Description, Documents number (filled by hand); no Record No.
    [InlineData(GateFormKind.Electrical, "GateForm_Electrical.docx", new[] { "UNIT", "SOURCE", "DATE", "SECTION", "DESCRIPTION" })]
    public void Built_in_templates_hold_every_field_of_the_client_form(GateFormKind kind, string resource, string[] fields)
    {
        var (name, content) = GateFormService.BuiltIn(kind);
        Assert.Equal(resource, name);
        var check = GateFormDocument.Inspect(content);
        Assert.Empty(check.Errors);
        Assert.Equal(fields, check.Fields);
    }

    [Fact]
    public async Task Mechanical_form_has_one_page_per_asset_with_the_unit_values_and_keeps_what_was_typed()
    {
        var unitId = await UnitAsync("Unit 39");
        var vessel = await CreateAsync(unitId, "Equipment", "Static", "39-V-901");
        var line = await CreateAsync(unitId, "Pipeline", "Piping", "39-0901-HC-4\"-77010-P", pid: "39-00-30-201\n39-00-30-202");

        var prepared = await PrepareAsync(admin, unitId, "Mechanical", vessel, line);
        Assert.True(prepared.GetProperty("canSave").GetBoolean());
        Assert.Equal("DOD-52618-H2-39-69-00-156", prepared.GetProperty("source").GetString());
        Assert.Contains("WORKPACK", prepared.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        var rows = prepared.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("39-V-901", rows[0].GetProperty("section").GetString());
        Assert.Equal("39-00-30-201, 39-00-30-202", rows[1].GetProperty("workPack").GetString());

        var doc = await GenerateAsync(admin, unitId, "Mechanical",
            new { assetId = vessel, recordNo = "REC-7", date = "2026-11-25", section = "39-V-901 / circuit 2", workPack = "WP-12 rev B", unitId = (string?)null },
            new { assetId = line, recordNo = (string?)null, date = (string?)null, section = rows[1].GetProperty("section").GetString(), workPack = rows[1].GetProperty("workPack").GetString(), unitId = (string?)null });

        Assert.Equal(2, doc.Pages.Count);
        Assert.Contains("UNIT 39 PRESERVATION CHECKLISTS AND ITRs", doc.Header);
        Assert.Contains("Source: DOD-52618-H2-39-69-00-156 Rev 01 | 6.8", doc.Pages[0]);
        Assert.Contains("Record No. REC-7", doc.Pages[0]);
        Assert.Contains("Date 25/11/2026", doc.Pages[0]);
        Assert.Contains("System / circuit / section 39-V-901 / circuit 2", doc.Pages[0]);
        Assert.Contains("Work pack / P&ID rev. WP-12 rev B", doc.Pages[0]);
        // Empty fields print the blank line of the client form; an empty Record No. takes the next one of the discipline (Piping).
        Assert.Contains("Record No. ITR-039-PIP-", doc.Pages[1]);
        Assert.Contains("Date ______________________________", doc.Pages[1]);
        Assert.Contains("System / circuit / section 39-0901-HC-4\"-77010-P", doc.Pages[1]);
        Assert.Contains("Work pack / P&ID rev. 39-00-30-201, 39-00-30-202", doc.Pages[1]);
        Assert.DoesNotContain("{{", string.Join("", doc.Pages) + doc.Header);

        // Saved: typed values and overrides only; the line keeps following its tag and P&ID.
        var entries = await app.WithDbAsync(db => db.GateFormEntries.AsNoTracking().ToDictionaryAsync(e => e.AssetId));
        Assert.Equal("REC-7", entries[vessel].RecordNo);
        Assert.Equal(new DateOnly(2026, 11, 25), entries[vessel].Date);
        Assert.Equal("39-V-901 / circuit 2", entries[vessel].Section);
        Assert.StartsWith("ITR-039-PIP-", entries[line].RecordNo);
        Assert.Null(entries[line].Section);
        Assert.Null(entries[line].WorkPack);

        var again = (await PrepareAsync(admin, unitId, "Mechanical", vessel)).GetProperty("rows")[0];
        Assert.Equal("REC-7", again.GetProperty("recordNo").GetString());
        Assert.Equal("2026-11-25", again.GetProperty("date").GetString());
        Assert.Equal("WP-12 rev B", again.GetProperty("workPack").GetString());

        var history = await app.WithDbAsync(db => db.ChangeLogs.Where(c => c.AssetId == vessel && c.EntityType == "GateFormEntry").CountAsync());
        Assert.True(history > 0);
    }

    [Fact]
    public async Task Unit_541_prints_the_work_pack_like_every_other_unit_and_its_source()
    {
        var unitId = await UnitAsync("Unit 541");
        var line = await CreateAsync(unitId, "Pipeline", "Piping", "541-0901-HC-4\"-77010-P", pid: "541-00-30-201");

        var prepared = await PrepareAsync(admin, unitId, "Mechanical", line);
        var fields = prepared.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("WORKPACK", fields);
        Assert.DoesNotContain("UNIT_ID", fields);

        var doc = await GenerateAsync(admin, unitId, "Mechanical", new { assetId = line, recordNo = (string?)null, date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null });

        Assert.Single(doc.Pages);
        Assert.Contains("UNIT 541 PRESERVATION", doc.Header);
        Assert.Contains("DOD-52618-H5-541-69-00-156 Rev 01", doc.Pages[0]);
        Assert.Contains("System / circuit / section 541-0901-HC-4\"-77010-P", doc.Pages[0]);
        Assert.Contains("Work pack / P&ID rev. 541-00-30-201", doc.Pages[0]);
        Assert.DoesNotContain("Unit ID", doc.Pages[0]);
    }

    [Fact]
    public async Task Electrical_form_is_only_for_electrical_items_and_prints_tag_and_description_but_not_the_record_no()
    {
        var unitId = await UnitAsync("Unit 42");
        var motor = await CreateAsync(unitId, "ElectricalInstrument", "Electrical", "42-PM-901");
        var bare = await CreateAsync(unitId, "ElectricalInstrument", "Electrical", "42-PM-902", withDescription: false);
        var analyser = await CreateAsync(unitId, "ElectricalInstrument", "E&I", "42-JB-901");
        var vessel = await CreateAsync(unitId, "Equipment", "Static", "42-V-901");

        var prepared = await PrepareAsync(admin, unitId, "Electrical", motor, bare, analyser, vessel);
        Assert.Equal(["UNIT", "SOURCE", "DATE", "SECTION", "DESCRIPTION"], prepared.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(2, prepared.GetProperty("rows").GetArrayLength());
        Assert.Equal(2, prepared.GetProperty("skipped").GetArrayLength());

        var doc = await GenerateAsync(admin, unitId, "Electrical",
            new { assetId = motor, recordNo = "E-1", date = "2026-11-25", section = (string?)null, workPack = (string?)null, unitId = (string?)null },
            new { assetId = bare, recordNo = (string?)null, date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null },
            new { assetId = analyser, recordNo = "E-2", date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null });
        Assert.Equal(2, doc.Pages.Count);
        Assert.Contains("UNIT 42 PRESERVATION", doc.Header);
        Assert.Contains("Printable section acceptance gate for Electrical motors", doc.Pages[0]);
        Assert.Contains("Form ELECTRICAL GATE | Source:  Rev 01", doc.Pages[0]);
        Assert.DoesNotContain("6.8", doc.Pages[0]);
        Assert.Contains("Equipment Tag Number 42-PM-901", doc.Pages[0]);
        Assert.Contains("Date 25/11/2026", doc.Pages[0]);
        Assert.Contains("Tag Description Item 42-PM-901", doc.Pages[0]);
        Assert.Contains("Documents number ___", doc.Pages[0]);
        // No description: the blank line of the form.
        Assert.Contains("Equipment Tag Number 42-PM-902", doc.Pages[1]);
        Assert.Contains("Tag Description __________________", doc.Pages[1]);
        Assert.DoesNotContain("{{", string.Join("", doc.Pages) + doc.Header);

        // The Record No. is still kept with the asset (typed or the next one of the discipline) but is not printed.
        var entries = await app.WithDbAsync(db => db.GateFormEntries.AsNoTracking().Where(e => e.AssetId == motor || e.AssetId == bare).ToDictionaryAsync(e => e.AssetId));
        Assert.Equal("E-1", entries[motor].RecordNo);
        Assert.StartsWith("ITR-042-ELEC-", entries[bare].RecordNo);
        foreach (var page in doc.Pages)
        {
            Assert.DoesNotContain("Record No.", page);
            Assert.DoesNotContain("E-1", page);
            Assert.DoesNotContain("ITR-", page);
            Assert.DoesNotContain("Work pack", page);
        }
    }

    [Fact]
    public async Task Electrical_source_falls_back_to_the_mechanical_source()
    {
        var unitId = await UnitAsync("Unit 31");
        var motor = await CreateAsync(unitId, "ElectricalInstrument", "Electrical", "31-PM-901");
        var row = new { assetId = motor, recordNo = (string?)null, date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null };
        async Task SetAsync(string? mechanical, string? electrical) =>
            (await admin.PutAsJsonAsync($"/api/gate-forms/settings/{unitId}", new { mechanicalSource = mechanical, electricalSource = electrical, defaultUnitId = (string?)null })).EnsureSuccessStatusCode();

        // Neither: empty.
        await SetAsync(null, null);
        Assert.Equal(JsonValueKind.Null, (await PrepareAsync(admin, unitId, "Electrical", motor)).GetProperty("source").ValueKind);
        Assert.Contains("Form ELECTRICAL GATE | Source:  Rev 01", (await GenerateAsync(admin, unitId, "Electrical", row)).Pages[0]);

        // Only the mechanical one: the electrical form prints it (same source document).
        await SetAsync("DOD-31-MECH", null);
        Assert.Equal("DOD-31-MECH", (await PrepareAsync(admin, unitId, "Electrical", motor)).GetProperty("source").GetString());
        Assert.Contains("Source: DOD-31-MECH Rev 01", (await GenerateAsync(admin, unitId, "Electrical", row)).Pages[0]);

        // Both: the electrical one; the mechanical form keeps its own.
        await SetAsync("DOD-31-MECH", "DOD-31-EL");
        Assert.Equal("DOD-31-EL", (await PrepareAsync(admin, unitId, "Electrical", motor)).GetProperty("source").GetString());
        Assert.Contains("Source: DOD-31-EL Rev 01", (await GenerateAsync(admin, unitId, "Electrical", row)).Pages[0]);
        var vessel = await CreateAsync(unitId, "Equipment", "Static", "31-V-902");
        Assert.Equal("DOD-31-MECH", (await PrepareAsync(admin, unitId, "Mechanical", vessel)).GetProperty("source").GetString());
    }

    [Fact]
    public async Task Unit_541_motor_prints_the_electrical_form_with_the_mechanical_source_and_takes_its_record_no()
    {
        var unitId = await UnitAsync("Unit 541");
        var motor = await CreateAsync(unitId, "ElectricalInstrument", "Electrical", "541-PM-901");

        var prepared = await PrepareAsync(admin, unitId, "Electrical", motor);
        Assert.Equal("DOD-52618-H5-541-69-00-156", prepared.GetProperty("source").GetString());
        Assert.StartsWith("ITR-541-ELEC-", prepared.GetProperty("rows")[0].GetProperty("recordNoNext").GetString());

        var doc = await GenerateAsync(admin, unitId, "Electrical", new { assetId = motor, recordNo = (string?)null, date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null });
        Assert.Single(doc.Pages);
        Assert.Contains("UNIT 541 PRESERVATION", doc.Header);
        Assert.Contains("Form ELECTRICAL GATE | Source: DOD-52618-H5-541-69-00-156 Rev 01", doc.Pages[0]);
        Assert.Contains("Equipment Tag Number 541-PM-901", doc.Pages[0]);
        Assert.Contains("Tag Description Item 541-PM-901", doc.Pages[0]);
        Assert.DoesNotContain("ITR-", doc.Pages[0]);
        var recordNo = await app.WithDbAsync(db => db.GateFormEntries.Where(e => e.AssetId == motor).Select(e => e.RecordNo).FirstAsync());
        Assert.Matches(@"^ITR-541-ELEC-\d{3}$", recordNo);
    }

    [Fact]
    public async Task Uploaded_electrical_template_with_the_description_is_accepted()
    {
        var plantId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 42").Select(u => u.PlantId).FirstAsync());
        var (_, builtIn) = GateFormService.BuiltIn(GateFormKind.Electrical);
        var revised = Edit(builtIn, body => body.Descendants<Text>().First(t => t.Text.Contains("Rev 01")).Text = ": {{SOURCE}} Rev 02");

        var uploaded = await UploadAsync(plantId, revised, "Electrical rev 02.docx", "Electrical");
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var version = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version");
        Assert.Contains("DESCRIPTION", version.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));

        var sample = await ReadAsync(await admin.GetByteArrayAsync($"/api/gate-forms/templates/{plantId}/Electrical/sample?version={version.GetProperty("id").GetInt32()}"));
        Assert.Contains("Tag Description REGEN. OVERHEAD COOLER FAN", sample.Pages[0]);
        Assert.Contains("Rev 02", sample.Pages[0]);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/gate-forms/templates/{plantId}/Electrical/versions/{version.GetProperty("id").GetInt32()}")).StatusCode);
    }

    [Fact]
    public async Task A_role_without_edit_rights_gets_the_file_but_nothing_is_saved()
    {
        await app.AddUserAsync(ViewerEmail, "Viewer");
        var viewer = app.ClientFor(ViewerEmail);
        var unitId = await UnitAsync("Unit 31");
        var vessel = await CreateAsync(unitId, "Equipment", "Static", "31-V-901");

        Assert.False((await PrepareAsync(viewer, unitId, "Mechanical", vessel)).GetProperty("canSave").GetBoolean());
        var doc = await GenerateAsync(viewer, unitId, "Mechanical", new { assetId = vessel, recordNo = "R-1", date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null });
        Assert.Contains("Record No. R-1", doc.Pages[0]);
        Assert.False(await app.WithDbAsync(db => db.GateFormEntries.AnyAsync(e => e.AssetId == vessel)));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/gate-forms/templates")).StatusCode);
    }

    [Fact]
    public async Task Uploaded_template_is_checked_kept_as_a_version_and_used_once_activated()
    {
        var unitId = await UnitAsync("Unit 32");
        var plantId = await app.WithDbAsync(db => db.Units.Where(u => u.Id == unitId).Select(u => u.PlantId).FirstAsync());
        var (_, builtIn) = GateFormService.BuiltIn(GateFormKind.Mechanical);

        // Word split the marker in two pieces and the client changed the revision: still a valid template.
        var revised = Edit(builtIn, body =>
        {
            var text = body.Descendants<Text>().First(t => t.Text == "Record No. {{RECORD_NO}}");
            text.Text = "Record No. {{RECO";
            text.Parent!.InsertAfterSelf(new Run(new Text("RD_NO}}")));
            body.Descendants<Text>().First(t => t.Text.Contains("Rev 01")).Text = "Form GATE1  |  Source: {{SOURCE}} Rev 02 | 6.8";
        });
        var uploaded = await UploadAsync(plantId, revised, "Gate rev 02.docx");
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var version = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version");
        Assert.False(version.GetProperty("isActive").GetBoolean());

        // Not active yet: the forms still use the built-in template. The sample shows the new one.
        var vessel = await CreateAsync(unitId, "Equipment", "Static", "32-V-901");
        var row = new { assetId = vessel, recordNo = "R-9", date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null };
        Assert.Contains("Rev 01", (await GenerateAsync(admin, unitId, "Mechanical", row)).Pages[0]);
        var sample = await ReadAsync(await admin.GetByteArrayAsync($"/api/gate-forms/templates/{plantId}/Mechanical/sample?version={version.GetProperty("id").GetInt32()}"));
        Assert.Equal(2, sample.Pages.Count);
        Assert.Contains("Record No. REC-0001", sample.Pages[0]);
        Assert.Contains("Rev 02", sample.Pages[0]);

        (await admin.PostAsync($"/api/gate-forms/templates/{plantId}/Mechanical/versions/{version.GetProperty("id").GetInt32()}/activate", null)).EnsureSuccessStatusCode();
        var doc = await GenerateAsync(admin, unitId, "Mechanical", row);
        Assert.Contains("Record No. R-9", doc.Pages[0]);
        Assert.Contains("Rev 02", doc.Pages[0]);

        var slots = await admin.GetFromJsonAsync<JsonElement>("/api/gate-forms/templates");
        var slot = slots.EnumerateArray().Single(s => s.GetProperty("plantId").GetInt32() == plantId && s.GetProperty("kind").GetString() == "Mechanical");
        Assert.False(slot.GetProperty("isBuiltIn").GetBoolean());
        Assert.Equal("Gate rev 02.docx", slot.GetProperty("fileName").GetString());

        // In use: cannot be deleted. Back to the built-in one, then it can.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/gate-forms/templates/{plantId}/Mechanical/versions/{version.GetProperty("id").GetInt32()}")).StatusCode);
        (await admin.PostAsync($"/api/gate-forms/templates/{plantId}/Mechanical/built-in", null)).EnsureSuccessStatusCode();
        Assert.Contains("Rev 01", (await GenerateAsync(admin, unitId, "Mechanical", row)).Pages[0]);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/gate-forms/templates/{plantId}/Mechanical/versions/{version.GetProperty("id").GetInt32()}")).StatusCode);
    }

    [Fact]
    public async Task Templates_with_wrong_markers_are_refused()
    {
        var plantId = await app.WithDbAsync(db => db.Plants.Where(p => p.Name == "Habshan - 0").Select(p => p.Id).FirstAsync());
        var (_, builtIn) = GateFormService.BuiltIn(GateFormKind.Mechanical);

        var unknown = Edit(builtIn, body => body.Descendants<Text>().First(t => t.Text == "Date {{DATE}}").Text = "Date {{FECHA}}");
        Assert.Contains("{{FECHA}} is not a known field", await ErrorAsync(await UploadAsync(plantId, unknown, "a.docx")));

        var broken = Edit(builtIn, body => body.Descendants<Text>().First(t => t.Text == "Date {{DATE}}").Text = "Date {{DATE}");
        Assert.Contains("incomplete or misspelt", await ErrorAsync(await UploadAsync(plantId, broken, "b.docx")));

        var noSection = Edit(builtIn, body => body.Descendants<Text>().First(t => t.Text.Contains("{{SECTION}}")).Text = "System / circuit / section");
        Assert.Contains("{{SECTION}} is missing", await ErrorAsync(await UploadAsync(plantId, noSection, "c.docx")));

        var inHeader = EditHeader(builtIn, header => header.Descendants<Text>().First(t => t.Text.Contains("{{UNIT}}")).Text = "UNIT {{UNIT}} {{RECORD_NO}}");
        Assert.Contains("is in a header or footer", await ErrorAsync(await UploadAsync(plantId, inHeader, "d.docx")));

        Assert.Contains("not a Word document", await ErrorAsync(await UploadAsync(plantId, [1, 2, 3], "e.docx")));
    }

    [Fact]
    public async Task Documentation_list_has_the_last_form_date_and_work_pack_and_says_whether_the_G8_file_is_uploaded()
    {
        var unitId = await UnitAsync("Unit 39");
        var signed = await CreateAsync(unitId, "Equipment", "Static", "39-V-951");
        var line = await CreateAsync(unitId, "Pipeline", "Piping", "39-0951-HC-4\"-77010-P", pid: "39-00-30-951");
        var noForm = await CreateAsync(unitId, "Equipment", "Static", "39-V-952");

        // Two prints: only the last date counts.
        await GenerateAsync(admin, unitId, "Mechanical", new { assetId = signed, recordNo = (string?)null, date = "2026-11-10", section = (string?)null, workPack = "WP-1", unitId = (string?)null });
        await GenerateAsync(admin, unitId, "Mechanical", new { assetId = signed, recordNo = (string?)null, date = "2026-11-20", section = (string?)null, workPack = "WP-2", unitId = (string?)null });
        await GenerateAsync(admin, unitId, "Mechanical", new { assetId = line, recordNo = (string?)null, date = "2026-11-21", section = (string?)null, workPack = (string?)null, unitId = (string?)null });

        // The signed file of G8 for the vessel; a G1 file for the line does not count.
        foreach (var (id, gate) in new[] { (signed, 8), (line, 1) })
        {
            var file = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes($"%PDF-1.7\n% {id}\n%%EOF\n"));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var form = new MultipartFormDataContent { { file, "file", "signed.pdf" }, { new StringContent(JsonSerializer.Serialize(new[] { new { assetId = id, gate } })), "links" } };
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/documents", form)).StatusCode);
        }

        var response = await admin.PostAsJsonAsync($"/api/units/{unitId}/gate-forms/documentation", new { type = "Equipment", assetIds = new[] { noForm, signed, line } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet("Documentation");
        Assert.Equal(["Tag", "Description", "Record No.", "Gate form date", "Work pack / P&ID rev.", "G8 signed file uploaded"], Enumerable.Range(1, 6).Select(c => ws.Cell(1, c).GetString()));
        // The line is not in the Equipment register; the rest keep the order of the screen.
        Assert.Equal("39-V-952", ws.Cell(2, 1).GetString());
        Assert.Equal("", ws.Cell(2, 3).GetString());
        Assert.True(ws.Cell(2, 4).IsEmpty());
        Assert.Equal("", ws.Cell(2, 5).GetString());
        Assert.Equal("No", ws.Cell(2, 6).GetString());
        Assert.Equal("39-V-951", ws.Cell(3, 1).GetString());
        Assert.Equal("Item 39-V-951", ws.Cell(3, 2).GetString());
        Assert.StartsWith("ITR-039-STAT-", ws.Cell(3, 3).GetString());
        Assert.Equal(new DateTime(2026, 11, 20), ws.Cell(3, 4).GetDateTime());
        Assert.Equal("WP-2", ws.Cell(3, 5).GetString());
        Assert.Equal("Yes", ws.Cell(3, 6).GetString());
        Assert.True(ws.Cell(4, 1).IsEmpty());

        // Lines: the work pack printed is the P&ID number when nothing else was typed.
        response = await admin.PostAsJsonAsync($"/api/units/{unitId}/gate-forms/documentation", new { type = "Pipeline", assetIds = new[] { line } });
        using var lines = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var lws = lines.Worksheet("Documentation");
        Assert.Equal(new DateTime(2026, 11, 21), lws.Cell(2, 4).GetDateTime());
        Assert.Equal("39-00-30-951", lws.Cell(2, 5).GetString());
        Assert.Equal("No", lws.Cell(2, 6).GetString());
    }

    [Fact]
    public async Task Record_no_is_the_next_of_the_discipline_and_unit_kept_on_reprints_and_can_be_typed_by_hand()
    {
        var unitId = await UnitAsync("Unit 41");
        var a = await CreateAsync(unitId, "Equipment", "Static", "41-V-901");
        var b = await CreateAsync(unitId, "Equipment", "Static", "41-V-902");
        var c = await CreateAsync(unitId, "Equipment", "Static", "41-V-903");
        var d = await CreateAsync(unitId, "Equipment", "Static", "41-V-904");
        object Row(Guid id, string? recordNo = null) => new { assetId = id, recordNo, date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null };

        // The dialog shows what each asset will get, in the order of the rows.
        var rows = (await PrepareAsync(admin, unitId, "Mechanical", a, b)).GetProperty("rows");
        Assert.Equal("ITR-041-STAT-001", rows[0].GetProperty("recordNoNext").GetString());
        Assert.Equal("ITR-041-STAT-002", rows[1].GetProperty("recordNoNext").GetString());

        var doc = await GenerateAsync(admin, unitId, "Mechanical", Row(a), Row(b));
        Assert.Contains("Record No. ITR-041-STAT-001", doc.Pages[0]);
        Assert.Contains("Record No. ITR-041-STAT-002", doc.Pages[1]);

        // A reprint keeps the number, even sent empty.
        doc = await GenerateAsync(admin, unitId, "Mechanical", Row(a));
        Assert.Contains("Record No. ITR-041-STAT-001", doc.Pages[0]);
        Assert.Equal(JsonValueKind.Null, (await PrepareAsync(admin, unitId, "Mechanical", a)).GetProperty("rows")[0].GetProperty("recordNoNext").ValueKind);

        // A form made outside the app: its number typed by hand and saved without printing. The counter follows it.
        var saved = await admin.PostAsJsonAsync($"/api/units/{unitId}/gate-forms/save", new { kind = "Mechanical", rows = new[] { Row(c, " itr-stat-41-10 ") } });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal("ITR-041-STAT-010", await app.WithDbAsync(db => db.GateFormEntries.Where(e => e.AssetId == c).Select(e => e.RecordNo).FirstAsync()));
        doc = await GenerateAsync(admin, unitId, "Mechanical", Row(d));
        Assert.Contains("Record No. ITR-041-STAT-011", doc.Pages[0]);

        // Another asset's number is refused; saving without printing never takes a number.
        var fifth = await CreateAsync(unitId, "Equipment", "Static", "41-V-905");
        var taken = await admin.PostAsJsonAsync($"/api/units/{unitId}/gate-forms/generate", new { kind = "Mechanical", rows = new[] { Row(fifth, "ITR-041-STAT-002") } });
        Assert.Contains("already used by 41-V-902", await ErrorAsync(taken));
        (await admin.PostAsJsonAsync($"/api/units/{unitId}/gate-forms/save", new { kind = "Mechanical", rows = new[] { new { assetId = fifth, recordNo = (string?)null, date = "2026-11-22", section = (string?)null, workPack = (string?)null, unitId = (string?)null } } })).EnsureSuccessStatusCode();
        Assert.Null(await app.WithDbAsync(db => db.GateFormEntries.Where(x => x.AssetId == fifth).Select(x => x.RecordNo).FirstAsync()));

        // Admin: the counter of each code and unit, and the code of each discipline.
        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/gate-forms/record-numbers");
        var counter = settings.GetProperty("counters").EnumerateArray().Single(x => x.GetProperty("unitNumber").GetInt32() == 41 && x.GetProperty("code").GetString() == "STAT");
        Assert.Equal(11, counter.GetProperty("lastNumber").GetInt32());
        Assert.Equal("ITR-041-STAT-012", counter.GetProperty("next").GetString());
        var codes = settings.GetProperty("codes").EnumerateArray().ToDictionary(x => x.GetProperty("discipline").GetString()!, x => x);
        Assert.Equal("STAT", codes["Static"].GetProperty("code").GetString());
        Assert.Equal("ELEC", codes["Electrical"].GetProperty("code").GetString());
        Assert.Equal("PIP", codes["Piping"].GetProperty("code").GetString());

        // Without a code, the Record No. is typed by hand.
        var staticId = codes["Static"].GetProperty("disciplineId").GetInt32();
        Assert.Contains("letters and digits", await ErrorAsync(await admin.PutAsJsonAsync($"/api/gate-forms/record-numbers/disciplines/{staticId}", new { code = "ST-1" })));
        (await admin.PutAsJsonAsync($"/api/gate-forms/record-numbers/disciplines/{staticId}", new { code = "" })).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await PrepareAsync(admin, unitId, "Mechanical", fifth)).GetProperty("rows")[0].GetProperty("recordNoNext").ValueKind);
        var put = await admin.PutAsJsonAsync($"/api/gate-forms/record-numbers/disciplines/{staticId}", new { code = "stat" });
        Assert.Equal("STAT", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("ITR-041-STAT-012", (await PrepareAsync(admin, unitId, "Mechanical", fifth)).GetProperty("rows")[0].GetProperty("recordNoNext").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Doc(string Header, List<string> Pages);

    private Task<int> UnitAsync(string name) => app.WithDbAsync(db => db.Units.Where(u => u.Name == name).Select(u => u.Id).FirstAsync());

    private async Task<Guid> CreateAsync(int unitId, string type, string discipline, string tag, string? pid = null, bool withDescription = true)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = withDescription ? "Item " + tag : null, pidNumber = pid });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> PrepareAsync(HttpClient client, int unitId, string kind, params Guid[] ids)
    {
        var response = await client.PostAsJsonAsync($"/api/units/{unitId}/gate-forms/prepare", new { kind, assetIds = ids });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Doc> GenerateAsync(HttpClient client, int unitId, string kind, params object[] rows)
    {
        var response = await client.PostAsJsonAsync($"/api/units/{unitId}/gate-forms/generate", new { kind, rows });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(GateFormService.DocxType, response.Content.Headers.ContentType?.MediaType);
        return await ReadAsync(await response.Content.ReadAsByteArrayAsync());
    }

    private Task<HttpResponseMessage> UploadAsync(int plantId, byte[] content, string name, string kind = "Mechanical")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(GateFormService.DocxType);
        form.Add(file, "file", name);
        return admin.PostAsync($"/api/gate-forms/templates/{plantId}/{kind}", form);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
    }

    /// <summary>Header text and the text of each page (a page starts at a paragraph with "page break before").</summary>
    private static Task<Doc> ReadAsync(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var doc = WordprocessingDocument.Open(stream, false);
        var main = doc.MainDocumentPart!;
        var header = string.Join("\n", main.HeaderParts.Select(h => string.Join("\n", h.Header.Descendants<Paragraph>().Select(p => p.InnerText))));
        var pages = new List<string>();
        var current = new List<string>();
        foreach (var p in main.Document.Body!.Descendants<Paragraph>())
        {
            if (p.ParagraphProperties?.PageBreakBefore is not null && current.Count > 0)
            {
                pages.Add(string.Join("\n", current));
                current = [];
            }
            current.Add(p.InnerText);
        }
        pages.Add(string.Join("\n", current));
        return Task.FromResult(new Doc(header, pages));
    }

    private static byte[] Edit(byte[] template, Action<Body> change)
    {
        using var stream = new MemoryStream();
        stream.Write(template);
        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            change(doc.MainDocumentPart!.Document.Body!);
            doc.MainDocumentPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] EditHeader(byte[] template, Action<Header> change)
    {
        using var stream = new MemoryStream();
        stream.Write(template);
        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            var header = doc.MainDocumentPart!.HeaderParts.First().Header;
            change(header);
            header.Save();
        }
        return stream.ToArray();
    }
}
