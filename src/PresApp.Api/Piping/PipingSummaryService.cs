using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Piping;

public sealed record PipingCount(int Done, int Total);
public sealed record CleaningScopeRow(string Value, int Lines, bool InScope);
/// <summary>InspectionTotal: the lines of the service without Inspection Done = N/A (what Inspected is counted against).</summary>
public sealed record PipingServiceRow(string Code, string? Description, int Lines, int Walkdown, int Drained, int Inspected, int Scope, int Cleaned,
    int InspectionTotal);
/// <param name="Lines">Rows counted: lines, or groups when <paramref name="Grouped"/>.</param>
/// <param name="Sections">The lines themselves (equal to <paramref name="Lines"/> when not grouped).</param>
public sealed record PipingSummary(int Lines, decimal LengthM, PipingCount Walkdown, PipingCount Drained, PipingCount Inspected,
    PipingCount Cleaned, int NoCleaningSet, List<CleaningScopeRow> Cleaning, List<PipingServiceRow> ByService, bool Grouped = false, int Sections = 0);

/// <summary>
/// The figures of the piping Summary for the app (same rules as the Excel): walkdown Completed, bulk liquid drained
/// (Completed), inspection done (Yes; lines with N/A are left out of the total), cleaning scope per Cleaning Required value (TBC and similar are not scope)
/// and lines cleaned (Cleaned = Yes) against the scope.
/// Grouped, each size group counts once, as the Summary of the grouped line list counts its rows: an activity is done
/// when every line of the group has it, and a group whose lines have different Cleaning Required values is "Mixed" (not scope).
/// </summary>
public sealed class PipingSummaryService(PresAppDbContext db)
{
    private sealed record Row(string Service, decimal? Length, bool Walkdown, bool Drained, bool Inspected, bool InspectionNa, string? Cleaning, bool InScope, bool Cleaned);

    public async Task<PipingSummary> GetAsync(int unitId, CancellationToken ct, bool grouped = false)
    {
        var lines = await db.Assets.AsNoTracking()
            .Where(a => a.UnitId == unitId && a.IsActive && a.Type == AssetType.Pipeline)
            .Select(a => new { a.Id, a.Tag, Service = a.Pipeline != null ? a.Pipeline.Service : null, Length = a.Pipeline != null ? a.Pipeline.LengthM : null,
                Walkdown = a.Preservation != null ? a.Preservation.EmWalkdownStatus : null, Drained = a.Preservation != null ? a.Preservation.G1BulkLiquidDrained : null })
            .ToListAsync(ct);
        var defs = await db.FieldDefinitions.AsNoTracking().Include(f => f.OptionSet).ThenInclude(o => o!.Values)
            .Where(f => f.AssetType == AssetType.Pipeline && Excel.PipingLayout.FixedKeys.Contains(f.Key)).ToDictionaryAsync(f => f.Key, ct);
        var ids = defs.Values.Select(d => d.Id).ToList();
        var values = (await db.FieldValues.AsNoTracking().Where(v => ids.Contains(v.FieldDefinitionId) && v.Asset.UnitId == unitId)
                .Select(v => new { v.AssetId, v.FieldDefinitionId, v.ValueText }).ToListAsync(ct))
            .ToLookup(v => v.AssetId);
        string? Value(Guid asset, string key) => defs.TryGetValue(key, out var d) ? values[asset].FirstOrDefault(v => v.FieldDefinitionId == d.Id)?.ValueText : null;
        var codes = await LineCodeMeanings.LoadAsync(db, ct);

        var perLine = lines.Select(l =>
        {
            var cleaning = Value(l.Id, PipingFieldKeys.CleaningRequired);
            return (l.Tag, Row: new Row(
                (l.Service ?? LineNumber.Parse(l.Tag)?.Service ?? "").ToUpperInvariant(),
                l.Length,
                AssetPreservation.IsCompleted(l.Walkdown),
                AssetPreservation.IsCompleted(l.Drained),
                string.Equals(Value(l.Id, PipingFieldKeys.InspectionDone), "Yes", StringComparison.OrdinalIgnoreCase),
                Value(l.Id, PipingFieldKeys.InspectionDone) is { } inspection && ValueNormalizer.IsNotApplicable(inspection),
                cleaning,
                cleaning is not null && !Excel.PipingTemplate.NotScope(cleaning),
                string.Equals(Value(l.Id, PipingFieldKeys.Cleaned), "Yes", StringComparison.OrdinalIgnoreCase)));
        }).ToList();
        var rows = grouped
            ? perLine.Select((x, i) => (Key: LineNumber.GroupKey(x.Tag) ?? "#" + i, x.Row)).GroupBy(x => x.Key).Select(g => Merge(g.Select(x => x.Row).ToList())).ToList()
            : perLine.Select(x => x.Row).ToList();

        // Every value of the list, in its order, plus values still used but no longer in the list.
        var listed = defs.TryGetValue(PipingFieldKeys.CleaningRequired, out var cr)
            ? cr.OptionSet?.Values.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Value).ToList() ?? []
            : [];
        var used = rows.Where(r => r.Cleaning is not null).Select(r => r.Cleaning!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(u => !listed.Contains(u, StringComparer.OrdinalIgnoreCase));
        var cleaningRows = listed.Concat(used)
            .Select(v => new CleaningScopeRow(v, rows.Count(r => string.Equals(r.Cleaning, v, StringComparison.OrdinalIgnoreCase)),
                v != Excel.PipingLayout.Mixed && !Excel.PipingTemplate.NotScope(v)))
            .ToList();
        var scope = rows.Count(r => r.InScope);

        return new PipingSummary(
            rows.Count, rows.Sum(r => r.Length ?? 0),
            new PipingCount(rows.Count(r => r.Walkdown), rows.Count),
            new PipingCount(rows.Count(r => r.Drained), rows.Count),
            new PipingCount(rows.Count(r => r.Inspected), rows.Count(r => !r.InspectionNa)),
            new PipingCount(rows.Count(r => r.Cleaned && r.InScope), scope),
            rows.Count(r => r.Cleaning is null),
            cleaningRows,
            rows.GroupBy(r => r.Service).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new PipingServiceRow(g.Key, codes.Service(g.Key, unitId), g.Count(), g.Count(r => r.Walkdown), g.Count(r => r.Drained),
                    g.Count(r => r.Inspected), g.Count(r => r.InScope), g.Count(r => r.Cleaned && r.InScope), g.Count(r => !r.InspectionNa)))
                .ToList(),
            grouped, perLine.Count);
    }

    /// <summary>One row for a size group: done when all its lines are; one Cleaning Required value, or Mixed.</summary>
    private static Row Merge(List<Row> g)
    {
        if (g.Count == 1) return g[0];
        var cleaning = g.All(r => string.Equals(r.Cleaning ?? "", g[0].Cleaning ?? "", StringComparison.OrdinalIgnoreCase)) ? g[0].Cleaning : Excel.PipingLayout.Mixed;
        var inScope = cleaning is not null && cleaning != Excel.PipingLayout.Mixed && !Excel.PipingTemplate.NotScope(cleaning);
        return new Row(g[0].Service, g.Any(r => r.Length is not null) ? g.Sum(r => r.Length ?? 0) : null,
            g.All(r => r.Walkdown), g.All(r => r.Drained), g.All(r => r.Inspected), g.All(r => r.InspectionNa), cleaning, inScope, g.All(r => r.Cleaned));
    }
}
