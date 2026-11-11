namespace PresApp.Domain.Catalog;

/// <summary>Admin-editable setting stored in the database (e.g. Excel.MaxAgeHours = 48).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }
}

public static class AppSettingKeys
{
    public const string ExcelMaxAgeHours = "Excel.MaxAgeHours";
    public const string SimproScheduleEnabled = "Simpro.Schedule.Enabled";
    public const string SimproScheduleTime = "Simpro.Schedule.TimeLocal";
    public const string MonitoringIntervalDays = "Monitoring.IntervalDays";
    /// <summary>Minimum Pressure (barg) of a POST asset; below it the asset is flagged as low. Empty = off.</summary>
    public const string MonitoringMinPressureBarg = "Monitoring.MinPressureBarg";
    /// <summary>N2 blanket pressure (barg) that completes the blanket while DURING (0.5 by default): the system can then be closed.</summary>
    public const string MonitoringTargetN2Barg = "Monitoring.TargetN2Barg";

    /// <summary>
    /// "Yes" when the lines of the unit are shown grouped by default (sizes of one line in one row): Pipeline Register,
    /// piping cards of the Unit overview, the PDF and the line list Excel. Missing = individual.
    /// </summary>
    public static string PipingGrouped(int unitId) => $"Piping.Grouped.{unitId}";
}
