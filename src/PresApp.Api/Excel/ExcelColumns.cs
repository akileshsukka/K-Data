using System.Text.RegularExpressions;
using PresApp.Domain.Assets;

namespace PresApp.Api.Excel;

public enum ColumnRole
{
    /// <summary>Not stored by the app (e.g. "Pre-Assessment By OEM", "Walk down date"): written blank, never read.</summary>
    NotStored,
    Serial,
    OperatingUnit,
    Discipline,
    Tag,
    Description,
    /// <summary>A registry field. Editable columns are read back by the import.</summary>
    Field,
    /// <summary>The template row holds a formula (e.g. "Category"): copied to every row.</summary>
    Formula,
    Key,
    /// <summary>Template v4: code of the asset's Hold category (Cat1, Cat2…). Read back by the import.</summary>
    HoldCategory,
    /// <summary>Template v4: code of the asset's N/A category. Read back by the import.</summary>
    NaCategory
}

public sealed record ExcelColumn(int Index, string Header, ColumnRole Role, FieldDef? Field, bool Editable);

/// <summary>
/// Recognises the client template columns by header text (order free), exactly as the Office Scripts did:
/// the first "Preservation" column is the Yes/No status, the second one the free-text remarks (column S).
/// A field is editable in one column only; the first column wins.
/// </summary>
public static partial class ExcelColumns
{
    public const string KeyHeader = "Key";
    public const string MasterTable = "MasterList";
    public const string CategoriesTable = "Categories";

    /// <summary>Template v4 named ranges: dashboard rows of the Hold and N/A categories (one row per category).</summary>
    public const string HoldSlotsName = "KD_HoldSlots";
    public const string NaSlotsName = "KD_NASlots";

    /// <summary>Fields the engineers may change in the unit Excel (same set as the Power Automate flow).</summary>
    private static readonly HashSet<string> EditableInUnitFile = new(StringComparer.Ordinal)
    {
        nameof(AssetPreservation.ThirdPartyInspectionStatus), nameof(AssetPreservation.PreservationStatusAdnoc),
        nameof(AssetPreservation.PreservationRemarks), nameof(AssetPreservation.RemarksAdnoc), nameof(AssetPreservation.EmComments),
        nameof(AssetPreservation.EmWalkdownStatus), nameof(AssetPreservation.PreservedByEu), nameof(AssetPreservation.ActionBy),
        nameof(AssetPreservation.CurrentStatusAdnoc), nameof(AssetPreservation.Response), nameof(AssetPreservation.ReadyForPreservation),
        "G1Status", "G2Status", "G3Status", "G4Status", "G5Status", "G6Status", "G7Status", "G8Status"
    };

    public static string Norm(string? header) => NonAlnum().Replace((header ?? "").ToLowerInvariant(), "");

    /// <summary>Maps the headers of a unit file. <paramref name="onboarding"/> also accepts the long client list columns.</summary>
    public static List<ExcelColumn> Map(IReadOnlyList<string> headers, IReadOnlyList<bool> formulaColumns, bool onboarding = false)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var preservationSeen = 0;
        var result = new List<ExcelColumn>(headers.Count);

        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var k = Norm(header);
            if (i < formulaColumns.Count && formulaColumns[i]) { result.Add(new(i, header, ColumnRole.Formula, null, false)); continue; }

            ColumnRole role;
            string? fieldKey = null;
            switch (k)
            {
                case "key": role = ColumnRole.Key; break;
                case "sr" or "srno" or "sno": role = ColumnRole.Serial; break;
                case "operatingunit" or "unit": role = ColumnRole.OperatingUnit; break;
                case "discipline": role = ColumnRole.Discipline; break;
                case "equipno" or "equipmentno" or "tag" or "equipmenttag": role = ColumnRole.Tag; break;
                case "lineno" or "linenumber" or "tagno" when onboarding: role = ColumnRole.Tag; break;
                case "equipdescription" or "description" or "equipmentdescription": role = ColumnRole.Description; break;
                case "holdcategory" when !onboarding: role = ColumnRole.HoldCategory; break;
                case "nacategory" when !onboarding: role = ColumnRole.NaCategory; break;
                default:
                    role = ColumnRole.Field;
                    if (k is "preservation" or "preservation2")
                    {
                        preservationSeen++;
                        fieldKey = preservationSeen == 1 ? nameof(AssetPreservation.PreservationStatusAdnoc) : nameof(AssetPreservation.PreservationRemarks);
                    }
                    else fieldKey = FieldFor(k, onboarding);
                    if (fieldKey is null) role = ColumnRole.NotStored;
                    break;
            }

            if (role != ColumnRole.Field) { result.Add(new(i, header, role, null, false)); continue; }
            if (!used.Add(fieldKey!)) { result.Add(new(i, header, ColumnRole.NotStored, null, false)); continue; }
            var field = PreservationFields.Find(fieldKey!)!;
            result.Add(new(i, header, ColumnRole.Field, field, onboarding ? field.IsEditable : EditableInUnitFile.Contains(field.Key)));
        }
        return result;
    }

    private static string? FieldFor(string k, bool onboarding)
    {
        var gate = GateStatusHeader().Match(k);
        if (gate.Success) return $"G{gate.Groups[1].Value}Status";

        if (k == "thirdpartyinspectionstatus") return nameof(AssetPreservation.ThirdPartyInspectionStatus);
        if (k is "preservationstatus" or "preservationremarks") return nameof(AssetPreservation.PreservationRemarks);
        if (k is "remark" or "remarks") return nameof(AssetPreservation.RemarksAdnoc);
        if (k.StartsWith("gapan", StringComparison.Ordinal)) return nameof(AssetPreservation.EmComments);
        if (k.StartsWith("walkdown", StringComparison.Ordinal) && !k.Contains("date", StringComparison.Ordinal)) return nameof(AssetPreservation.EmWalkdownStatus);
        if (k == "preservedbyeu") return nameof(AssetPreservation.PreservedByEu);
        if (k == "actionby") return nameof(AssetPreservation.ActionBy);
        if (k.StartsWith("expecteddispatch", StringComparison.Ordinal)) return nameof(AssetPreservation.CurrentStatusAdnoc);
        if (k == "response") return nameof(AssetPreservation.Response);
        if (k == "oemname") return nameof(AssetPreservation.OemName);
        if (k == "readyforpreservation") return nameof(AssetPreservation.ReadyForPreservation);
        if (!onboarding) return null;

        // Long client master list (first load of a unit)
        var comments = GateCommentsHeader().Match(k);
        if (comments.Success) return $"G{comments.Groups[1].Value}Comments";
        return k switch
        {
            "emwalkdown" or "emwalkdownstatus" => nameof(AssetPreservation.EmWalkdownStatus),
            "statusofn2blanket" or "n2blanketstatus" => nameof(AssetPreservation.N2BlanketStatus),
            "bulkliquiddrained" or "g1bulkliquiddrained" => nameof(AssetPreservation.G1BulkLiquidDrained),
            "positivemechanicalisolation" or "g5positivemechisolation" => nameof(AssetPreservation.G5PositiveMechIsolation),
            "cleaningmethod" or "g6cleaningmethod" => nameof(AssetPreservation.G6CleaningMethod),
            "currentactivity" => nameof(AssetPreservation.CurrentActivity),
            "assetavailability" => nameof(AssetPreservation.AssetAvailability),
            "priority" => "Priority",
            "length" or "lengthm" or "linelength" or "linelengthm" => nameof(PipelineDetail.LengthM),
            "pid" or "pidno" or "pidnumber" or "pidref" or "pandid" or "pandidno" => nameof(PipelineDetail.PidNumber),
            _ => null
        };
    }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex("^g([1-8])status$")]
    private static partial Regex GateStatusHeader();

    [GeneratedRegex("^g([1-8])comments?$")]
    private static partial Regex GateCommentsHeader();
}
