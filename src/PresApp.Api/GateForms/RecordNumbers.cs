using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.GateForms;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.GateForms;

public sealed record RecordNoCodeDto(int DisciplineId, string Discipline, string Register, string? Code);
public sealed record RecordNoCounterDto(int UnitNumber, string Code, int LastNumber, string Next);
public sealed record RecordNoSettingsDto(List<RecordNoCodeDto> Codes, List<RecordNoCounterDto> Counters);
public sealed record SaveRecordNoCode(string? Code);

/// <summary>
/// Record No. of the gate forms (ITR): <c>ITR-{discipline code}-{unit}-{number}</c>, e.g. ITR-STAT-541-001.
/// <list type="bullet">
/// <item>Each discipline has its code (Admin → Gate forms); without one, the Record No. of its assets is typed by hand.</item>
/// <item>One counter per code and unit. Printing an asset with no Record No. gives it the next number; reprints keep it.</item>
/// <item>A Record No. can always be typed by hand (forms made outside the app). It may not be another asset's, and when it
/// follows the pattern above the counter, the counter moves up so the next automatic one follows it.</item>
/// </list>
/// </summary>
public sealed class RecordNumbers(PresAppDbContext db)
{
    public async Task<RecordNoSettingsDto> SettingsAsync(CancellationToken ct)
    {
        var codes = await db.Disciplines.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.SortOrder)
            .Select(d => new RecordNoCodeDto(d.Id, d.Name, d.AssetType.ToString(), d.RecordNoCode)).ToListAsync(ct);
        var counter = await CounterAsync(tracking: false, ct);
        var counters = counter.Keys
            .Select(k => new RecordNoCounterDto(k.Unit, k.Code, counter.Last(k.Code, k.Unit), counter.Peek(k.Code, k.Unit)))
            .OrderBy(c => c.UnitNumber).ThenBy(c => c.Code).ToList();
        return new RecordNoSettingsDto(codes, counters);
    }

    public async Task<RecordNoCodeDto> SaveCodeAsync(int disciplineId, SaveRecordNoCode request, CancellationToken ct)
    {
        var d = await db.Disciplines.FirstOrDefaultAsync(x => x.Id == disciplineId, ct) ?? throw ApiProblem.NotFound("Discipline");
        var code = request.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) code = null;
        else if (!RecordNoFormat.IsCode(code)) throw ApiProblem.Invalid($"The code is letters and digits, at most {RecordNoFormat.CodeMaxLength}.");
        d.RecordNoCode = code;
        await db.SaveChangesAsync(ct);
        return new RecordNoCodeDto(d.Id, d.Name, d.AssetType.ToString(), d.RecordNoCode);
    }

    /// <summary>The Record Nos. in use and the counters. <paramref name="tracking"/>: numbers taken are saved with the next SaveChanges.</summary>
    internal async Task<RecordNoCounter> CounterAsync(bool tracking, CancellationToken ct)
    {
        var used = await db.GateFormEntries.AsNoTracking().Where(e => e.RecordNo != null).Select(e => new { e.AssetId, e.RecordNo }).ToListAsync(ct);
        var sequences = await (tracking ? db.RecordNoSequences : db.RecordNoSequences.AsNoTracking()).ToListAsync(ct);
        return new RecordNoCounter(tracking ? db : null, sequences, used.Select(u => (u.AssetId, u.RecordNo!)));
    }
}

/// <summary>Record Nos. in use (by asset) and the counters, while one print is prepared or saved.</summary>
internal sealed class RecordNoCounter
{
    private readonly PresAppDbContext? db;
    private readonly Dictionary<(string Code, int Unit), RecordNoSequence> sequences = [];
    private readonly Dictionary<(string Code, int Unit), int> highestUsed = [];
    private readonly Dictionary<string, HashSet<Guid>> holders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> byAsset = [];

    /// <param name="db">Context the new counters are added to; null = nothing is saved (preview).</param>
    public RecordNoCounter(PresAppDbContext? db, IEnumerable<RecordNoSequence> sequences, IEnumerable<(Guid AssetId, string RecordNo)> used)
    {
        this.db = db;
        foreach (var s in sequences) this.sequences[(s.Code, s.UnitNumber)] = s;
        foreach (var (assetId, recordNo) in used) Hold(assetId, recordNo);
    }

    public IEnumerable<(string Code, int Unit)> Keys => sequences.Keys.Union(highestUsed.Keys);

    /// <summary>Highest number given by the counter or found on an asset.</summary>
    public int Last(string code, int unit) =>
        Math.Max(sequences.GetValueOrDefault((code, unit))?.LastNumber ?? 0, highestUsed.GetValueOrDefault((code, unit)));

    /// <summary>What <see cref="Next"/> would give, without taking it.</summary>
    public string Peek(string code, int unit)
    {
        code = code.Trim().ToUpperInvariant();
        var n = Last(code, unit);
        string value;
        do value = RecordNoFormat.Build(code, unit, ++n);
        while (holders.ContainsKey(value));
        return value;
    }

    /// <summary>Takes the next number of the code and unit (never below one already used) for the asset.</summary>
    public string Next(string code, int unit, Guid assetId)
    {
        code = code.Trim().ToUpperInvariant();
        var value = Peek(code, unit);
        RecordNoFormat.TryParse(value, out _, out _, out var number);
        Sequence(code, unit).LastNumber = number;
        Hold(assetId, value);
        return value;
    }

    /// <summary>The asset (other than <paramref name="assetId"/>) that already has this Record No., if any.</summary>
    public Guid? HolderOtherThan(string recordNo, Guid assetId) =>
        holders.TryGetValue(recordNo.Trim(), out var set) ? set.Where(id => id != assetId).Cast<Guid?>().FirstOrDefault() : null;

    /// <summary>The asset now has this Record No. (typed by hand): it is taken, and the counter moves up to it.</summary>
    public void Use(Guid assetId, string recordNo)
    {
        Hold(assetId, recordNo);
        if (!RecordNoFormat.TryParse(recordNo, out var code, out var unit, out var number)) return;
        var s = Sequence(code, unit);
        if (s.LastNumber < number) s.LastNumber = number;
    }

    private void Hold(Guid assetId, string recordNo)
    {
        recordNo = recordNo.Trim();
        if (byAsset.TryGetValue(assetId, out var old) && holders.TryGetValue(old, out var oldSet)) oldSet.Remove(assetId);
        byAsset[assetId] = recordNo;
        if (!holders.TryGetValue(recordNo, out var set)) holders[recordNo] = set = [];
        set.Add(assetId);
        if (RecordNoFormat.TryParse(recordNo, out var code, out var unit, out var number) && highestUsed.GetValueOrDefault((code, unit)) < number)
            highestUsed[(code, unit)] = number;
    }

    private RecordNoSequence Sequence(string code, int unit)
    {
        if (sequences.TryGetValue((code, unit), out var s)) return s;
        s = new RecordNoSequence { Code = code, UnitNumber = unit };
        sequences[(code, unit)] = s;
        db?.RecordNoSequences.Add(s);
        return s;
    }
}
