using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Catalog;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Piping;

/// <summary>
/// Meanings of the active line codes, read for one unit: the exception of that unit when there is one, otherwise the
/// general meaning. A code with no general meaning and no exception for the unit has no meaning there.
/// </summary>
public sealed class LineCodeMeanings
{
    private readonly Dictionary<(LineCodeKind Kind, string Code, int? UnitId), string> meanings;

    private LineCodeMeanings(IEnumerable<LineCode> codes) =>
        meanings = codes.GroupBy(c => (c.Kind, c.Code.ToUpperInvariant(), c.UnitId)).ToDictionary(g => g.Key, g => g.First().Description);

    public static async Task<LineCodeMeanings> LoadAsync(PresAppDbContext db, CancellationToken ct) =>
        new(await db.LineCodes.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct));

    /// <summary>Builds the meanings from a list of codes (inactive codes are left out).</summary>
    public static LineCodeMeanings From(IEnumerable<LineCode> codes) => new(codes.Where(c => c.IsActive));

    public string? Describe(LineCodeKind kind, string? code, int unitId)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var key = code.Trim().ToUpperInvariant();
        return meanings.GetValueOrDefault((kind, key, unitId)) ?? meanings.GetValueOrDefault((kind, key, null));
    }

    public string? Service(string? code, int unitId) => Describe(LineCodeKind.Service, code, unitId);
}
