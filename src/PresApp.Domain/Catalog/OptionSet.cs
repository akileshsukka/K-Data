namespace PresApp.Domain.Catalog;

/// <summary>A dropdown list (e.g. GateStatus). Values are deactivated, never deleted.</summary>
public class OptionSet
{
    public int Id { get; set; }

    /// <summary>Stable key used by code and Excel (e.g. "GateStatus").</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public List<OptionValue> Values { get; set; } = [];
}

public class OptionValue
{
    public int Id { get; set; }
    public int OptionSetId { get; set; }
    public OptionSet OptionSet { get; set; } = null!;
    public string Value { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Keys of the option sets the application relies on.</summary>
public static class OptionSetKeys
{
    public const string Priority = "Priority";
    public const string CurrentActivity = "CurrentActivity";
    public const string PreservationStatus = "PreservationStatus";
    public const string N2BlanketStatus = "N2BlanketStatus";
    public const string ThirdPartyInspection = "ThirdPartyInspection";
    public const string G1BulkLiquidDrained = "G1BulkLiquidDrained";
    public const string GateStatus = "GateStatus";
    public const string AssetAvailability = "AssetAvailability";
    public const string PreservedByEu = "PreservedByEU";
    public const string EmWalkdown = "EMWalkdown";
    public const string ActionBy = "ActionBy";
    public const string ReadyForPreservation = "ReadyForPreservation";

    // Lists of the Pipeline Register (lines never show the equipment values and vice versa).
    public const string PipingCurrentActivity = "PipingCurrentActivity";
    public const string PipingBulkLiquidDrained = "PipingBulkLiquidDrained";
}

/// <summary>
/// Fixed keys of the custom fields of the Pipeline Register that the app relies on (register indicators,
/// piping Excel and its Summary). Created at startup; the label and list values can be edited in Admin → Custom fields.
/// </summary>
public static class PipingFieldKeys
{
    /// <summary>Yes / No.</summary>
    public const string InspectionDone = "InspectionDone";
    /// <summary>List (Hydromilling, TBC…): one Summary column per value.</summary>
    public const string CleaningRequired = "CleaningRequired";
    /// <summary>List (Yes; Not Applicable can be added later). Empty = not cleaned or not required.</summary>
    public const string Cleaned = "Cleaned";
}
