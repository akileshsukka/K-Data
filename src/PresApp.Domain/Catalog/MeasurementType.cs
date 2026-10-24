using PresApp.Domain.Assets;

namespace PresApp.Domain.Catalog;

/// <summary>A kind of field reading (Pressure, Dew Point, O2…). E&amp;I can get its own later.</summary>
public class MeasurementType
{
    public int Id { get; set; }

    /// <summary>Stable key (e.g. "PressureBarg").</summary>
    public string Key { get; set; } = "";

    /// <summary>Label as used today, e.g. "G2 - Dew Point (C)".</summary>
    public string Label { get; set; } = "";
    public string? UnitOfMeasure { get; set; }

    /// <summary>Gate whose completion this reading proves (G2 Dew Point, G3 O2, G4 N2 Blanket).</summary>
    public int? EvidenceForGate { get; set; }

    /// <summary>Gate shown with the reading (null = General).</summary>
    public int? Gate { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public AssetTypes AppliesTo { get; set; } = AssetTypes.Equipment | AssetTypes.Pipeline;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
