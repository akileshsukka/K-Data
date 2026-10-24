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
}
