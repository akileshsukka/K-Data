using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;

namespace PresApp.Api.Excel;

/// <summary>Layout of the MasterList table at export time. Any difference at import stops the import.</summary>
public sealed record StructureSignature(string Table, int HeaderRow, int FirstColumn, List<string> Headers)
{
    public string ToJson() => JsonSerializer.Serialize(this, WorkbookFormat.Json);
    public static StructureSignature? FromJson(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StructureSignature>(json, WorkbookFormat.Json);
}

/// <summary>Values of one asset at export: what the engineer saw.</summary>
public sealed record BaseRow(Guid Key, string? Tag, string Code, Dictionary<string, string?> Values);

public sealed record BaseSnapshot(List<BaseRow> Rows)
{
    public string ToJson() => JsonSerializer.Serialize(this, WorkbookFormat.Json);
    public static BaseSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<BaseSnapshot>(json, WorkbookFormat.Json) ?? new BaseSnapshot([]);
}

/// <summary>Shared constants and helpers of the unit workbook.</summary>
public static class WorkbookFormat
{
    public const int FormatVersion = 3;
    public const string MetaSheet = "_meta";
    public const string ListsSheet = "_lists";
    public const string BaseSheet = "_base";
    public const string AppName = "K Data";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IXLTable? FindTable(XLWorkbook wb, string name) =>
        wb.Worksheets.SelectMany(ws => ws.Tables).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cell content as the engineer sees it, never through float. Formula cells give the value Excel saved
    /// (they are never recalculated here).
    /// </summary>
    public static string CellText(IXLCell cell)
    {
        var v = cell.HasFormula ? cell.CachedValue : cell.Value;
        return v.Type switch
        {
            XLDataType.Blank => "",
            XLDataType.Text => v.GetText(),
            XLDataType.Number => ((decimal)v.GetNumber()).ToString(CultureInfo.InvariantCulture),
            XLDataType.Boolean => v.GetBoolean() ? "TRUE" : "FALSE",
            XLDataType.DateTime => v.GetDateTime().ToString(v.GetDateTime().TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => v.GetTimeSpan().ToString(),
            XLDataType.Error => "",
            _ => cell.GetFormattedString()
        };
    }

    public static Dictionary<string, string> ReadMeta(XLWorkbook wb)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!wb.TryGetWorksheet(MetaSheet, out var ws)) return meta;
        foreach (var row in ws.RangeUsed()?.Rows() ?? Enumerable.Empty<IXLRangeRow>())
        {
            var k = WorkbookFormat.CellText(row.Cell(1)).Trim();
            if (k.Length > 0) meta[k] = WorkbookFormat.CellText(row.Cell(2)).Trim();
        }
        return meta;
    }
}
