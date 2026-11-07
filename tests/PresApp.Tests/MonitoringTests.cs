using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class MonitoringTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public MonitoringTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<(int UnitId, Guid Id)> CreateAsync(string tag, string unit = "Unit 31")
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var r = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (unitId, Guid.Parse((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!));
    }

    private Task<int> N2Async() => app.WithDbAsync(db => db.MeasurementTypes.Where(t => t.Key == "G4N2BlanketBarg").Select(t => t.Id).FirstAsync());

    private static JsonElement Row(JsonElement view, Guid id) => view.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);

    private Task<int> TypeAsync(string key) => app.WithDbAsync(db => db.MeasurementTypes.Where(t => t.Key == key).Select(t => t.Id).FirstAsync());

    [Fact]
    public async Task Preserved_assets_are_monitored_with_the_pressure_readings_taken_since_they_were_preserved()
    {
        var (unitId, a) = await CreateAsync("31-MON-1");
        var (_, b) = await CreateAsync("31-MON-2");
        var (_, c) = await CreateAsync("31-MON-3");
        var n2 = await N2Async();
        var pressure = await TypeAsync("PressureBarg");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var id in new[] { a, b })
            (await admin.PostAsJsonAsync($"/api/assets/{id}/preserved", new { preservedOn = today.AddDays(-20) })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/assets/{a}/readings", new { measuredAtUtc = DateTime.UtcNow.AddDays(-10), values = new[] { new { measurementTypeId = pressure, value = 0.8m } } })).EnsureSuccessStatusCode();
        // An N2 blanket reading does not count for POST monitoring.
        (await admin.PostAsJsonAsync($"/api/assets/{a}/readings", new { measuredAtUtc = DateTime.UtcNow.AddDays(-1), values = new[] { new { measurementTypeId = n2, value = 0.8m } } })).EnsureSuccessStatusCode();

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/monitoring?unitId={unitId}");
        Assert.Equal("Overdue", Row(view, a).GetProperty("status").GetString());
        Assert.Equal(10, Row(view, a).GetProperty("daysSincePressure").GetInt32());
        Assert.Equal("Post", Row(view, a).GetProperty("phase").GetString());
        Assert.Equal("No reading since preserved", Row(view, b).GetProperty("status").GetString());
        Assert.Equal("During preservation", Row(view, c).GetProperty("status").GetString());

        // A round with one pressure column: Pressure (barg) for the preserved assets, G4 - N2 Blanket for the one still DURING.
        // With a minimum of 1 barg only the POST 0.5 reading is low; a DURING asset is never flagged.
        (await admin.PutAsJsonAsync("/api/settings/monitoring", new { intervalDays = 7, minPressureBarg = 1.0m })).EnsureSuccessStatusCode();
        var round = await admin.PostAsJsonAsync("/api/monitoring/rounds", new
        {
            measuredAtUtc = DateTime.UtcNow.AddMinutes(-5),
            rows = new[] { new { assetId = a, pressure = 1.2m }, new { assetId = b, pressure = 0.5m }, new { assetId = c, pressure = 0.4m } }
        });
        Assert.True(round.IsSuccessStatusCode, await round.Content.ReadAsStringAsync());
        Assert.Equal(3, (await round.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("readings").GetInt32());
        view = await admin.GetFromJsonAsync<JsonElement>($"/api/monitoring?unitId={unitId}");
        Assert.Equal("OK", Row(view, a).GetProperty("status").GetString());
        Assert.Equal(1.2m, Row(view, a).GetProperty("pressureBarg").GetDecimal());
        Assert.False(Row(view, a).GetProperty("lowPressure").GetBoolean());
        Assert.True(Row(view, b).GetProperty("lowPressure").GetBoolean());
        Assert.False(Row(view, c).GetProperty("lowPressure").GetBoolean());
        Assert.Equal(0.4m, Row(view, c).GetProperty("n2Barg").GetDecimal());
        Assert.Equal(JsonValueKind.Null, Row(view, c).GetProperty("pressureBarg").ValueKind);
        Assert.Equal("During preservation", Row(view, c).GetProperty("status").GetString());
        (await admin.PutAsJsonAsync("/api/settings/monitoring", new { intervalDays = 7, minPressureBarg = (decimal?)null })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Readings_are_loaded_from_the_template_by_tag_and_date()
    {
        var (unitId, post) = await CreateAsync("31-RDG-1");
        var (_, during) = await CreateAsync("31-RDG-2 A1/A2");
        var (_, other) = await CreateAsync("31-RDG-3");
        var today = DateTime.UtcNow.AddHours(4).Date;   // Dubai
        (await admin.PostAsJsonAsync($"/api/assets/{post}/preserved", new { preservedOn = DateOnly.FromDateTime(today.AddDays(-20)) })).EnsureSuccessStatusCode();

        var response = await admin.GetAsync($"/api/units/{unitId}/monitoring/readings-template");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var template = await response.Content.ReadAsByteArrayAsync();
        int o2Column;
        using (var wb = new XLWorkbook(new MemoryStream(template)))
        {
            var header = wb.Worksheet(1).Row(4);
            Assert.Equal("Tag", header.Cell(1).GetString());
            Assert.Equal("Measured date", header.Cell(2).GetString());
            Assert.Equal("Pressure (barg)", header.Cell(3).GetString());
            Assert.DoesNotContain(header.CellsUsed(), c => c.GetString().Contains("N2 Blanket"));
            o2Column = header.CellsUsed().Single(c => c.GetString() == "G3 - O2 (%)").Address.ColumnNumber;
        }

        var file = TestWorkbooks.Edit(template, wb =>
        {
            var ws = wb.Worksheet(1);
            void Row(int r, string tag, object? date, object? pressure, object? o2 = null)
            {
                ws.Cell(r, 1).Value = tag;
                if (date is DateTime d) ws.Cell(r, 2).Value = d; else if (date is string s) ws.Cell(r, 2).Value = s;
                if (pressure is double p) ws.Cell(r, 3).Value = p; else if (pressure is string ps) ws.Cell(r, 3).Value = ps;
                if (o2 is double x) ws.Cell(r, o2Column).Value = x;
            }
            Row(5, "31-rdg-1", today.AddDays(-1), 0.6, 1.0);                                           // POST: Pressure (barg)
            Row(6, "31–RDG-2  a1/a2", today.AddDays(-2).ToString("dd-MMM-yy", System.Globalization.CultureInfo.InvariantCulture), 0.45); // DURING: N2; en dash, spaces, case
            Row(7, "31-RDG-3", today.AddDays(-1), "UNDER MAINTENANCE");                                 // text: not loaded
            Row(8, "31-RDG-3", today.AddDays(-2), 0.0);                                                 // zero: loaded, warned
            Row(9, "31-RDG-999", today.AddDays(-1), 1.0);                                               // unknown tag
            Row(10, "31-RDG-1", null, 1.0);                                                             // no date
            Row(11, "31-RDG-1", today.AddDays(-1), null);                                               // nothing measured: skipped
        });

        var preview = await PostFileAsync($"/api/units/{unitId}/monitoring/readings-import", file);
        var changes = Changes(preview);
        Assert.Contains(("31-RDG-1", "PressureBarg", "Applied"), changes);
        Assert.Contains(("31-RDG-1", "G3O2Pct", "Applied"), changes);
        Assert.Contains(("31-RDG-2 A1/A2", "G4N2BlanketBarg", "Applied"), changes);
        Assert.Contains(("31-RDG-3", "G4N2BlanketBarg", "Invalid"), changes);
        Assert.Contains(("31-RDG-3", "G4N2BlanketBarg", "Warning"), changes);
        Assert.Contains(("31-RDG-999", "(row)", "Rejected"), changes);
        Assert.Contains(("31-RDG-1", "(row)", "Rejected"), changes);
        Assert.Equal(7, changes.Count);
        Assert.False(await app.WithDbAsync(db => db.Readings.AnyAsync(r => r.AssetId == post)));   // a preview saves nothing

        var applied = await admin.PostAsync($"/api/imports/{preview.GetProperty("run").GetProperty("id").GetInt64()}/apply", null);
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        var saved = await app.WithDbAsync(db => db.Readings.Where(r => r.AssetId == post || r.AssetId == during || r.AssetId == other)
            .Select(r => new { r.AssetId, r.MeasurementType.Key, r.Value, r.MeasuredAtUtc, r.Phase, r.Source }).ToListAsync());
        Assert.Equal(4, saved.Count);
        var pressure = saved.Single(r => r.AssetId == post && r.Key == "PressureBarg");
        Assert.Equal(0.6m, pressure.Value);
        Assert.Equal(today.AddDays(-1).AddHours(8), pressure.MeasuredAtUtc);   // 12:00 Dubai
        Assert.Equal("Post", pressure.Phase.ToString());
        Assert.Equal("Excel", pressure.Source.ToString());
        Assert.Equal(0.45m, saved.Single(r => r.AssetId == during).Value);
        Assert.Equal("During", saved.Single(r => r.AssetId == during).Phase.ToString());
        Assert.Equal(0m, saved.Single(r => r.AssetId == other).Value);

        // The same file again: what is already in the app is not loaded twice.
        var again = Changes(await PostFileAsync($"/api/units/{unitId}/monitoring/readings-import", file));
        Assert.Contains(("31-RDG-1", "PressureBarg", "No change"), again);
        Assert.Contains(("31-RDG-3", "G4N2BlanketBarg", "No change"), again);
    }

    [Fact]
    public async Task The_monitoring_report_shows_daily_readings_until_preservation_and_week_1_then_weekly_readings_after()
    {
        var (unitId, building) = await CreateAsync("31-RPT-1");
        var (_, preserved) = await CreateAsync("31-RPT-2");
        var ei = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Electrical").Select(d => d.Id).FirstAsync());
        var eiResponse = await admin.PostAsJsonAsync("/api/assets", new { type = "ElectricalInstrument", unitId, disciplineId = ei, tag = "31-RPT-EI" });
        Assert.Equal(HttpStatusCode.Created, eiResponse.StatusCode);
        var eiId = Guid.Parse((await eiResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);

        var n2 = await N2Async();
        var pressure = await TypeAsync("PressureBarg");
        var dewPoint = await TypeAsync("G2DewPointC");
        var today = DateTime.UtcNow.AddHours(4).Date;   // Dubai
        DateTime Noon(DateTime day) => day.AddHours(8);  // 12:00 Dubai in UTC
        async Task Add(Guid id, int type, DateTime at, decimal value) =>
            Assert.True((await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = at, values = new[] { new { measurementTypeId = type, value } } })).IsSuccessStatusCode);

        // Still being preserved: N2 and dew point, two N2 readings on the same day.
        await Add(building, n2, Noon(today.AddDays(-5)), 0.2m);
        await Add(building, n2, Noon(today.AddDays(-3)), 0.4m);
        await Add(building, n2, Noon(today.AddDays(-3)).AddHours(2), 1m);    // same day: the last one is shown, target reached
        await Add(building, dewPoint, Noon(today.AddDays(-4)), -40m);
        await Add(eiId, n2, Noon(today.AddDays(-2)), 0.5m);                    // E&I: not in this report

        // Preserved 40 days ago: first reading 30 days ago, 6 readings in its first 7 days, then 2 more.
        await Add(preserved, n2, Noon(today.AddDays(-45)), 0.3m);               // before it was preserved
        (await admin.PostAsJsonAsync($"/api/assets/{preserved}/preserved", new { preservedOn = DateOnly.FromDateTime(today.AddDays(-40)) })).EnsureSuccessStatusCode();
        decimal[] firstWeek = [1m, 1.5m, 2m, 2.25m, 3m, 3.5m];
        for (var n = 0; n < firstWeek.Length; n++) await Add(preserved, pressure, Noon(today.AddDays(-30 + n)), firstWeek[n]);
        await Add(preserved, pressure, Noon(today.AddDays(-15)), 0.8m);
        await Add(preserved, pressure, Noon(today.AddDays(-10)), 7m);
        await Add(preserved, dewPoint, Noon(today.AddDays(-10)), -35m);

        var response = await admin.GetAsync($"/api/units/{unitId}/monitoring/report");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var wb = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(["Until preservation", "Post-preservation", "Readings"], wb.Worksheets.Select(w => w.Name));

        static IXLRow Row(IXLWorksheet ws, string tag, string measurement) =>
            ws.RowsUsed().Single(r => r.Cell(1).GetString() == tag && r.Cell(5).GetString().Contains(measurement, StringComparison.Ordinal));
        static int Column(IXLWorksheet ws, string header) =>
            ws.Row(4).CellsUsed().Single(c => c.GetString() == header).Address.ColumnNumber;
        static string DayHeader(DateTime day) => day.ToString("ddd\ndd-MMM-yy", System.Globalization.CultureInfo.InvariantCulture);

        // Until preservation: no Workpack / Target / Reached columns, one column per day up to today.
        var until = wb.Worksheet("Until preservation");
        var headers = until.Row(4).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Equal(["Tag", "Equipment ID", "Description", "Type", "Measurement", "Preserved on"], headers.Take(6));
        Assert.Equal(DayHeader(today), headers[^1]);
        Assert.DoesNotContain(until.RowsUsed(), r => r.Cell(1).GetString() == "31-RPT-EI");
        var bN2 = Row(until, "31-RPT-1", "N2");
        Assert.Equal(0.2, bN2.Cell(Column(until, DayHeader(today.AddDays(-5)))).GetDouble());
        Assert.Empty(bN2.Cell(Column(until, DayHeader(today.AddDays(-4)))).GetString());
        var sameDay = bN2.Cell(Column(until, DayHeader(today.AddDays(-3))));
        Assert.Equal(1, sameDay.GetDouble());
        Assert.Equal("0", sameDay.Style.NumberFormat.Format);                  // 1, not "1."
        Assert.True(sameDay.HasComment);
        Assert.Equal(-40, Row(until, "31-RPT-1", "Dew Point").Cell(Column(until, DayHeader(today.AddDays(-4)))).GetDouble());
        Assert.Equal(0.3, Row(until, "31-RPT-2", "N2").Cell(Column(until, DayHeader(today.AddDays(-45)))).GetDouble());

        // Post-preservation: Week 1 #1..#5 from the first reading after preservation, then one reading per week.
        var post = wb.Worksheet("Post-preservation");
        Assert.DoesNotContain(post.RowsUsed(), r => r.Cell(1).GetString() == "31-RPT-1");
        var p = Row(post, "31-RPT-2", "Pressure");
        Assert.Equal(1, p.Cell(Column(post, "Week 1\n#1")).GetDouble());
        Assert.Equal(2.25, p.Cell(Column(post, "Week 1\n#4")).GetDouble());
        Assert.Equal("0.###", p.Cell(Column(post, "Week 1\n#4")).Style.NumberFormat.Format);
        var fifth = p.Cell(Column(post, "Week 1\n#5"));
        Assert.Equal(3, fifth.GetDouble());
        Assert.Contains("3.5", fifth.GetComment().Text);                        // the 6th reading of week 1 is in the note
        Assert.Equal(0.8, p.Cell(Column(post, "Week 2")).GetDouble());
        Assert.Equal(7, p.Cell(Column(post, "Week 3")).GetDouble());
        var dew = Row(post, "31-RPT-2", "Dew Point");                           // after the first 7 days of the asset: week 2
        Assert.Empty(dew.Cell(Column(post, "Week 1\n#1")).GetString());
        Assert.Equal(-35, dew.Cell(Column(post, "Week 2")).GetDouble());
        Assert.Equal("Overdue", p.Cell(8).GetString());

        // Readings: every reading of every type, without E&I.
        var readings = wb.Worksheet("Readings");
        Assert.Equal(4, readings.RowsUsed().Count(r => r.Cell(1).GetString() == "31-RPT-1"));
        Assert.Equal(10, readings.RowsUsed().Count(r => r.Cell(1).GetString() == "31-RPT-2"));
        Assert.DoesNotContain(readings.RowsUsed(), r => r.Cell(1).GetString() == "31-RPT-EI");
    }

    private async Task<JsonElement> PostFileAsync(string url, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var response = await admin.PostAsync(url, new MultipartFormDataContent { { content, "file", "readings.xlsx" } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(string? Tag, string? Field, string? Outcome)> Changes(JsonElement detail) =>
        detail.GetProperty("changes").EnumerateArray()
            .Select(x => (x.GetProperty("assetTag").GetString(), x.GetProperty("field").GetString(), x.GetProperty("outcome").GetString())).ToList();

    [Fact]
    public async Task A_round_with_one_bad_value_saves_nothing()
    {
        var (_, a) = await CreateAsync("31-MON-4");
        var (_, b) = await CreateAsync("31-MON-5");
        var o2 = await app.WithDbAsync(db => db.MeasurementTypes.Where(t => t.Key == "G3O2Pct").Select(t => t.Id).FirstAsync());
        var round = await admin.PostAsJsonAsync("/api/monitoring/rounds", new
        {
            measuredAtUtc = DateTime.UtcNow.AddMinutes(-5),
            rows = new[] { new { assetId = a, values = new[] { new { measurementTypeId = o2, value = 2m } } }, new { assetId = b, values = new[] { new { measurementTypeId = o2, value = 150m } } } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, round.StatusCode);
        Assert.Contains("31-MON-5", await round.Content.ReadAsStringAsync());
        Assert.False(await app.WithDbAsync(db => db.Readings.AnyAsync(r => r.AssetId == a)));
    }

    [Fact]
    public async Task Workpacks_get_a_code_group_assets_of_the_unit_and_show_progress()
    {
        var (unitId, a) = await CreateAsync("32-WP-1", "Unit 32");
        var (_, b) = await CreateAsync("32-WP-2", "Unit 32");
        var (_, other) = await CreateAsync("41-WP-9", "Unit 41");
        var types = await admin.GetFromJsonAsync<JsonElement>("/api/group-types");
        var wp = types.EnumerateArray().First(t => t.GetProperty("name").GetString() == "Workpack");
        Assert.Equal("WP", wp.GetProperty("prefix").GetString());

        var created = await admin.PostAsJsonAsync("/api/groups", new { typeId = wp.GetProperty("id").GetInt32(), name = "Air coolers", unitId });
        created.EnsureSuccessStatusCode();
        var group = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("group");
        Assert.Equal("WP-032-001", group.GetProperty("code").GetString());
        var id = group.GetProperty("id").GetInt32();

        (await admin.PatchAsJsonAsync($"/api/assets/{a}", new { changes = new[] { new { field = "G1Status", from = (string?)null, to = "Completed" } } })).EnsureSuccessStatusCode();
        var added = await (await admin.PostAsJsonAsync($"/api/groups/{id}/members", new { assetIds = new[] { a, b, a } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, added.GetProperty("added").GetInt32());
        var progress = added.GetProperty("detail").GetProperty("group").GetProperty("progress");
        Assert.Equal(2, progress.GetProperty("members").GetInt32());
        Assert.Equal(1, progress.GetProperty("gatesCompleted")[0].GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/groups/{id}/members", new { assetIds = new[] { other } })).StatusCode);

        var register = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Equipment");
        Assert.Contains("WP-032-001", register.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == a).GetProperty("groups").EnumerateArray().Select(x => x.GetString()));
        var monitoring = await admin.GetFromJsonAsync<JsonElement>($"/api/monitoring?groupId={id}");
        Assert.Equal(2, monitoring.GetProperty("rows").GetArrayLength());

        var removed = await (await admin.PostAsJsonAsync($"/api/groups/{id}/members/remove", new { assetIds = new[] { b } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, removed.GetProperty("removed").GetInt32());
        var second = await (await admin.PostAsJsonAsync("/api/groups", new { typeId = wp.GetProperty("id").GetInt32(), name = "Pumps", unitId })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("WP-032-002", second.GetProperty("group").GetProperty("code").GetString());
    }
}
