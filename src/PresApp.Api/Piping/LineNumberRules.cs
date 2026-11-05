using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Piping;

/// <summary>
/// Reads line numbers with the current catalogues (pipe specs, service and insulation codes) and decides whether a number
/// may be loaded. With the format check on (the default) only standard numbers are accepted, as always. With it off
/// (permission lines.nonStandard) a number that is not standard is accepted when its unit and sequence can be read;
/// the line is then marked as having a non-standard number.
/// </summary>
public sealed class LineNumberRules(HashSet<string> specs, HashSet<string> insulation, HashSet<string> services)
{
    public static async Task<LineNumberRules> LoadAsync(PresAppDbContext db, CancellationToken ct)
    {
        var specs = await db.PipeSpecs.AsNoTracking().Where(s => s.IsActive).Select(s => s.Code).ToListAsync(ct);
        var codes = await db.LineCodes.AsNoTracking().Where(c => c.IsActive).Select(c => new { c.Kind, c.Code }).ToListAsync(ct);
        HashSet<string> Of(LineCodeKind kind) => codes.Where(c => c.Kind == kind).Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new LineNumberRules(specs.ToHashSet(StringComparer.OrdinalIgnoreCase), Of(LineCodeKind.Insulation), Of(LineCodeKind.Service));
    }

    public LineNumberReading Read(string? lineNumber) => LineNumber.Read(lineNumber, specs.Contains, insulation.Contains, services.Contains);

    /// <summary>Why the number cannot be loaded into a unit, or null when it can.</summary>
    public static string? Refusal(LineNumberReading r, int unitNumber, string unitName, bool checkFormat, bool mayTurnCheckOff)
    {
        if (checkFormat && !r.IsStandard)
            return r.Problem + (mayTurnCheckOff ? " To load it as it is, turn off the line number format check." : "");
        if (r.Unit is null) return "The line number must start with the unit number, e.g. " + unitNumber + "-1009.";
        if (r.Unit != unitNumber) return $"The line number starts with {r.Unit}, but this is {unitName}.";
        if (r.Sequence is null) return "The line number needs at least the unit and the line sequence, e.g. " + unitNumber + "-1009.";
        return null;
    }
}

/// <summary>
/// Finds an existing line that may be the same as a line number: the same identity (unit, sequence, service, size, spec) for
/// two standard numbers, or <see cref="LineNumber.MayBeSameLine"/> when one of them is not standard.
/// </summary>
public sealed class LineMatcher<T> where T : class
{
    private readonly Dictionary<string, T> byIdentity = [];
    private readonly Dictionary<string, List<(LineNumberReading Reading, T Item)>> bySequence = [];

    public void Add(LineNumberReading r, T item)
    {
        if (r.Parts is { } p) byIdentity.TryAdd(LineNumber.IdentityKey(p), item);
        if (SequenceKey(r) is { } key)
        {
            if (!bySequence.TryGetValue(key, out var list)) bySequence[key] = list = [];
            list.Add((r, item));
        }
    }

    public T? Find(LineNumberReading r)
    {
        if (r.Parts is { } p && byIdentity.TryGetValue(LineNumber.IdentityKey(p), out var same)) return same;
        if (SequenceKey(r) is not { } key || !bySequence.TryGetValue(key, out var list)) return null;
        return list.FirstOrDefault(x => (!r.IsStandard || !x.Reading.IsStandard) && LineNumber.MayBeSameLine(r, x.Reading)).Item;
    }

    private static string? SequenceKey(LineNumberReading r) =>
        r.Unit is null || r.Sequence is null ? null : $"{r.Unit}|{r.Sequence.TrimStart('0')}";
}
