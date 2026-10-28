using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>Template v4 (Unit 39 file): upload, export with categories by code, import of the category columns.</summary>
public sealed class TemplateV4Tests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public TemplateV4Tests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task V4_template_round_trip_keeps_the_category_chosen_by_code()
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());

        // ---- upload: the unit has no categories yet, so it takes the template's list with codes and notes
        var upload = await admin.PutAsync($"/api/excel-templates/{unitId}", File(await System.IO.File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Support", "Template_Unit_39_v4.xlsx")), "Template_Unit_39.xlsx"));
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());
        var categories = (await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/categories")).EnumerateArray().ToList();
        Assert.Equal(5, categories.Count(c => c.GetProperty("section").GetString() == "Hold"));
        Assert.Equal(2, categories.Count(c => c.GetProperty("section").GetString() == "NotApplicable"));
        var cat1 = categories.First(c => c.GetProperty("section").GetString() == "Hold" && c.GetProperty("code").GetString() == "Cat1");
        Assert.Equal("Conflict Between DNV and EnerMech", cat1.GetProperty("label").GetString());
        Assert.Equal("Preservation Not Requried", cat1.GetProperty("recommendation").GetString());
        int Id(string section, string code) => categories.First(c => c.GetProperty("section").GetString() == section && c.GetProperty("code").GetString() == code).GetProperty("id").GetInt32();

        // ---- three equipment: Hold Cat1, N/A Cat2, ready for preservation
        var a = await CreateAsync(unitId, "39-V-901");
        var b = await CreateAsync(unitId, "39-V-902");
        var c = await CreateAsync(unitId, "39-V-903");
        Assert.True((await admin.PutAsJsonAsync($"/api/assets/{a}/dashboard-category", new { categoryId = Id("Hold", "Cat1"), expected = (int?)null })).IsSuccessStatusCode);
        Assert.True((await admin.PutAsJsonAsync($"/api/assets/{b}/dashboard-category", new { categoryId = Id("NotApplicable", "Cat2"), expected = (int?)null })).IsSuccessStatusCode);
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{c}", new { changes = new[] { new { field = "ReadyForPreservation", from = (string?)null, to = "Yes" } } });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        // ---- export
        var response = await admin.GetAsync($"/api/units/{unitId}/excel/export");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (Environment.GetEnvironmentVariable("PRESAPP_V4_EXPORT") is { Length: > 0 } dump) await System.IO.File.WriteAllBytesAsync(dump, bytes);

        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            Assert.Equal("4", Meta(wb, "FormatVersion"));
            Assert.Equal("Cat1", TestWorkbooks.Row(wb, "39-V-901").Field("Hold Category").GetString());
            Assert.Equal("", TestWorkbooks.Row(wb, "39-V-901").Field("N/A Category").GetString());
            Assert.Equal("Cat2", TestWorkbooks.Row(wb, "39-V-902").Field("N/A Category").GetString());
            Assert.Equal("Yes", TestWorkbooks.Row(wb, "39-V-903").Field("Ready For Preservation").GetString());
            var table = wb.Worksheets.SelectMany(w => w.Tables).First(t => t.Name == "Categories");
            Assert.Equal(7, table.DataRange.RowCount());
            Assert.Equal(("Hold", "Cat1"), (table.DataRange.Row(1).Cell(1).GetString(), table.DataRange.Row(1).Cell(2).GetString()));
            Assert.Equal(("N/A", "Cat2"), (table.DataRange.Row(7).Cell(1).GetString(), table.DataRange.Row(7).Cell(2).GetString()));
            var dashboard = wb.Worksheet("Dashboard");
            Assert.False(dashboard.Row(24).IsHidden);   // Hold slot 5 of 10
            Assert.True(dashboard.Row(25).IsHidden);    // Hold slot 6
            Assert.False(dashboard.Row(33).IsHidden);   // N/A slot 2
            Assert.True(dashboard.Row(34).IsHidden);    // N/A slot 3
            Assert.Contains("Categories[Code]", dashboard.Cell("B20").FormulaA1);
        }

        // ---- import: move A to Hold Cat2, clear B, fill both columns on C
        var edited = TestWorkbooks.Edit(bytes, wb =>
        {
            TestWorkbooks.Row(wb, "39-V-901").Field("Hold Category").SetValue("cat 2");
            TestWorkbooks.Row(wb, "39-V-902").Field("N/A Category").SetValue("");
            TestWorkbooks.Row(wb, "39-V-903").Field("Hold Category").SetValue("Cat3");
            TestWorkbooks.Row(wb, "39-V-903").Field("N/A Category").SetValue("Cat1");
            TestWorkbooks.Row(wb, "39-V-903").Field("Ready For Preservation").SetValue("No");
        });
        var preview = await PostFileAsync($"/api/units/{unitId}/excel/import", edited);
        var changes = preview.GetProperty("changes").EnumerateArray()
            .Select(x => (x.GetProperty("assetTag").GetString(), x.GetProperty("field").GetString(), x.GetProperty("outcome").GetString(), x.GetProperty("newValue").GetString())).ToList();
        Assert.Contains(("39-V-901", "DashboardCategory", "Applied", "Hold:Cat2"), changes);
        Assert.Contains(("39-V-902", "DashboardCategory", "Applied", (string?)null), changes);
        Assert.Contains(changes, x => x is ("39-V-903", "DashboardCategory", "Invalid", _));
        Assert.Contains(("39-V-903", "ReadyForPreservation", "Applied", "No"), changes);

        var applied = await admin.PostAsync($"/api/imports/{preview.GetProperty("run").GetProperty("id").GetInt64()}/apply", null);
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        var profiles = new List<JsonElement>();
        foreach (var id in new[] { a, b, c }) profiles.Add(await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}"));   // one at a time: the test database has one connection
        Assert.Equal("Cat2", profiles[0].GetProperty("dashboardCategory").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, profiles[1].GetProperty("dashboardCategory").ValueKind);
        Assert.Equal(JsonValueKind.Null, profiles[2].GetProperty("dashboardCategory").ValueKind);
        Assert.Equal("No", profiles[2].GetProperty("values").GetProperty("ReadyForPreservation").GetString());
    }

    private async Task<Guid> CreateAsync(int unitId, string tag)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Item " + tag });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> PostFileAsync(string url, byte[] bytes)
    {
        var response = await admin.PostAsync(url, File(bytes, "edited.xlsx"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static MultipartFormDataContent File(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }

    private static string Meta(XLWorkbook wb, string key)
    {
        var ws = wb.Worksheet("_meta");
        return ws.RangeUsed()!.Rows().First(r => r.Cell(1).GetString() == key).Cell(2).GetString();
    }
}
