using PresApp.Domain.Catalog;
using PresApp.Domain.Common;

namespace PresApp.Domain.Reporting;

/// <summary>
/// One highlight of a unit for one site day, written by the engineers for the daily report ("P-107A/B blanketing
/// completed, system held at 0.5 barg"). Only important points; as many as needed. Removed highlights are deactivated,
/// never deleted, so their history stays.
/// </summary>
public class UnitHighlight : AuditableEntity, IFieldAudited
{
    public const int TextLength = 1000;

    public int Id { get; set; }
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;
    public DateOnly Date { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Order inside the unit and day.</summary>
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    Guid? IFieldAudited.AuditAssetId => null;
}
