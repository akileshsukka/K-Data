using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class CustomFieldTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public CustomFieldTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<(int UnitId, Guid Id)> CreateAsync(string tag, string unit, string type, string discipline)
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var r = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (unitId, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static MultipartFormDataContent File(byte[] bytes)
    {
        var f = new ByteArrayContent(bytes);
        f.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { f, "file", "list.xlsx" } };
    }

    [Fact]
    public async Task EI_custom_fields_are_defined_by_an_admin_edited_on_the_asset_shown_in_the_register_and_audited()
    {
        var eiDiscipline = await app.WithDbAsync(db => db.Disciplines.Where(d => d.AssetType == AssetType.ElectricalInstrument).Select(d => d.Name).FirstAsync());
        var (unitId, id) = await CreateAsync("31-TT-501", "Unit 31", "ElectricalInstrument", eiDiscipline);

        var type = await admin.PostAsJsonAsync("/api/custom-fields", new { assetType = "ElectricalInstrument", label = "Instrument type", dataType = "List", options = new[] { "Transmitter", "Gauge", "Switch" }, section = "Instrument", sortOrder = 1, showInRegister = true });
        type.EnsureSuccessStatusCode();
        var def = await type.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InstrumentType", def.GetProperty("key").GetString());
        (await admin.PostAsJsonAsync("/api/custom-fields", new { assetType = "ElectricalInstrument", label = "Calibration due", dataType = "Date", section = "Instrument", sortOrder = 2, showInRegister = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/custom-fields", new { assetType = "ElectricalInstrument", label = "Instrument type", dataType = "Text", sortOrder = 3, showInRegister = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/custom-fields", new { assetType = "ElectricalInstrument", label = "Action By", dataType = "Text", sortOrder = 3, showInRegister = false })).StatusCode);

        var values = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/custom-fields");
        Assert.Equal(2, values.GetArrayLength());

        var save = await admin.PutAsJsonAsync($"/api/assets/{id}/custom-fields", new { changes = new[] { new { key = "InstrumentType", from = (string?)null, to = "transmitter" }, new { key = "CalibrationDue", from = (string?)null, to = "2027-01-15" } } });
        save.EnsureSuccessStatusCode();
        var after = await save.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Transmitter", after.EnumerateArray().Single(v => v.GetProperty("key").GetString() == "InstrumentType").GetProperty("value").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/assets/{id}/custom-fields", new { changes = new[] { new { key = "InstrumentType", from = "Transmitter", to = "Valve" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/assets/{id}/custom-fields", new { changes = new[] { new { key = "InstrumentType", from = "Gauge", to = "Switch" } } })).StatusCode);

        var register = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=ElectricalInstrument");
        var row = register.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal("Transmitter", row.GetProperty("custom").GetProperty("InstrumentType").GetString());
        Assert.False(row.GetProperty("custom").TryGetProperty("CalibrationDue", out _));

        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("label").GetString() == "Instrument type" && h.GetProperty("newValue").GetString() == "Transmitter");

        // A list option removed later is deactivated; the kind of a field with values cannot change.
        var defId = def.GetProperty("id").GetInt32();
        var changed = await admin.PutAsJsonAsync($"/api/custom-fields/{defId}", new { assetType = "ElectricalInstrument", label = "Instrument type", dataType = "Text", sortOrder = 1, showInRegister = true });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
    }

    [Fact]
    public async Task A_client_list_loads_custom_columns_and_puts_lines_in_their_circuit()
    {
        (await admin.PostAsJsonAsync("/api/custom-fields", new { assetType = "Pipeline", label = "Test pressure (barg)", dataType = "Number", sortOrder = 1, showInRegister = true })).EnsureSuccessStatusCode();
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 541").Select(u => u.Id).FirstAsync());

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Lines");
        string[] headers = ["Line No.", "Circuit", "Test Pressure (barg)", "Length"];
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).SetValue(headers[c]);
        ws.Cell(2, 1).SetValue("541-0001-HC-2\"-11010"); ws.Cell(2, 2).SetValue("c-541-01"); ws.Cell(2, 3).SetValue(15.5);
        ws.Cell(3, 1).SetValue("541-0002-HC-2\"-11010"); ws.Cell(3, 2).SetValue("C-541-01"); ws.Cell(3, 3).SetValue("abc");
        ws.Cell(4, 1).SetValue("541-0003-HC-3\"-11010"); ws.Cell(4, 2).SetValue("C-541-02");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var preview = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File(ms.ToArray()))).Content.ReadFromJsonAsync<JsonElement>();
        var changes = preview.GetProperty("changes").EnumerateArray().ToList();
        Assert.Contains(changes, c => c.GetProperty("field").GetString() == "Circuit" && c.GetProperty("newValue").GetString() == "C-541-01" && c.GetProperty("reason").GetString()!.StartsWith("New circuit"));
        Assert.Contains(changes, c => c.GetProperty("field").GetString() == "Test pressure (barg)" && c.GetProperty("outcome").GetString() == "Invalid");
        Assert.False(await app.WithDbAsync(db => db.Groups.AnyAsync(g => g.Code == "C-541-01")));

        var id = preview.GetProperty("run").GetProperty("id").GetInt64();
        (await admin.PostAsync($"/api/imports/{id}/apply", null)).EnsureSuccessStatusCode();
        var circuit = await app.WithDbAsync(db => db.Groups.Include(g => g.Members).Include(g => g.GroupType).SingleAsync(g => g.Code == "C-541-01"));
        Assert.Equal("Circuit", circuit.GroupType.Name);
        Assert.Equal(2, circuit.Members.Count);

        var register = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Pipeline");
        var first = register.EnumerateArray().Single(r => r.GetProperty("tag").GetString() == "541-0001-HC-2\"-11010");
        Assert.Equal("C-541-01", first.GetProperty("circuit").GetString());
        Assert.Equal("15.5", first.GetProperty("custom").GetProperty("TestPressureBarg").GetString());

        // A later list moves the line to another circuit.
        using var wb2 = new XLWorkbook();
        var ws2 = wb2.AddWorksheet("Lines");
        ws2.Cell(1, 1).SetValue("Line No."); ws2.Cell(1, 2).SetValue("Circuit");
        ws2.Cell(2, 1).SetValue("541-0001-HC-2\"-11010"); ws2.Cell(2, 2).SetValue("C-541-02");
        using var ms2 = new MemoryStream();
        wb2.SaveAs(ms2);
        var p2 = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File(ms2.ToArray()))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(p2.GetProperty("changes").EnumerateArray(), c => c.GetProperty("reason").GetString() == "Moved from C-541-01.");
        (await admin.PostAsync($"/api/imports/{p2.GetProperty("run").GetProperty("id").GetInt64()}/apply", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, await app.WithDbAsync(db => db.GroupMembers.CountAsync(m => m.Group.Code == "C-541-01")));
        Assert.Equal(2, await app.WithDbAsync(db => db.GroupMembers.CountAsync(m => m.Group.Code == "C-541-02")));

        // Deleting a group keeps its assets.
        var c2 = await app.WithDbAsync(db => db.Groups.Where(g => g.Code == "C-541-02").Select(g => g.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/groups/{c2}")).StatusCode);
        Assert.False(await app.WithDbAsync(db => db.Groups.AnyAsync(g => g.Code == "C-541-02")));
        Assert.True(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Tag == "541-0003-HC-3\"-11010")));
        var c1 = await app.WithDbAsync(db => db.Groups.Where(g => g.Code == "C-541-01").Select(g => g.Id).FirstAsync());
        var off = await (await admin.PostAsync($"/api/groups/{c1}/active?active=false", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(off.GetProperty("group").GetProperty("isActive").GetBoolean());
    }
}
