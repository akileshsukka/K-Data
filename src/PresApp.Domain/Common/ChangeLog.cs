namespace PresApp.Domain.Common;

/// <summary>Append-only record of one field change: who, when, old and new value, source.</summary>
public class ChangeLog
{
    public long Id { get; set; }
    public string EntityType { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public Guid? AssetId { get; set; }
    public string Field { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string ChangedBy { get; set; } = "";
    public ChangeSource Source { get; set; }

    /// <summary>Groups the changes made by one save, bulk edit or import.</summary>
    public Guid? BatchId { get; set; }
}
