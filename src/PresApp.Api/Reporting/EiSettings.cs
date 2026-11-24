using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Catalog;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Reporting;

/// <summary>
/// Part of the E&amp;I dashboard (24-nov): the instrument lists are far larger than the electrical ones, so each part has its
/// own figures. <see cref="Instrumentation"/> = items of the Instrumentation discipline; <see cref="Electrical"/> = every
/// other E&amp;I item (Electrical, and the few mixed E&amp;I items, which keep their own row in the discipline table).
/// <see cref="All"/> = both, the client Excel's E&amp;I Dashboard; its % is also given weighted by part.
/// </summary>
public enum EiPart
{
    All = 0,
    Electrical = 1,
    Instrumentation = 2
}

public static class EiParts
{
    public const string InstrumentationDiscipline = "Instrumentation";

    /// <summary>"electrical", "instrumentation" (or "instruments"); anything else (empty, "all", "combined") → All.</summary>
    public static EiPart Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "electrical" => EiPart.Electrical,
        "instrumentation" or "instruments" or "instrument" => EiPart.Instrumentation,
        _ => EiPart.All
    };

    public static string Key(EiPart part) => part switch
    {
        EiPart.Electrical => "electrical",
        EiPart.Instrumentation => "instrumentation",
        _ => "all"
    };

    public static string Label(EiPart part) => part switch
    {
        EiPart.Electrical => "Electrical",
        EiPart.Instrumentation => "Instrumentation",
        _ => "E&I"
    };

    /// <summary>Part of an E&amp;I item, by its discipline.</summary>
    public static EiPart Of(string discipline) =>
        string.Equals(discipline.Trim(), InstrumentationDiscipline, StringComparison.OrdinalIgnoreCase) ? EiPart.Instrumentation : EiPart.Electrical;

    public static bool Contains(EiPart part, string discipline) => part == EiPart.All || Of(discipline) == part;
}

/// <summary>
/// <paramref name="ElectricalWeight"/> (0–100) of the weighted E&amp;I %, instrumentation weighing the rest.
/// <paramref name="InstrumentationInReport"/>: the daily report counts the instruments too (off by default: electrical only).
/// </summary>
public sealed record EiSettingsDto(int ElectricalWeight, int InstrumentationWeight, bool InstrumentationInReport);

public sealed record SaveEiSettings(int ElectricalWeight, bool InstrumentationInReport);

/// <summary>E&amp;I settings of each unit, kept as settings (<see cref="AppSetting"/>); the assets never change.</summary>
public sealed class EiSettings(PresAppDbContext db)
{
    public const int DefaultElectricalWeight = 60;

    public static string WeightKey(int unitId) => $"EI.ElectricalWeight.{unitId}";
    public static string ReportKey(int unitId) => $"EI.ReportInstrumentation.{unitId}";

    public Task<EiSettingsDto> GetAsync(int unitId, CancellationToken ct) => ReadAsync(db, unitId, ct);

    public static async Task<EiSettingsDto> ReadAsync(PresAppDbContext db, int unitId, CancellationToken ct)
    {
        string[] keys = [WeightKey(unitId), ReportKey(unitId)];
        var values = await db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var weight = int.TryParse(values.GetValueOrDefault(WeightKey(unitId)), out var w) && w is >= 0 and <= 100 ? w : DefaultElectricalWeight;
        return new EiSettingsDto(weight, 100 - weight, values.GetValueOrDefault(ReportKey(unitId)) == "Yes");
    }

    /// <summary>The part of the E&amp;I items the daily report counts for the unit.</summary>
    public static async Task<EiPart> ReportPartAsync(PresAppDbContext db, int unitId, CancellationToken ct) =>
        (await ReadAsync(db, unitId, ct)).InstrumentationInReport ? EiPart.All : EiPart.Electrical;

    public async Task<EiSettingsDto> SetAsync(int unitId, SaveEiSettings request, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit not found.");
        if (request.ElectricalWeight is < 0 or > 100) throw ApiProblem.Invalid("The electrical weight must be between 0 and 100 %.");
        await SetAsync(WeightKey(unitId), request.ElectricalWeight.ToString(), "Weight (%) of electrical in the weighted E&I %; instrumentation weighs the rest.", ct);
        await SetAsync(ReportKey(unitId), request.InstrumentationInReport ? "Yes" : "No", "Instrumentation counted in the daily report (Yes) or electrical only.", ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(unitId, ct);
    }

    private async Task SetAsync(string key, string value, string description, CancellationToken ct)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Settings.Add(setting = new AppSetting { Key = key, Description = description });
        setting.Value = value;
    }
}
