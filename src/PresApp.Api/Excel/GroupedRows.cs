using PresApp.Api.Assets;
using PresApp.Domain.Assets;
using PresApp.Domain.Rules;

namespace PresApp.Api.Excel;

/// <summary>
/// Parts of a split item (same <see cref="Asset.ClientTag"/>, e.g. P-101A and P-101B of "P-101A/B") are one row of the
/// client Excel, as the client lists them. The row shows the client tag and, per column, one value made from the parts:
/// <list type="bullet">
/// <item>Statuses (gates and the other lists): the most advanced one, Completed &gt; Ongoing &gt; other values &gt; Hold &gt; empty.
/// Not Applicable only when every part is N/A (a part that does not apply does not hold the row back).</item>
/// <item>Current Gate: the highest (Removed from Site only when every part is removed).</item>
/// <item>Dates: the first one (the part that got there first); Last Reading: the latest.</item>
/// <item>Latest readings: those of the part measured last.</item>
/// <item>Text: once when the parts agree; otherwise "P-101A: … / P-101B: …".</item>
/// <item>Hold / N/A category: the same when the parts agree; otherwise none when a part has none (it is available),
/// else Hold before N/A.</item>
/// </list>
/// The import uses the same rules: a cell still reading as the row was written is not a change.
/// </summary>
public static class GroupedRows
{
    /// <summary>
    /// Assets in rows, in the order given: the active parts of a split item join the row of the first of them; any
    /// other asset is a row of its own. Parts are only joined within one asset type (one list of the file).
    /// </summary>
    public static List<IReadOnlyList<Asset>> Group(IEnumerable<Asset> assets) =>
        assets.Select((a, i) => (Asset: a, Key: a.ClientTag is { } c ? $"{a.Type}|{ValueNormalizer.ListKey(c)}" : $"#{i}"))
            .GroupBy(x => x.Key)
            .Select(g => (IReadOnlyList<Asset>)g.Select(x => x.Asset).ToList())
            .ToList();

    /// <summary>The tag of the row: the client tag of a split item, otherwise the asset's tag.</summary>
    public static string? RowTag(IReadOnlyList<Asset> row) => row[0].ClientTag ?? row[0].Tag;

    /// <summary>Value of one column for a row of several parts (one part: its own value).</summary>
    public static string? Value(FieldDef field, IReadOnlyList<Asset> row)
    {
        if (row.Count == 1) return FieldValues.Read(field, row[0]);
        var type = Nullable.GetUnderlyingType(field.Property.PropertyType) ?? field.Property.PropertyType;
        if (type == typeof(decimal))
        {
            // Latest readings: from the part measured last that has this one.
            var latest = row.Where(a => FieldValues.Read(field, a) is not null)
                .OrderByDescending(a => a.Preservation?.LastReadingAtUtc ?? DateTime.MinValue).FirstOrDefault();
            return latest is null ? null : FieldValues.Read(field, latest);
        }
        return Combine(field, row.Select(a => (a.Tag ?? a.Code, FieldValues.Read(field, a))).ToList());
    }

    /// <summary>Value of one column from the parts' values (tag, value), as <see cref="Value"/> without the readings.</summary>
    public static string? Combine(FieldDef field, IReadOnlyList<(string Tag, string? Value)> parts)
    {
        var values = parts.Select(p => ValueNormalizer.CleanText(p.Value)).ToList();
        if (values.Count == 0) return null;
        if (values.All(v => FieldValues.Same(field, v, values[0]))) return parts[0].Value;

        if (field.Key == nameof(AssetPreservation.CurrentGate))
        {
            var gates = values.Select(v => FieldValues.TryParseCurrentGate(v, out var g) ? g : CurrentGate.New).ToList();
            var onSite = gates.Where(g => g != CurrentGate.RemovedFromSite).ToList();
            return FieldValues.CurrentGateText(onSite.Count == 0 ? CurrentGate.RemovedFromSite : onSite.Max());
        }
        var type = Nullable.GetUnderlyingType(field.Property.PropertyType) ?? field.Property.PropertyType;
        if (type == typeof(DateOnly) || type == typeof(DateTime))
        {
            // Sortable text (yyyy-MM-dd, ISO 8601): the first date, or the latest reading.
            var dates = values.Where(v => v is not null).Order(StringComparer.Ordinal).ToList();
            return dates.Count == 0 ? null : field.Key == nameof(AssetPreservation.LastReadingAtUtc) ? dates[^1] : dates[0];
        }
        if (field.Kind == FieldKind.Choice) return Status(values);

        // Free text: each part's own, labelled with its tag.
        var said = parts.Select((p, i) => (p.Tag, Value: values[i])).Where(p => p.Value is not null).ToList();
        if (said.Count == 0) return null;
        if (said.All(p => p.Value == said[0].Value)) return said[0].Value;
        var text = string.Join(" / ", said.Select(p => $"{p.Tag}: {p.Value}"));
        return field.MaxLength > 0 && text.Length > field.MaxLength ? text[..field.MaxLength] : text;
    }

    /// <summary>The most advanced status: Completed &gt; Ongoing &gt; any other value &gt; Hold &gt; empty; N/A only when all are N/A.</summary>
    public static string? Status(IReadOnlyList<string?> values)
    {
        var applicable = values.Where(v => v is null || !ValueNormalizer.IsNotApplicable(v)).ToList();
        if (applicable.Count == 0) return values[0];
        return applicable.OrderByDescending(Rank).First();

        static int Rank(string? v) => v is null ? 0
            : AssetPreservation.IsCompleted(v) ? 4
            : string.Equals(v.Trim(), "Ongoing", StringComparison.OrdinalIgnoreCase) ? 3
            : string.Equals(v.Trim(), "Hold", StringComparison.OrdinalIgnoreCase) ? 1
            : 2;
    }

    /// <summary>
    /// Hold / N/A category of a row from the parts' categories (any comparable form, e.g. the Excel token or the code
    /// with its section; null = none): the same when they agree; none when a part has none; else a Hold one before an N/A one.
    /// </summary>
    public static T? Category<T>(IReadOnlyList<T?> parts, Func<T, bool> isHold) where T : class
    {
        if (parts.All(p => Equals(p, parts[0]))) return parts[0];
        if (parts.Any(p => p is null)) return null;
        return parts.FirstOrDefault(p => isHold(p!)) ?? parts[0];
    }
}
