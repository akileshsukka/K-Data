using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Monitoring;

public sealed record MonitoringSettingsDto(int IntervalDays, decimal? MinN2Barg);

public sealed record MonitoringRow(
    Guid Id, string Code, string? Tag, string? Description, string Type, string Discipline, string CurrentGate,
    string Phase, DateTime? PreservedAtUtc, decimal? N2Barg, DateTime? N2AtUtc, int? DaysSinceN2, decimal? PressureBarg,
    decimal? O2Pct, decimal? DewPointC, DateTime? LastReadingAtUtc, string Status, bool LowN2, IReadOnlyList<string> Groups);

public sealed record MonitoringSummary(int Post, int During, int Overdue, int NoReading, int LowN2, int Ok);

public sealed record MonitoringView(MonitoringSettingsDto Settings, MonitoringSummary Summary, List<MonitoringRow> Rows);

public sealed record RoundRow(Guid AssetId, IReadOnlyList<ReadingValue> Values);
public sealed record RoundRequest(DateTime MeasuredAtUtc, string? Note, IReadOnlyList<RoundRow> Rows);
public sealed record RoundProposal(Guid AssetId, string Code, string? Tag, string? CurrentGate, DateOnly? LastFieldActionDate);
public sealed record RoundResult(int Assets, int Readings, List<RoundProposal> Proposals);

/// <summary>
/// Monitoring of the N2 blanket. DURING = the asset is not marked as preserved yet; POST = on or after the date it was marked.
/// A POST asset is due when its last N2 blanket reading is older than the interval (setting, 7 days by default)
/// and low when that reading is below the minimum (setting, off until an engineer sets it).
/// </summary>
public sealed class MonitoringService(PresAppDbContext db, AssetEditor editor, TimeProvider clock)
{
    public const string Overdue = "Overdue";
    public const string NoReading = "No reading since preserved";
    public const string Ok = "OK";
    public const string During = "During preservation";
    public const string Removed = "Removed from site";
    private const string N2Key = "G4N2BlanketBarg";

    public async Task<MonitoringSettingsDto> SettingsAsync(CancellationToken ct)
    {
        var s = await db.Settings.AsNoTracking()
            .Where(x => x.Key == AppSettingKeys.MonitoringIntervalDays || x.Key == AppSettingKeys.MonitoringMinN2Barg)
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var days = int.TryParse(s.GetValueOrDefault(AppSettingKeys.MonitoringIntervalDays), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 7;
        decimal? min = decimal.TryParse(s.GetValueOrDefault(AppSettingKeys.MonitoringMinN2Barg), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : null;
        return new MonitoringSettingsDto(days, min);
    }

    public async Task<MonitoringSettingsDto> SaveSettingsAsync(MonitoringSettingsDto request, CancellationToken ct)
    {
        if (request.IntervalDays is < 1 or > 365) throw ApiProblem.Invalid("The interval must be between 1 and 365 days.");
        if (request.MinN2Barg is < 0 or > 100) throw ApiProblem.Invalid("The minimum N2 blanket pressure must be between 0 and 100 barg.");
        await SetAsync(AppSettingKeys.MonitoringIntervalDays, request.IntervalDays.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(AppSettingKeys.MonitoringMinN2Barg, request.MinN2Barg?.ToString(CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
        return await SettingsAsync(ct);
    }

    private async Task SetAsync(string key, string? value, CancellationToken ct)
    {
        var s = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (value is null) { if (s is not null) db.Settings.Remove(s); return; }
        if (s is null) db.Settings.Add(s = new AppSetting { Key = key });
        s.Value = value;
    }

    /// <summary>All active assets of the unit (or of one group), with their monitoring status.</summary>
    public async Task<MonitoringView> ViewAsync(int? unitId, int? groupId, CancellationToken ct)
    {
        if (unitId is null && groupId is null) throw ApiProblem.Invalid("Choose a unit or a group.");
        var settings = await SettingsAsync(ct);
        var n2Id = await db.MeasurementTypes.Where(t => t.Key == N2Key).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);

        var assets = db.Assets.AsNoTracking().Where(a => a.IsActive);
        if (unitId is { } u) assets = assets.Where(a => a.UnitId == u);
        if (groupId is { } g) assets = assets.Where(a => a.GroupMemberships.Any(m => m.GroupId == g));

        var rows = await assets.Select(a => new
        {
            a.Id, a.Code, a.Tag, a.Description, a.Type, Discipline = a.Discipline.Name, P = a.Preservation,
            Groups = a.GroupMemberships.Where(m => m.Group.IsActive).Select(m => m.Group.Code).ToList(),
            N2At = db.Readings.Where(r => r.AssetId == a.Id && r.MeasurementTypeId == n2Id).Max(r => (DateTime?)r.MeasuredAtUtc)
        }).ToListAsync(ct);

        var today = SiteClock.Today(clock);
        var result = rows.Select(r =>
        {
            var p = r.P;
            var post = p?.PreservedAtUtc is not null;
            int? days = r.N2At is { } at ? today.DayNumber - SiteClock.DateOf(at).DayNumber : null;
            var low = post && settings.MinN2Barg is { } min && p!.G4N2BlanketBarg is { } v && v < min;
            var status = p?.CurrentGate == CurrentGate.RemovedFromSite ? Removed
                : !post ? During
                : r.N2At is null || r.N2At < p!.PreservedAtUtc ? NoReading
                : days >= settings.IntervalDays ? Overdue
                : Ok;
            return new MonitoringRow(r.Id, r.Code, r.Tag, r.Description, r.Type.ToString(), r.Discipline,
                FieldValues.CurrentGateText(p?.CurrentGate ?? CurrentGate.New), post ? "Post" : "During", p?.PreservedAtUtc,
                p?.G4N2BlanketBarg, r.N2At, days, p?.PressureBarg, p?.G3O2Pct, p?.G2DewPointC, p?.LastReadingAtUtc, status, low, r.Groups);
        })
        .OrderBy(r => StatusRank(r.Status)).ThenByDescending(r => r.LowN2).ThenBy(r => r.Tag ?? r.Code, NaturalComparer.Instance)
        .ToList();

        var active = result.Where(r => r.Status != Removed).ToList();
        return new MonitoringView(settings, new MonitoringSummary(
            active.Count(r => r.Phase == "Post"), active.Count(r => r.Phase == "During"),
            active.Count(r => r.Status == Overdue), active.Count(r => r.Status == NoReading),
            active.Count(r => r.LowN2), active.Count(r => r.Status == Ok)), result);
    }

    private static int StatusRank(string s) => s switch { NoReading => 0, Overdue => 1, Ok => 2, During => 3, _ => 4 };

    /// <summary>
    /// A monitoring round: the readings of many assets measured at the same time, saved together (all or nothing).
    /// Each asset follows the same rules as a single reading (latest values, Last Field Action Date, DURING/POST).
    /// </summary>
    public async Task<RoundResult> SaveRoundAsync(RoundRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var rows = (request.Rows ?? []).Where(r => r.Values is { Count: > 0 }).ToList();
        if (rows.Count == 0) throw ApiProblem.Invalid("Enter at least one reading.");
        if (rows.Count > 500) throw ApiProblem.Invalid("Save at most 500 assets in one round.");
        if (rows.Select(r => r.AssetId).Distinct().Count() != rows.Count) throw ApiProblem.Invalid("An asset appears twice in the round.");

        var proposals = new List<RoundProposal>();
        var readings = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            AddReadingsResponse saved;
            try
            {
                saved = await editor.AddReadingsAsync(row.AssetId, new AddReadingsRequest(request.MeasuredAtUtc, request.Note, row.Values), user, ct);
            }
            catch (ApiProblem p) when (p.Status is 400 or 403 or 404)
            {
                var name = await db.Assets.Where(a => a.Id == row.AssetId).Select(a => a.Tag ?? a.Code).FirstOrDefaultAsync(ct) ?? "an asset";
                throw new ApiProblem(p.Status, p.Title, $"{name}: {p.Message} Nothing in this round was saved.");
            }
            readings += saved.Saved;
            if (saved.Proposals.CurrentGate is not null || saved.Proposals.LastFieldActionDate is not null)
                proposals.Add(new RoundProposal(row.AssetId, saved.Asset.Code, saved.Asset.Values.GetValueOrDefault("Tag"),
                    saved.Proposals.CurrentGate, saved.Proposals.LastFieldActionDate));
        }
        await tx.CommitAsync(ct);
        return new RoundResult(rows.Count, readings, proposals);
    }
}
