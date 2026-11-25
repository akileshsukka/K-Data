using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Reporting;

/// <summary>Effective on of one asset: its gate / walkdown changes recorded today and the daily report day they count in.</summary>
/// <param name="EffectiveOn">"Today", "Yesterday" or null when nothing was recorded today (nothing to move).</param>
/// <param name="Moved">Changes of today that count yesterday; a later change of today counts today until it is moved too.</param>
public sealed record EffectiveOnDto(DateOnly Today, DateOnly Yesterday, int Changes, int Moved, string? EffectiveOn);

public sealed record SetEffectiveOnRequest(string Day);

/// <summary>
/// Effective on (25-nov-2026): an engineer who could not update K Data on the day reports the next morning, and those
/// changes would count in the wrong daily report. The data engineer or an admin sets, per asset or in bulk, that the
/// gate and walkdown changes the asset had <b>today</b> count <b>yesterday</b> (or back to today). Only those two days:
/// changes recorded on an earlier day can never move. Untouched changes count when they were recorded, as always.
/// The moment of the change is kept; the move is written to the history ("Effective on"). Only from K Data (never by
/// an Excel import), and only the daily report and the dashboards rebuilt from the history read it.
/// </summary>
public sealed class EffectiveOnService(PresAppDbContext db, AuditContext audit, TimeProvider clock)
{
    /// <summary>Bulk edit field.</summary>
    public const string BulkField = "EffectiveOn";
    /// <summary>History entry of a move.</summary>
    public const string HistoryField = "Effective on";
    public const string Today = "Today";
    public const string Yesterday = "Yesterday";

    public async Task<EffectiveOnDto> GetAsync(Guid assetId, CancellationToken ct)
    {
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) throw ApiProblem.NotFound("The asset");
        var (today, start) = Day();
        var rows = await TodayRows([assetId], start).AsNoTracking().Select(c => c.EffectiveAtUtc).ToListAsync(ct);
        return Dto(today, rows.Count, rows.Count(e => e != null));
    }

    public async Task<EffectiveOnDto> SetAsync(Guid assetId, SetEffectiveOnRequest request, CancellationToken ct)
    {
        var yesterday = Parse(request.Day);
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) throw ApiProblem.NotFound("The asset");
        var (today, start) = Day();
        var rows = await TodayRows([assetId], start).ToListAsync(ct);
        if (rows.Count == 0)
            throw ApiProblem.Invalid("This asset has no gate or walkdown change recorded today: only today's changes can count yesterday.");
        if (Move(assetId, rows, yesterday, start, ChangeSource.App, Guid.NewGuid())) await db.SaveChangesAsync(ct);
        return Dto(today, rows.Count, rows.Count(r => r.EffectiveAtUtc != null));
    }

    /// <summary>Bulk edit (preview / apply): assets without changes today are skipped.</summary>
    public async Task<BulkResult> BulkAsync(BulkRequest request, bool apply, CancellationToken ct)
    {
        if (request.AssetIds is not { Count: > 0 }) throw ApiProblem.Invalid("Select at least one asset.");
        if (request.AssetIds.Count > 2000) throw ApiProblem.Invalid("Select at most 2,000 assets at a time.");
        var yesterday = Parse(request.Value);
        var target = yesterday ? Yesterday : Today;
        var ids = request.AssetIds.Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Code, a.Tag }).ToListAsync(ct);
        var (_, start) = Day();
        var all = await TodayRows(ids, start).ToListAsync(ct);
        var byAsset = all.ToLookup(r => r.AssetId!.Value);
        var batch = Guid.NewGuid();
        var result = new List<BulkRow>();
        foreach (var a in assets.OrderBy(a => a.Tag ?? a.Code, NaturalComparer.Instance))
        {
            var rows = byAsset[a.Id].ToList();
            if (rows.Count == 0)
            {
                result.Add(new BulkRow(a.Id, a.Code, a.Tag, null, target, "Skipped", "No gate change recorded today"));
                continue;
            }
            var current = State(rows.Count, rows.Count(r => r.EffectiveAtUtc != null));
            var same = yesterday ? rows.All(r => r.EffectiveAtUtc != null) : rows.All(r => r.EffectiveAtUtc == null);
            if (same)
            {
                result.Add(new BulkRow(a.Id, a.Code, a.Tag, current, target, "No change", null));
                continue;
            }
            result.Add(new BulkRow(a.Id, a.Code, a.Tag, current, target, "Change", $"{rows.Count} change(s) recorded today"));
            if (apply) Move(a.Id, rows, yesterday, start, ChangeSource.Bulk, batch);
        }
        foreach (var missing in ids.Except(assets.Select(a => a.Id)))
            result.Add(new BulkRow(missing, "?", null, null, target, "Skipped", "Asset not found"));
        if (apply) await db.SaveChangesAsync(ct);
        return new BulkResult(result.Count(r => r.Outcome == "Change"), result.Count(r => r.Outcome == "No change"),
            result.Count(r => r.Outcome == "Skipped"), result);
    }

    /// <summary>Gate and walkdown changes recorded today (site day), the ones the daily report counts as work.</summary>
    private IQueryable<ChangeLog> TodayRows(List<Guid> ids, DateTime startUtc)
    {
        var preservation = nameof(AssetPreservation);
        var fields = StatusHistory.Fields;
        var notWork = StatusHistory.NotWork;
        var nullable = ids.Select(id => (Guid?)id).ToList();
        return db.ChangeLogs.Where(c => nullable.Contains(c.AssetId) && c.EntityType == preservation && fields.Contains(c.Field)
                                        && c.ChangedAtUtc >= startUtc && !notWork.Contains(c.Source));
    }

    /// <summary>Moves the rows (tracked) and writes the history entry; false when nothing changed.</summary>
    private bool Move(Guid assetId, List<ChangeLog> rows, bool yesterday, DateTime startUtc, ChangeSource source, Guid batch)
    {
        var before = State(rows.Count, rows.Count(r => r.EffectiveAtUtc != null));
        // The last moment of yesterday: after everything recorded yesterday, so the order of the changes stays the same.
        DateTime? effective = yesterday ? startUtc.AddSeconds(-1) : null;
        var changed = false;
        foreach (var r in rows.Where(r => r.EffectiveAtUtc != effective))
        {
            r.EffectiveAtUtc = effective;
            changed = true;
        }
        if (!changed) return false;
        db.ChangeLogs.Add(new ChangeLog
        {
            EntityType = nameof(Asset), EntityKey = assetId.ToString(), AssetId = assetId, Field = HistoryField,
            OldValue = before, NewValue = yesterday ? Yesterday : Today,
            ChangedAtUtc = clock.GetUtcNow().UtcDateTime, ChangedBy = audit.UserName, Source = source, BatchId = batch,
        });
        return true;
    }

    private (DateOnly Today, DateTime StartUtc) Day()
    {
        var today = SiteClock.Today(clock);
        return (today, SiteClock.StartOfDayUtc(today));
    }

    private static EffectiveOnDto Dto(DateOnly today, int changes, int moved) =>
        new(today, today.AddDays(-1), changes, moved, changes == 0 ? null : State(changes, moved));

    private static string State(int changes, int moved) => moved > 0 ? Yesterday : Today;

    private static bool Parse(string? day) => day?.Trim() switch
    {
        { } d when d.Equals(Yesterday, StringComparison.OrdinalIgnoreCase) => true,
        { } d when d.Equals(Today, StringComparison.OrdinalIgnoreCase) => false,
        _ => throw ApiProblem.Invalid("Effective on is Yesterday or Today."),
    };
}
