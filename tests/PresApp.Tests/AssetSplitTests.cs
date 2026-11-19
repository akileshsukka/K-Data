using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class AssetSplitTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public AssetSplitTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    private async Task<Guid> CreateAsync(string tag, string unit = "Unit 32")
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == unit).Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var response = await admin.PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag, description = "Pumps " + tag });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task PatchAsync(Guid id, string field, string? to)
    {
        var profile = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
        var from = profile.GetProperty("values").GetProperty(field).GetString();
        (await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field, from, to } } })).EnsureSuccessStatusCode();
    }

    private async Task<int> MeasurementAsync(string key) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/measurements")).EnumerateArray()
            .First(t => t.GetProperty("key").GetString() == key).GetProperty("id").GetInt32();

    private async Task AddReadingAsync(Guid id, int type, decimal value, DateTime at, string? note) =>
        (await admin.PostAsJsonAsync($"/api/assets/{id}/readings", new { measuredAtUtc = at, note, values = new[] { new { measurementTypeId = type, value } } })).EnsureSuccessStatusCode();

    private static object[] Gates(JsonElement preview, Func<int, string?, string?>? status = null) =>
        preview.GetProperty("gates").EnumerateArray().Select(g =>
        {
            var gate = g.GetProperty("gate").GetInt32();
            var s = g.GetProperty("status").GetString();
            return (object)new { gate, status = status is null ? s : status(gate, s), comments = g.GetProperty("comments").GetString() };
        }).ToArray();

    [Theory]
    [InlineData("P-101A/B", "P-101A|P-101B")]
    [InlineData("P-101A / B / C", "P-101A|P-101B|P-101C")]
    [InlineData("P-101 A&B", "P-101 A|P-101 B")]
    [InlineData("541-E-116 A1/A2", "541-E-116 A1|541-E-116 A2")]
    [InlineData("541-E-119 (S/N 043&044)", "541-E-119 (S/N 043)|541-E-119 (S/N 044)")]
    [InlineData("39-E-104 (S/N 27518/27517)", "39-E-104 (S/N 27518)|39-E-104 (S/N 27517)")]
    [InlineData("541-E-119 (S/N: 09107-11B-047 & 048)", "541-E-119 (S/N: 09107-11B-047)|541-E-119 (S/N: 09107-11B-048)")]
    [InlineData("541-E-120 (S/N 049)", "")]
    [InlineData("P-101A/B (spare)", "P-101A (spare)|P-101B (spare)")]
    [InlineData("P-101A/P-101B", "P-101A|P-101B")]
    [InlineData("32-V-100A,B", "32-V-100A|32-V-100B")]
    [InlineData("P-101", "")]
    public void Grouped_tags_suggest_the_tags_of_their_parts(string tag, string expected) =>
        Assert.Equal(expected, string.Join("|", AssetSplit.SuggestTags(tag)));

    [Fact]
    public async Task An_asset_is_split_into_two_with_its_own_gates_and_readings()
    {
        var id = await CreateAsync("32-P-101A/B");
        await PatchAsync(id, "G1Status", "Completed");
        await PatchAsync(id, "G2Status", "Completed");
        await PatchAsync(id, "G2Comments", "Dried");
        var n2 = await MeasurementAsync("G4N2BlanketBarg");
        var now = DateTime.UtcNow;
        await AddReadingAsync(id, n2, 1.1m, now.AddDays(-3), "both");
        await AddReadingAsync(id, n2, 2.2m, now.AddDays(-2), "A");
        await AddReadingAsync(id, n2, 3.3m, now.AddDays(-1), "B only");
        var groupId = await app.WithDbAsync(async db =>
        {
            var type = await db.GroupTypes.FirstAsync();
            var g = new PresApp.Domain.Groups.AssetGroup { Code = "WP-SPLIT", Name = "Split workpack", GroupTypeId = type.Id, UnitId = (await db.Assets.FirstAsync(a => a.Id == id)).UnitId };
            db.Groups.Add(g);
            await db.SaveChangesAsync();
            db.GroupMembers.Add(new PresApp.Domain.Groups.AssetGroupMember { GroupId = g.Id, AssetId = id, AddedAtUtc = DateTime.UtcNow, AddedBy = "test" });
            await db.SaveChangesAsync();
            return g.Id;
        });

        var preview = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("blocked").ValueKind);
        Assert.Equal(["32-P-101A", "32-P-101B"], preview.GetProperty("suggestedTags").EnumerateArray().Select(t => t.GetString()));
        var readings = preview.GetProperty("readings").EnumerateArray().ToList();
        Assert.Equal(3, readings.Count);
        long Reading(decimal v) => readings.First(r => r.GetProperty("value").GetDecimal() == v).GetProperty("id").GetInt64();
        var code = await app.WithDbAsync(db => db.Assets.Where(a => a.Id == id).Select(a => a.Code).FirstAsync());

        // Only A finished G2: B goes back to Ongoing. The oldest reading was for both, the newest only for B.
        var response = await admin.PostAsJsonAsync($"/api/assets/{id}/split", new
        {
            stamp = preview.GetProperty("stamp").GetString(),
            parts = new object[]
            {
                new { tag = "32-P-101A", gates = Gates(preview) },
                new { tag = "32-P-101B", gates = Gates(preview, (g, s) => g == 2 ? "Ongoing" : s) },
            },
            readings = new object[] { new { id = Reading(1.1m), part = (int?)null }, new { id = Reading(2.2m), part = (int?)0 }, new { id = Reading(3.3m), part = (int?)1 } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("32-P-101A/B", result.GetProperty("clientTag").GetString());
        Assert.Equal(1, result.GetProperty("readingsMoved").GetInt32());
        Assert.Equal(1, result.GetProperty("readingsCopied").GetInt32());
        var parts = result.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(id, parts[0].GetProperty("id").GetGuid());
        Assert.Equal(code, parts[0].GetProperty("code").GetString());
        var b = parts[1].GetProperty("id").GetGuid();
        Assert.Matches(@"^ST-EMP-032-\d{3}$", parts[1].GetProperty("code").GetString());
        Assert.NotEqual(code, parts[1].GetProperty("code").GetString());

        // A keeps its Id, Equipment ID and its gates; its tag changes and it knows the client tag.
        var pa = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
        Assert.Equal("32-P-101A", pa.GetProperty("values").GetProperty("Tag").GetString());
        Assert.Equal("32-P-101A/B", pa.GetProperty("clientTag").GetString());
        Assert.Equal("Completed", pa.GetProperty("values").GetProperty("G2Status").GetString());
        // A's gates did not change in the split: its Current Gate stays as the engineer last approved it.
        Assert.Equal("New", pa.GetProperty("currentGate").GetString());

        // B is a copy with its own G2, a Current Gate that follows it, and the same group and description.
        var pb = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{b}");
        Assert.Equal("32-P-101B", pb.GetProperty("values").GetProperty("Tag").GetString());
        Assert.Equal("32-P-101A/B", pb.GetProperty("clientTag").GetString());
        Assert.Equal("Pumps 32-P-101A/B", pb.GetProperty("values").GetProperty("Description").GetString());
        Assert.Equal("Completed", pb.GetProperty("values").GetProperty("G1Status").GetString());
        Assert.Equal("Ongoing", pb.GetProperty("values").GetProperty("G2Status").GetString());
        Assert.Equal("Dried", pb.GetProperty("values").GetProperty("G2Comments").GetString());
        Assert.Equal("G1", pb.GetProperty("currentGate").GetString());
        Assert.Contains(pb.GetProperty("groups").EnumerateArray(), g => g.GetProperty("id").GetInt32() == groupId);
        Assert.Contains(pb.GetProperty("gateDocuments").EnumerateArray(), d => d.GetProperty("gate").GetInt32() == 1);
        Assert.Null(await app.WithDbAsync(db => db.Assets.Where(a => a.Id == b).Select(a => a.SimproFlangeId).FirstAsync()));

        // Readings: A has the shared one and its own; B has a marked copy of the shared one and its own. Latest values follow.
        var ra = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/readings");
        Assert.Equal([2.2m, 1.1m], ra.EnumerateArray().Select(r => r.GetProperty("value").GetDecimal()));
        var rb = (await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{b}/readings")).EnumerateArray().ToList();
        Assert.Equal([3.3m, 1.1m], rb.Select(r => r.GetProperty("value").GetDecimal()));
        Assert.StartsWith("Copied from 32-P-101A/B when it was split.", rb[1].GetProperty("note").GetString());
        Assert.Equal(3.3m, await app.WithDbAsync(db => db.Preservations.Where(p => p.AssetId == b).Select(p => p.G4N2BlanketBarg).FirstAsync()));
        Assert.Equal(2.2m, await app.WithDbAsync(db => db.Preservations.Where(p => p.AssetId == id).Select(p => p.G4N2BlanketBarg).FirstAsync()));

        // Both parts have the split in their history.
        Assert.Equal(2, await app.WithDbAsync(db => db.ChangeLogs.CountAsync(c => (c.AssetId == id || c.AssetId == b) && c.Field == AssetSplit.SplitField)));

        // Splitting again uses the client tag, never the part's tag: A can be split once more into A1 / A2.
        var again = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        Assert.Equal("32-P-101A/B", again.GetProperty("clientTag").GetString());
    }

    [Fact]
    public async Task A_split_is_refused_when_the_asset_changed_or_the_tags_are_wrong()
    {
        var id = await CreateAsync("32-P-202A/B");
        await CreateAsync("32-P-TAKEN");
        var preview = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        var stamp = preview.GetProperty("stamp").GetString();
        object Body(string a, string b, string? s = null) => new
        {
            stamp = s ?? stamp,
            parts = new object[] { new { tag = a, gates = Gates(preview) }, new { tag = b, gates = Gates(preview) } },
            readings = Array.Empty<object>(),
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/assets/{id}/split", Body("32-P-202A", "32-P-202A"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/assets/{id}/split", Body("32-P-202A/B", "32-P-202B"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/assets/{id}/split", Body("32-P-202A", "32-P-TAKEN"))).StatusCode);
        var badStatus = await admin.PostAsJsonAsync($"/api/assets/{id}/split", new
        {
            stamp, parts = new object[] { new { tag = "32-P-202A", gates = Gates(preview) }, new { tag = "32-P-202B", gates = Gates(preview, (g, s) => g == 1 ? "Finished" : s) } },
            readings = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

        // Someone changes the asset after the preview: the split is refused.
        await PatchAsync(id, "G1Status", "Completed");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/assets/{id}/split", Body("32-P-202A", "32-P-202B"))).StatusCode);

        // A reading added after the preview: also refused (it would not be assigned).
        var fresh = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        await AddReadingAsync(id, await MeasurementAsync("G4N2BlanketBarg"), 1m, DateTime.UtcNow.AddHours(-1), null);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/assets/{id}/split", Body("32-P-202A", "32-P-202B", fresh.GetProperty("stamp").GetString()))).StatusCode);

        // Nothing was split.
        Assert.Equal("32-P-202A/B", await app.WithDbAsync(db => db.Assets.Where(a => a.Id == id).Select(a => a.Tag).FirstAsync()));
        Assert.False(await app.WithDbAsync(db => db.Assets.AnyAsync(a => a.Tag == "32-P-202B")));
    }

    [Fact]
    public async Task Lines_and_inactive_assets_are_not_split()
    {
        var id = await CreateAsync("32-P-303A/B");
        (await admin.PostAsync($"/api/assets/{id}/active?active=false", null)).EnsureSuccessStatusCode();
        var preview = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        Assert.Contains("inactive", preview.GetProperty("blocked").GetString());
        var refused = await admin.PostAsJsonAsync($"/api/assets/{id}/split", new
        {
            stamp = preview.GetProperty("stamp").GetString(),
            parts = new object[] { new { tag = "32-P-303A", gates = Gates(preview) }, new { tag = "32-P-303B", gates = Gates(preview) } },
            readings = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Onboarding_never_creates_a_split_client_tag_again()
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 41").Select(u => u.Id).FirstAsync());
        (await admin.PutAsync($"/api/excel-templates/{unitId}", File(TestWorkbooks.Template(), "Template.xlsx"))).EnsureSuccessStatusCode();
        var id = await CreateAsync("41-P-404A/B", "Unit 41");
        var preview = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        (await admin.PostAsJsonAsync($"/api/assets/{id}/split", new
        {
            stamp = preview.GetProperty("stamp").GetString(),
            parts = new object[] { new { tag = "41-P-404A", gates = Gates(preview) }, new { tag = "41-P-404B", gates = Gates(preview) } },
            readings = Array.Empty<object>(),
        })).EnsureSuccessStatusCode();

        var list = TestWorkbooks.Edit(TestWorkbooks.Template(), wb =>
        {
            var row = TestWorkbooks.Master(wb).DataRange.Rows().First();
            row.Field("EQUIP NO").SetValue("41-P-404A/B"); row.Field("Discipline").SetValue("Static"); row.Field("G1-Status").SetValue("Completed");
        });
        var response = await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File(list, "client list.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changes = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changes").EnumerateArray().ToList();
        var row = Assert.Single(changes, c => c.GetProperty("assetTag").GetString() == "41-P-404A/B");
        Assert.Equal("Rejected", row.GetProperty("outcome").GetString());
        Assert.Contains("was split into 41-P-404A", row.GetProperty("reason").GetString());
        Assert.DoesNotContain(changes, c => c.GetProperty("outcome").GetString() == "Created");
    }

    private async Task<(Guid A, Guid B)> CreateSplitAsync(string tag, string tagA, string tagB)
    {
        var id = await CreateAsync(tag);
        var preview = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/split");
        var response = await admin.PostAsJsonAsync($"/api/assets/{id}/split", new
        {
            stamp = preview.GetProperty("stamp").GetString(),
            parts = new object[] { new { tag = tagA, gates = Gates(preview) }, new { tag = tagB, gates = Gates(preview) } },
            readings = Array.Empty<object>(),
        });
        response.EnsureSuccessStatusCode();
        return (id, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("parts")[1].GetProperty("id").GetGuid());
    }

    private static List<(string? Tag, string? Field, string? Outcome, string? Reason)> Changes(JsonElement preview) =>
        preview.GetProperty("changes").EnumerateArray()
            .Select(c => (c.GetProperty("assetTag").GetString(), c.GetProperty("field").GetString(), c.GetProperty("outcome").GetString(), c.GetProperty("reason").GetString()))
            .ToList();

    [Fact]
    public async Task A_readings_file_with_the_client_tag_says_which_parts_to_use()
    {
        await CreateSplitAsync("32-P-701A/B", "32-P-701A", "32-P-701B");
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 32").Select(u => u.Id).FirstAsync());
        var template = await (await admin.GetAsync($"/api/units/{unitId}/monitoring/readings-template")).Content.ReadAsByteArrayAsync();
        var yesterday = DateTime.UtcNow.AddHours(4).Date.AddDays(-1);
        var file = TestWorkbooks.Edit(template, wb =>
        {
            var ws = wb.Worksheet(1);
            ws.Cell(5, 1).Value = "32-P-701A/B"; ws.Cell(5, 2).Value = yesterday; ws.Cell(5, 3).Value = 0.5;
            ws.Cell(6, 1).Value = "32-P-701B"; ws.Cell(6, 2).Value = yesterday; ws.Cell(6, 3).Value = 0.6;
        });
        var response = await admin.PostAsync($"/api/units/{unitId}/monitoring/readings-import", File(file, "readings.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changes = Changes(await response.Content.ReadFromJsonAsync<JsonElement>());

        var grouped = Assert.Single(changes, c => c.Tag == "32-P-701A/B");
        Assert.Equal("Rejected", grouped.Outcome);
        Assert.Contains("was split into 32-P-701A", grouped.Reason);
        Assert.Contains("(32-P-701A or 32-P-701B)", grouped.Reason);
        Assert.Contains(changes, c => c is { Tag: "32-P-701B", Outcome: "Applied" });
    }

    [Fact]
    public async Task An_E7_sheet_with_the_client_tag_says_which_parts_to_use()
    {
        var (a, _) = await CreateSplitAsync("32-PM-702A/B", "32-PM-702A", "32-PM-702B");
        (await admin.PutAsJsonAsync($"/api/assets/{a}/electrical-test", new { required = true, testedOn = (string?)null, values = new Dictionary<string, decimal?>(), expected = (DateTime?)null })).EnsureSuccessStatusCode();
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 32").Select(u => u.Id).FirstAsync());
        var sheet = await (await admin.GetAsync($"/api/units/{unitId}/electrical-tests/export")).Content.ReadAsByteArrayAsync();
        var edited = TestWorkbooks.Edit(sheet, wb =>
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("32-PM-702A", ws.Cell("A4").GetString());
            ws.Cell("A4").Value = "32-PM-702A/B";
            ws.Cell("C4").Value = 0.3;
        });
        var response = await admin.PostAsync($"/api/units/{unitId}/electrical-tests/import", File(edited, "e7.xlsx"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(Changes(await response.Content.ReadFromJsonAsync<JsonElement>()), c => c.Tag == "32-PM-702A/B");
        Assert.Equal("Rejected", row.Outcome);
        Assert.Contains("was split into 32-PM-702A", row.Reason);
    }

    [Fact]
    public async Task Searching_the_client_tag_finds_every_part()
    {
        var (a, b) = await CreateSplitAsync("32-P-703A/B", "32-P-703A", "32-P-703B");
        var hits = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/search?q={Uri.EscapeDataString("32-P-703A/B")}");
        Assert.Equal(new[] { a, b }.Order(), hits.EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).Order());
    }

    private static MultipartFormDataContent File(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }
}
