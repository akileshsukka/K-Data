using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>The inactive assets chosen in a register (the rows shown with "Inactive only"), to be listed in their register's template.</summary>
public sealed record InactiveSelection(AssetType Type, IReadOnlyCollection<Guid> Ids);

/// <summary>
/// List of inactive assets in the template of their register (unit file for equipment and E&amp;I, line list for lines).
/// It is a read-only report: only the list sheet is kept (no dashboard, Summary or other visible sheet), two columns say
/// when and by whom each asset was deactivated, and the file has no session, so neither the update import nor the
/// onboarding accept it.
/// </summary>
public static class InactiveListExport
{
    public const string Kind = "InactiveList";
    public const int MaxAssets = 5000;

    public const string RejectionMessage =
        "This file is a list of inactive assets downloaded from K Data. It is a report only and cannot be imported. " +
        "To change an inactive asset, reactivate it in its register first.";

    /// <summary>True when the workbook is an inactive list (its _meta sheet says so).</summary>
    public static bool Is(XLWorkbook wb) =>
        string.Equals(WorkbookFormat.ReadMeta(wb).GetValueOrDefault("Kind"), Kind, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyCollection<Guid> Check(InactiveSelection selection)
    {
        var ids = selection.Ids.Distinct().ToList();
        if (ids.Count == 0) throw Hosting.ApiProblem.Invalid("There are no inactive assets on screen to download.");
        if (ids.Count > MaxAssets) throw Hosting.ApiProblem.Invalid($"At most {MaxAssets} inactive assets can be downloaded at once.");
        return ids;
    }

    /// <summary>Last deactivation of each asset (from the change log): when (UTC) and by whom.</summary>
    public static async Task<Dictionary<Guid, (DateTime At, string By)>> DeactivationsAsync(PresAppDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await db.ChangeLogs.AsNoTracking()
            .Where(c => c.AssetId != null && ids.Contains(c.AssetId.Value) && c.EntityType == nameof(Asset) && c.Field == nameof(Asset.IsActive) && c.NewValue == "False")
            .Select(c => new { AssetId = c.AssetId!.Value, c.ChangedAtUtc, c.ChangedBy })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.AssetId)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.ChangedAtUtc) is { } last ? (last.ChangedAtUtc, last.ChangedBy) : default);
    }

    /// <summary>
    /// Adds "Deactivated on" and "Deactivated by" at the end of the table, reading each row's asset ids from the hidden Key
    /// column (a grouped line row lists several ids). An asset deactivated before the change log existed is left blank.
    /// </summary>
    public static void AddDeactivationColumns(IXLTable table, int keyColumn, IReadOnlyDictionary<Guid, (DateTime At, string By)> deactivations, int rows)
    {
        var ws = table.Worksheet;
        table.InsertColumnsAfter(2);
        var headers = table.HeadersRow();
        var onColumn = headers.LastCell().Address.ColumnNumber - 1;
        var byColumn = onColumn + 1;
        ws.Cell(headers.RowNumber(), onColumn).Value = "Deactivated on";
        ws.Cell(headers.RowNumber(), byColumn).Value = "Deactivated by";
        var firstRow = headers.RowNumber() + 1;
        for (var i = 0; i < rows; i++)
        {
            var r = firstRow + i;
            var ids = ws.Cell(r, keyColumn).GetString().Split(PipingWorkbookExporter.KeySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty).ToList();
            var found = ids.Select(id => deactivations.TryGetValue(id, out var d) ? d : ((DateTime At, string By)?)null).ToList();
            var on = ws.Cell(r, onColumn);
            var by = ws.Cell(r, byColumn);
            if (found.Count == 1 && found[0] is { } single)
            {
                on.Value = TimeZoneInfo.ConvertTimeFromUtc(single.At, SiteClock.Dubai);
                on.Style.NumberFormat.Format = "dd mmm yyyy hh:mm";
                by.SetValue(single.By);
            }
            else if (found.Count > 1)
            {
                // A grouped line row: one value per line, in the order of its line numbers.
                on.SetValue(string.Join('\n', found.Select(d => d is { } x ? TimeZoneInfo.ConvertTimeFromUtc(x.At, SiteClock.Dubai).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) : "-")));
                by.SetValue(string.Join('\n', found.Select(d => d?.By ?? "-")));
                on.Style.Alignment.WrapText = true;
                by.Style.Alignment.WrapText = true;
            }
            else
            {
                on.Value = Blank.Value;
                by.Value = Blank.Value;
            }
        }
        ws.Column(onColumn).Width = 20;
        ws.Column(byColumn).Width = 34;
        ws.Column(onColumn).Unhide();
        ws.Column(byColumn).Unhide();
    }

    /// <summary>
    /// Keeps only the given list sheets and the hidden ones (categories, dropdown lists): dashboards, Summary and every other
    /// visible sheet are removed, with the names that point to them. The kept list's title gets "INACTIVE ASSETS".
    /// </summary>
    public static void KeepOnly(XLWorkbook wb, IReadOnlyCollection<IXLWorksheet> keep)
    {
        var removed = wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible && !keep.Contains(w)).ToList();
        var names = removed.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in wb.DefinedNames.Where(n => n.Ranges.Any(r => names.Contains(r.Worksheet.Name))).Select(n => n.Name).ToList())
            wb.DefinedNames.Delete(name);
        foreach (var ws in removed) ws.Delete();

        foreach (var ws in keep)
        {
            var headerRow = ws.Tables.Select(t => t.HeadersRow().RowNumber()).DefaultIfEmpty(1).Min();
            if (headerRow <= 1) continue;
            var title = ws.Range(1, 1, headerRow - 1, 26).CellsUsed()
                .FirstOrDefault(c => !c.HasFormula && c.Value.Type == XLDataType.Text && WorkbookFormat.CellText(c).Trim().Length > 0);
            if (title is not null) title.Value = "INACTIVE ASSETS - " + WorkbookFormat.CellText(title).TrimStart();
        }
    }

    /// <summary>Very hidden _meta sheet marking the file as an inactive list (no session: it can never be imported).</summary>
    public static void WriteMeta(XLWorkbook wb, string unitName, DateTime nowUtc, string exportedBy, AssetType type, int rows)
    {
        var meta = wb.Worksheets.Add(WorkbookFormat.MetaSheet);
        string[][] metaRows =
        [
            ["Kind", Kind],
            ["Unit", unitName],
            ["AssetType", type.ToString()],
            ["ExportedAtUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture)],
            ["ExportedBy", exportedBy],
            ["RowCount", rows.ToString(CultureInfo.InvariantCulture)],
            ["App", WorkbookFormat.AppName]
        ];
        for (var r = 0; r < metaRows.Length; r++)
        {
            meta.Cell(r + 1, 1).SetValue(metaRows[r][0]);
            meta.Cell(r + 1, 2).SetValue(metaRows[r][1]);
        }
        meta.Visibility = XLWorksheetVisibility.VeryHidden;
    }
}
