using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>Signed documents: stored on disk, linked to gates and assets, visible to every role that reads the registers.</summary>
public sealed class DocumentTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    public DocumentTests(TestApp app) => this.app = app;

    private static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.7\n% {text}\n%%EOF\n");

    private async Task<string> CreateAssetAsync(string tag)
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == "Static").Select(d => d.Id).FirstAsync());
        var response = await app.ClientFor(TestApp.AdminEmail).PostAsJsonAsync("/api/assets", new { type = "Equipment", unitId, disciplineId, tag });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static MultipartFormDataContent Upload(byte[] content, string fileName, object links, string? note = null)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent { { file, "file", fileName }, { new StringContent(JsonSerializer.Serialize(links)), "links" } };
        if (note is not null) form.Add(new StringContent(note), "note");
        return form;
    }

    private async Task<HttpClient> ClientWithRoleAsync(string email, string role)
    {
        if (!await app.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email))) await app.AddUserAsync(email, role);
        return app.ClientFor(email);
    }

    [Fact]
    public async Task Uploading_to_a_gate_marks_it_received_and_a_viewer_can_open_it()
    {
        var engineer = await ClientWithRoleAsync("doc.engineer@enermech.com", RoleNames.Engineer);
        var viewer = await ClientWithRoleAsync("doc.viewer@enermech.com", RoleNames.Viewer);
        var id = await CreateAssetAsync("39-DOC-1");
        var content = Pdf("G1 signed");

        var response = await engineer.PostAsync("/api/documents", Upload(content, "G1 certificate.pdf", new[] { new { assetId = id, gate = 1 } }, "Signed by PF"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var docId = doc.GetProperty("id").GetString();
        Assert.Equal("G1 certificate.pdf", doc.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", doc.GetProperty("contentType").GetString());

        // The file is on disk under the documents folder, not in the database.
        var stored = await app.WithDbAsync(db => db.Documents.FirstAsync(d => d.Id == Guid.Parse(docId!)));
        Assert.True(File.Exists(Path.Combine(app.DocumentsRoot, stored.StoragePath)));

        var profile = await viewer.GetFromJsonAsync<JsonElement>($"/api/assets/{id}");
        var g1 = profile.GetProperty("gateDocuments").EnumerateArray().Single(d => d.GetProperty("gate").GetInt32() == 1);
        Assert.Equal("Received", g1.GetProperty("status").GetString());
        Assert.Equal("G1 certificate.pdf", g1.GetProperty("reference").GetString());

        var list = await viewer.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/documents");
        Assert.Equal(docId, list.EnumerateArray().Single().GetProperty("id").GetString());

        var file = await viewer.GetAsync($"/api/documents/{docId}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", file.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(content, await file.Content.ReadAsByteArrayAsync());
        var download = await viewer.GetAsync($"/api/documents/{docId}/file?download=1");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);

        // The viewer reads but cannot upload; the status can still be changed by hand.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/documents", Upload(Pdf("x"), "x.pdf", new[] { new { assetId = id, gate = 2 } }))).StatusCode);
        var manual = await engineer.PutAsJsonAsync($"/api/assets/{id}/gates/1/documentation", new { status = "Pending", receivedOn = (string?)null, reference = "G1 certificate.pdf", note = (string?)null });
        manual.EnsureSuccessStatusCode();
        Assert.Equal("Pending", (await manual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gateDocuments").EnumerateArray()
            .Single(d => d.GetProperty("gate").GetInt32() == 1).GetProperty("status").GetString());

        var history = await app.ClientFor(TestApp.AdminEmail).GetFromJsonAsync<JsonElement>($"/api/assets/{id}/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("label").GetString() == "G1 signed document" && h.GetProperty("newValue").GetString() == "G1 certificate.pdf");
    }

    [Fact]
    public async Task One_document_covers_several_gates_and_assets_and_the_same_file_is_stored_once()
    {
        var engineer = await ClientWithRoleAsync("doc.engineer2@enermech.com", RoleNames.Engineer);
        var a = await CreateAssetAsync("39-DOC-2A");
        var b = await CreateAssetAsync("39-DOC-2B");
        var content = Pdf("G2 and G3 for two pumps");

        var first = await (await engineer.PostAsync("/api/documents", Upload(content, "pumps.pdf",
            new object[] { new { assetId = a, gate = 2 }, new { assetId = a, gate = 3 }, new { assetId = b, gate = (int?)null } }))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, first.GetProperty("links").GetArrayLength());

        // Same bytes again for asset B gate 2: no second copy, the link is added to the same document.
        var again = await (await engineer.PostAsync("/api/documents", Upload(content, "copy.pdf", new[] { new { assetId = b, gate = 2 } })))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("id").GetString(), again.GetProperty("id").GetString());
        Assert.Equal(4, again.GetProperty("links").GetArrayLength());
        Assert.Equal(1, await app.WithDbAsync(db => db.Documents.CountAsync(d => d.Sha256 == db.Documents.First(x => x.Id == Guid.Parse(first.GetProperty("id").GetString()!)).Sha256)));

        var gatesOfB = await app.WithDbAsync(db => db.GateDocuments.Where(d => d.AssetId == Guid.Parse(b)).ToListAsync());
        Assert.Equal(DocumentationStatus.Received, Assert.Single(gatesOfB).Status);
        Assert.Single((await engineer.GetFromJsonAsync<JsonElement>($"/api/assets/{b}/documents")).EnumerateArray());
    }

    [Fact]
    public async Task Only_pdf_and_photos_are_accepted()
    {
        var engineer = await ClientWithRoleAsync("doc.engineer3@enermech.com", RoleNames.Engineer);
        var id = await CreateAssetAsync("39-DOC-3");
        var links = new[] { new { assetId = id, gate = 1 } };

        var text = await engineer.PostAsync("/api/documents", Upload(Encoding.UTF8.GetBytes("not a pdf"), "fake.pdf", links));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        var photo = await engineer.PostAsync("/api/documents", Upload(png, "IMG_0042.PNG", links));
        Assert.Equal(HttpStatusCode.Created, photo.StatusCode);
        Assert.Equal("image/png", (await photo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contentType").GetString());
        byte[] heic = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0, 0, 0];
        Assert.Equal(HttpStatusCode.Created, (await engineer.PostAsync("/api/documents", Upload(heic, "IMG_0043.HEIC", links))).StatusCode);

        // Nothing is left behind for the rejected file.
        var incoming = Path.Combine(app.DocumentsRoot, "_incoming");
        Assert.True(!Directory.Exists(incoming) || Directory.GetFiles(incoming).Length == 0);
    }

    [Fact]
    public async Task Removing_the_last_link_moves_the_file_to_deleted_and_only_admin_deletes_outright()
    {
        var technician = await ClientWithRoleAsync("doc.tech@enermech.com", RoleNames.FieldTechnician);
        var admin = app.ClientFor(TestApp.AdminEmail);
        var id = await CreateAssetAsync("39-DOC-4");

        var doc = await (await technician.PostAsync("/api/documents", Upload(Pdf("to unlink"), "g4.pdf", new[] { new { assetId = id, gate = 4 } })))
            .Content.ReadFromJsonAsync<JsonElement>();
        var docId = doc.GetProperty("id").GetString()!;
        var linkId = doc.GetProperty("links")[0].GetProperty("linkId").GetInt64();

        Assert.Equal(HttpStatusCode.Forbidden, (await technician.DeleteAsync($"/api/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await technician.DeleteAsync($"/api/documents/{docId}/links/{linkId}")).StatusCode);

        var stored = await app.WithDbAsync(db => db.Documents.FirstAsync(d => d.Id == Guid.Parse(docId)));
        Assert.True(stored.IsDeleted);
        Assert.StartsWith("_deleted/", stored.StoragePath);
        Assert.True(File.Exists(Path.Combine(app.DocumentsRoot, stored.StoragePath)));
        Assert.Equal(HttpStatusCode.NotFound, (await technician.GetAsync($"/api/documents/{docId}/file")).StatusCode);
        Assert.Empty((await technician.GetFromJsonAsync<JsonElement>($"/api/assets/{id}/documents")).EnumerateArray());

        var other = await (await technician.PostAsync("/api/documents", Upload(Pdf("to delete"), "g5.pdf", new[] { new { assetId = id, gate = 5 } })))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/documents/{other.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task Viewer_role_gets_document_view_but_not_upload()
    {
        var me = await (await ClientWithRoleAsync("doc.viewer2@enermech.com", RoleNames.Viewer)).GetFromJsonAsync<JsonElement>("/api/me");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(Permissions.DocumentsView, permissions);
        Assert.DoesNotContain(Permissions.DocumentsUpload, permissions);
        Assert.DoesNotContain(Permissions.DocumentsDelete, permissions);
    }
}
