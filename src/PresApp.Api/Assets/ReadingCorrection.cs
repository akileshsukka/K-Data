using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Api.Monitoring;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Monitoring;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

/// <summary>
/// A reading corrected by an admin: the date and/or the value it should have had. <paramref name="Expected"/> is the reading
/// as the admin saw it, so a change made meanwhile by someone else is not overwritten.
/// </summary>
public sealed record CorrectReadingRequest(DateTime MeasuredAtUtc, decimal Value, string? Reason, ReadingSeen? Expected = null);

/// <summary>A reading removed by an admin (entered twice, on the wrong asset…).</summary>
public sealed record RemoveReadingRequest(string? Reason, ReadingSeen? Expected = null);

public sealed record ReadingSeen(DateTime MeasuredAtUtc, decimal Value);

/// <summary>
/// Correction of monitoring readings (permission readings.correct, Admin only by default). Readings are otherwise never
/// edited, so every correction leaves its trace in the asset history: the reading before and after, who, when and why.
/// <list type="bullet">
/// <item>Phase (DURING / POST) follows the new date, as when the reading is entered.</item>
/// <item>A pressure that follows the round rule (G4 - N2 Blanket before the asset was preserved, Pressure (barg) since)
/// changes measurement when its new date crosses the preservation date. A pressure entered by hand as the other
/// measurement keeps it.</item>
/// <item>The latest values of the asset (and Last reading) are worked out again only when this reading was or becomes the
/// latest one, so values that came from elsewhere (migration) are not touched otherwise.</item>
/// <item>Last Field Action Date is not changed (it can be edited by hand). Daily report figures do not use readings.</item>
/// <item>Copies made when an asset was split are separate readings: each part is corrected on its own.</item>
/// </list>
/// </summary>
public sealed class ReadingCorrection(PresAppDbContext db, AssetQueries queries, AuditContext audit, TimeProvider clock)
{
    public const string HistoryEntity = "Reading";
    public const int MaxReason = 500;

    public async Task<AssetProfile> CorrectAsync(Guid assetId, long readingId, CorrectReadingRequest request, CancellationToken ct)
    {
        var reason = Reason(request.Reason);
        var now = clock.GetUtcNow().UtcDateTime;
        var measuredAt = DateTime.SpecifyKind(request.MeasuredAtUtc, DateTimeKind.Utc);
        if (measuredAt > now.AddMinutes(10)) throw ApiProblem.Invalid("The measurement time cannot be in the future.");
        if (measuredAt < now.AddYears(-2)) throw ApiProblem.Invalid("The measurement time is more than two years ago.");

        var (asset, readings, reading) = await LoadAsync(assetId, readingId, request.Expected, ct);
        var types = await db.MeasurementTypes.ToDictionaryAsync(t => t.Id, ct);
        var oldType = types[reading.MeasurementTypeId];
        var p = asset.Preservation!;
        // Nobody measures after entering the reading: a later time would also show the reading was moved.
        if (measuredAt > reading.RecordedAtUtc.AddMinutes(10))
            throw ApiProblem.Invalid($"The measurement time cannot be after the reading was entered ({Dubai(reading.RecordedAtUtc)}).");

        // A pressure of a round moves between G4 - N2 Blanket and Pressure (barg) with the preservation date.
        var newType = oldType;
        if (oldType.Key is MonitoringService.N2Key or MonitoringService.PressureKey
            && MonitoringService.PressureKeyFor(p.PreservedAtUtc, reading.MeasuredAtUtc) == oldType.Key)
        {
            var key = MonitoringService.PressureKeyFor(p.PreservedAtUtc, measuredAt);
            newType = types.Values.FirstOrDefault(t => t.Key == key) ?? oldType;
        }
        if (newType.MinValue is { } min && request.Value < min || newType.MaxValue is { } max && request.Value > max)
            throw ApiProblem.Invalid($"{newType.Label} must be between {newType.MinValue?.ToString() ?? "−∞"} and {newType.MaxValue?.ToString() ?? "∞"}.");

        if (reading.MeasuredAtUtc == measuredAt && reading.Value == request.Value && newType.Id == oldType.Id)
            throw ApiProblem.Invalid("Nothing to correct: the date and the value are the same.");

        var before = Describe(oldType, reading.Value, reading.MeasuredAtUtc);
        var latestBefore = Latest(readings);
        var lastBefore = readings.Max(r => r.MeasuredAtUtc);

        reading.MeasuredAtUtc = measuredAt;
        reading.Value = request.Value;
        reading.MeasurementTypeId = newType.Id;
        reading.Phase = p.PreservedAtUtc is { } start && measuredAt >= start ? MonitoringPhase.Post : MonitoringPhase.During;

        Refresh(p, readings, reading, latestBefore, lastBefore, types);
        db.ChangeLogs.Add(History(assetId, reading.Id, $"Reading corrected: {oldType.Label}", before,
            $"{Describe(newType, reading.Value, measuredAt)}. Reason: {reason}", now));
        await SaveInternalAsync(ct);
        return await queries.ProfileAsync(assetId, ct);
    }

    public async Task<AssetProfile> RemoveAsync(Guid assetId, long readingId, RemoveReadingRequest request, CancellationToken ct)
    {
        var reason = Reason(request.Reason);
        var now = clock.GetUtcNow().UtcDateTime;
        var (asset, readings, reading) = await LoadAsync(assetId, readingId, request.Expected, ct);
        var types = await db.MeasurementTypes.ToDictionaryAsync(t => t.Id, ct);
        var type = types[reading.MeasurementTypeId];

        var latestBefore = Latest(readings);
        var lastBefore = readings.Max(r => r.MeasuredAtUtc);
        readings.Remove(reading);
        db.Readings.Remove(reading);

        Refresh(asset.Preservation!, readings, reading, latestBefore, lastBefore, types);
        db.ChangeLogs.Add(History(assetId, reading.Id, $"Reading removed: {type.Label}",
            $"{Describe(type, reading.Value, reading.MeasuredAtUtc)} (entered by {reading.RecordedBy})", $"Removed. Reason: {reason}", now));
        await SaveInternalAsync(ct);
        return await queries.ProfileAsync(assetId, ct);
    }

    private static string Reason(string? text)
    {
        var reason = ValueNormalizer.CleanText(text);
        if (reason is null) throw ApiProblem.Invalid("Write the reason of the correction: it stays in the history.");
        if (reason.Length > MaxReason) throw ApiProblem.Invalid($"The reason allows at most {MaxReason} characters.");
        return reason;
    }

    private async Task<(Asset Asset, List<Reading> Readings, Reading Reading)> LoadAsync(Guid assetId, long readingId, ReadingSeen? expected, CancellationToken ct)
    {
        var asset = await queries.LoadAsync(assetId, tracking: true, ct);
        if (!asset.IsActive) throw ApiProblem.Forbidden("The asset is inactive. Reactivate it to correct its readings.");
        var readings = await db.Readings.Where(r => r.AssetId == assetId).ToListAsync(ct);
        var reading = readings.FirstOrDefault(r => r.Id == readingId) ?? throw ApiProblem.NotFound("The reading");
        if (expected is { } seen && (DateTime.SpecifyKind(seen.MeasuredAtUtc, DateTimeKind.Utc) != reading.MeasuredAtUtc || seen.Value != reading.Value))
            throw ApiProblem.Conflict("This reading was changed meanwhile. Reload the page and check it again.");
        return (asset, readings, reading);
    }

    /// <summary>The latest reading of each measurement: latest measurement time, then the last entered.</summary>
    private static Dictionary<int, long> Latest(IEnumerable<Reading> readings) => readings
        .GroupBy(r => r.MeasurementTypeId)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.MeasuredAtUtc).ThenByDescending(r => r.Id).First().Id);

    /// <summary>
    /// Latest values of the asset after the change, only for the measurements where the changed reading was or is now the
    /// latest one; Last reading the same way.
    /// </summary>
    private static void Refresh(AssetPreservation p, List<Reading> readings, Reading changed, Dictionary<int, long> latestBefore,
        DateTime lastBefore, Dictionary<int, Domain.Catalog.MeasurementType> types)
    {
        var latestAfter = Latest(readings);
        foreach (var (typeId, type) in types)
        {
            var was = latestBefore.GetValueOrDefault(typeId) == changed.Id;
            var isNow = latestAfter.GetValueOrDefault(typeId) == changed.Id;
            if (!was && !isNow) continue;
            AssetEditor.SetLatest(p, type.Key, latestAfter.TryGetValue(typeId, out var id) ? readings.First(r => r.Id == id).Value : null);
        }

        var lastAfter = readings.Count == 0 ? (DateTime?)null : readings.Max(r => r.MeasuredAtUtc);
        if (p.LastReadingAtUtc is null || p.LastReadingAtUtc == lastBefore || lastAfter > p.LastReadingAtUtc) p.LastReadingAtUtc = lastAfter;
    }

    /// <summary>
    /// Saves without the automatic history of the asset's latest values (Pressure 1.1 → 0.9 with no new reading would show
    /// a change to anyone who reads the history): the only trace is the internal reading row, seen by roles with
    /// readings.correctionsView, Engineer and Admin (<see cref="AssetQueries.HistoryAsync"/>).
    /// </summary>
    private async Task SaveInternalAsync(CancellationToken ct)
    {
        var suppressed = audit.SuppressFieldAudit;
        audit.SuppressFieldAudit = true;
        try { await db.SaveChangesAsync(ct); }
        finally { audit.SuppressFieldAudit = suppressed; }
    }

    private static string Dubai(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, SiteClock.Dubai).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Describe(Domain.Catalog.MeasurementType type, decimal value, DateTime measuredAtUtc) =>
        $"{value.ToString(CultureInfo.InvariantCulture)}{(type.UnitOfMeasure is { Length: > 0 } u ? " " + u : "")} measured {Dubai(measuredAtUtc)}";

    private ChangeLog History(Guid assetId, long readingId, string field, string? oldValue, string newValue, DateTime now) => new()
    {
        EntityType = HistoryEntity,
        EntityKey = readingId.ToString(CultureInfo.InvariantCulture),
        AssetId = assetId,
        Field = field,
        OldValue = oldValue,
        NewValue = newValue,
        ChangedAtUtc = now,
        ChangedBy = audit.UserName,
        Source = audit.Source,
        BatchId = audit.BatchId
    };
}
