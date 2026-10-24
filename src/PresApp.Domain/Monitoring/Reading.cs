using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;

namespace PresApp.Domain.Monitoring;

public enum MonitoringPhase
{
    /// <summary>Before the asset was marked as preserved.</summary>
    During = 0,

    /// <summary>On or after the date an engineer marked the asset as preserved.</summary>
    Post = 1
}

/// <summary>One field measurement. Append-only: never edited, repeats allowed.</summary>
public class Reading
{
    public long Id { get; set; }
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public int MeasurementTypeId { get; set; }
    public MeasurementType MeasurementType { get; set; } = null!;
    public decimal Value { get; set; }

    /// <summary>When it was measured in the field (can be back-dated).</summary>
    public DateTime MeasuredAtUtc { get; set; }

    /// <summary>When it was entered in the system.</summary>
    public DateTime RecordedAtUtc { get; set; }
    public string RecordedBy { get; set; } = "";
    public ChangeSource Source { get; set; } = ChangeSource.App;
    public MonitoringPhase Phase { get; set; }
    public string? Note { get; set; }

    /// <summary>SharePoint item ID for migrated readings.</summary>
    public int? LegacySharePointId { get; set; }
}
