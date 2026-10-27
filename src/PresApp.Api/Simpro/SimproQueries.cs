using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Catalog;
using PresApp.Domain.Simpro;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Simpro;

public sealed record SimproRunDto(long Id, string Mode, string Status, string TriggeredBy, DateTime StartedAtUtc, DateTime? FinishedAtUtc,
    int CreatedCount, int UpdatedCount, int FieldCount, int HeldCount, int RejectedCount, int NotInAppCount, string? Message, long? UndoOfRunId, bool CanUndo);

public sealed record SimproItemDto(long Id, Guid AssetId, string AssetCode, string? AssetTag, string? UnitName, string Field, string FieldLabel,
    string? SimproValue, string? NewValue, string? LastSyncedValue, string Outcome, string? Reason, bool CanCopy, bool Forced);

public sealed record SimproRunDetail(SimproRunDto Run, List<SimproItemDto> Items);

public sealed record SimproSettingsDto(bool Configured, bool ScheduleEnabled, string ScheduleTime, string SyncUsername, int ForcedCount);

public static class SimproQueries
{
    public static async Task<SimproRunDetail> DetailAsync(PresAppDbContext db, long runId, CancellationToken ct)
    {
        var run = await db.SimproSyncRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw ApiProblem.NotFound("Run not found.");
        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var forced = (await db.SimproForcedAssets.AsNoTracking().Select(f => f.AssetId).ToListAsync(ct)).ToHashSet();
        var items = await db.SimproSyncItems.AsNoTracking().Where(i => i.RunId == runId).ToListAsync(ct);
        var latestApplied = await LatestAppliedIdAsync(db, ct);
        return new SimproRunDetail(ToDto(run, latestApplied), items
            .OrderBy(i => i.UnitId is { } u ? units.GetValueOrDefault(u) : "").ThenBy(i => i.AssetCode, Assets.NaturalComparer.Instance)
            .ThenBy(i => SimproFields.ByKey.GetValueOrDefault(i.Field)?.Sort ?? 0)
            .Select(i =>
            {
                var field = SimproFields.ByKey.GetValueOrDefault(i.Field);
                return new SimproItemDto(i.Id, i.AssetId, i.AssetCode, i.AssetTag, i.UnitId is { } u ? units.GetValueOrDefault(u) : null, i.Field,
                    field?.Label ?? "Equipment", i.SimproValue, i.NewValue, i.LastSyncedValue, i.Outcome, i.Reason,
                    i.Outcome is "Changed in Simpro" or "Conflict" or "Gate goes back" && field is { AppField: not null } && field.Type is not (SimproType.Number or SimproType.Reference),
                    forced.Contains(i.AssetId));
            }).ToList());
    }

    public static async Task<List<SimproRunDto>> RecentAsync(PresAppDbContext db, int take, CancellationToken ct)
    {
        var latestApplied = await LatestAppliedIdAsync(db, ct);
        return (await db.SimproSyncRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(take).ToListAsync(ct))
            .Select(r => ToDto(r, latestApplied)).ToList();
    }

    public static async Task<SimproSettingsDto> SettingsAsync(PresAppDbContext db, bool configured, string username, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.SimproScheduleEnabled || s.Key == AppSettingKeys.SimproScheduleTime)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new SimproSettingsDto(configured,
            string.Equals(settings.GetValueOrDefault(AppSettingKeys.SimproScheduleEnabled), "true", StringComparison.OrdinalIgnoreCase),
            settings.GetValueOrDefault(AppSettingKeys.SimproScheduleTime) ?? "05:00", username,
            await db.SimproForcedAssets.CountAsync(ct));
    }

    public static async Task<SimproSettingsDto> SaveSettingsAsync(PresAppDbContext db, bool enabled, string time, bool configured, string username, CancellationToken ct)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) throw ApiProblem.Invalid("The time must be HH:mm (Dubai time).");
        foreach (var (key, value) in new[] { (AppSettingKeys.SimproScheduleEnabled, enabled ? "true" : "false"), (AppSettingKeys.SimproScheduleTime, t.ToString("HH:mm", CultureInfo.InvariantCulture)) })
        {
            var s = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (s is null) db.Settings.Add(s = new AppSetting { Key = key });
            s.Value = value;
        }
        await db.SaveChangesAsync(ct);
        return await SettingsAsync(db, configured, username, ct);
    }

    private static Task<long> LatestAppliedIdAsync(PresAppDbContext db, CancellationToken ct) =>
        db.SimproSyncRuns.Where(r => r.Mode != SyncMode.Preview && r.Mode != SyncMode.Undo && (r.Status == SyncStatus.Applied || r.Status == SyncStatus.HeldForConflicts))
            .Select(r => r.Id).DefaultIfEmpty().MaxAsync(ct);

    private static SimproRunDto ToDto(SimproSyncRun r, long latestApplied) => new(
        r.Id, r.Mode.ToString(), r.Status.ToString(), r.TriggeredBy, r.StartedAtUtc, r.FinishedAtUtc, r.CreatedCount, r.UpdatedCount,
        r.FieldCount, r.HeldCount, r.RejectedCount, r.NotInAppCount, r.Message, r.UndoOfRunId,
        r.Id == latestApplied && r.Mode != SyncMode.Undo && (r.CreatedCount + r.UpdatedCount) > 0);
}
