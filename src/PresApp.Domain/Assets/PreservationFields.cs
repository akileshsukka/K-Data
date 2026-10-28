using System.Reflection;
using PresApp.Domain.Catalog;
using PresApp.Domain.Security;

namespace PresApp.Domain.Assets;

public enum FieldKind
{
    Text,
    LongText,
    Choice,
    Date,
    Number,
    ReadOnly
}

/// <summary>Where a field lives: on <see cref="Asset"/> or on <see cref="AssetPreservation"/>.</summary>
public enum FieldOwner
{
    Asset,
    Preservation,
    /// <summary>On <see cref="PipelineDetail"/>: only pipelines have these fields.</summary>
    Pipeline
}

/// <summary>
/// Definition of one editable (or displayed) field. The API validates and audits with it and the
/// front end builds forms from it, so labels, lists and permissions live in one place.
/// </summary>
public sealed record FieldDef(
    string Key,
    string Label,
    string Section,
    FieldKind Kind,
    FieldOwner Owner,
    string? OptionSet,
    int MaxLength,
    string Permission,
    int? Gate = null,
    bool BulkEditable = true,
    decimal? Min = null)
{
    private PropertyInfo? property;

    public PropertyInfo Property => property ??=
        (Owner switch { FieldOwner.Asset => typeof(Asset), FieldOwner.Pipeline => typeof(PipelineDetail), _ => typeof(AssetPreservation) }).GetProperty(Key)
        ?? throw new InvalidOperationException($"Field {Key} has no property.");

    public bool IsEditable => Kind != FieldKind.ReadOnly;

    /// <summary>Asset type the field belongs to (null = every type).</summary>
    public AssetType? OnlyFor => Owner == FieldOwner.Pipeline ? AssetType.Pipeline : null;
}

public static class PreservationFields
{
    public const string SectionIdentity = "Identification";
    public const string SectionStatus = "Status";
    public const string SectionClient = "Client tracking";
    public const string SectionGates = "Gates";
    public const string SectionReadings = "Latest readings";
    public const string SectionDates = "Milestones";
    public const string SectionLine = "Line";

    private const int Short = AssetPreservation.ShortText;
    private const int Long = AssetPreservation.LongText;

    public static readonly IReadOnlyList<FieldDef> All = Build();

    private static readonly Dictionary<string, FieldDef> ByKey =
        All.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    public static FieldDef? Find(string key) => ByKey.GetValueOrDefault(key);

    public static bool IsGateStatus(FieldDef f) => f.Gate is not null && f.Key.EndsWith("Status", StringComparison.Ordinal);

    private static List<FieldDef> Build()
    {
        var p = FieldOwner.Preservation;
        var list = new List<FieldDef>
        {
            // Identification (on Asset)
            new("Code", "Equipment ID", SectionIdentity, FieldKind.ReadOnly, FieldOwner.Asset, null, Asset.CodeMaxLength, Permissions.AssetsManage, BulkEditable: false),
            new("Tag", "Equipment Tag", SectionIdentity, FieldKind.Text, FieldOwner.Asset, null, 55, Permissions.AssetsEdit, BulkEditable: false),
            new("Description", "Description", SectionIdentity, FieldKind.Text, FieldOwner.Asset, null, 255, Permissions.AssetsEdit, BulkEditable: false),
            new("Priority", "Priority", SectionIdentity, FieldKind.Choice, FieldOwner.Asset, OptionSetKeys.Priority, 40, Permissions.AssetsEdit),

            // Line (pipelines only): size, spec, service and insulation come from the line number
            new(nameof(PipelineDetail.LengthM), "Length (m)", SectionLine, FieldKind.Number, FieldOwner.Pipeline, null, 0, Permissions.AssetsEdit, Min: 0),
            new(nameof(PipelineDetail.SizeIn), "Size (in)", SectionLine, FieldKind.Number, FieldOwner.Pipeline, null, 0, Permissions.AssetsEdit, BulkEditable: false, Min: 0),
            new(nameof(PipelineDetail.PipeSpec), "Pipe spec", SectionLine, FieldKind.Text, FieldOwner.Pipeline, null, 50, Permissions.AssetsEdit),
            new(nameof(PipelineDetail.Service), "Service", SectionLine, FieldKind.Text, FieldOwner.Pipeline, null, PipelineDetail.CodeLength, Permissions.AssetsEdit),
            new(nameof(PipelineDetail.PidNumber), "P&ID number", SectionLine, FieldKind.Text, FieldOwner.Pipeline, null, PipelineDetail.PidLength, Permissions.AssetsEdit),

            // Status
            new(nameof(AssetPreservation.AssetAvailability), "Asset Availability", SectionStatus, FieldKind.Choice, p, OptionSetKeys.AssetAvailability, 50, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.CurrentActivity), "Current Activity", SectionStatus, FieldKind.Choice, p, OptionSetKeys.CurrentActivity, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.PreservationStatusAdnoc), "Preservation Status (ADNOC)", SectionStatus, FieldKind.Choice, p, OptionSetKeys.PreservationStatus, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.N2BlanketStatus), "N2 Blanket Status", SectionStatus, FieldKind.Choice, p, OptionSetKeys.N2BlanketStatus, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.ThirdPartyInspectionStatus), "Third Party Inspection Status", SectionStatus, FieldKind.Choice, p, OptionSetKeys.ThirdPartyInspection, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.EmWalkdownStatus), "EM Walkdown Status", SectionStatus, FieldKind.Choice, p, OptionSetKeys.EmWalkdown, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.PreservedByEu), "Preserved by EU", SectionStatus, FieldKind.Choice, p, OptionSetKeys.PreservedByEu, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.ActionBy), "Action By", SectionStatus, FieldKind.Choice, p, OptionSetKeys.ActionBy, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.OemName), "OEM Name", SectionStatus, FieldKind.Text, p, null, Short, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.ReadyForPreservation), "Ready For Preservation", SectionStatus, FieldKind.Choice, p, OptionSetKeys.ReadyForPreservation, 10, Permissions.AssetsEdit),

            // Client tracking (free text columns of the client Excel)
            new(nameof(AssetPreservation.PreservationRemarks), "Preservation Remarks", SectionClient, FieldKind.LongText, p, null, Long, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.RemarksAdnoc), "Remarks (ADNOC)", SectionClient, FieldKind.LongText, p, null, Long, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.EmComments), "EM Comments (Gap Analysis)", SectionClient, FieldKind.LongText, p, null, Long, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.CurrentStatusAdnoc), "Current status (Expected Dispatch)", SectionClient, FieldKind.LongText, p, null, Long, Permissions.AssetsEdit),
            new(nameof(AssetPreservation.Response), "Response", SectionClient, FieldKind.LongText, p, null, Long, Permissions.AssetsEdit),

            // Gate details
            new(nameof(AssetPreservation.G1BulkLiquidDrained), "G1 - Bulk liquid drained", SectionGates, FieldKind.Choice, p, OptionSetKeys.G1BulkLiquidDrained, Short, Permissions.GatesEdit, Gate: 1),
            new(nameof(AssetPreservation.G5PositiveMechIsolation), "G5 - Positive Mech Isolation", SectionGates, FieldKind.Text, p, null, Short, Permissions.GatesEdit, Gate: 5),
            new(nameof(AssetPreservation.G6CleaningMethod), "G6 - Cleaning Method", SectionGates, FieldKind.Text, p, null, Short, Permissions.GatesEdit, Gate: 6),
        };

        for (var g = 1; g <= 8; g++)
        {
            list.Add(new($"G{g}Status", $"G{g} Status", SectionGates, FieldKind.Choice, p, OptionSetKeys.GateStatus, Short, Permissions.GatesEdit, Gate: g));
            list.Add(new($"G{g}Comments", $"G{g} Comments", SectionGates, FieldKind.LongText, p, null, Long, Permissions.GatesEdit, Gate: g));
        }

        list.AddRange(
        [
            new(nameof(AssetPreservation.PressureBarg), "Pressure (barg)", SectionReadings, FieldKind.ReadOnly, p, null, 0, Permissions.ReadingsWrite, BulkEditable: false),
            new(nameof(AssetPreservation.G2DewPointC), "G2 - Dew Point (C)", SectionReadings, FieldKind.ReadOnly, p, null, 0, Permissions.ReadingsWrite, BulkEditable: false),
            new(nameof(AssetPreservation.G3O2Pct), "G3 - O2 (%)", SectionReadings, FieldKind.ReadOnly, p, null, 0, Permissions.ReadingsWrite, BulkEditable: false),
            new(nameof(AssetPreservation.G4N2BlanketBarg), "G4 - N2 Blanket (barg)", SectionReadings, FieldKind.ReadOnly, p, null, 0, Permissions.ReadingsWrite, BulkEditable: false),
            new(nameof(AssetPreservation.G6H2S), "G6 - H2S", SectionReadings, FieldKind.ReadOnly, p, null, 0, Permissions.ReadingsWrite, BulkEditable: false),
            new(nameof(AssetPreservation.G6LelPct), "G6 - LEL (%)", SectionReadings, FieldKind.ReadOnly, p, null, 0, Permissions.ReadingsWrite, BulkEditable: false),

            new(nameof(AssetPreservation.CurrentGate), "Current Gate", SectionDates, FieldKind.ReadOnly, p, null, 20, Permissions.GatesEdit, BulkEditable: false),
            new(nameof(AssetPreservation.LastFieldActionDate), "Last Field Action Date", SectionDates, FieldKind.Date, p, null, 0, Permissions.AssetsEdit, BulkEditable: false),
            new(nameof(AssetPreservation.PreservationDate), "Preservation Date", SectionDates, FieldKind.ReadOnly, p, null, 0, Permissions.AssetsEdit, BulkEditable: false),
            new(nameof(AssetPreservation.N2BlanketedDate), "N2 Blanketed Date", SectionDates, FieldKind.ReadOnly, p, null, 0, Permissions.AssetsEdit, BulkEditable: false),
            new(nameof(AssetPreservation.LastReadingAtUtc), "Last Reading", SectionDates, FieldKind.ReadOnly, p, null, 0, Permissions.ReadingsWrite, BulkEditable: false),
            new(nameof(AssetPreservation.PreservedAtUtc), "Preserved since", SectionDates, FieldKind.ReadOnly, p, null, 0, Permissions.AssetsMarkPreserved, BulkEditable: false),
        ]);
        return list;
    }

    /// <summary>Display label for a ChangeLog field name, falling back to the raw name.</summary>
    public static string LabelOf(string field) => Find(field)?.Label ?? field switch
    {
        "(created)" => "Created",
        nameof(Asset.IsActive) => "Active",
        nameof(Asset.DisciplineId) => "Discipline",
        nameof(Asset.UnitId) => "Unit",
        nameof(AssetPreservation.PreservedBy) => "Preserved by",
        nameof(AssetPreservation.DashboardCategoryId) => "Dashboard category",
        "DashboardCategory" => "Dashboard category",
        _ => field
    };
}
