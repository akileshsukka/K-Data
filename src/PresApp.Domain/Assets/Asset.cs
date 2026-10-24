using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Domain.Groups;
using PresApp.Domain.Monitoring;

namespace PresApp.Domain.Assets;

/// <summary>
/// One physical item: an equipment, a pipeline or an E&amp;I item. Never deleted, only deactivated.
/// <see cref="Id"/> is stable forever and is the Simpro <c>flangeId</c> for equipment.
/// </summary>
public class Asset : AuditableEntity, IFieldAudited
{
    public const int CodeMaxLength = 40;

    public Guid Id { get; set; }
    public AssetType Type { get; set; }

    /// <summary>Equipment ID (e.g. ST-EMP-039-001) or line number. Unique.</summary>
    public string Code { get; set; } = "";

    /// <summary>Physical tag (client "Equip No"), Simpro custID.</summary>
    public string? Tag { get; set; }

    public string? Description { get; set; }
    public int DisciplineId { get; set; }
    public Discipline Discipline { get; set; } = null!;
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;
    public string? Priority { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Set once the asset exists in Simpro (equals <see cref="Id"/>).</summary>
    public Guid? SimproFlangeId { get; set; }

    /// <summary>SharePoint item ID, kept for traceability of the migration.</summary>
    public int? LegacySharePointId { get; set; }
    public string? MigrationNotes { get; set; }

    public AssetPreservation? Preservation { get; set; }
    public PipelineDetail? Pipeline { get; set; }
    public List<GateDocumentation> GateDocuments { get; set; } = [];
    public List<AssetFieldValue> FieldValues { get; set; } = [];
    public List<AssetGroupMember> GroupMemberships { get; set; } = [];
    public List<Reading> Readings { get; set; } = [];

    Guid? IFieldAudited.AuditAssetId => Id;
}
