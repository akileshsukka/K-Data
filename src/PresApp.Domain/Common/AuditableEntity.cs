namespace PresApp.Domain.Common;

/// <summary>Base for rows that track who created/changed them and use optimistic concurrency.</summary>
public abstract class AuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = "";

    /// <summary>SQL Server rowversion: a save fails if someone else changed the row first.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Marks entities whose field-level changes are written to <c>ChangeLog</c> automatically on save.
/// <see cref="AuditAssetId"/> links each change to its asset so the asset history shows it.
/// </summary>
public interface IFieldAudited
{
    Guid? AuditAssetId { get; }
}
