using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;

namespace PresApp.Domain.Groups;

/// <summary>Kind of internal grouping: Workpack, Circuit, System, Test Pack…</summary>
public class GroupType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Internal group of assets of any type (equipment, lines, E&amp;I).
/// Never exported to the client Excel nor sent to Simpro.
/// </summary>
public class AssetGroup : AuditableEntity
{
    public int Id { get; set; }
    public int GroupTypeId { get; set; }
    public GroupType GroupType { get; set; } = null!;
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public bool IsActive { get; set; } = true;
    public List<AssetGroupMember> Members { get; set; } = [];
}

public class AssetGroupMember
{
    public int GroupId { get; set; }
    public AssetGroup Group { get; set; } = null!;
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public DateTime AddedAtUtc { get; set; }
    public string AddedBy { get; set; } = "";
}
