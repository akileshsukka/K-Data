using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;

namespace PresApp.Api.Simpro;

public enum SimproKind
{
    /// <summary>A column of <c>tbl_flange</c>.</summary>
    Header,
    /// <summary>A row of <c>tbl_flangeCustomFieldValue</c> (by field ID, never by name).</summary>
    Custom
}

public enum SimproType
{
    Text, List, Number, Date, Bit, Gate, Plant, Unit, Priority, Discipline,
    /// <summary>Identifier of an existing equipment: differences are only reported, never written.</summary>
    Reference
}

/// <summary>
/// One K Data value and where it lives in Simpro. Same map as the sync script v5 (field keys kept, so its reports
/// and this app speak the same language). <see cref="KeepIfBlank"/>: a blank K Data value never clears Simpro.
/// </summary>
public sealed record SimproField(
    string Key, string Label, SimproKind Kind, string? Column, Guid? FieldId, SimproType Type, string? OptionSet,
    bool KeepIfBlank, int MaxLength, int Sort, string? AppField)
{
    public bool IsGateStatus => Key is ['G', >= '1' and <= '8', 'S'];
}

public static class SimproFields
{
    private static SimproField H(string key, string label, string column, SimproType type, bool keep, int max, int sort, string? appField = null) =>
        new(key, label, SimproKind.Header, column, null, type, null, keep, max, sort, appField);

    private static SimproField C(string key, string label, string fieldId, SimproType type, string? set, int sort, string? appField) =>
        new(key, label, SimproKind.Custom, null, Guid.Parse(fieldId), type, set, false, 4000, sort, appField);

    /// <summary>Lengths from the Simpro schema export (24-Oct-2026).</summary>
    public static readonly IReadOnlyList<SimproField> All =
    [
        H("TAGNO", "Equipment ID (tagNo)", "tagNo", SimproType.Reference, true, 40, 1),
        H("CUSTID", "Equipment Tag (custID)", "custID", SimproType.Reference, true, 50, 2),
        H("DESC", "Description", "flangeDescription", SimproType.Text, true, 255, 3, nameof(Asset.Description)),
        H("DISC", "Discipline", "flangeTag", SimproType.Discipline, true, 50, 4),
        H("PLANT", "Plant", "pbsLevel1Id", SimproType.Plant, true, 36, 5),
        H("UNIT", "Unit", "pbsLevel2Id", SimproType.Unit, true, 36, 6),
        H("PRIO", "Priority", "flangeCriticalityId", SimproType.Priority, true, 10, 7, nameof(Asset.Priority)),
        H("GATE", "Current Gate", "flangeStatusId", SimproType.Gate, true, 10, 8, nameof(AssetPreservation.CurrentGate)),
        H("AVAIL", "Asset Availability", "workOrder", SimproType.Text, false, 50, 9, nameof(AssetPreservation.AssetAvailability)),
        H("LFAD", "Last Field Action Date", "pipeSpec", SimproType.Date, false, 50, 10, nameof(AssetPreservation.LastFieldActionDate)),
        H("REMARK", "Remarks (ADNOC)", "WorkInstructions", SimproType.Text, false, 4000, 11, nameof(AssetPreservation.RemarksAdnoc)),
        H("WALK", "EM Walkdown (verified)", "verified", SimproType.Bit, false, 1, 12),
        H("EMCOM", "EM Comments", "comments", SimproType.Text, false, 4000, 13, nameof(AssetPreservation.EmComments)),
        C("WSTAT", "EM Walkdown Status", "8CCBD009-A06C-420D-89B3-A950764CF1D6", SimproType.List, OptionSetKeys.EmWalkdown, 14, nameof(AssetPreservation.EmWalkdownStatus)),
        C("CURACT", "Current Activity", "9DC9103C-6011-4E5A-9420-885ADB210299", SimproType.List, OptionSetKeys.CurrentActivity, 20, nameof(AssetPreservation.CurrentActivity)),
        C("CURSTAT", "Current status (Expected Dispatch)", "4863A008-FFA9-4EE4-B8C3-8AB3CF6AE937", SimproType.Text, null, 21, nameof(AssetPreservation.CurrentStatusAdnoc)),
        C("PRES", "Preservation Status (ADNOC)", "1FEC363F-84C2-4A91-9F5A-6ED596E4B60B", SimproType.List, OptionSetKeys.PreservationStatus, 22, nameof(AssetPreservation.PreservationStatusAdnoc)),
        C("PDATE", "Preservation Date", "B22480EB-AD56-4E82-A885-38C2A4A3AE5B", SimproType.Date, null, 23, nameof(AssetPreservation.PreservationDate)),
        C("N2S", "N2 Blanket Status", "B1786D04-18C6-4772-AAFA-5A3CD9480DA8", SimproType.List, OptionSetKeys.N2BlanketStatus, 24, nameof(AssetPreservation.N2BlanketStatus)),
        C("N2D", "N2 Blanketed Date", "036CF03C-11B3-48EF-AB97-528F7EEC76A8", SimproType.Date, null, 25, nameof(AssetPreservation.N2BlanketedDate)),
        C("TPI", "Third Party Inspection Status", "3D21B46F-AF37-4360-9C66-D67810EE056A", SimproType.List, OptionSetKeys.ThirdPartyInspection, 26, nameof(AssetPreservation.ThirdPartyInspectionStatus)),
        C("G1B", "G1 - Bulk liquid drained", "3E3D50ED-76E5-4114-88C8-B1EA510CB72D", SimproType.List, OptionSetKeys.G1BulkLiquidDrained, 27, nameof(AssetPreservation.G1BulkLiquidDrained)),
        C("G1S", "G1 Status", "0E5535EC-C94E-4CE1-8169-762E52828C17", SimproType.List, OptionSetKeys.GateStatus, 31, "G1Status"),
        C("G2S", "G2 Status", "889EA3AC-2D9B-4C75-834C-E11986014028", SimproType.List, OptionSetKeys.GateStatus, 32, "G2Status"),
        C("G3S", "G3 Status", "2316FBA3-1A98-4F03-AD6C-4DD3832AFD56", SimproType.List, OptionSetKeys.GateStatus, 33, "G3Status"),
        C("G4S", "G4 Status", "4DFEB3C9-0BAE-42FB-BEFF-D6DDA5629385", SimproType.List, OptionSetKeys.GateStatus, 34, "G4Status"),
        C("G5S", "G5 Status", "1BD55B9D-37D4-408D-AF41-A42264A7FA17", SimproType.List, OptionSetKeys.GateStatus, 35, "G5Status"),
        C("G6S", "G6 Status", "A3CDA293-122C-46D6-8794-A7C9EDA9992A", SimproType.List, OptionSetKeys.GateStatus, 36, "G6Status"),
        C("G7S", "G7 Status", "E251A56A-6B34-4AE2-B650-E3783954C633", SimproType.List, OptionSetKeys.GateStatus, 37, "G7Status"),
        C("G8S", "G8 Status", "5B1BB4B9-0CBE-4DB6-9960-8A0BCA0F9E50", SimproType.List, OptionSetKeys.GateStatus, 38, "G8Status"),
        C("G1C", "G1 Comments", "648DDACE-754B-489F-988F-72EBF8B63710", SimproType.Text, null, 41, "G1Comments"),
        C("G2C", "G2 Comments", "0A57DAA8-895B-41F3-A735-4402EBB16EF9", SimproType.Text, null, 42, "G2Comments"),
        C("G3C", "G3 Comments", "B8D2867E-1CBE-4D5C-8187-6085B8A67AEE", SimproType.Text, null, 43, "G3Comments"),
        C("G4C", "G4 Comments", "91A17B4B-4E3A-43C1-9422-576F518766C9", SimproType.Text, null, 44, "G4Comments"),
        C("G5C", "G5 Comments", "819DE4A8-A5DD-45DF-9B17-63EE20A2F24D", SimproType.Text, null, 45, "G5Comments"),
        C("G6C", "G6 Comments", "307E12FE-6FD7-4A5E-9CA4-06428E56D10E", SimproType.Text, null, 46, "G6Comments"),
        C("G7C", "G7 Comments", "887B22B1-0B90-465B-AB6E-0DA7B7ECCDEA", SimproType.Text, null, 47, "G7Comments"),
        C("G8C", "G8 Comments", "08CDEAA3-C28C-44EB-8ADE-8749A23A5450", SimproType.Text, null, 48, "G8Comments"),
        C("PRESS", "Pressure (barg)", "DCB8D2A1-7EAC-4B72-B59C-F6614AB120C9", SimproType.Number, null, 51, null),
        C("G2DP", "G2 - Dew Point (C)", "C535B0AB-5C89-4A27-A0C0-42ACBA55BAD2", SimproType.Number, null, 52, null),
        C("G3O2", "G3 - O2 (%)", "FF5E75F6-10E6-4075-9D48-1117A6741B81", SimproType.Number, null, 53, null),
        C("G4N2", "G4 - N2 Blanket (barg)", "C2F9FD54-1ED2-4EE2-8549-2EF956FB5847", SimproType.Number, null, 54, null),
        C("G6H2S", "G6 - H2S", "01F7987B-C1A0-46D3-B247-993912F424F9", SimproType.Number, null, 55, null),
        C("G6LEL", "G6 - LEL (%)", "9292640C-731B-4502-91E4-CC2991909222", SimproType.Number, null, 56, null),
        C("G5PMI", "G5 - Positive Mech Isolation", "7BD3C9EC-34AF-4398-9CEA-065655C131BC", SimproType.Text, null, 61, nameof(AssetPreservation.G5PositiveMechIsolation)),
        C("G6CM", "G6 - Cleaning Method", "F7BF8754-280C-4D52-AB17-84D6A9B59D41", SimproType.Text, null, 62, nameof(AssetPreservation.G6CleaningMethod)),
        C("EU", "Preserved by EU", "1031A159-7D1C-4F97-801D-4B4F0C66456D", SimproType.List, OptionSetKeys.PreservedByEu, 71, nameof(AssetPreservation.PreservedByEu)),
        C("PREM", "Preservation Remarks", "629D3828-06DB-4D1E-A92B-AA6B4B3EC039", SimproType.Text, null, 72, nameof(AssetPreservation.PreservationRemarks)),
        C("ACTBY", "Action By", "F8346CD9-BC32-45D2-9FBC-F5BE401B0AB6", SimproType.Text, null, 73, nameof(AssetPreservation.ActionBy)),
        C("RESP", "Response", "9A39087D-6DA0-4CCE-92B7-BA4B6482E709", SimproType.Text, null, 74, nameof(AssetPreservation.Response)),
        C("OEM", "OEM Name", "F976E3DA-7A81-4135-9E44-2719FBF837DD", SimproType.Text, null, 75, nameof(AssetPreservation.OemName)),
    ];

    public static readonly IReadOnlyDictionary<string, SimproField> ByKey = All.ToDictionary(f => f.Key);

    public static IEnumerable<SimproField> Headers => All.Where(f => f.Kind == SimproKind.Header);
    public static IEnumerable<SimproField> Customs => All.Where(f => f.Kind == SimproKind.Custom);

    /// <summary>Current Gate label ↔ Simpro flangeStatusId, with the order of progress (null = Removed from Site).</summary>
    public static readonly IReadOnlyDictionary<CurrentGate, (int Code, int? Rank)> GateCodes = new Dictionary<CurrentGate, (int, int?)>
    {
        [CurrentGate.New] = (1, 0), [CurrentGate.RemovedFromSite] = (2, null),
        [CurrentGate.G1] = (9, 1), [CurrentGate.G2] = (10, 2), [CurrentGate.G3] = (3, 3), [CurrentGate.G4] = (4, 4),
        [CurrentGate.G5] = (8, 5), [CurrentGate.G6] = (13, 6), [CurrentGate.G7] = (12, 7), [CurrentGate.G8] = (11, 8),
    };

    public static CurrentGate? GateOfCode(int code) => GateCodes.FirstOrDefault(g => g.Value.Code == code) is { Value.Code: > 0 } kv ? kv.Key : null;

    /// <summary>Measurement key of the readings that feed each number field.</summary>
    public static readonly IReadOnlyDictionary<string, string> ReadingFields = new Dictionary<string, string>
    {
        ["PRESS"] = nameof(AssetPreservation.PressureBarg), ["G2DP"] = nameof(AssetPreservation.G2DewPointC),
        ["G3O2"] = nameof(AssetPreservation.G3O2Pct), ["G4N2"] = nameof(AssetPreservation.G4N2BlanketBarg),
        ["G6H2S"] = nameof(AssetPreservation.G6H2S), ["G6LEL"] = nameof(AssetPreservation.G6LelPct),
    };
}
