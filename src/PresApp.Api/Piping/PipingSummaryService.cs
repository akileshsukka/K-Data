using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Piping;

public sealed record PipingCount(int Done, int Total);
public sealed record CleaningScopeRow(string Value, int Lines, bool InScope);
public sealed record PipingServiceRow(string Code, string? Description, int Lines, int Walkdown, int Drained, int Inspected, int Scope, int Cleaned);
public sealed record PipingSummary(int Lines, decimal LengthM, PipingCount Walkdown, PipingCount Drained, PipingCount Inspected,
    PipingCount Cleaned, int NoCleaningSet, List<CleaningScopeRow> Cleaning, List<PipingServiceRow> ByService);

/// <summary>
/// The figures of the piping Summary for the app (same rules as the Excel): walkdown Completed, bulk liquid drained
/// (Completed), inspection done (Yes), cleaning scope per Cleaning Required value (TBC and similar are not scope)
/// and lines cleaned (Cleaned = Yes) against the scope.
/// </summary>
public sealed class PipingSummaryService(PresAppDbContext db)
{
    public async Task<PipingSummary> GetAsync(int unitId, CancellationToken ct)
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
        var codes = await db.LineCodes.AsNoTracking().Where(c => c.IsActive && c.Kind == LineCodeKind.Service)
            .ToDictionaryAsync(c => c.Code, c => c.Description, StringComparer.OrdinalIgnoreCase, ct);

        var rows = lines.Select(l =>
        {
            var cleaning = Value(l.Id, PipingFieldKeys.CleaningRequired);
            return new
            {
                Service = (l.Service ?? LineNumber.Parse(l.Tag)?.Service ?? "").ToUpperInvariant(),
                l.Length,
                Walkdown = AssetPreservation.IsCompleted(l.Walkdown),
                Drained = AssetPreservation.IsCompleted(l.Drained),
                Inspected = string.Equals(Value(l.Id, PipingFieldKeys.InspectionDone), "Yes", StringComparison.OrdinalIgnoreCase),
                Cleaning = cleaning,
                InScope = cleaning is not null && !Excel.PipingTemplate.NotScope(cleaning),
                Cleaned = string.Equals(Value(l.Id, PipingFieldKeys.Cleaned), "Yes", StringComparison.OrdinalIgnoreCase)
            };
        }).ToList();

        // Every value of the list, in its order, plus values still used but no longer in the list.
        var listed = defs.TryGetValue(PipingFieldKeys.CleaningRequired, out var cr)
            ? cr.OptionSet?.Values.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Value).ToList() ?? []
            : [];
        var used = rows.Where(r => r.Cleaning is not null).Select(r => r.Cleaning!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(u => !listed.Contains(u, StringComparer.OrdinalIgnoreCase));
        var cleaningRows = listed.Concat(used)
            .Select(v => new CleaningScopeRow(v, rows.Count(r => string.Equals(r.Cleaning, v, StringComparison.OrdinalIgnoreCase)), !Excel.PipingTemplate.NotScope(v)))
            .ToList();
        var scope = rows.Count(r => r.InScope);

        return new PipingSummary(
            rows.Count, rows.Sum(r => r.Length ?? 0),
            new PipingCount(rows.Count(r => r.Walkdown), rows.Count),
            new PipingCount(rows.Count(r => r.Drained), rows.Count),
            new PipingCount(rows.Count(r => r.Inspected), rows.Count),
            new PipingCount(rows.Count(r => r.Cleaned && r.InScope), scope),
            rows.Count(r => r.Cleaning is null),
            cleaningRows,
            rows.GroupBy(r => r.Service).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new PipingServiceRow(g.Key, codes.GetValueOrDefault(g.Key), g.Count(), g.Count(r => r.Walkdown), g.Count(r => r.Drained),
                    g.Count(r => r.Inspected), g.Count(r => r.InScope), g.Count(r => r.Cleaned && r.InScope)))
                .ToList());
    }
}
