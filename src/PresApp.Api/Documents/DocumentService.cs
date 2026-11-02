using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Auth;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Documents;
using PresApp.Domain.Rules;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Documents;

public sealed record DocumentLinkDto(long LinkId, Guid AssetId, string AssetCode, string? AssetTag, int? Gate);
public sealed record DocumentDto(
    Guid Id, string FileName, string ContentType, long SizeBytes, string? Note, string UploadedBy, DateTime UploadedAtUtc,
    IReadOnlyList<DocumentLinkDto> Links);
public sealed record LinkTarget(Guid AssetId, int? Gate);
public sealed record AddLinksRequest(IReadOnlyList<LinkTarget> Links);

/// <summary>
/// Upload, link, list and remove signed documents. Uploading a file to a gate marks that gate's documentation as
/// Received (date = today in Dubai); the user can still change the status by hand afterwards. Every link added or
/// removed is written to the asset history.
/// </summary>
public sealed class DocumentService(PresAppDbContext db, DocumentStorage storage, AuditContext audit, TimeProvider clock)
{
    public const string HistoryEntity = "DocumentLink";

    public async Task<List<DocumentDto>> ListForAssetAsync(Guid assetId, CancellationToken ct)
    {
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) throw ApiProblem.NotFound("The asset");
        var docs = await db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted && d.Links.Any(l => l.AssetId == assetId && l.RemovedAtUtc == null))
            .Include(d => d.Links.Where(l => l.RemovedAtUtc == null)).ThenInclude(l => l.Asset)
            .AsSplitQuery()
            .ToListAsync(ct);
        return docs.OrderByDescending(d => d.CreatedAtUtc).Select(ToDto).ToList();
    }

    /// <summary>
    /// Stores the file and links it. If exactly the same file (same SHA-256) is already in K Data, it is not stored
    /// twice: the links are added to the existing document.
    /// </summary>
    public async Task<DocumentDto> UploadAsync(IFormFile file, string? note, IReadOnlyList<LinkTarget> links, ClaimsPrincipal user, CancellationToken ct)
    {
        var targets = await ValidateTargetsAsync(links, user, ct);
        note = ValueNormalizer.CleanText(note);
        if (note?.Length > StoredDocument.NoteMaxLength) throw ApiProblem.Invalid("The note allows 1,000 characters.");
        var fileName = CleanFileName(file.FileName);

        var now = clock.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        SavedFile saved;
        await using (var stream = file.OpenReadStream())
            saved = await storage.SaveAsync(stream, id, now, ct);

        var existing = await db.Documents.Include(d => d.Links)
            .FirstOrDefaultAsync(d => !d.IsDeleted && d.Sha256 == saved.Sha256, ct);
        StoredDocument doc;
        if (existing is not null)
        {
            storage.Discard(saved.RelativePath);
            doc = existing;
        }
        else
        {
            doc = new StoredDocument
            {
                Id = id, FileName = Path.GetFileNameWithoutExtension(fileName) + saved.Extension, ContentType = saved.ContentType,
                SizeBytes = saved.SizeBytes, Sha256 = saved.Sha256, StoragePath = saved.RelativePath, Note = note
            };
            db.Documents.Add(doc);
        }

        try
        {
            AddLinks(doc, targets, now);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (existing is null) storage.Discard(saved.RelativePath);
            throw;
        }
        return await GetAsync(doc.Id, ct);
    }

    public async Task<DocumentDto> LinkAsync(Guid id, AddLinksRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var targets = await ValidateTargetsAsync(request.Links, user, ct);
        var doc = await db.Documents.Include(d => d.Links).FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, ct)
                  ?? throw ApiProblem.NotFound("The document");
        AddLinks(doc, targets, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Takes the document off one asset/gate. When nothing links to it any more, the document is removed too.</summary>
    public async Task UnlinkAsync(Guid id, long linkId, CancellationToken ct)
    {
        var doc = await db.Documents.Include(d => d.Links).FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, ct)
                  ?? throw ApiProblem.NotFound("The document");
        var link = doc.Links.FirstOrDefault(l => l.Id == linkId && l.IsActive) ?? throw ApiProblem.NotFound("The link");
        var now = clock.GetUtcNow().UtcDateTime;
        RemoveLink(doc, link, now);
        if (doc.Links.All(l => !l.IsActive)) MarkDeleted(doc, now);
        await SaveAndMoveAsync(doc, ct);
    }

    /// <summary>Removes the document everywhere. The file is kept in the _deleted folder.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.Include(d => d.Links).FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, ct)
                  ?? throw ApiProblem.NotFound("The document");
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var link in doc.Links.Where(l => l.IsActive).ToList()) RemoveLink(doc, link, now);
        MarkDeleted(doc, now);
        await SaveAndMoveAsync(doc, ct);
    }

    /// <summary>The file to send to the browser.</summary>
    public async Task<(string Path, string ContentType, string FileName)> OpenAsync(Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, ct)
                  ?? throw ApiProblem.NotFound("The document");
        var path = storage.FullPath(doc.StoragePath);
        if (!File.Exists(path)) throw ApiProblem.NotFound("The file of this document");
        return (path, doc.ContentType, doc.FileName);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<DocumentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking()
            .Include(d => d.Links.Where(l => l.RemovedAtUtc == null)).ThenInclude(l => l.Asset)
            .AsSplitQuery()
            .FirstAsync(d => d.Id == id, ct);
        return ToDto(doc);
    }

    private static DocumentDto ToDto(StoredDocument d) => new(
        d.Id, d.FileName, d.ContentType, d.SizeBytes, d.Note, d.CreatedBy, d.CreatedAtUtc,
        d.Links.Where(l => l.IsActive)
            .OrderBy(l => l.Asset.Tag ?? l.Asset.Code, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Gate ?? 0)
            .Select(l => new DocumentLinkDto(l.Id, l.AssetId, l.Asset.Code, l.Asset.Tag, l.Gate)).ToList());

    private async Task<List<(Asset Asset, int? Gate)>> ValidateTargetsAsync(IReadOnlyList<LinkTarget>? links, ClaimsPrincipal user, CancellationToken ct)
    {
        if (links is not { Count: > 0 }) throw ApiProblem.Invalid("Choose at least one gate or asset for the document.");
        if (links.Count > 200) throw ApiProblem.Invalid("A document can be linked to at most 200 gates at once.");
        if (links.Any(l => l.Gate is < 1 or > 8)) throw ApiProblem.Invalid("Gate must be 1 to 8.");
        var ids = links.Select(l => l.AssetId).Distinct().ToList();
        var assets = await db.Assets.Include(a => a.GateDocuments).Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (assets.Count != ids.Count) throw ApiProblem.NotFound("One of the assets");
        if (assets.Any(a => !a.IsActive) && !user.HasPermission(Permissions.AssetsManage))
            throw ApiProblem.Forbidden("One of the assets is inactive. Only an administrator can add documents to it.");
        return links.Select(l => (l.AssetId, l.Gate)).Distinct()
            .Select(l => (assets.First(a => a.Id == l.AssetId), l.Gate)).ToList();
    }

    private void AddLinks(StoredDocument doc, List<(Asset Asset, int? Gate)> targets, DateTime now)
    {
        var today = SiteClock.Today(clock);
        foreach (var (asset, gate) in targets)
        {
            if (doc.Links.Any(l => l.IsActive && l.AssetId == asset.Id && l.Gate == gate)) continue;
            doc.Links.Add(new DocumentLink { AssetId = asset.Id, Gate = gate, LinkedAtUtc = now, LinkedBy = audit.UserName });
            db.ChangeLogs.Add(History(doc, asset.Id, gate, null, doc.FileName, now));

            if (gate is not { } g) continue;
            // A signed document for the gate = its documentation is Received. The user can still change it by hand.
            var gateDoc = asset.GateDocuments.FirstOrDefault(d => d.Gate == g);
            if (gateDoc is null)
            {
                gateDoc = new GateDocumentation { AssetId = asset.Id, Gate = g };
                asset.GateDocuments.Add(gateDoc);
            }
            if (gateDoc.Status != DocumentationStatus.Received)
            {
                gateDoc.Status = DocumentationStatus.Received;
                gateDoc.ReceivedOn = today;
            }
            gateDoc.Reference ??= doc.FileName.Length > 255 ? doc.FileName[..255] : doc.FileName;
        }
    }

    private void RemoveLink(StoredDocument doc, DocumentLink link, DateTime now)
    {
        link.RemovedAtUtc = now;
        link.RemovedBy = audit.UserName;
        db.ChangeLogs.Add(History(doc, link.AssetId, link.Gate, doc.FileName, null, now));
    }

    private void MarkDeleted(StoredDocument doc, DateTime now)
    {
        doc.IsDeleted = true;
        doc.DeletedAtUtc = now;
        doc.DeletedBy = audit.UserName;
    }

    /// <summary>Saves first, then moves the file: if the save fails, the file stays where it was.</summary>
    private async Task SaveAndMoveAsync(StoredDocument doc, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        if (!doc.IsDeleted) return;
        var moved = storage.MoveToDeleted(doc.StoragePath);
        if (moved == doc.StoragePath) return;
        doc.StoragePath = moved;
        await db.SaveChangesAsync(ct);
    }

    private ChangeLog History(StoredDocument doc, Guid assetId, int? gate, string? oldValue, string? newValue, DateTime now) => new()
    {
        EntityType = HistoryEntity,
        EntityKey = doc.Id.ToString(),
        AssetId = assetId,
        Field = gate is { } g ? $"G{g} signed document" : "Signed document",
        OldValue = oldValue,
        NewValue = newValue,
        ChangedAtUtc = now,
        ChangedBy = audit.UserName,
        Source = audit.Source,
        BatchId = audit.BatchId
    };

    /// <summary>Only the name part, without folders or characters Windows does not accept.</summary>
    private static string CleanFileName(string? name)
    {
        var clean = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last()).Trim();
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*'])) clean = clean.Replace(c, '_');
        if (clean.Length == 0) clean = "document";
        return clean.Length > 200 ? clean[^200..] : clean;
    }
}
