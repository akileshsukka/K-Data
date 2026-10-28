using PresApp.Domain.Common;
using PresApp.Domain.Reporting;

namespace PresApp.Domain.Assets;

/// <summary>
/// Preservation tracking of an equipment or pipeline (1:1 with <see cref="Asset"/>).
/// Field names, lengths and meanings match the SharePoint list and the client Excel.
/// </summary>
public class AssetPreservation : AuditableEntity, IFieldAudited
{
    public const int ShortText = 255;
    public const int LongText = 4000;
    public const string Completed = "Completed";

    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    public CurrentGate CurrentGate { get; set; } = CurrentGate.New;
    public string? AssetAvailability { get; set; }

    // Client tracking (Excel columns)
    public string? ThirdPartyInspectionStatus { get; set; }
    public string? PreservationStatusAdnoc { get; set; }
    public string? PreservedByEu { get; set; }
    public string? PreservationRemarks { get; set; }
    public string? RemarksAdnoc { get; set; }
    public string? EmComments { get; set; }
    public string? EmWalkdownStatus { get; set; }
    public string? ActionBy { get; set; }
    public string? Response { get; set; }
    public string? CurrentStatusAdnoc { get; set; }
    public string? OemName { get; set; }

    // Dashboard (template v4)
    /// <summary>Hold or N/A category chosen by the engineer; its section decides the dashboard table. Null = none.</summary>
    public int? DashboardCategoryId { get; set; }
    public DashboardCategory? DashboardCategory { get; set; }

    /// <summary>"Yes" lists the asset under "Assets available for preservation"; "No" or empty does not.</summary>
    public string? ReadyForPreservation { get; set; }

    // Field status
    public string? CurrentActivity { get; set; }
    public string? N2BlanketStatus { get; set; }
    public string? G1BulkLiquidDrained { get; set; }
    public string? G5PositiveMechIsolation { get; set; }
    public string? G6CleaningMethod { get; set; }

    // Gates
    public string? G1Status { get; set; }
    public string? G2Status { get; set; }
    public string? G3Status { get; set; }
    public string? G4Status { get; set; }
    public string? G5Status { get; set; }
    public string? G6Status { get; set; }
    public string? G7Status { get; set; }
    public string? G8Status { get; set; }
    public string? G1Comments { get; set; }
    public string? G2Comments { get; set; }
    public string? G3Comments { get; set; }
    public string? G4Comments { get; set; }
    public string? G5Comments { get; set; }
    public string? G6Comments { get; set; }
    public string? G7Comments { get; set; }
    public string? G8Comments { get; set; }

    // Latest readings (projection of the newest MeasuredAt per measurement)
    public decimal? PressureBarg { get; set; }
    public decimal? G2DewPointC { get; set; }
    public decimal? G3O2Pct { get; set; }
    public decimal? G4N2BlanketBarg { get; set; }
    public decimal? G6H2S { get; set; }
    public decimal? G6LelPct { get; set; }

    // Milestones
    public DateOnly? LastFieldActionDate { get; set; }
    public DateOnly? PreservationDate { get; set; }
    public DateOnly? N2BlanketedDate { get; set; }
    public DateTime? LastReadingAtUtc { get; set; }

    /// <summary>When an engineer marked the asset as preserved. Readings after it are POST monitoring.</summary>
    public DateTime? PreservedAtUtc { get; set; }
    public string? PreservedBy { get; set; }

    Guid? IFieldAudited.AuditAssetId => AssetId;

    public string? GetGateStatus(int gate) => gate switch
    {
        1 => G1Status, 2 => G2Status, 3 => G3Status, 4 => G4Status,
        5 => G5Status, 6 => G6Status, 7 => G7Status, 8 => G8Status,
        _ => throw new ArgumentOutOfRangeException(nameof(gate))
    };

    public void SetGateStatus(int gate, string? value)
    {
        switch (gate)
        {
            case 1: G1Status = value; break;
            case 2: G2Status = value; break;
            case 3: G3Status = value; break;
            case 4: G4Status = value; break;
            case 5: G5Status = value; break;
            case 6: G6Status = value; break;
            case 7: G7Status = value; break;
            case 8: G8Status = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(gate));
        }
    }

    public static bool IsCompleted(string? status) =>
        string.Equals(status?.Trim(), Completed, StringComparison.OrdinalIgnoreCase);

    /// <summary>Highest gate whose status is Completed, or null when none is.</summary>
    public int? HighestCompletedGate()
    {
        for (var g = 8; g >= 1; g--)
        {
            if (IsCompleted(GetGateStatus(g))) return g;
        }
        return null;
    }
}
