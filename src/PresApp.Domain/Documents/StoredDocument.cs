using PresApp.Domain.Assets;
using PresApp.Domain.Common;

namespace PresApp.Domain.Documents;

/// <summary>
/// A file uploaded to K Data (a signed gate certificate, a photo of a signed sheet…). The file itself lives on the
/// server disk under the documents folder; this row only describes it. Never physically deleted: a removed document
/// is flagged and its file moved to the "_deleted" folder.
/// </summary>
public class StoredDocument : AuditableEntity
{
    public const int FileNameMaxLength = 255;
    public const int NoteMaxLength = 1000;

    /// <summary>Also the file name on disk, so two uploads with the same name never clash.</summary>
    public Guid Id { get; set; }

    /// <summary>The name the file had on the user's computer or phone.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Detected from the file content (PDF, JPEG, PNG, HEIC, WebP), never trusted from the browser.</summary>
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the content (hex): proves the file was not changed and spots duplicates.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Path relative to the documents folder, e.g. 2026/11/{id}.pdf.</summary>
    public string StoragePath { get; set; } = "";
    public string? Note { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public List<DocumentLink> Links { get; set; } = [];
}

/// <summary>
/// Where a document applies: one asset and one gate (1–8), or the asset in general (Gate = null).
/// One file can be linked to several gates and several assets without being stored twice.
/// Removing a link keeps the row (RemovedAtUtc) so the history stays complete.
/// </summary>
public class DocumentLink
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public StoredDocument Document { get; set; } = null!;
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public int? Gate { get; set; }

    public DateTime LinkedAtUtc { get; set; }
    public string LinkedBy { get; set; } = "";
    public DateTime? RemovedAtUtc { get; set; }
    public string? RemovedBy { get; set; }

    public bool IsActive => RemovedAtUtc is null;
}
