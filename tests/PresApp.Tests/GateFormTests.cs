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
    [InlineData(GateFormKind.Mechanical, "GateForm_WorkPack.docx")]
    [InlineData(GateFormKind.Electrical, "GateForm_Electrical.docx")]
    public void Built_in_templates_hold_every_field_of_the_client_form(GateFormKind kind, string resource)
    {
        var (name, content) = GateFormService.BuiltIn(kind);
        Assert.Equal(resource, name);
        var check = GateFormDocument.Inspect(content);
        Assert.Empty(check.Errors);
        Assert.Equal(["UNIT", "SOURCE", "RECORD_NO", "DATE", "SECTION", "WORKPACK"], check.Fields);
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
        // Empty fields print the blank line of the client form.
        Assert.Contains("Record No. __________________________", doc.Pages[1]);
        Assert.Contains("Date ______________________________", doc.Pages[1]);
        Assert.Contains("System / circuit / section 39-0901-HC-4\"-77010-P", doc.Pages[1]);
        Assert.Contains("Work pack / P&ID rev. 39-00-30-201, 39-00-30-202", doc.Pages[1]);
        Assert.DoesNotContain("{{", string.Join("", doc.Pages) + doc.Header);

        // Saved: typed values and overrides only; the line keeps following its tag and P&ID.
        var entries = await app.WithDbAsync(db => db.GateFormEntries.AsNoTracking().ToDictionaryAsync(e => e.AssetId));
        Assert.Equal("REC-7", entries[vessel].RecordNo);
        Assert.Equal(new DateOnly(2026, 11, 25), entries[vessel].Date);
        Assert.Equal("39-V-901 / circuit 2", entries[vessel].Section);
        Assert.False(entries.ContainsKey(line));

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
    public async Task Electrical_form_is_only_for_electrical_items_and_prints_an_empty_source_when_unknown()
    {
        var unitId = await UnitAsync("Unit 42");
        var motor = await CreateAsync(unitId, "ElectricalInstrument", "Electrical", "42-PM-901");
        var analyser = await CreateAsync(unitId, "ElectricalInstrument", "E&I", "42-JB-901");
        var vessel = await CreateAsync(unitId, "Equipment", "Static", "42-V-901");

        var prepared = await PrepareAsync(admin, unitId, "Electrical", motor, analyser, vessel);
        Assert.Equal(JsonValueKind.Null, prepared.GetProperty("source").ValueKind);
        Assert.Single(prepared.GetProperty("rows").EnumerateArray());
        Assert.Equal(2, prepared.GetProperty("skipped").GetArrayLength());

        var doc = await GenerateAsync(admin, unitId, "Electrical",
            new { assetId = motor, recordNo = "E-1", date = "2026-11-25", section = (string?)null, workPack = (string?)null, unitId = (string?)null },
            new { assetId = analyser, recordNo = "E-2", date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null });
        Assert.Single(doc.Pages);
        Assert.Contains("Printable section acceptance gate for Electrical motors", doc.Pages[0]);
        Assert.Contains("Form GATE | Source:  Rev 01 | 6.8", doc.Pages[0]);
        Assert.Contains("UNIT 42 PRESERVATION", doc.Header);

        // Once the source is set in Admin it is printed.
        (await admin.PutAsJsonAsync($"/api/gate-forms/settings/{unitId}", new { mechanicalSource = (string?)null, electricalSource = "DOD-42-EL", defaultUnitId = (string?)null })).EnsureSuccessStatusCode();
        doc = await GenerateAsync(admin, unitId, "Electrical", new { assetId = motor, recordNo = "E-1", date = (string?)null, section = (string?)null, workPack = (string?)null, unitId = (string?)null });
        Assert.Contains("Source: DOD-42-EL Rev 01 | 6.8", doc.Pages[0]);
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

    // ------------------------------------------------------------------ helpers

    private sealed record Doc(string Header, List<string> Pages);

    private Task<int> UnitAsync(string name) => app.WithDbAsync(db => db.Units.Where(u => u.Name == name).Select(u => u.Id).FirstAsync());

    private async Task<Guid> CreateAsync(int unitId, string type, string discipline, string tag, string? pid = null)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = "Item " + tag, pidNumber = pid });
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

    private Task<HttpResponseMessage> UploadAsync(int plantId, byte[] content, string name)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(GateFormService.DocxType);
        form.Add(file, "file", name);
        return admin.PostAsync($"/api/gate-forms/templates/{plantId}/Mechanical", form);
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
