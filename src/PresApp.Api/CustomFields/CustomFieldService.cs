using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.CustomFields;

public sealed record CustomFieldDto(int Id, string AssetType, string Key, string Label, string DataType, IReadOnlyList<string> Options,
    string? Section, int SortOrder, bool ShowInRegister, bool IsActive, int Values);

public sealed record SaveCustomFieldRequest(string AssetType, string Label, string DataType, IReadOnlyList<string>? Options, string? Section,
    int SortOrder, bool ShowInRegister, bool IsActive = true);

public sealed record CustomValueDto(string Key, string Label, string DataType, IReadOnlyList<string> Options, string? Section, string? Value);
public sealed record CustomChange(string Key, string? From, string? To);
public sealed record SaveCustomValuesRequest(IReadOnlyList<CustomChange> Changes);
public sealed record CustomConflict(string Key, string Label, string? Expected, string? Current);

/// <summary>
/// Fields an administrator adds to an asset type without code changes (the E&amp;I register first, but any type works).
/// Kinds: text, number, date, list (options kept like the other lists: removed options are deactivated, never deleted) and yes/no.
/// A field with values cannot change its kind; fields are deactivated, never deleted, so their history stays readable.
/// </summary>
public sealed partial class CustomFieldService(PresAppDbContext db)
{
    public async Task<List<CustomFieldDto>> ListAsync(AssetType? type, bool activeOnly, CancellationToken ct)
    {
        var q = db.FieldDefinitions.AsNoTracking().Include(f => f.OptionSet).ThenInclude(o => o!.Values).AsQueryable();
        if (type is { } t) q = q.Where(f => f.AssetType == t);
        if (activeOnly) q = q.Where(f => f.IsActive);
        var defs = await q.ToListAsync(ct);
        var ids = defs.Select(d => d.Id).ToList();
        var counts = await db.FieldValues.Where(v => ids.Contains(v.FieldDefinitionId))
            .GroupBy(v => v.FieldDefinitionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return defs.OrderBy(d => d.AssetType).ThenBy(d => d.Section ?? "").ThenBy(d => d.SortOrder).ThenBy(d => d.Label)
            .Select(d => ToDto(d, counts.GetValueOrDefault(d.Id))).ToList();
    }

    public async Task<CustomFieldDto> SaveAsync(int? id, SaveCustomFieldRequest r, CancellationToken ct)
    {
        if (!Enum.TryParse<AssetType>(r.AssetType, out var type)) throw ApiProblem.Invalid("Choose the register (asset type).");
        if (!Enum.TryParse<FieldDataType>(r.DataType, out var kind)) throw ApiProblem.Invalid("Choose the kind of field.");
        var label = ValueNormalizer.CleanText(r.Label) ?? throw ApiProblem.Invalid("Enter the name of the field.");
        if (label.Length > 128) throw ApiProblem.Invalid("The name allows at most 128 characters.");
        var section = ValueNormalizer.CleanText(r.Section);
        if (section?.Length > 64) throw ApiProblem.Invalid("The section allows at most 64 characters.");
        if (PreservationFields.All.Any(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase)))
            throw ApiProblem.Invalid($"\"{label}\" is already a standard field.");

        var def = id is { } x
            ? await db.FieldDefinitions.Include(f => f.OptionSet).ThenInclude(o => o!.Values).FirstOrDefaultAsync(f => f.Id == x, ct) ?? throw ApiProblem.NotFound("Field")
            : new FieldDefinition { AssetType = type, Key = await NewKeyAsync(type, label, ct) };
        if (id is not null)
        {
            if (def.AssetType != type) throw ApiProblem.Invalid("A field cannot move to another register.");
            if (IsFixed(def) && (def.DataType != kind || !r.IsActive))
                throw ApiProblem.Invalid($"{def.Label} is used by the Pipeline Register and the piping Excel: it cannot be deactivated or change kind. Its name and list values can be edited.");
            if (def.DataType != kind && await db.FieldValues.AnyAsync(v => v.FieldDefinitionId == def.Id, ct))
                throw ApiProblem.Invalid("This field already has values: its kind cannot change. Create a new field instead.");
        }
        if (await db.FieldDefinitions.AnyAsync(f => f.Id != def.Id && f.AssetType == type && f.Label == label, ct))
            throw new ApiProblem(409, "Already used", $"There is already a field \"{label}\" in this register.");

        def.Label = label;
        def.DataType = kind;
        def.Section = section;
        def.SortOrder = r.SortOrder;
        def.ShowInRegister = r.ShowInRegister;
        def.IsActive = r.IsActive;
        def.MaxLength = kind == FieldDataType.Text ? FieldDefinition.DefaultTextLength : null;

        if (kind == FieldDataType.List)
        {
            var options = (r.Options ?? []).Select(ValueNormalizer.CleanText).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (options.Count == 0) throw ApiProblem.Invalid("A list needs at least one option.");
            if (options.Any(o => o.Length > 200)) throw ApiProblem.Invalid("Each option allows at most 200 characters.");
            def.OptionSet ??= new OptionSet { Key = $"CF.{type}.{def.Key}", Label = label };
            def.OptionSet.Label = label;
            foreach (var v in def.OptionSet.Values) v.IsActive = options.Contains(v.Value, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < options.Count; i++)
            {
                var existing = def.OptionSet.Values.FirstOrDefault(v => string.Equals(v.Value, options[i], StringComparison.OrdinalIgnoreCase));
                if (existing is null) def.OptionSet.Values.Add(new OptionValue { Value = options[i], SortOrder = i, IsActive = true });
                else { existing.SortOrder = i; existing.Value = options[i]; }
            }
        }
        if (id is null) db.FieldDefinitions.Add(def);
        await db.SaveChangesAsync(ct);
        return ToDto(def, await db.FieldValues.CountAsync(v => v.FieldDefinitionId == def.Id, ct));
    }

    /// <summary>Pipeline fields the app relies on by key (register indicators, piping Excel and Summary).</summary>
    public static bool IsFixed(FieldDefinition def) =>
        def.AssetType == AssetType.Pipeline && def.Key is PipingFieldKeys.InspectionDone or PipingFieldKeys.CleaningRequired or PipingFieldKeys.Cleaned;

    private async Task<string> NewKeyAsync(AssetType type, string label, CancellationToken ct)
    {
        var baseKey = NonAlnum().Replace(label, " ").Trim();
        baseKey = string.Concat(baseKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        if (baseKey.Length == 0) baseKey = "Field";
        if (baseKey.Length > 56) baseKey = baseKey[..56];
        var used = (await db.FieldDefinitions.Where(f => f.AssetType == type).Select(f => f.Key).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = baseKey;
        for (var n = 2; used.Contains(key); n++) key = $"{baseKey}{n}";
        return key;
    }

    // ------------------------------------------------------------------ values

    public async Task<List<CustomValueDto>> ValuesAsync(Guid assetId, CancellationToken ct)
    {
        var type = await db.Assets.Where(a => a.Id == assetId).Select(a => (AssetType?)a.Type).FirstOrDefaultAsync(ct) ?? throw ApiProblem.NotFound("The asset");
        var defs = await ListDefinitionsAsync(type, ct);
        var values = await db.FieldValues.AsNoTracking().Where(v => v.AssetId == assetId).ToDictionaryAsync(v => v.FieldDefinitionId, ct);
        return defs.Select(d => new CustomValueDto(d.Key, d.Label, d.DataType.ToString(), ActiveOptions(d), d.Section,
            values.TryGetValue(d.Id, out var v) ? Format(d, v) : null)).ToList();
    }

    public async Task<List<CustomValueDto>> SaveValuesAsync(Guid assetId, SaveCustomValuesRequest request, CancellationToken ct)
    {
        var asset = await db.Assets.Include(a => a.FieldValues).FirstOrDefaultAsync(a => a.Id == assetId, ct) ?? throw ApiProblem.NotFound("The asset");
        if (!asset.IsActive) throw ApiProblem.Forbidden("The asset is inactive.");
        var defs = (await ListDefinitionsAsync(asset.Type, ct)).ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<CustomConflict>();
        var errors = new Dictionary<string, string[]>();
        foreach (var c in request.Changes ?? [])
        {
            if (!defs.TryGetValue(c.Key, out var def)) { errors[c.Key] = ["This field does not exist in this register."]; continue; }
            var row = asset.FieldValues.FirstOrDefault(v => v.FieldDefinitionId == def.Id);
            var current = row is null ? null : Format(def, row);
            if (!SameText(current, ValueNormalizer.CleanText(c.From))) { conflicts.Add(new CustomConflict(def.Key, def.Label, c.From, current)); continue; }
            var error = Write(def, asset, row, c.To);
            if (error is not null) errors[def.Key] = [error];
        }
        if (errors.Count > 0) throw ApiProblem.Invalid(string.Join(" ", errors.Values.SelectMany(e => e)), new { errors });
        if (conflicts.Count > 0) throw ApiProblem.Conflict("Someone changed these fields after you opened them. Check the current values.", new { conflicts });
        await db.SaveChangesAsync(ct);
        return await ValuesAsync(assetId, ct);
    }

    /// <summary>Validates and writes one value (empty clears it). Returns an error message or null.</summary>
    public string? Write(FieldDefinition def, Asset asset, AssetFieldValue? row, string? raw)
    {
        var text = ValueNormalizer.CleanText(raw);
        row ??= asset.FieldValues.FirstOrDefault(v => v.FieldDefinitionId == def.Id);
        if (text is null)
        {
            if (row is not null) { row.ValueText = null; row.ValueNumber = null; row.ValueDate = null; }
            return null;
        }
        string? vText = null; decimal? vNumber = null; DateOnly? vDate = null;
        switch (def.DataType)
        {
            case FieldDataType.Text:
                if (text.Length > (def.MaxLength ?? FieldDefinition.DefaultTextLength)) return $"{def.Label} allows at most {def.MaxLength ?? FieldDefinition.DefaultTextLength} characters.";
                vText = text; break;
            case FieldDataType.Number:
                if (!ValueNormalizer.TryParseDecimal(text, out var n)) return $"{def.Label} must be a number.";
                vNumber = n; break;
            case FieldDataType.Date:
                if (!DateOnly.TryParseExact(text.Length > 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return $"{def.Label} must be a date (yyyy-MM-dd).";
                vDate = d; break;
            case FieldDataType.YesNo:
                vText = ValueNormalizer.ListKey(text) switch { "yes" or "y" or "true" or "1" => "Yes", "no" or "n" or "false" or "0" => "No", _ => null };
                if (vText is null) return $"{def.Label} must be Yes or No.";
                break;
            case FieldDataType.List:
                vText = ActiveOptions(def).FirstOrDefault(o => ValueNormalizer.ListKey(o) == ValueNormalizer.ListKey(text));
                if (vText is null) return $"\"{text}\" is not an option of {def.Label}.";
                break;
        }
        if (row is null)
        {
            row = new AssetFieldValue { AssetId = asset.Id, FieldDefinitionId = def.Id, FieldDefinition = def };
            asset.FieldValues.Add(row);
        }
        row.ValueText = vText; row.ValueNumber = vNumber; row.ValueDate = vDate;
        return null;
    }

    public Task<List<FieldDefinition>> ListDefinitionsAsync(AssetType type, CancellationToken ct) =>
        db.FieldDefinitions.Include(f => f.OptionSet).ThenInclude(o => o!.Values)
            .Where(f => f.AssetType == type && f.IsActive).OrderBy(f => f.Section).ThenBy(f => f.SortOrder).ThenBy(f => f.Label).ToListAsync(ct);

    public static string? Format(FieldDefinition def, AssetFieldValue v) => def.DataType switch
    {
        FieldDataType.Number => v.ValueNumber?.ToString("0.############", CultureInfo.InvariantCulture),
        FieldDataType.Date => v.ValueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => v.ValueText
    };

    private static bool SameText(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal)
        || (decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y) && x == y);

    private static List<string> ActiveOptions(FieldDefinition d) =>
        d.OptionSet?.Values.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(v => v.Value).ToList() ?? [];

    private static CustomFieldDto ToDto(FieldDefinition d, int values) => new(d.Id, d.AssetType.ToString(), d.Key, d.Label, d.DataType.ToString(),
        ActiveOptions(d), d.Section, d.SortOrder, d.ShowInRegister, d.IsActive, values);

    [GeneratedRegex("[^0-9A-Za-z]+")]
    private static partial Regex NonAlnum();
}
