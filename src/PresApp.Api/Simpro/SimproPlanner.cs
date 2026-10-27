using System.Globalization;
using PresApp.Api.Assets;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;

namespace PresApp.Api.Simpro;

public enum FieldClass { Same, Keep, Invalid, Reference, Write, SimproChanged, Conflict, Held, New, GateBack }

/// <summary>One field of one equipment, seen from the three sides: K Data, Simpro now, last sync snapshot.</summary>
public sealed class FieldPlan(SimproField field)
{
    public SimproField Field { get; } = field;
    public string? AppShow { get; set; }
    public string? AppNorm { get; set; }
    public string? AppOut { get; set; }
    public string? Bad { get; set; }
    public string? Derived { get; set; }
    public bool FromReading { get; set; }
    public string? SimRaw { get; set; }
    public string? SimNorm { get; set; }
    public string? BaseRaw { get; set; }
    public string? BaseNorm { get; set; }
    public FieldClass Class { get; set; }
    public Guid? CustomRowId { get; set; }
}

public enum PlanStatus { Existing, New, Inactive, Excluded, NotSynced }

public sealed class AssetPlan(Asset asset)
{
    public Asset Asset { get; } = asset;
    public PlanStatus Status { get; set; }
    public bool HasBase { get; set; }
    public bool Forced { get; set; }
    public string? Problem { get; set; }
    public List<FieldPlan> Fields { get; } = [];

    public bool IsHeld => Fields.Any(f => f.Class is FieldClass.SimproChanged or FieldClass.Conflict);
    public bool NeedsDecision => IsHeld || Fields.Any(f => f.Class == FieldClass.GateBack);
    public IEnumerable<FieldPlan> Writes => Fields.Where(f => f.Class == FieldClass.Write);
    public bool WillCreate => Status == PlanStatus.New && Problem is null;
}

public sealed record SyncPlan(List<AssetPlan> Assets, List<(Guid Id, string? TagNo, string? CustId, string? Unit)> SimproOnly);

/// <summary>
/// Pure comparison logic of the sync script v5, applied to the K Data values: normalise the three sides the same way,
/// apply the rules (Current Gate forward only, Removed from Site, Last Field Action Date after readings, EM walkdown),
/// then classify every field: same, write, changed in Simpro, conflict, invalid. Nothing here touches a database.
/// </summary>
public sealed class SimproPlanner(SimproOptions options, Dictionary<string, List<string>> allOptions)
{
    private static readonly Dictionary<string, Dictionary<string, string>> SimproAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [OptionSetKeys.ThirdPartyInspection] = new(StringComparer.OrdinalIgnoreCase) { ["Not on site"] = "Not Found / Not Assessed", ["Not found"] = "Not Found / Not Assessed" },
        [OptionSetKeys.G1BulkLiquidDrained] = new(StringComparer.OrdinalIgnoreCase) { ["PENDING"] = "Draining is required" },
        [OptionSetKeys.GateStatus] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DONE"] = "Completed - Documentation Pending", ["Complete"] = "Completed", ["On-going"] = "Ongoing", ["On going"] = "Ongoing",
            ["Not Started"] = "Ongoing", ["Not Verified"] = "Ongoing", ["Open"] = "Ongoing",
        },
    };

    public SyncPlan Build(IEnumerable<Asset> assets, SimproData sim, ISet<Guid> forced, DateOnly today)
    {
        var plans = new List<AssetPlan>();
        var simproTags = sim.Flanges.Values.GroupBy(f => (f.Values["TAGNO"] ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(f => f.FlangeId).ToList(), StringComparer.OrdinalIgnoreCase);
        var appIds = new HashSet<Guid>();

        foreach (var asset in assets)
        {
            appIds.Add(asset.Id);
            var plan = new AssetPlan(asset) { Forced = forced.Contains(asset.Id) };
            plans.Add(plan);
            var simRow = sim.Flanges.GetValueOrDefault(asset.Id);

            if (IsExcluded(asset.Code, asset.Discipline.Name) || simRow is not null && IsExcluded(simRow.Values["TAGNO"], simRow.Values["DISC"])) { plan.Status = PlanStatus.Excluded; continue; }
            if (asset.Type == AssetType.Pipeline || !asset.Discipline.SyncToSimpro) { plan.Status = PlanStatus.NotSynced; continue; }
            if (!asset.IsActive) { plan.Status = PlanStatus.Inactive; continue; }
            plan.Status = simRow is null ? PlanStatus.New : PlanStatus.Existing;

            // ---- three sides, normalised
            foreach (var field in SimproFields.All) plan.Fields.Add(new FieldPlan(field));
            FillApp(plan, asset, sim, today);
            if (simRow is not null)
            {
                var customs = sim.CustomValues.GetValueOrDefault(asset.Id) ?? [];
                foreach (var f in plan.Fields)
                {
                    if (f.Field.Kind == SimproKind.Header) f.SimRaw = simRow.Values[f.Field.Key];
                    else
                    {
                        var rows = customs.Where(c => c.FieldId == f.Field.FieldId).ToList();
                        f.SimRaw = rows.Select(r => r.Value).Max(StringComparer.Ordinal);
                        f.CustomRowId = rows.Count == 1 ? rows[0].Id : null;
                        if (rows.Count > 1) f.Bad ??= "Simpro has two rows for this custom field: fix it in Simpro first.";
                    }
                    f.SimNorm = NormSimpro(f, f.SimRaw);
                }
                plan.HasBase = sim.BaseHeaders.ContainsKey(asset.Id);
                if (plan.HasBase)
                {
                    var bh = sim.BaseHeaders[asset.Id];
                    var bc = sim.BaseCustom.GetValueOrDefault(asset.Id) ?? [];
                    foreach (var f in plan.Fields)
                    {
                        f.BaseRaw = f.Field.Kind == SimproKind.Header ? bh.Values[f.Field.Key] : bc.GetValueOrDefault(f.Field.FieldId!.Value);
                        f.BaseNorm = NormSimpro(f, f.BaseRaw);
                    }
                }
                PressureAsBlanket(plan.Fields, f => f.SimNorm, (f, v) => f.SimNorm = v);
                if (plan.HasBase) PressureAsBlanket(plan.Fields, f => f.BaseNorm, (f, v) => f.BaseNorm = v);
            }
            PressureAsBlanket(plan.Fields, f => f.AppNorm, (f, v) => f.AppNorm = v);

            if (plan.Status == PlanStatus.New)
            {
                plan.Problem = asset.Code.Length == 0 ? "Equipment ID is blank."
                    : simproTags.ContainsKey(asset.Code.Trim()) ? "Equipment ID already exists in Simpro with another GUID."
                    : plan.Fields.FirstOrDefault(f => f.Bad is not null) is { } bad ? $"{bad.Field.Label}: {bad.Bad}"
                    : plan.Fields.Any(f => f.Field.Type is SimproType.Plant or SimproType.Unit && f.AppNorm is null) ? "Plant and Unit need their Simpro location."
                    : null;
                foreach (var f in plan.Fields) f.Class = FieldClass.New;
                continue;
            }
            Classify(plan);
        }

        var simproOnly = sim.Flanges.Values
            .Where(f => !appIds.Contains(f.FlangeId) && !IsExcluded(f.Values["TAGNO"], f.Values["DISC"]))
            .Select(f => (f.FlangeId, f.Values["TAGNO"], f.Values["CUSTID"], f.Values["UNIT"] is { } u && Guid.TryParse(u, out var g) ? sim.Locations.GetValueOrDefault(g) : null))
            .OrderBy(x => x.Item4).ThenBy(x => x.Item2, NaturalComparer.Instance).ToList();
        return new SyncPlan(plans, simproOnly);
    }

    public static bool IsExcluded(string? tagNo, string? discipline) =>
        (tagNo ?? "").TrimStart().StartsWith("FM", StringComparison.OrdinalIgnoreCase)
        || string.Equals(discipline?.Trim(), "Flange Management", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ K Data side

    private void FillApp(AssetPlan plan, Asset a, SimproData sim, DateOnly today)
    {
        var p = a.Preservation ?? new AssetPreservation();
        FieldPlan F(string key) => plan.Fields.First(f => f.Field.Key == key);

        foreach (var f in plan.Fields)
        {
            var key = f.Field.Key;
            switch (f.Field.Type)
            {
                case SimproType.Reference:
                    f.AppShow = f.AppNorm = f.AppOut = Clean(key == "TAGNO" ? a.Code : a.Tag);
                    break;
                case SimproType.Discipline:
                    var d = Clean(a.Discipline.Name);
                    f.AppShow = f.AppOut = d ?? "SU";
                    f.AppNorm = (d ?? "SU").ToUpperInvariant();
                    break;
                case SimproType.Plant:
                    f.AppShow = a.Unit.Plant.Name;
                    f.AppNorm = f.AppOut = a.Unit.Plant.SimproLocationId?.ToString();
                    if (f.AppNorm is null) f.Bad = $"Plant {a.Unit.Plant.Name} has no Simpro location.";
                    break;
                case SimproType.Unit:
                    f.AppShow = a.Unit.Name;
                    f.AppNorm = f.AppOut = a.Unit.SimproLocationId?.ToString();
                    if (f.AppNorm is null) f.Bad = $"{a.Unit.Name} has no Simpro location.";
                    break;
                case SimproType.Priority:
                    f.AppShow = Clean(a.Priority);
                    if (f.AppShow is not null)
                    {
                        var id = sim.Criticalities.FirstOrDefault(c => string.Equals(c.Value, f.AppShow, StringComparison.OrdinalIgnoreCase)).Key;
                        if (id == 0) f.Bad = "Priority not found in Simpro.";
                        else f.AppNorm = f.AppOut = id.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case SimproType.Gate:
                    f.AppShow = FieldValues.CurrentGateText(p.CurrentGate);
                    f.AppNorm = f.AppOut = SimproFields.GateCodes[p.CurrentGate].Code.ToString(CultureInfo.InvariantCulture);
                    break;
                case SimproType.Bit:
                    var walked = string.Equals(p.EmWalkdownStatus?.Trim(), "Completed", StringComparison.OrdinalIgnoreCase);
                    f.AppShow = walked ? "Yes" : "No";
                    f.AppNorm = f.AppOut = walked ? "1" : "0";
                    break;
                case SimproType.Number:
                    var number = (decimal?)typeof(AssetPreservation).GetProperty(SimproFields.ReadingFields[key])!.GetValue(p);
                    f.FromReading = number is not null;
                    f.AppShow = f.AppOut = number?.ToString("0.############", CultureInfo.InvariantCulture);
                    f.AppNorm = f.AppOut;
                    break;
                case SimproType.Date:
                    var date = (DateOnly?)typeof(AssetPreservation).GetProperty(f.Field.AppField!)!.GetValue(p);
                    f.AppShow = f.AppNorm = f.AppOut = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    break;
                case SimproType.List:
                    var raw = ReadApp(f.Field, a);
                    var (canonical, matched) = ValueNormalizer.Canonical(raw, OptionsOf(f.Field.OptionSet), f.Field.IsGateStatus);
                    f.AppShow = canonical;
                    f.AppNorm = canonical;
                    f.AppOut = canonical is not null && string.Equals(canonical, ValueNormalizer.NotApplicable, StringComparison.OrdinalIgnoreCase)
                        ? options.NotApplicableText : canonical;
                    if (canonical is not null && !matched) f.Bad = "Value not in the list.";
                    break;
                default:
                    f.AppShow = f.AppNorm = f.AppOut = Clean(ReadApp(f.Field, a));
                    break;
            }
        }

        // Rule: Last Field Action Date = the later of the app date and the latest reading
        var lfad = F("LFAD");
        if (p.LastReadingAtUtc is { } lastReading)
        {
            var readingDay = SiteClock.DateOf(lastReading).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (lfad.AppNorm is null || string.CompareOrdinal(lfad.AppNorm, readingDay) < 0)
            {
                lfad.Derived = $"Last Field Action Date {lfad.AppNorm ?? "(blank)"} → {readingDay} (date of the latest reading)";
                lfad.AppNorm = lfad.AppOut = readingDay;
                lfad.FromReading = true;
            }
        }

        // Rule: Current Gate moves forward only, to the highest completed gate; never touches Removed from Site
        var gate = F("GATE");
        var highest = Enumerable.Range(1, 8).Where(g => AssetPreservation.IsCompleted(F($"G{g}S").AppNorm)).DefaultIfEmpty(0).Max();
        var current = SimproFields.GateCodes[p.CurrentGate];
        if (highest > 0 && current.Rank is { } rank && highest > rank)
        {
            var target = (CurrentGate)highest;
            gate.Derived = $"Current Gate {FieldValues.CurrentGateText(p.CurrentGate)} → {FieldValues.CurrentGateText(target)} (highest completed gate)";
            gate.AppNorm = gate.AppOut = SimproFields.GateCodes[target].Code.ToString(CultureInfo.InvariantCulture);
            gate.AppShow = FieldValues.CurrentGateText(target);
        }
        // Rule: Asset Availability = Removed from Site → Current Gate = Removed from Site, only with no completed gate
        if (highest == 0 && string.Equals(F("AVAIL").AppNorm, "Removed from Site", StringComparison.OrdinalIgnoreCase) && p.CurrentGate != CurrentGate.RemovedFromSite)
        {
            gate.Derived = $"Current Gate {FieldValues.CurrentGateText(p.CurrentGate)} → Removed from Site (Asset Availability = Removed from Site)";
            gate.AppNorm = gate.AppOut = "2";
            gate.AppShow = "Removed from Site";
        }

        foreach (var f in plan.Fields.Where(f => f.Bad is null && f.AppOut is not null && f.AppOut.Length > f.Field.MaxLength))
            f.Bad = $"Too long for Simpro (max {f.Field.MaxLength} characters).";
    }

    private static string? ReadApp(SimproField field, Asset a)
    {
        if (field.AppField is null) return null;
        var def = PreservationFields.Find(field.AppField);
        return def is null ? null : FieldValues.Read(def, a);
    }

    // ------------------------------------------------------------------ Simpro side

    private string? NormSimpro(FieldPlan f, string? raw)
    {
        var clean = Clean(raw);
        switch (f.Field.Type)
        {
            case SimproType.List:
                if (clean is null) return null;
                if (ValueNormalizer.IsNotApplicable(clean)) return ValueNormalizer.NotApplicable;
                if (SimproAliases.TryGetValue(f.Field.OptionSet!, out var map) && map.TryGetValue(clean, out var alias)) clean = alias;
                return ValueNormalizer.Canonical(clean, OptionsOf(f.Field.OptionSet), isGateStatus: false).Value;
            case SimproType.Number:
                return clean is not null && ValueNormalizer.TryParseDecimal(clean, out var n) ? n.ToString("0.############", CultureInfo.InvariantCulture) : clean;
            case SimproType.Date:
                if (clean is null) return null;
                var part = clean.Split(' ', 'T')[0];
                return DateOnly.TryParseExact(part, ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : clean;
            case SimproType.Discipline:
                return (clean ?? "SU").ToUpperInvariant();
            case SimproType.Plant or SimproType.Unit:
                return clean is not null && Guid.TryParse(clean, out var g) ? g.ToString() : clean;
            default:
                return clean;
        }
    }

    /// <summary>Pressure 0 in Simpro = not measured: compared as the G4 N2 Blanket value of the same side.</summary>
    private static void PressureAsBlanket(List<FieldPlan> fields, Func<FieldPlan, string?> get, Action<FieldPlan, string?> set)
    {
        var press = fields.First(f => f.Field.Key == "PRESS");
        if (get(press) is "0") set(press, get(fields.First(f => f.Field.Key == "G4N2")));
    }

    // ------------------------------------------------------------------ decide

    private static void Classify(AssetPlan plan)
    {
        foreach (var f in plan.Fields)
        {
            bool Eq(string? x, string? y) => string.Equals(x ?? "", y ?? "", StringComparison.Ordinal);
            f.Class = Eq(f.AppNorm, f.SimNorm) ? FieldClass.Same
                : f.Field.KeepIfBlank && f.AppNorm is null ? FieldClass.Keep
                : f.Bad is not null ? FieldClass.Invalid
                : f.Field.Type == SimproType.Reference ? FieldClass.Reference
                : !plan.HasBase || Eq(f.SimNorm, f.BaseNorm) ? FieldClass.Write
                : Eq(f.AppNorm, f.BaseNorm) ? FieldClass.SimproChanged
                : FieldClass.Conflict;
        }

        // Current Gate never goes back in Simpro on its own (e.g. a gate Simpro had while the old list said "Nuevo").
        var gate = plan.Fields.First(f => f.Field.Key == "GATE");
        if (gate.Class == FieldClass.Write && !plan.Forced && RankOf(gate.AppNorm) is { } to && RankOf(gate.SimNorm) is { } from && to < from)
            gate.Class = FieldClass.GateBack;

        if (plan.Forced)
        {
            foreach (var f in plan.Fields.Where(f => f.Class is FieldClass.SimproChanged or FieldClass.Conflict)) f.Class = FieldClass.Write;
        }
        else if (plan.IsHeld)
        {
            foreach (var f in plan.Fields.Where(f => f.Class == FieldClass.Write)) f.Class = FieldClass.Held;
        }

        // A write that would leave the stored text exactly as it is: nothing to do.
        foreach (var f in plan.Fields.Where(f => f.Class == FieldClass.Write))
        {
            var stored = f.Field.Type is SimproType.Plant or SimproType.Unit or SimproType.Priority or SimproType.Gate or SimproType.Bit ? f.SimNorm : f.SimRaw;
            if (string.Equals(f.AppOut ?? "", stored ?? "", StringComparison.Ordinal)) f.Class = FieldClass.Same;
        }
    }

    private static int? RankOf(string? code) =>
        int.TryParse(code, out var c) && SimproFields.GateOfCode(c) is { } g ? SimproFields.GateCodes[g].Rank : null;

    private IReadOnlyList<string> OptionsOf(string? set) => set is null ? [] : allOptions.GetValueOrDefault(set) ?? [];

    private static string? Clean(string? s) => ValueNormalizer.CleanText(s?.Replace('\t', ' '));
}
