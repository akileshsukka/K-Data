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
}
