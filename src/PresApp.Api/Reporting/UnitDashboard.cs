using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Excel;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Reporting;

/// <summary>
/// One activity of the dashboard (Walkdown, a gate or an E&amp;I step). <see cref="Code"/> is the short column title
/// ("Walkdown", "G1", "E1"). <see cref="NotApplicable"/> is only counted apart on the E&amp;I dashboard, where it is left
/// out of the completion %; the mechanical one keeps it in Other, like its Excel sheet.
/// </summary>
public sealed record ActivityRow(string Code, string Label, int Completed, int Pending, int Hold, int NotApplicable, int Other, int Total, decimal CompletionPct);

/// <summary>One discipline: total tags and, in the order of the activities, how many have each one completed.</summary>
public sealed record DisciplineRow(string Discipline, int Total, List<int> Done, int EuPreserved, decimal ProgressPct);

public sealed record CategoryAsset(Guid Id, string Code, string? Tag);

public sealed record CategoryRow(int Id, string? Code, string Label, string Section, string Scope, int Priority, List<string> Keywords, string? ActionBy,
    string? EmailAction, string? Recommendation, string? Note, bool IsActive, int Count, List<CategoryAsset> Assets);

/// <summary>
/// The dashboard of one unit for one scope: "mechanical" (static, rotating, package) or "ei" (Electrical &amp; Instrumentation).
/// E&amp;I: <see cref="Part"/> = the part counted ("electrical", "instrumentation" or "all"); <see cref="Parts"/> = the figures of
/// each part, whichever part is shown, and <see cref="WeightedPreservedPct"/> their % weighted by the unit's weights (parts
/// without applicable tags left out); null on the mechanical dashboard.
/// </summary>
public sealed record DashboardDto(
    int UnitId, string UnitName, string PlantName, string Scope, int TotalTags,
    List<ActivityRow> Activities, List<DisciplineRow> Disciplines,
    List<CategoryRow> Hold, int TotalHold, List<CategoryRow> NotApplicable, int TotalNotApplicable,
    int GrandTotal, int AvailableForPreservation, int Preserved, int ReadyForPreservation,
    Dictionary<string, int> ByCurrentGate, int DocumentsPending, int ElectricalInstrumentCount, int InactiveCount,
    int LineCount, decimal LineLengthM, int LinesBlanketed, string CategorySource, bool Grouped = false,
    string Part = "all", List<EiPartRow>? Parts = null, decimal? WeightedPreservedPct = null);

/// <summary>One part of the E&amp;I items: tags, not applicable, preserved, preserved ÷ (tags − not applicable), weight (%).</summary>
public sealed record EiPartRow(string Part, string Label, int Tags, int NotApplicable, int Preserved, decimal PreservedPct, int Weight);

/// <summary>
/// One category in the editor. Code is assigned by the app. Keywords and priority are optional: they only feed the
/// current (v3) Excel, which still classifies by keyword until the v4 export is in use.
/// </summary>
public sealed record SaveCategory(int? Id, string Label, string Section, int? Priority = null, List<string>? Keywords = null,
    string? ActionBy = null, string? EmailAction = null, string? Recommendation = null, string? Note = null, bool IsActive = true);

/// <summary>
/// The equipment behind one dashboard figure, so the register can show exactly those rows.
/// Mechanical cards: tags, preserved, ready, hold, na, available. E&amp;I cards: ei-tags, ei-preserved, ei-hold, ei-na,
/// ei-available. Both: category-{id}.
/// </summary>
public sealed record DashboardCardDto(string Card, string Label, int Count, List<Guid> AssetIds);

/// <summary>Category options of a unit for the asset page (active only).</summary>
public sealed record CategoryOption(int Id, string Code, string Label, string Section);

/// <summary>
/// The unit dashboards, computed exactly like the client Excel dashboard sheets so both always agree.
/// <list type="bullet">
/// <item><b>Mechanical</b> (sheet "Dashboard"): static and rotating scope (Electrical, Instrumentation and E&amp;I disciplines
/// excluded), Walkdown and G1–G4, "Completed*" counts as completed, "Ongoing" as pending. Preserved = G4 completed.
/// Categories: once any equipment of the unit has a category assigned (template v4), the assigned category counts;
/// until then, the keyword in Preservation Remarks with the lowest priority (template v3).</item>
/// <item><b>E&amp;I</b> (sheet "E&amp;I Dashboard"): the E&amp;I items, Walkdown and E1–E8. A "Not Applicable" step is left
/// out of the completion and progress %. Preserved = E1–E7 Completed or Not Applicable, at least one Completed (physically
/// preserved; E8 is the signed closing, and Mark as preserved stays the official closure).
/// Categories: the assigned E&amp;I category (CatE1, CatE2…).</item>
/// </list>
/// </summary>
public sealed class UnitDashboard(PresAppDbContext db)
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { "Electrical", "Instrumentation", "E&I" };
    private static readonly string[] MechanicalDisciplines = ["Static", "Rotating", "Package Mechanical"];

    /// <summary>E&amp;I disciplines, in the dashboard order (matched by the whole name, like the Excel sheet).</summary>
    public static readonly string[] EiDisciplines = ["Electrical", "Instrumentation", "E&I"];

    /// <summary>Names of the E&amp;I steps E1–E8 (stored in the G1–G8 status fields).</summary>
    public static readonly string[] EiSteps =
    [
        "Tag & Nameplate Check", "Motor Body Inspection", "Terminal Box Check", "Earthing Check",
        "Space Heater Test", "Shaft Rotation & Greasing", "IR / Winding Test", "Protective Covering"
    ];

    public const string EiCardPrefix = "ei-";

    /// <param name="grouped">"As the client": the parts of a split item are one tag, with the values of its client Excel row (GroupedRows).</param>
    /// <param name="part">E&amp;I only: electrical, instrumentation or both (the client Excel).</param>
    public async Task<DashboardDto> GetAsync(int unitId, CancellationToken ct, CategoryScope scope = CategoryScope.Mechanical, bool grouped = false, EiPart part = EiPart.All) =>
        (await ComputeAsync(unitId, scope, grouped, ct, part: part)).Dashboard;

    /// <summary>
    /// Equipment counted by one dashboard figure; same computation as the dashboard, so the counts always agree.
    /// Grouped, a split item counts once and its figure lists all its parts.
    /// </summary>
    public async Task<DashboardCardDto> CardAsync(int unitId, string card, CancellationToken ct, bool grouped = false, EiPart part = EiPart.All)
    {
        var key = (card ?? "").Trim().ToLowerInvariant();
        var scope = key.StartsWith(EiCardPrefix, StringComparison.Ordinal) ? CategoryScope.ElectricalInstrument : CategoryScope.Mechanical;
        if (key.StartsWith("category-", StringComparison.Ordinal) && int.TryParse(key["category-".Length..], out var categoryId))
            scope = await db.DashboardCategories.Where(c => c.Id == categoryId && c.UnitId == unitId).Select(c => (CategoryScope?)c.Scope).FirstOrDefaultAsync(ct)
                    ?? throw ApiProblem.NotFound("Dashboard figure not found.");
        var ei = scope == CategoryScope.ElectricalInstrument;
        if (!ei) part = EiPart.All;
        var (dashboard, cards) = await ComputeAsync(unitId, scope, grouped, ct, part: part);
        if (!cards.TryGetValue(key, out var items)) throw ApiProblem.NotFound("Dashboard figure not found.");
        var ids = items.SelectMany(i => i.Ids).ToList();
        var name = EiParts.Label(part);
        var label = (ei ? key[EiCardPrefix.Length..] : key) switch
        {
            "tags" => ei ? $"{name} tags" : "Equipment tags",
            "preserved" => ei ? $"{name} preserved" : "Preserved",
            "ready" => "Ready for preservation",
            "hold" => ei ? $"{name} on hold" : "On hold",
            "na" => ei ? $"{name} not applicable" : "Not applicable",
            "available" => ei ? $"{name} available" : "Available",
            _ => dashboard.Hold.Concat(dashboard.NotApplicable).Where(c => $"category-{c.Id}" == key)
                .Select(c => $"{(c.Section == "Hold" ? "Hold" : "N/A")} {c.Code}: {c.Label}".Replace("  ", " ")).FirstOrDefault() ?? "Category"
        };
        return new DashboardCardDto(key, label, items.Count, ids);
    }

    /// <summary>
    /// Figures of one scope of a unit for the daily report, counted exactly like the dashboard: totals, one row per
    /// discipline (E&amp;I as one row) and the tags preserved, keyed by their first asset. <paramref name="valueAt"/> gives the
    /// statuses at an earlier moment; categories (hold, not applicable) are always the current ones.
    /// </summary>
    /// <param name="part">E&amp;I items counted: electrical only (one "Electrical" row) or all (an "Electrical" and an
    /// "Instrumentation" row).</param>
    internal async Task<ReportFigures> ReportFiguresAsync(int unitId, CategoryScope scope, bool grouped, Func<Guid, string, string?, string?>? valueAt, CancellationToken ct,
        EiPart part = EiPart.All)
    {
        var ei = scope == CategoryScope.ElectricalInstrument;
        var (d, cards) = await ComputeAsync(unitId, scope, grouped, ct, valueAt, ei ? part : EiPart.All);
        var prefix = ei ? EiCardPrefix : "";
        var items = cards[prefix + "tags"];
        var preserved = cards[prefix + "preserved"];
        var preservedIds = preserved.Select(i => i.Id).ToHashSet();
        var holdIds = cards[prefix + "hold"].Select(i => i.Id).ToHashSet();
        var naIds = cards[prefix + "na"].Select(i => i.Id).ToHashSet();

        var groups = new List<(string Name, List<Item> Rows, decimal Pct)>();
        if (ei)
            foreach (var p in part == EiPart.All ? [EiPart.Electrical, EiPart.Instrumentation] : new[] { part })
            {
                var inPart = items.Where(a => EiParts.Of(a.Discipline) == p).ToList();
                groups.Add((EiParts.Label(p), inPart, Pct(inPart.Sum(a => a.Steps.Count(IsCompleted)), inPart.Sum(a => a.Steps.Count(s => !IsNotApplicable(s))))));
            }
        else
        {
            foreach (var name in MechanicalDisciplines)
                groups.Add((name, items.Where(a => a.Discipline.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList(),
                    d.Disciplines.First(r => r.Discipline == name).ProgressPct));
            var other = items.Where(a => !MechanicalDisciplines.Any(n => a.Discipline.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
            groups.Add(("Other", other, Pct(other.Sum(a => a.Steps.Count(IsCompleted)), other.Count * 5)));
        }
        var rows = groups.Where(g => g.Rows.Count > 0).Select(g =>
        {
            var na = g.Rows.Count(a => naIds.Contains(a.Id));
            return new DailyDisciplineRow(g.Name, g.Rows.Count, na, g.Rows.Count(a => preservedIds.Contains(a.Id)), g.Rows.Count(a => holdIds.Contains(a.Id)),
                na == g.Rows.Count ? null : g.Pct);
        }).ToList();
        // Each preserved tag with the name of its row in the table, so the report can list them by discipline.
        var rowOf = new Dictionary<Guid, string>();
        foreach (var g in groups)
            foreach (var a in g.Rows) rowOf.TryAdd(a.Id, g.Name);
        return new ReportFigures(d.TotalTags, d.TotalNotApplicable, d.Preserved, d.TotalHold, rows,
            preserved.ToDictionary(i => i.Ids.Min(), i => new PreservedTag(i.Tag ?? i.Code, rowOf.GetValueOrDefault(i.Id, "Other"))));
    }

    /// <summary>
    /// Statuses of one tag, in the order of the activities of its dashboard: one asset, or (grouped) a split item whose
    /// <see cref="Ids"/> are all its parts and whose values are those of its client Excel row.
    /// </summary>
    private sealed record Item(Guid Id, string Code, string? Tag, string Discipline, string? Eu, string? Ready, string? Remarks, int? CategoryId, string?[] Steps,
        IReadOnlyList<Guid> Ids);

    /// <param name="valueAt">Statuses at an earlier moment (asset, field, current value → value then); null = the current ones.</param>
    /// <param name="part">E&amp;I only: the items counted (by discipline, <see cref="EiParts.Of"/>); ignored for mechanical.</param>
    private async Task<(DashboardDto Dashboard, Dictionary<string, List<Item>> Cards)> ComputeAsync(int unitId, CategoryScope scope, bool grouped, CancellationToken ct,
        Func<Guid, string, string?, string?>? valueAt = null, EiPart part = EiPart.All)
    {
        string? V(Guid id, string field, string? current) => valueAt is null ? current : valueAt(id, field, current);
        var unit = await db.Units.AsNoTracking().Include(u => u.Plant).FirstOrDefaultAsync(u => u.Id == unitId, ct)
                   ?? throw ApiProblem.NotFound("Unit not found.");
        var ei = scope == CategoryScope.ElectricalInstrument;
        if (!ei) part = EiPart.All;
        var all = await db.Assets.AsNoTracking().Where(a => a.UnitId == unitId && a.Type != AssetType.Pipeline)
            .Select(a => new
            {
                a.Id, a.Code, a.Tag, a.ClientTag, a.IsActive, a.Type, Discipline = a.Discipline.Name,
                Walkdown = a.Preservation!.EmWalkdownStatus, Ready = a.Preservation.ReadyForPreservation, a.Preservation.G1Status, a.Preservation.G2Status, a.Preservation.G3Status,
                a.Preservation.G4Status, a.Preservation.G5Status, a.Preservation.G6Status, a.Preservation.G7Status, a.Preservation.G8Status,
                Eu = a.Preservation.PreservedByEu, Remarks = a.Preservation.PreservationRemarks,
                CategoryId = a.Preservation.DashboardCategoryId,
                Gate = (CurrentGate?)a.Preservation.CurrentGate
            })
            .ToListAsync(ct);
        var categories = await LoadCategoriesAsync(unitId, scope, ct);

        // Scope: E&I items for the E&I dashboard; for the mechanical one, everything else outside the E&I disciplines.
        var scope0 = all.Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Tag)
                                   && (ei ? a.Type == AssetType.ElectricalInstrument : a.Type != AssetType.ElectricalInstrument && !Excluded.Contains(a.Discipline.Trim())))
            .ToList();
        var fields = StatusHistory.Fields;   // walkdown, G1–G8: the order of the step arrays
        var single = scope0.Select(a =>
            {
                string?[] current = [a.Walkdown, a.G1Status, a.G2Status, a.G3Status, a.G4Status, a.G5Status, a.G6Status, a.G7Status, a.G8Status];
                var steps = current.Take(ei ? 9 : 5).Select((v, i) => V(a.Id, fields[i], v)).ToArray();
                return new Item(a.Id, a.Code, a.Tag, a.Discipline, a.Eu, a.Ready, a.Remarks, a.CategoryId, steps, [a.Id]);
            })
            .ToList();
        // "As the client": the parts of a split item are one tag, with the values of its row in the client Excel.
        var clientTags = scope0.Where(a => a.ClientTag is not null).ToDictionary(a => a.Id, a => (a.ClientTag!, a.Type));
        var everything = grouped ? Join(single, clientTags, categories) : single;
        // E&I: only the part asked for (a split item goes by the discipline of its first part).
        var items = everything.Where(a => EiParts.Contains(part, a.Discipline)).ToList();
        var total = items.Count;

        (string Code, string Label)[] activityNames = ei
            ? [("Walkdown", "Walkdown"), .. EiSteps.Select((s, i) => ($"E{i + 1}", $"E{i + 1} - {s}"))]
            : [("Walkdown", "Walkdown"), ("G1", "G1 - Product Drain"), ("G2", "G2 - Dew-Point"), ("G3", "G3 - O2%"), ("G4", "G4 - N2 Blanketing")];
        var activities = activityNames.Select((n, i) =>
        {
            var values = items.Select(a => a.Steps[i]).ToList();
            var done = values.Count(IsCompleted);
            var pending = values.Count(v => Is(v, "Ongoing"));
            var hold = values.Count(v => Is(v, "Hold"));
            var na = ei ? values.Count(IsNotApplicable) : 0;
            return new ActivityRow(n.Code, n.Label, done, pending, hold, na, total - done - pending - hold - na, total, Pct(done, total - na));
        }).ToList();

        var disciplines = (ei ? EiDisciplines.Where(d => EiParts.Contains(part, d)) : MechanicalDisciplines).Select(d =>
        {
            var rows = items.Where(a => ei ? string.Equals(a.Discipline.Trim(), d, StringComparison.OrdinalIgnoreCase)
                : a.Discipline.Contains(d, StringComparison.OrdinalIgnoreCase)).ToList();
            var done = Enumerable.Range(0, activityNames.Length).Select(i => rows.Count(a => IsCompleted(a.Steps[i]))).ToList();
            // Mechanical: every activity counts (Excel: done ÷ tags × 5). E&I: Not Applicable steps are left out.
            var applicable = ei ? rows.Sum(a => a.Steps.Count(s => !IsNotApplicable(s))) : rows.Count * activityNames.Length;
            return new DisciplineRow(d, rows.Count, done, rows.Count(a => Is(a.Eu, "Yes")), Pct(done.Sum(), applicable));
        }).ToList();

        // Mechanical v3 files classify by keyword until the first category is assigned; E&I always uses the assigned one.
        var assigned = ei || all.Any(a => a.Type == AssetType.Equipment && a.CategoryId is not null);
        var activeIds = categories.Select(c => c.Id).ToHashSet();
        var matcher = new CategoryMatcher(categories);
        int? CategoryOf(Item a) => assigned ? (a.CategoryId is { } id && activeIds.Contains(id) ? id : null) : matcher.Match(a.Remarks)?.Id;
        bool IsPreserved(Item a) => ei ? AllStepsClosed(a.Steps[1..8]) : IsCompleted(a.Steps[4]);
        var byCategory = items
            .Select(a => (Asset: a, CategoryId: CategoryOf(a)))
            .Where(x => x.CategoryId is not null)
            .GroupBy(x => x.CategoryId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Asset).OrderBy(x => x.Tag ?? x.Code, NaturalComparer.Instance).ToList());
        List<CategoryRow> Section(CategorySection s) => categories.Where(c => c.Section == s).OrderBy(c => c.SortOrder).ThenBy(c => c.Priority)
            .Select(c => ToRow(c, (byCategory.GetValueOrDefault(c.Id) ?? []).Select(x => new CategoryAsset(x.Id, x.Code, x.Tag)).ToList())).ToList();
        var hold = Section(CategorySection.Hold);
        var na = Section(CategorySection.NotApplicable);
        var totalHold = hold.Sum(c => c.Count);
        var totalNa = na.Sum(c => c.Count);

        var active = all.Where(a => a.IsActive).ToList();
        // Grouped, a split item is one tag here too (its highest Current Gate).
        string KeyOf(Guid id, AssetType type, string? clientTag) => grouped && clientTag is not null ? $"{type}|{ValueNormalizer.ListKey(clientTag)}" : id.ToString();
        var gateField = PreservationFields.Find(nameof(AssetPreservation.CurrentGate))!;
        var gates = active.Where(a => a.Type == (ei ? AssetType.ElectricalInstrument : AssetType.Equipment) && (!ei || EiParts.Contains(part, a.Discipline)))
            .GroupBy(a => KeyOf(a.Id, a.Type, a.ClientTag))
            .Select(g => GroupedRows.Combine(gateField, g.Select(a => (a.Tag ?? a.Code, (string?)FieldValues.CurrentGateText(a.Gate ?? CurrentGate.New))).ToList())!)
            .ToList();
        // Lines stay out of the Excel dashboard figures; the unit page shows them on their own.
        var lines = await db.Assets.AsNoTracking().Where(a => a.UnitId == unitId && a.IsActive && a.Type == AssetType.Pipeline)
            .Select(a => new { Length = a.Pipeline == null ? null : a.Pipeline.LengthM, a.Preservation!.G4Status }).ToListAsync(ct);
        var pendingDocs = await db.GateDocuments.CountAsync(d => d.Asset.UnitId == unitId && d.Asset.IsActive && d.Status == DocumentationStatus.Pending, ct);

        // Tags behind each key figure (the register filters on their assets; counts equal the figures by construction).
        var holdItems = hold.SelectMany(c => byCategory.GetValueOrDefault(c.Id) ?? []).ToList();
        var naItems = na.SelectMany(c => byCategory.GetValueOrDefault(c.Id) ?? []).ToList();
        var categorized = holdItems.Concat(naItems).Select(a => a.Id).ToHashSet();
        var ready = items.Where(a => Is(a.Ready, "Yes")).ToList();
        var preserved = items.Where(IsPreserved).ToList();

        // E&I: the figures of each part, whichever is shown, and their % weighted by the unit's weights.
        List<EiPartRow>? parts = null;
        decimal? weighted = null;
        if (ei)
        {
            var settings = await EiSettings.ReadAsync(db, unitId, ct);
            var naCategories = na.Select(c => c.Id).ToHashSet();
            parts = [.. new[] { EiPart.Electrical, EiPart.Instrumentation }.Select(p =>
            {
                var rows = everything.Where(a => EiParts.Of(a.Discipline) == p).ToList();
                var notApplicable = rows.Count(a => CategoryOf(a) is { } c && naCategories.Contains(c));
                var done = rows.Count(IsPreserved);
                return new EiPartRow(EiParts.Key(p), EiParts.Label(p), rows.Count, notApplicable, done, Pct(done, rows.Count - notApplicable),
                    p == EiPart.Electrical ? settings.ElectricalWeight : settings.InstrumentationWeight);
            })];
            var counted = parts.Where(p => p.Tags > p.NotApplicable && p.Weight > 0).ToList();
            weighted = counted.Count == 0 ? null : Math.Round(counted.Sum(p => p.Weight * p.PreservedPct) / counted.Sum(p => p.Weight), 1);
        }
        var prefix = ei ? EiCardPrefix : "";
        var cards = new Dictionary<string, List<Item>>
        {
            [prefix + "tags"] = items,
            [prefix + "preserved"] = preserved,
            [prefix + "hold"] = holdItems,
            [prefix + "na"] = naItems,
            [prefix + "available"] = items.Where(a => !categorized.Contains(a.Id)).ToList(),
        };
        if (!ei) cards["ready"] = ready;
        foreach (var c in hold.Concat(na)) cards[$"category-{c.Id}"] = byCategory.GetValueOrDefault(c.Id) ?? [];

        // Ready for preservation = the "Ready For Preservation" column = Yes, like the Excel Dashboard sheet.
        var dto = new DashboardDto(unit.Id, unit.Name, unit.Plant.Name, CategoryScopes.Key(scope), total, activities, disciplines,
            hold, totalHold, na, totalNa, totalHold + totalNa, total - totalHold - totalNa,
            preserved.Count, ready.Count,
            gates.GroupBy(g => g)
                .OrderBy(g => g.Key == "New" ? 0 : g.Key == "Removed from Site" ? 99 : g.Key[1] - '0')
                .ToDictionary(g => g.Key, g => g.Count()),
            pendingDocs, active.Where(a => a.Type == AssetType.ElectricalInstrument).Select(a => KeyOf(a.Id, a.Type, a.ClientTag)).Distinct().Count(), all.Count(a => !a.IsActive),
            lines.Count, lines.Sum(l => l.Length ?? 0), lines.Count(l => IsCompleted(l.G4Status)),
            assigned ? "Assigned" : "Keywords", grouped, EiParts.Key(part), parts, weighted);
        return (dto, cards);
    }

    /// <summary>
    /// "As the client": the parts of a split item (same client tag and type) become one item with the values of its row
    /// in the client Excel (GroupedRows): the most advanced status of each activity, the category of the row, the
    /// remarks joined. Any other asset stays as it is. In the order given (a split item where its first part is).
    /// </summary>
    private static List<Item> Join(List<Item> items, Dictionary<Guid, (string ClientTag, AssetType Type)> clientTags, List<DashboardCategory> categories)
    {
        var byId = categories.ToDictionary(c => c.Id);
        var remarks = PreservationFields.Find(nameof(AssetPreservation.PreservationRemarks))!;
        return items
            .GroupBy(i => clientTags.TryGetValue(i.Id, out var c) ? $"{c.Type}|{ValueNormalizer.ListKey(c.ClientTag)}" : i.Id.ToString())
            .Select(g =>
            {
                var parts = g.ToList();
                if (parts.Count == 1) return parts[0];
                var first = parts[0];
                var category = GroupedRows.Category(parts.Select(p => p.CategoryId is { } id ? byId.GetValueOrDefault(id) : null).ToList(),
                    c => c.Section == CategorySection.Hold);
                return new Item(first.Id, first.Code, clientTags[first.Id].ClientTag, first.Discipline,
                    GroupedRows.Status(parts.Select(p => p.Eu).ToList()), GroupedRows.Status(parts.Select(p => p.Ready).ToList()),
                    GroupedRows.Combine(remarks, parts.Select(p => (p.Tag ?? p.Code, p.Remarks)).ToList()),
                    category?.Id,
                    first.Steps.Select((_, i) => GroupedRows.Status(parts.Select(p => p.Steps[i]).ToList())).ToArray(),
                    parts.SelectMany(p => p.Ids).ToList());
            })
            .ToList();
    }

    /// <summary>E&amp;I preserved: steps E1–E7 Completed or Not Applicable, at least one Completed (22-nov: E8, the signed closing, is not needed).</summary>
    private static bool AllStepsClosed(IReadOnlyList<string?> steps) =>
        steps.All(s => IsCompleted(s) || IsNotApplicable(s)) && steps.Any(IsCompleted);

    private static CategoryRow ToRow(DashboardCategory c, List<CategoryAsset> assets) =>
        new(c.Id, c.Code, c.Label, SectionKey(c.Section), CategoryScopes.Key(c.Scope), c.Priority, c.Keywords.Select(k => k.Keyword).ToList(),
            c.ActionBy, c.EmailAction, c.Recommendation, c.Note, c.IsActive, assets.Count, assets);

    // ------------------------------------------------------------------ categories

    /// <summary>All categories of one catalogue of the unit for the editor: active ones in dashboard order with their counts, then inactive ones.</summary>
    public async Task<List<CategoryRow>> CategoriesAsync(int unitId, CategoryScope scope, CancellationToken ct)
    {
        var d = await GetAsync(unitId, ct, scope);
        var inactive = await db.DashboardCategories.AsNoTracking().Include(c => c.Keywords)
            .Where(c => c.UnitId == unitId && c.Scope == scope && !c.IsActive).OrderBy(c => c.Section).ThenBy(c => c.SortOrder).ToListAsync(ct);
        return d.Hold.Concat(d.NotApplicable).Concat(inactive.Select(c => ToRow(c, []))).ToList();
    }

    /// <summary>Active categories of one catalogue of the unit, for the category dropdown of the asset page.</summary>
    public async Task<List<CategoryOption>> OptionsAsync(int unitId, CategoryScope scope, CancellationToken ct)
    {
        var rows = await db.DashboardCategories.AsNoTracking().Where(c => c.UnitId == unitId && c.Scope == scope && c.IsActive && c.Code != null)
            .OrderBy(c => c.Section).ThenBy(c => c.SortOrder).ToListAsync(ct);
        return rows.Select(c => new CategoryOption(c.Id, c.Code!, c.Label, SectionKey(c.Section))).ToList();
    }

    /// <summary>
    /// Saves one catalogue of the unit (the editor always sends the whole catalogue, inactive ones included).
    /// Categories are never deleted: one left out, or sent with IsActive = false, is deactivated, and only when no
    /// equipment has it. New categories, and old ones without a code, get the next free code of their section
    /// (Cat1, Cat2… or CatE1, CatE2… for E&amp;I); a code is never reused because deactivated categories keep theirs.
    /// The other catalogue is never touched.
    /// </summary>
    public async Task<List<CategoryRow>> SaveCategoriesAsync(int unitId, CategoryScope scope, List<SaveCategory> request, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit not found.");
        var clean = new List<(SaveCategory Source, string Label, CategorySection Section, List<string> Keywords)>();
        foreach (var c in request)
        {
            var label = ValueNormalizer.CleanText(c.Label) ?? throw ApiProblem.Invalid("Every category needs a description.");
            if (label.Length > DashboardCategory.ShortText) throw ApiProblem.Invalid($"\"{label[..40]}…\" is longer than 200 characters.");
            if (!TryParseSection(c.Section, out var section)) throw ApiProblem.Invalid($"Section of \"{label}\" must be Hold or Not Applicable.");
            if (c.Priority is < 1 or > 999) throw ApiProblem.Invalid($"Keyword priority of \"{label}\" must be between 1 and 999.");
            var keywords = (c.Keywords ?? []).Select(ValueNormalizer.CleanText).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (keywords.Any(k => k.Length > 200)) throw ApiProblem.Invalid($"A keyword of \"{label}\" is longer than 200 characters.");
            if (clean.Any(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))) throw ApiProblem.Invalid($"\"{label}\" appears twice.");
            if ((c.ActionBy?.Length ?? 0) > DashboardCategory.ShortText || (c.EmailAction?.Length ?? 0) > DashboardCategory.ShortText
                || (c.Recommendation?.Length ?? 0) > DashboardCategory.LongText || (c.Note?.Length ?? 0) > DashboardCategory.LongText)
                throw ApiProblem.Invalid($"Action By and Email Action allow 200 characters; EnerMech Recommendation and Note 1,000 (\"{label}\").");
            clean.Add((c, label, section, keywords));
        }

        var existing = await db.DashboardCategories.Include(c => c.Keywords).Where(c => c.UnitId == unitId && c.Scope == scope).ToListAsync(ct);
        var unknown = clean.FirstOrDefault(c => c.Source.Id is { } id && existing.All(e => e.Id != id));
        if (unknown.Source is not null) throw ApiProblem.Invalid($"\"{unknown.Label}\" is not a category of this list. Reload the page.");

        // Equipment per category: a category in use cannot be deactivated nor moved to the other section.
        var existingIds = existing.Select(e => e.Id).ToList();
        var inUse = await db.Preservations.Where(p => p.DashboardCategoryId != null && existingIds.Contains(p.DashboardCategoryId.Value))
            .GroupBy(p => p.DashboardCategoryId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        foreach (var e in existing)
        {
            var sent = clean.FirstOrDefault(c => c.Source.Id == e.Id);
            var count = inUse.GetValueOrDefault(e.Id);
            if (count == 0) continue;
            if (sent.Source is null || !sent.Source.IsActive)
                throw ApiProblem.Invalid($"{e.Code ?? e.Label} \"{sent.Label ?? e.Label}\" is assigned to {count} equipment. Move them to another category before deactivating it.");
            if (sent.Section != e.Section)
                throw ApiProblem.Invalid($"{e.Code ?? e.Label} \"{sent.Label}\" is assigned to {count} equipment, so it cannot change section.");
        }

        foreach (var e in existing.Where(e => clean.All(c => c.Source.Id != e.Id))) e.IsActive = false;

        var order = 0;
        foreach (var (source, label, section, keywords) in clean)
        {
            var entity = existing.FirstOrDefault(e => e.Id == source.Id);
            if (entity is null) { db.DashboardCategories.Add(entity = new DashboardCategory { UnitId = unitId, Scope = scope }); existing.Add(entity); }
            if (entity.Section != section && entity.Id != 0) entity.Code = null;   // a code belongs to its section
            entity.Label = label;
            entity.Section = section;
            entity.Priority = source.Priority ?? (entity.Priority > 0 ? entity.Priority : 100 + order);
            entity.SortOrder = ++order;
            entity.ActionBy = ValueNormalizer.CleanText(source.ActionBy);
            entity.EmailAction = ValueNormalizer.CleanText(source.EmailAction);
            entity.Recommendation = ValueNormalizer.CleanText(source.Recommendation);
            entity.Note = ValueNormalizer.CleanText(source.Note);
            entity.IsActive = source.IsActive;
            db.CategoryKeywords.RemoveRange(entity.Keywords.Where(k => !keywords.Contains(k.Keyword, StringComparer.OrdinalIgnoreCase)));
            foreach (var k in keywords.Where(k => !entity.Keywords.Any(e => string.Equals(e.Keyword, k, StringComparison.OrdinalIgnoreCase))))
                entity.Keywords.Add(new CategoryKeyword { Keyword = k });
        }
        AssignCodes(existing);
        await db.SaveChangesAsync(ct);
        return await CategoriesAsync(unitId, scope, ct);
    }

    /// <summary>
    /// Gives every category without a code one: the number in its label when free ("Cat 2: Missing drawing" → Cat2,
    /// "CatE 2: Motor removed" → CatE2), otherwise the lowest free number of its catalogue and section. Codes of inactive
    /// categories stay taken.
    /// </summary>
    public static void AssignCodes(IReadOnlyCollection<DashboardCategory> unitCategories)
    {
        foreach (var group in unitCategories.GroupBy(c => (c.Scope, c.Section)))
        {
            var scope = group.Key.Scope;
            var prefix = CategoryScopes.CodePrefix(scope);
            var taken = group.Select(c => CodeNumber(c.Code, scope)).Where(n => n > 0).ToHashSet();
            var pending = group.Where(c => string.IsNullOrEmpty(c.Code)).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
            foreach (var c in pending.ToList())
            {
                if (LabelNumber(c.Label, scope) is not (> 0 and var n) || !taken.Add(n)) continue;
                c.Code = $"{prefix}{n}";
                pending.Remove(c);
            }
            // Lowest number not taken. Codes are never freed (categories are only deactivated), so none is reused.
            var next = 1;
            foreach (var c in pending)
            {
                while (!taken.Add(next)) next++;
                c.Code = $"{prefix}{next}";
            }
        }
    }

    /// <summary>Number written after "Cat" (or "CatE" for E&amp;I) at the start of a label ("Cat 2 :Level…" → 2), or 0.</summary>
    private static int LabelNumber(string label, CategoryScope scope)
    {
        var t = label.TrimStart();
        var prefix = CategoryScopes.CodePrefix(scope);
        if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        var digits = new string(t[prefix.Length..].TrimStart().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    /// <summary>Number of "Cat12" (mechanical) or "CatE12" (E&amp;I); 0 when the code is not one of that catalogue.</summary>
    public static int CodeNumber(string? code, CategoryScope scope = CategoryScope.Mechanical)
    {
        var prefix = CategoryScopes.CodePrefix(scope);
        return code is not null && code.Length > prefix.Length && code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && code[prefix.Length..].All(char.IsAsciiDigit) && int.TryParse(code[prefix.Length..], out var n) ? n : 0;
    }

    /// <summary>"Cat 1", "cat1", " CAT1 " → "Cat1"; "cat e2", "CATE2" → "CatE2"; anything else → null.</summary>
    public static string? NormalizeCode(string? code)
    {
        var compact = (code ?? "").Replace(" ", "", StringComparison.Ordinal);
        if (CodeNumber(compact, CategoryScope.ElectricalInstrument) is > 0 and var e) return $"CatE{e}";
        return CodeNumber(compact) is > 0 and var n ? $"Cat{n}" : null;
    }

    /// <summary>Catalogue a code belongs to: CatE… is E&amp;I, Cat… mechanical.</summary>
    public static CategoryScope ScopeOfCode(string code) =>
        CodeNumber(code, CategoryScope.ElectricalInstrument) > 0 ? CategoryScope.ElectricalInstrument : CategoryScope.Mechanical;

    private Task<List<DashboardCategory>> LoadCategoriesAsync(int unitId, CategoryScope scope, CancellationToken ct) =>
        db.DashboardCategories.AsNoTracking().Include(c => c.Keywords).Where(c => c.UnitId == unitId && c.Scope == scope && c.IsActive).ToListAsync(ct);

    public static string SectionKey(CategorySection s) => s == CategorySection.Hold ? "Hold" : "NotApplicable";

    private static bool TryParseSection(string? s, out CategorySection section)
    {
        var k = ValueNormalizer.ListKey(s ?? "");
        section = k is "notapplicable" or "n/a" or "na" ? CategorySection.NotApplicable : CategorySection.Hold;
        return k is "hold" or "notapplicable" or "n/a" or "na";
    }

    // Same tests as the Excel COUNTIFS criteria: "Completed*" (begins with), "Ongoing", "Hold", "Not Applicable" (whole value, any case).
    private static bool IsCompleted(string? v) => v is not null && v.TrimStart().StartsWith("Completed", StringComparison.OrdinalIgnoreCase);
    private static bool IsNotApplicable(string? v) => Is(v, ValueNormalizer.NotApplicable);
    private static bool Is(string? v, string expected) => string.Equals(v?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    private static decimal Pct(int part, int whole) => whole <= 0 ? 0 : Math.Round(100m * part / whole, 1);
}
