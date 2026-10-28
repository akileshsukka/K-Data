using PresApp.Domain.Catalog;
using PresApp.Domain.Common;

namespace PresApp.Domain.Reporting;

/// <summary>
/// A note under the Summary of the piping line list of a unit ("27 Oct: FRA preparation started by PA").
/// A note without a date is the general note written first ("Estimated date of Cleaning completion: 16 Nov 2026").
/// One active note per date and unit; notes are deactivated, never deleted, so their history stays.
/// </summary>
public class PipingNote : AuditableEntity, IFieldAudited
{
    public const int TextLength = 4000;

    public int Id { get; set; }
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;
    public DateOnly? NoteDate { get; set; }
    public string Text { get; set; } = "";
    public bool IsActive { get; set; } = true;

    Guid? IFieldAudited.AuditAssetId => null;
}
