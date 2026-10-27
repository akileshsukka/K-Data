using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;
using PresApp.Domain.Simpro;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Simpro;

/// <summary>
/// Daily automatic sync at the configured Dubai time (default 05:00), when enabled in the Simpro page.
/// Conflicts do not stop it: held equipment wait for a decision, everything else is written; new equipment are created.
/// Runs once per day; if the server was off at that time it runs as soon as it starts that day.
/// </summary>
public sealed class SimproScheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SimproScheduler> logger) : BackgroundService
{
    public const string ScheduleUser = "schedule";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Simpro schedule check failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PresAppDbContext>();
        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.SimproScheduleEnabled || s.Key == AppSettingKeys.SimproScheduleTime)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        if (!string.Equals(settings.GetValueOrDefault(AppSettingKeys.SimproScheduleEnabled), "true", StringComparison.OrdinalIgnoreCase)) return;
        if (!TimeOnly.TryParseExact(settings.GetValueOrDefault(AppSettingKeys.SimproScheduleTime) ?? "05:00", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return;

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, SiteClock.Dubai);
        if (TimeOnly.FromDateTime(nowLocal) < at) return;
        var startOfDayUtc = TimeZoneInfo.ConvertTimeToUtc(nowLocal.Date, SiteClock.Dubai);
        if (await db.SimproSyncRuns.AnyAsync(r => r.Mode == SyncMode.Scheduled && r.StartedAtUtc >= startOfDayUtc, ct)) return;

        var service = scope.ServiceProvider.GetRequiredService<SimproSyncService>();
        if (!service.IsConfigured) return;
        logger.LogInformation("Starting the daily Simpro sync");
        var result = await service.RunAsync(SyncMode.Scheduled, ScheduleUser, ct);
        logger.LogInformation("Daily Simpro sync {RunId}: {Status}. {Message}", result.Run.Id, result.Run.Status, result.Run.Message);
    }
}
