using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Reporting;

public sealed record ActivityRow(string Label, int Completed, int Pending, int Hold, int Other, int Total, decimal CompletionPct);

public sealed record DisciplineRow(string Discipline, int Total, int Walkdown, int G1, int G2, int G3, int G4, int EuPreserved, decimal ProgressPct);

public sealed record CategoryAsset(Guid Id, string Code, string? Tag);

public sealed record CategoryRow(int Id, string? Code, string Label, string Section, int Priority, List<string> Keywords, string? ActionBy,
    string? EmailAction, string? Recommendation, string? Note, bool IsActive, int Count, List<CategoryAsset> Assets);

public sealed record DashboardDto(
    int UnitId, string UnitName, string PlantName, int TotalTags,
    List<ActivityRow> Activities, List<DisciplineRow> Disciplines,
    List<CategoryRow> Hold, int TotalHold, List<CategoryRow> NotApplicable, int TotalNotApplicable,
    int GrandTotal, int AvailableForPreservation, int Preserved, int ReadyForPreservation,
    Dictionary<string, int> ByCurrentGate, int DocumentsPending, int ElectricalInstrumentCount, int InactiveCount,
    int LineCount, decimal LineLengthM, int LinesBlanketed, string CategorySource);

/// <summary>
/// One category in the editor. Code is assigned by the app. Keywords and priority are optional: they only feed the
/// current (v3) Excel, which still classifies by keyword until the v4 export is in use.
/// </summary>
public sealed record SaveCategory(int? Id, string Label, string Section, int? Priority = null, List<string>? Keywords = null,
    string? ActionBy = null, string? EmailAction = null, string? Recommendation = null, string? Note = null, bool IsActive = true);

/// <summary>Category options of a unit for the asset page (active only).</summary>
public sealed record CategoryOption(int Id, string Code, string Label, string Section);

/// <summary>
/// The unit dashboard, computed exactly like the client Excel Dashboard sheet so both always agree:
/// static and rotating scope (Electrical, Instrumentation and E&amp;I excluded), "Completed*" counts as completed,
/// "Ongoing" as pending. Categories: once any asset of the unit has a category assigned (template v4), the assigned
/// category counts; until then, the keyword in Preservation Remarks with the lowest priority (template v3).
/// </summary>
public sealed class UnitDashboard(PresAppDbContext db)
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { "Electrical", "Instrumentation", "E&I" };
    private static readonly string[] Disciplines = ["Static", "Rotating", "Package Mechanical"];

    public async Task<DashboardDto> GetAsync(int unitId, CancellationToken ct)
    {
        var unit = await db.Units.AsNoTracking().Include(u => u.Plant).FirstOrDefaultAsync(u => u.Id == unitId, ct)
                   ?? throw ApiProblem.NotFound("Unit not found.");
        var all = await db.Assets.AsNoTracking().Where(a => a.UnitId == unitId && a.Type != AssetType.Pipeline)
            .Select(a => new
            {
                a.Id, a.Code, a.Tag, a.IsActive, a.Type, Discipline = a.Discipline.Name,
                Walkdown = a.Preservation!.EmWalkdownStatus, a.Preservation.G1Status, a.Preservation.G2Status, a.Preservation.G3Status,
                a.Preservation.G4Status, Eu = a.Preservation.PreservedByEu, Remarks = a.Preservation.PreservationRemarks,
                CategoryId = a.Preservation.DashboardCategoryId,
                Gate = (CurrentGate?)a.Preservation.CurrentGate
            })
            .ToListAsync(ct);
        var categories = await LoadCategoriesAsync(unitId, ct);

        var scope = all.Where(a => a.IsActive && !Excluded.Contains(a.Discipline.Trim()) && !string.IsNullOrWhiteSpace(a.Tag)).ToList();
        var total = scope.Count;

        ActivityRow Row(string label, IEnumerable<string?> values)
        {
            var list = values.ToList();
            var done = list.Count(IsCompleted);
            var pending = list.Count(v => Is(v, "Ongoing"));
            var hold = list.Count(v => Is(v, "Hold"));
            return new ActivityRow(label, done, pending, hold, total - done - pending - hold, total, Pct(done, total));
        }

        var activities = new List<ActivityRow>
        {
            Row("Walkdown", scope.Select(a => a.Walkdown)),
            Row("G1 - Product Drain", scope.Select(a => a.G1Status)),
            Row("G2 - Dew-Point", scope.Select(a => a.G2Status)),
            Row("G3 - O2%", scope.Select(a => a.G3Status)),
            Row("G4 - N2 Blanketing", scope.Select(a => a.G4Status)),
        };

        var disciplines = Disciplines.Select(d =>
        {
            var rows = scope.Where(a => a.Discipline.Contains(d, StringComparison.OrdinalIgnoreCase)).ToList();
            int c = rows.Count, w = rows.Count(a => IsCompleted(a.Walkdown)), g1 = rows.Count(a => IsCompleted(a.G1Status)),
                g2 = rows.Count(a => IsCompleted(a.G2Status)), g3 = rows.Count(a => IsCompleted(a.G3Status)), g4 = rows.Count(a => IsCompleted(a.G4Status));
            return new DisciplineRow(d, c, w, g1, g2, g3, g4, rows.Count(a => Is(a.Eu, "Yes")), c == 0 ? 0 : Pct(w + g1 + g2 + g3 + g4, c * 5));
        }).ToList();

        var assigned = all.Any(a => a.CategoryId is not null);
        var activeIds = categories.Select(c => c.Id).ToHashSet();
        var matcher = new CategoryMatcher(categories);
        var byCategory = scope
            .Select(a => (Asset: a, CategoryId: assigned ? (a.CategoryId is { } id && activeIds.Contains(id) ? id : (int?)null) : matcher.Match(a.Remarks)?.Id))
            .Where(x => x.CategoryId is not null)
            .GroupBy(x => x.CategoryId!.Value).ToDictionary(g => g.Key, g => g.Select(x => new CategoryAsset(x.Asset.Id, x.Asset.Code, x.Asset.Tag))
                .OrderBy(x => x.Tag ?? x.Code, NaturalComparer.Instance).ToList());
        List<CategoryRow> Section(CategorySection s) => categories.Where(c => c.Section == s).OrderBy(c => c.SortOrder).ThenBy(c => c.Priority)
            .Select(c =>
            {
                var assets = byCategory.GetValueOrDefault(c.Id) ?? [];
                return ToRow(c, assets);
            }).ToList();
        var hold = Section(CategorySection.Hold);
        var na = Section(CategorySection.NotApplicable);
        var totalHold = hold.Sum(c => c.Count);
        var totalNa = na.Sum(c => c.Count);

        var active = all.Where(a => a.IsActive).ToList();
        var equipment = active.Where(a => a.Type == AssetType.Equipment).ToList();
        // Lines stay out of the Excel dashboard figures; the unit page shows them on their own.
        var lines = await db.Assets.AsNoTracking().Where(a => a.UnitId == unitId && a.IsActive && a.Type == AssetType.Pipeline)
            .Select(a => new { Length = a.Pipeline == null ? null : a.Pipeline.LengthM, a.Preservation!.G4Status }).ToListAsync(ct);
        var pendingDocs = await db.GateDocuments.CountAsync(d => d.Asset.UnitId == unitId && d.Asset.IsActive && d.Status == DocumentationStatus.Pending, ct);

        return new DashboardDto(unit.Id, unit.Name, unit.Plant.Name, total, activities, disciplines,
            hold, totalHold, na, totalNa, totalHold + totalNa, total - totalHold - totalNa,
            activities[4].Completed, activities[4].Pending,
            equipment.GroupBy(a => FieldValues.CurrentGateText(a.Gate ?? CurrentGate.New))
                .OrderBy(g => g.Key == "New" ? 0 : g.Key == "Removed from Site" ? 99 : g.Key[1] - '0')
                .ToDictionary(g => g.Key, g => g.Count()),
            pendingDocs, active.Count(a => a.Type == AssetType.ElectricalInstrument), all.Count(a => !a.IsActive),
            lines.Count, lines.Sum(l => l.Length ?? 0), lines.Count(l => IsCompleted(l.G4Status)),
            assigned ? "Assigned" : "Keywords");
    }

    private static CategoryRow ToRow(DashboardCategory c, List<CategoryAsset> assets) =>
        new(c.Id, c.Code, c.Label, SectionKey(c.Section), c.Priority, c.Keywords.Select(k => k.Keyword).ToList(),
            c.ActionBy, c.EmailAction, c.Recommendation, c.Note, c.IsActive, assets.Count, assets);

    // ------------------------------------------------------------------ categories

    /// <summary>All categories of the unit for the editor: active ones in dashboard order with their counts, then inactive ones.</summary>
    public async Task<List<CategoryRow>> CategoriesAsync(int unitId, CancellationToken ct)
    {
        var d = await GetAsync(unitId, ct);
        var inactive = await db.DashboardCategories.AsNoTracking().Include(c => c.Keywords)
            .Where(c => c.UnitId == unitId && !c.IsActive).OrderBy(c => c.Section).ThenBy(c => c.SortOrder).ToListAsync(ct);
        return d.Hold.Concat(d.NotApplicable).Concat(inactive.Select(c => ToRow(c, []))).ToList();
    }

    /// <summary>Active categories of the unit, for the category dropdown of the asset page.</summary>
    public async Task<List<CategoryOption>> OptionsAsync(int unitId, CancellationToken ct)
    {
        var rows = await db.DashboardCategories.AsNoTracking().Where(c => c.UnitId == unitId && c.IsActive && c.Code != null)
            .OrderBy(c => c.Section).ThenBy(c => c.SortOrder).ToListAsync(ct);
        return rows.Select(c => new CategoryOption(c.Id, c.Code!, c.Label, SectionKey(c.Section))).ToList();
    }

    /// <summary>
    /// Saves the unit's categories (the editor always sends the whole set, inactive ones included).
    /// Categories are never deleted: one left out, or sent with IsActive = false, is deactivated, and only when no
    /// equipment has it. New categories, and old ones without a code, get the next free code of their section
    /// (Cat1, Cat2…); a code is never reused because deactivated categories keep theirs.
    /// </summary>
    public async Task<List<CategoryRow>> SaveCategoriesAsync(int unitId, List<SaveCategory> request, CancellationToken ct)
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

        var existing = await db.DashboardCategories.Include(c => c.Keywords).Where(c => c.UnitId == unitId).ToListAsync(ct);
        var unknown = clean.FirstOrDefault(c => c.Source.Id is { } id && existing.All(e => e.Id != id));
        if (unknown.Source is not null) throw ApiProblem.Invalid($"\"{unknown.Label}\" is not a category of this unit. Reload the page.");

        // Equipment per category: a category in use cannot be deactivated nor moved to the other section.
        var inUse = await db.Preservations.Where(p => p.DashboardCategoryId != null && existing.Select(e => e.Id).Contains(p.DashboardCategoryId.Value))
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
            if (entity is null) { db.DashboardCategories.Add(entity = new DashboardCategory { UnitId = unitId }); existing.Add(entity); }
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
        return await CategoriesAsync(unitId, ct);
    }

    /// <summary>
    /// Gives every category without a code one: the number in its label when free ("Cat 2: Missing drawing" → Cat2),
    /// otherwise the lowest free CatN of its section. Codes of inactive categories stay taken.
    /// </summary>
    public static void AssignCodes(IReadOnlyCollection<DashboardCategory> unitCategories)
    {
        foreach (var section in unitCategories.GroupBy(c => c.Section))
        {
            var taken = section.Select(c => CodeNumber(c.Code)).Where(n => n > 0).ToHashSet();
            var pending = section.Where(c => string.IsNullOrEmpty(c.Code)).OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
            foreach (var c in pending.ToList())
            {
                if (LabelNumber(c.Label) is not (> 0 and var n) || !taken.Add(n)) continue;
                c.Code = $"Cat{n}";
                pending.Remove(c);
            }
            // Lowest number not taken. Codes are never freed (categories are only deactivated), so none is reused.
            var next = 1;
            foreach (var c in pending)
            {
                while (!taken.Add(next)) next++;
                c.Code = $"Cat{next}";
            }
        }
    }

    /// <summary>Number written after "Cat" at the start of a label ("Cat 2 :Level…" → 2), or 0.</summary>
    private static int LabelNumber(string label)
    {
        var t = label.TrimStart();
        if (!t.StartsWith("Cat", StringComparison.OrdinalIgnoreCase)) return 0;
        var digits = new string(t[3..].TrimStart().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    /// <summary>Number of "Cat12" (0 when there is none).</summary>
    public static int CodeNumber(string? code) =>
        code is { Length: > 3 } && code.StartsWith("Cat", StringComparison.OrdinalIgnoreCase) && int.TryParse(code[3..], out var n) ? n : 0;

    /// <summary>"Cat 1", "cat1", " CAT1 " → "Cat1"; anything else → null.</summary>
    public static string? NormalizeCode(string? code)
    {
        var compact = (code ?? "").Replace(" ", "", StringComparison.Ordinal);
        return CodeNumber(compact) is > 0 and var n ? $"Cat{n}" : null;
    }

    private Task<List<DashboardCategory>> LoadCategoriesAsync(int unitId, CancellationToken ct) =>
        db.DashboardCategories.AsNoTracking().Include(c => c.Keywords).Where(c => c.UnitId == unitId && c.IsActive).ToListAsync(ct);

    public static string SectionKey(CategorySection s) => s == CategorySection.Hold ? "Hold" : "NotApplicable";

    private static bool TryParseSection(string? s, out CategorySection section)
    {
        var k = ValueNormalizer.ListKey(s ?? "");
        section = k is "notapplicable" or "n/a" or "na" ? CategorySection.NotApplicable : CategorySection.Hold;
        return k is "hold" or "notapplicable" or "n/a" or "na";
    }

    // Same tests as the Excel COUNTIFS criteria: "Completed*" (begins with), "Ongoing", "Hold" (whole value, any case).
    private static bool IsCompleted(string? v) => v is not null && v.TrimStart().StartsWith("Completed", StringComparison.OrdinalIgnoreCase);
    private static bool Is(string? v, string expected) => string.Equals(v?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    private static decimal Pct(int part, int whole) => whole == 0 ? 0 : Math.Round(100m * part / whole, 1);
}
