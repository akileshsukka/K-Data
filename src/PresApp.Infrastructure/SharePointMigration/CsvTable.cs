namespace PresApp.Infrastructure.SharePointMigration;

/// <summary>A SharePoint "Export to CSV" file: header row + data rows, quoted multi-line values allowed.</summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> columns;

    private CsvTable(string name, string[] header, List<string[]> rows)
    {
        Name = name;
        Header = header;
        Rows = rows;
        columns = header.Select((h, i) => (h.Trim(), i))
            .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }
    public string[] Header { get; }
    public List<string[]> Rows { get; }

    public bool Has(string column) => columns.ContainsKey(column);

    public string Get(string[] row, string column) =>
        columns.TryGetValue(column, out var i) && i < row.Length ? row[i] : "";

    public static CsvTable Load(string path)
    {
        var records = Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        var header = records.Count > 0 ? records[0] : [];
        if (header.Length > 0) header[0] = header[0].TrimStart('\uFEFF');
        var rows = records.Skip(1).Where(r => !r.All(string.IsNullOrWhiteSpace)).ToList();
        return new CsvTable(Path.GetFileName(path), header, rows);
    }

    /// <summary>
    /// RFC 4180 reader. Written by hand because TextFieldParser drops empty lines inside quoted values,
    /// which lost the blank lines of multi-line remarks.
    /// </summary>
    internal static List<string[]> Parse(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' or '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add([.. fields]);
                    fields.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add([.. fields]);
        }
        return records;
    }
}
