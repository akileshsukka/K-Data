using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Reporting;

/// <summary>One discipline of a unit in the daily report. <see cref="GateProgressPct"/> null: every tag is not applicable.</summary>
public sealed record DailyDisciplineRow(string Discipline, int Tags, int NotApplicable, int Preserved, int Hold, decimal? GateProgressPct);

/// <summary>Figures of one scope (mechanical or E&amp;I) of a unit at a moment; <see cref="PreservedTags"/> by first asset id.</summary>
internal sealed record ReportFigures(int Tags, int NotApplicable, int Preserved, int Hold, List<DailyDisciplineRow> Disciplines, Dictionary<Guid, string> PreservedTags);

/// <summary>
/// One unit in the daily report, at the end of the day (and, for the change, at the end of the day before).
/// <see cref="PreservedPct"/> = preserved ÷ (tags − not applicable). Piping lines are apart and never in these figures.
/// </summary>
public sealed record DailyUnitDto(
    int UnitId, int UnitNumber, string UnitName, string PlantName, bool Grouped,
    int Tags, int NotApplicable, int Applicable, int Preserved, int Hold, decimal PreservedPct,
    int PreservedDayBefore, decimal PreservedPctDayBefore,
    List<DailyDisciplineRow> Disciplines, List<string> CompletedTags,
    int Lines, int LinesPreserved, List<UnitHighlightDto> Highlights);

/// <summary>Sum of the units, counted by tag (never an average of the unit %).</summary>
public sealed record DailyTotalsDto(int Tags, int NotApplicable, int Applicable, int Preserved, int Hold, decimal PreservedPct, int CompletedOnDay,
    int Lines, int LinesPreserved);

/// <summary>The daily report of one site day: the day ends at midnight UAE time (<see cref="CutoffUtc"/>).</summary>
public sealed record DailySummaryDto(DateOnly Date, DateTime CutoffUtc, DateTime GeneratedAtUtc, List<DailyUnitDto> Units, DailyTotalsDto Totals);

/// <summary>
/// Preservation figures of every unit for the daily report, the same counting as the Unit overview (Preserved = G4 N2
/// blanketing completed, E&amp;I = every step closed; categories for hold and not applicable; each unit in its default
/// view, by equipment or as the client). Statuses are those at the end of the day, rebuilt from the change log, so the
/// report of a past day does not move when work continues; "completed on the day" = tags preserved at the end of the
/// day that were not at its start.
/// </summary>
public sealed class DailySummary(PresAppDbContext db, UnitDashboard dashboard, ClientViewSettings view, UnitHighlightService highlights, TimeProvider clock)
{
    /// <param name="unitId">Only this unit (its panel in the Unit overview); null = every unit with active equipment.</param>
    public async Task<DailySummaryDto> GetAsync(DateOnly? date, CancellationToken ct, int? unitId = null)
    {
        var today = SiteClock.Today(clock);
        var day = date ?? today.AddDays(-1);
        if (day > today) throw ApiProblem.Invalid("The daily report cannot be for a day that has not started.");
        var startUtc = SiteClock.StartOfDayUtc(day);
        var endUtc = SiteClock.StartOfDayUtc(day.AddDays(1));

        var units = await db.Units.AsNoTracking().Include(u => u.Plant)
            .Where(u => unitId == null ? u.IsActive && db.Assets.Any(a => a.UnitId == u.Id && a.IsActive) : u.Id == unitId)
            .OrderBy(u => u.Number).ToListAsync(ct);
        var notes = await highlights.ForDayAsync(day, ct);

        var result = new List<DailyUnitDto>();
        foreach (var unit in units)
        {
            var history = await StatusHistory.LoadAsync(db, unit.Id, startUtc, ct);
            string? AtEnd(Guid id, string field, string? current) => history.ValueAt(id, field, current, endUtc);
            string? AtStart(Guid id, string field, string? current) => history.ValueAt(id, field, current, startUtc);
            var grouped = await view.GroupedAsync(unit.Id, ct);

            var mechanical = await dashboard.ReportFiguresAsync(unit.Id, CategoryScope.Mechanical, grouped, AtEnd, ct);
            var ei = await dashboard.ReportFiguresAsync(unit.Id, CategoryScope.ElectricalInstrument, grouped, AtEnd, ct);
            var mechanicalBefore = await dashboard.ReportFiguresAsync(unit.Id, CategoryScope.Mechanical, grouped, AtStart, ct);
            var eiBefore = await dashboard.ReportFiguresAsync(unit.Id, CategoryScope.ElectricalInstrument, grouped, AtStart, ct);

            var before = mechanicalBefore.PreservedTags.Keys.Concat(eiBefore.PreservedTags.Keys).ToHashSet();
            var completed = mechanical.PreservedTags.Concat(ei.PreservedTags).Where(p => !before.Contains(p.Key))
                .Select(p => p.Value).Order(NaturalComparer.Instance).ToList();

            var tags = mechanical.Tags + ei.Tags;
            var na = mechanical.NotApplicable + ei.NotApplicable;
            var preserved = mechanical.Preserved + ei.Preserved;
            var preservedBefore = mechanicalBefore.Preserved + eiBefore.Preserved;

            var lines = await db.Assets.AsNoTracking().Where(a => a.UnitId == unit.Id && a.IsActive && a.Type == AssetType.Pipeline)
                .Select(a => new { a.Id, a.Preservation!.G4Status }).ToListAsync(ct);
            var g4 = nameof(AssetPreservation.G4Status);
            var linesPreserved = lines.Count(l => IsCompleted(AtEnd(l.Id, g4, l.G4Status)));

            result.Add(new DailyUnitDto(unit.Id, unit.Number, unit.Name, unit.Plant.Name, grouped,
                tags, na, tags - na, preserved, mechanical.Hold + ei.Hold, Pct(preserved, tags - na),
                preservedBefore, Pct(preservedBefore, tags - na),
                [.. mechanical.Disciplines, .. ei.Disciplines], completed,
                lines.Count, linesPreserved, notes.GetValueOrDefault(unit.Id) ?? []));
        }

        var t = new DailyTotalsDto(result.Sum(u => u.Tags), result.Sum(u => u.NotApplicable), result.Sum(u => u.Applicable), result.Sum(u => u.Preserved),
            result.Sum(u => u.Hold), Pct(result.Sum(u => u.Preserved), result.Sum(u => u.Applicable)), result.Sum(u => u.CompletedTags.Count),
            result.Sum(u => u.Lines), result.Sum(u => u.LinesPreserved));
        return new DailySummaryDto(day, endUtc, clock.GetUtcNow().UtcDateTime, result, t);
    }

    private static bool IsCompleted(string? v) => v is not null && v.TrimStart().StartsWith("Completed", StringComparison.OrdinalIgnoreCase);
    private static decimal Pct(int part, int whole) => whole <= 0 ? 0 : Math.Round(100m * part / whole, 1);
}
