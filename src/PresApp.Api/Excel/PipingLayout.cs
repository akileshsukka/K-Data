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
    /// <summary>"Yes" when the line's G4 (N2 blanketing) is Completed; written for the Summary, never imported.</summary>
    N2Blanket,
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
    public const string N2BlanketHeader = "Nitrogen Blanket";

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
        new(N2BlanketHeader, PipingRole.N2Blanket, null, 11),
    ];

    /// <summary>"Yes" when G4 (N2 blanketing) is Completed, otherwise empty.</summary>
    public static string? N2Blanket(Asset a) => AssetPreservation.IsCompleted(a.Preservation?.G4Status) ? "Yes" : null;

    public static readonly string[] FixedKeys = [PipingFieldKeys.InspectionDone, PipingFieldKeys.CleaningRequired, PipingFieldKeys.Cleaned];

    /// <summary>
    /// What the engineer sees in the cell. Bulk liquid drained: Completed → "Yes", anything else → empty.
    /// Inspection Done: empty → "No", Not Applicable → "N/A".
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
                return c.Key == PipingFieldKeys.InspectionDone ? CellText(c.Key, text) ?? "No" : text;
            default:
                return null;
        }
    }

    /// <summary>A value of a fixed custom field as written in the cell: Inspection Done Not Applicable → "N/A".</summary>
    public static string? CellText(string key, string? value) =>
        key == PipingFieldKeys.InspectionDone && value is not null && ValueNormalizer.IsNotApplicable(value)
            ? PipingFieldKeys.InspectionNotApplicableInExcel
            : value;

    /// <summary>Written in a grouped row when the lines of the group do not have the same value. Left as it is, nothing is imported.</summary>
    public const string Mixed = "Mixed";

    /// <summary>
    /// What a grouped row shows for the values of its lines: the value when they all have it, otherwise <see cref="Mixed"/>.
    /// P&amp;ID numbers (<paramref name="join"/>) are listed together instead: "541-00-30-194, 541-00-30-195".
    /// </summary>
    public static string? GroupText(IReadOnlyList<string?> values, bool join = false)
    {
        if (values.Count == 1) return values[0];
        if (join)
        {
            var parts = values.SelectMany(v => (v ?? "").Split([',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .DistinctBy(Comparable).ToList();
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        return values.Skip(1).All(v => Same(v, values[0])) ? values[0] : Mixed;
    }

    /// <summary>Columns whose values are listed together in a grouped row (P&amp;ID).</summary>
    public static bool Joins(string baseKey) => baseKey == nameof(PipelineDetail.PidNumber);

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
