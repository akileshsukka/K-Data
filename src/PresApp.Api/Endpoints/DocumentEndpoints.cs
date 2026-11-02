using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PresApp.Api.Auth;
using PresApp.Api.Documents;
using PresApp.Api.Hosting;
using PresApp.Domain.Security;

namespace PresApp.Api.Endpoints;

/// <summary>Signed documents: files on the server disk, linked to assets and gates.</summary>
public static class DocumentEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").RequirePermission(Permissions.DocumentsView);

        view.MapGet("/assets/{id:guid}/documents", (Guid id, DocumentService s, CancellationToken ct) => s.ListForAssetAsync(id, ct));

        // PDF and photos open in the browser; ?download=1 saves the file instead.
        view.MapGet("/documents/{id:guid}/file", async (Guid id, string? download, HttpContext http, DocumentService s, CancellationToken ct) =>
        {
            var (path, contentType, fileName) = await s.OpenAsync(id, ct);
            var disposition = new ContentDispositionHeaderValue(download is "1" or "true" ? "attachment" : "inline");
            disposition.SetHttpFileName(fileName);
            http.Response.Headers.ContentDisposition = disposition.ToString();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.PhysicalFile(path, contentType, enableRangeProcessing: true);
        });

        // Multipart: "file", optional "note" and "links" = JSON [{ "assetId": "…", "gate": 1 }, …] (gate null = general).
        app.MapPost("/api/documents", async ([FromForm] IFormFile file, [FromForm] string links, [FromForm] string? note,
                ClaimsPrincipal user, DocumentService s, CancellationToken ct) =>
            {
                List<LinkTarget>? targets;
                try { targets = JsonSerializer.Deserialize<List<LinkTarget>>(links, Json); }
                catch (JsonException) { throw ApiProblem.Invalid("The list of gates is not valid."); }
                var doc = await s.UploadAsync(file, note, targets ?? [], user, ct);
                return Results.Created($"/api/documents/{doc.Id}", doc);
            })
            .RequirePermission(Permissions.DocumentsUpload)
            .DisableAntiforgery();

        app.MapPost("/api/documents/{id:guid}/links", (Guid id, AddLinksRequest request, ClaimsPrincipal user, DocumentService s, CancellationToken ct) =>
            s.LinkAsync(id, request, user, ct)).RequirePermission(Permissions.DocumentsUpload);

        app.MapDelete("/api/documents/{id:guid}/links/{linkId:long}", async (Guid id, long linkId, DocumentService s, CancellationToken ct) =>
        {
            await s.UnlinkAsync(id, linkId, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DocumentsUpload);

        app.MapDelete("/api/documents/{id:guid}", async (Guid id, DocumentService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DocumentsDelete);
    }
}
