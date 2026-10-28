using PresApp.Api.Assets;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;

namespace PresApp.Api.Excel;

public enum PipingRole
{
    Serial,
    Tag,
    /// <summary>Service code of the line number (never the code typed in the list).</summary>
    ProcessCode,
    /// <summary>Meaning of the service code (Admin → Line codes).</summary>
    ProcessDescription,
    /// <summary>A standard field, read back by the import.</summary>
    Field,
    /// <summary>A Pipeline custom field with a fixed key, read back by the import.</summary>
    Custom
}

public sealed record PipingColumn(string Header, PipingRole Role, string? Key, double Width)
{
    public bool Editable => Role is PipingRole.Field or PipingRole.Custom;

    /// <summary>Key of the column in the values kept with the export session.</summary>
    public string BaseKey => Role == PipingRole.Custom ? "cf:" + Key : Key ?? Header;
}

/// <summary>
/// The engineers' piping line list (U39 ALL PIPING LINES, 27-Oct-2026): sheet "Line List" with the table "Table1"
/// (headers on row 2 from column B) and the sheet "Summary" with COUNTIFS formulas on that table.
/// K Data writes it with the same headers, table name and formulas, plus a hidden Key column and the very hidden
/// _meta and _lists sheets, so the engineers keep the file they know and the import knows what they changed.
/// </summary>
public static class PipingLayout
{
    public const string TableName = "Table1";
    public const string LineSheet = "Line List";
    public const string SummarySheet = "Summary";
    public const int HeaderRow = 2;
    public const int FirstColumn = 2;
    public const string Kind = "Piping";

    public const string TagHeader = "Tag No";
    public const string WalkdownHeader = "EM Walkdown Status";
    public const string DrainedHeader = "Bulk liquid drained";
    public const string InspectionHeader = "Inspection Done ?";
    public const string CleaningRequiredHeader = "Cleaning Required ?";
    public const string CleanedHeader = "Cleaned?";
    public const string ProcessCodeHeader = "Process Code";
    public const string ProcessDescriptionHeader = "Process Description";

    public static readonly IReadOnlyList<PipingColumn> Columns =
    [
        new("Sr. No.", PipingRole.Serial, null, 8),
        new(TagHeader, PipingRole.Tag, null, 27),
        new("P&ID Number", PipingRole.Field, nameof(PipelineDetail.PidNumber), 14.8),
        new(ProcessCodeHeader, PipingRole.ProcessCode, null, 10),
        new(ProcessDescriptionHeader, PipingRole.ProcessDescription, null, 30),
        new("Current Activity", PipingRole.Field, nameof(AssetPreservation.CurrentActivity), 30),
        new(WalkdownHeader, PipingRole.Field, nameof(AssetPreservation.EmWalkdownStatus), 14),
        new(DrainedHeader, PipingRole.Field, nameof(AssetPreservation.G1BulkLiquidDrained), 12),
        new(InspectionHeader, PipingRole.Custom, PipingFieldKeys.InspectionDone, 12),
        new(CleaningRequiredHeader, PipingRole.Custom, PipingFieldKeys.CleaningRequired, 15),
        new(CleanedHeader, PipingRole.Custom, PipingFieldKeys.Cleaned, 10),
    ];

    public static readonly string[] FixedKeys = [PipingFieldKeys.InspectionDone, PipingFieldKeys.CleaningRequired, PipingFieldKeys.Cleaned];

    /// <summary>
    /// What the engineer sees in the cell. Bulk liquid drained: Completed → "Yes", anything else → empty.
    /// Inspection Done: empty → "No" (it is always Yes or No).
    /// </summary>
    public static string? Display(PipingColumn c, Asset a, IReadOnlyDictionary<string, FieldDefinition> defs)
    {
        switch (c.Role)
        {
            case PipingRole.Field:
                var field = PreservationFields.Find(c.Key!)!;
                var value = FieldValues.Read(field, a);
                return c.Key == nameof(AssetPreservation.G1BulkLiquidDrained) ? (AssetPreservation.IsCompleted(value) ? "Yes" : null) : value;
            case PipingRole.Custom:
                if (!defs.TryGetValue(c.Key!, out var def)) return null;
                var row = a.FieldValues.FirstOrDefault(v => v.FieldDefinitionId == def.Id);
                var text = row is null ? null : CustomFields.CustomFieldService.Format(def, row);
                return c.Key == PipingFieldKeys.InspectionDone ? text ?? "No" : text;
            default:
                return null;
        }
    }

    /// <summary>Same cell for the engineer: spaces and case do not count.</summary>
    public static bool Same(string? a, string? b) => Comparable(a) == Comparable(b);

    private static string Comparable(string? text) => ValueNormalizer.CleanText(text) is { } t ? ValueNormalizer.ListKey(t) : "";

    public static string Label(PipingColumn c, IReadOnlyDictionary<string, FieldDefinition> defs) => c.Role switch
    {
        PipingRole.Field => PreservationFields.Find(c.Key!)!.Label,
        PipingRole.Custom => defs.TryGetValue(c.Key!, out var d) ? d.Label : c.Header,
        _ => c.Header
    };
}
