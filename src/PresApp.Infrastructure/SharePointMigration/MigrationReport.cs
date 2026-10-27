using System.Text;

namespace PresApp.Infrastructure.SharePointMigration;

/// <summary>What the SharePoint migration loaded, converted and flagged. Written as Markdown.</summary>
public sealed class MigrationReport
{
    public sealed record Issue(string Category, string? AssetCode, string? Field, string? Value, string Message);
    public sealed record UnitCount(string Unit, int InCsv, int InDatabase);
    public sealed record FieldCount(string Field, int InCsv, int InDatabase);

    public bool Applied { get; set; }
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public Dictionary<string, int> Loaded { get; } = new();
    public Dictionary<string, int> Conversions { get; } = new();
    public List<Issue> Issues { get; } = [];
    public List<UnitCount> Units { get; } = [];
    public List<FieldCount> Fields { get; } = [];

    public void Count(string what, int n = 1) => Loaded[what] = Loaded.GetValueOrDefault(what) + n;
    public void Converted(string what) => Conversions[what] = Conversions.GetValueOrDefault(what) + 1;
    public void Flag(string category, string? asset, string? field, string? value, string message) =>
        Issues.Add(new Issue(category, asset, field, value, message));

    public bool CountsMatch => Units.All(u => u.InCsv == u.InDatabase) && Fields.All(f => f.InCsv == f.InDatabase);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# SharePoint migration report");
        sb.AppendLine();
        sb.AppendLine($"- Run: {StartedAtUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- Mode: {(Applied ? "APPLIED (data replaced)" : "PREVIEW (nothing saved)")}");
        sb.AppendLine($"- Verification: {(CountsMatch ? "all counts match" : "DIFFERENCES FOUND — see below")}");
        sb.AppendLine();
        sb.AppendLine("## Loaded");
        sb.AppendLine("| Item | Rows |\n| --- | --- |");
        foreach (var (k, v) in Loaded) sb.AppendLine($"| {k} | {v} |");
        sb.AppendLine();
        sb.AppendLine("## Assets per unit (CSV vs PresApp)");
        sb.AppendLine("| Unit | CSV | PresApp | |\n| --- | --- | --- | --- |");
        foreach (var u in Units) sb.AppendLine($"| {u.Unit} | {u.InCsv} | {u.InDatabase} | {(u.InCsv == u.InDatabase ? "OK" : "DIFFERENT")} |");
        sb.AppendLine();
        sb.AppendLine("## Filled values per field (CSV vs PresApp)");
        sb.AppendLine("| Field | CSV | PresApp | |\n| --- | --- | --- | --- |");
        foreach (var f in Fields) sb.AppendLine($"| {f.Field} | {f.InCsv} | {f.InDatabase} | {(f.InCsv == f.InDatabase ? "OK" : "DIFFERENT")} |");
        sb.AppendLine();
        sb.AppendLine("## Conversions applied");
        sb.AppendLine("| Conversion | Times |\n| --- | --- |");
        foreach (var (k, v) in Conversions.OrderByDescending(x => x.Value)) sb.AppendLine($"| {k} | {v} |");
        sb.AppendLine();
        sb.AppendLine($"## Items to review ({Issues.Count})");
        sb.AppendLine("| Category | Asset | Field | Value | Note |\n| --- | --- | --- | --- | --- |");
        foreach (var i in Issues.OrderBy(i => i.Category).ThenBy(i => i.AssetCode))
        {
            sb.AppendLine($"| {i.Category} | {i.AssetCode} | {i.Field} | {Esc(i.Value)} | {Esc(i.Message)} |");
        }
        return sb.ToString();
    }

    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");
}
