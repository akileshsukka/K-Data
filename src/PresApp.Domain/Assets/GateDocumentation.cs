using PresApp.Domain.Common;

namespace PresApp.Domain.Assets;

public enum DocumentationStatus
{
    Pending = 0,
    Received = 1,
    NotApplicable = 2
}

/// <summary>
/// Formal paperwork for one gate of one asset. The gate status says the work is Completed;
/// this row says whether its documentation arrived. Replaces "Completed - Documentation Pending".
/// </summary>
public class GateDocumentation : AuditableEntity, IFieldAudited
{
    public long Id { get; set; }
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public int Gate { get; set; }
    public DocumentationStatus Status { get; set; } = DocumentationStatus.Pending;
    public DateOnly? ReceivedOn { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }

    Guid? IFieldAudited.AuditAssetId => AssetId;
}
