using System.Text.RegularExpressions;

namespace PresApp.Domain.Reporting;

/// <summary>
/// Same rule as the Excel "Category" formula: an asset belongs to the category whose keyword appears in its
/// Preservation Remarks (case ignored, extra spaces ignored); when several match, the lowest priority wins.
/// </summary>
public sealed partial class CategoryMatcher
{
    private readonly List<(int Priority, int Order, DashboardCategory Category, string Keyword)> keywords;

    public CategoryMatcher(IEnumerable<DashboardCategory> categories)
    {
        keywords = categories.Where(c => c.IsActive)
            .SelectMany(c => c.Keywords.Select(k => (c.Priority, c.SortOrder, c, Normalize(k.Keyword))))
            .Where(x => x.Item4.Length > 0)
            .OrderBy(x => x.Priority).ThenBy(x => x.SortOrder)
            .ToList();
    }

    public DashboardCategory? Match(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks)) return null;
        var text = Normalize(remarks);
        foreach (var k in keywords)
        {
            if (text.Contains(k.Keyword, StringComparison.OrdinalIgnoreCase)) return k.Category;
        }
        return null;
    }

    /// <summary>Excel TRIM: no leading/trailing spaces, single spaces inside (line breaks kept, as in Excel).</summary>
    private static string Normalize(string s) => Spaces().Replace(s, " ").Trim();

    [GeneratedRegex(" +")]
    private static partial Regex Spaces();
}
