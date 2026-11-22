using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Api.Piping;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Reporting;

public sealed record UnitHighlightDto(int Id, DateOnly Date, string Text, string UpdatedBy, DateTime UpdatedAtUtc);

public sealed record AddHighlightRequest(DateOnly Date, string? Text);

/// <summary><c>Expected</c> = the text the user saw, so a change made meanwhile by someone else is not overwritten.</summary>
public sealed record SaveHighlightRequest(string? Text, string? Expected);

/// <summary>
/// Highlights of a unit for one site day, written by the engineers in the Unit overview for the daily report.
/// Added at the end of the day's list; an empty text or Remove deactivates one (never deleted). Days that have not
/// started cannot have highlights.
/// </summary>
public sealed class UnitHighlightService(PresAppDbContext db, TimeProvider clock)
{
    public async Task<List<UnitHighlightDto>> ListAsync(int unitId, DateOnly date, CancellationToken ct) =>
        (await db.UnitHighlights.AsNoTracking().Where(h => h.UnitId == unitId && h.Date == date && h.IsActive).ToListAsync(ct))
        .OrderBy(h => h.SortOrder).ThenBy(h => h.Id).Select(ToDto).ToList();

    /// <summary>Active highlights of every unit for one day, by unit.</summary>
    public async Task<Dictionary<int, List<UnitHighlightDto>>> ForDayAsync(DateOnly date, CancellationToken ct) =>
        (await db.UnitHighlights.AsNoTracking().Where(h => h.Date == date && h.IsActive).ToListAsync(ct))
        .GroupBy(h => h.UnitId)
        .ToDictionary(g => g.Key, g => g.OrderBy(h => h.SortOrder).ThenBy(h => h.Id).Select(ToDto).ToList());

    public async Task<List<UnitHighlightDto>> AddAsync(int unitId, AddHighlightRequest r, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit");
        CheckDay(r.Date);
        var text = Clean(r.Text) ?? throw ApiProblem.Invalid("Write the highlight first.");
        var last = await db.UnitHighlights.Where(h => h.UnitId == unitId && h.Date == r.Date).MaxAsync(h => (int?)h.SortOrder, ct) ?? 0;
        db.UnitHighlights.Add(new UnitHighlight { UnitId = unitId, Date = r.Date, Text = text, SortOrder = last + 1 });
        await db.SaveChangesAsync(ct);
        return await ListAsync(unitId, r.Date, ct);
    }

    /// <summary>Changes the text; an empty text removes the highlight.</summary>
    public async Task<List<UnitHighlightDto>> SaveAsync(int unitId, int id, SaveHighlightRequest r, CancellationToken ct)
    {
        var highlight = await FindAsync(unitId, id, ct);
        if (!PipingNoteService.Same(highlight.Text, r.Expected))
            throw ApiProblem.Conflict("Someone else changed this highlight while you were editing. Check it and try again.");
        var text = Clean(r.Text);
        if (text is null) highlight.IsActive = false;
        else highlight.Text = text;
        await db.SaveChangesAsync(ct);
        return await ListAsync(unitId, highlight.Date, ct);
    }

    public async Task<List<UnitHighlightDto>> RemoveAsync(int unitId, int id, CancellationToken ct)
    {
        var highlight = await FindAsync(unitId, id, ct);
        highlight.IsActive = false;
        await db.SaveChangesAsync(ct);
        return await ListAsync(unitId, highlight.Date, ct);
    }

    private async Task<UnitHighlight> FindAsync(int unitId, int id, CancellationToken ct)
    {
        var highlight = await db.UnitHighlights.FirstOrDefaultAsync(h => h.Id == id && h.UnitId == unitId && h.IsActive, ct)
                        ?? throw ApiProblem.NotFound("Highlight");
        CheckDay(highlight.Date);
        return highlight;
    }

    private void CheckDay(DateOnly date)
    {
        if (date > SiteClock.Today(clock)) throw ApiProblem.Invalid("Highlights can only be written for today or an earlier day.");
    }

    private static string? Clean(string? text)
    {
        var clean = PipingNoteService.Clean(text);
        if (clean?.Length > UnitHighlight.TextLength) throw ApiProblem.Invalid($"A highlight allows at most {UnitHighlight.TextLength} characters.");
        return clean;
    }

    private static UnitHighlightDto ToDto(UnitHighlight h) => new(h.Id, h.Date, h.Text, h.UpdatedBy, h.UpdatedAtUtc);
}
