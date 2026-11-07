using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Api.Monitoring;
using PresApp.Domain.Monitoring;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>
/// Monitoring report of a unit (or one of its groups), to read and share; readings are loaded with <see cref="ReadingsWorkbook"/>.
/// E&amp;I assets are left out: they are reported in their own way.
/// <list type="number">
/// <item><b>Until preservation</b>: assets not marked as preserved, and preserved ones that had readings before. One row per asset and
/// measurement type (N2 blanket, dew point, O2...), one column per calendar day from the first reading to today, with the reading of that day.</item>
/// <item><b>Post-preservation</b>: preserved assets. One row per asset and measurement type. Week 1 starts with the first reading taken
/// after the asset was preserved and has up to 5 readings (its first 7 days); every later reading is the next week: Week 2, Week 3...</item>
/// <item><b>Readings</b>: every reading of every type, one per row, for filters and pivot tables.</item>
/// </list>
/// Days are in Dubai time. Assets removed from site only appear in Readings.
/// </summary>
public sealed class MonitoringReportWorkbook(PresAppDbContext db, MonitoringService monitoring, TimeProvider clock)
{
    private const int HeaderRow = 4;
    private const int MaxDays = 1500;
    private const int FirstWeekReadings = 5;
    private const string ElectricalInstrument = "ElectricalInstrument";
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E8E3F5");
    private static readonly XLColor FirstWeekFill = XLColor.FromHtml("#F3F0FA");
    private static readonly XLColor Reached = XLColor.FromHtml("#D7F0DD");
    private static readonly XLColor Low = XLColor.FromHtml("#FCE3C4");
    private static readonly XLColor Missing = XLColor.FromHtml("#F8D7D7");
    private static readonly XLColor NotApplicable = XLColor.FromHtml("#EEEEEE");
    private static readonly XLColor Separator = XLColor.FromHtml("#8A80A8");

    private sealed record Item(long Id, Guid AssetId, string Key, string Label, string? Unit, int SortOrder, decimal Value,
        DateTime MeasuredAtUtc, DateTime RecordedAtUtc, string RecordedBy, string Source, MonitoringPhase Phase, string? Note)
    {
        public DateTime Local => TimeZoneInfo.ConvertTimeFromUtc(MeasuredAtUtc, SiteClock.Dubai);
        public DateOnly Day => DateOnly.FromDateTime(Local);
    }

    public async Task<ExportResult> ExportAsync(int unitId, int? groupId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit not found.");
        if (from > to) throw ApiProblem.Invalid("The start date is after the end date.");
        var group = groupId is { } g
            ? await db.Groups.AsNoTracking().Where(x => x.Id == g).Select(x => x.Code + " — " + x.Name).FirstOrDefaultAsync(ct) ?? throw ApiProblem.NotFound("Group not found.")
            : null;

        var view = await monitoring.ViewAsync(unitId, groupId, ct);
        var settings = view.Settings;
        var target = settings.TargetN2Barg ?? MonitoringService.DefaultTargetN2Barg;
        var assets = view.Rows.Where(r => r.Type != ElectricalInstrument).ToList();
        var ids = assets.Select(r => r.Id).ToList();

        var query = db.Readings.AsNoTracking().Where(r => ids.Contains(r.AssetId));
        if (from is { } f) query = query.Where(r => r.MeasuredAtUtc >= ToUtc(f));
        if (to is { } t) query = query.Where(r => r.MeasuredAtUtc < ToUtc(t.AddDays(1)));
        var items = (await query.Select(r => new
            {
                r.Id, r.AssetId, r.MeasurementType.Key, r.MeasurementType.Label, r.MeasurementType.UnitOfMeasure, r.MeasurementType.SortOrder,
                r.Value, r.MeasuredAtUtc, r.RecordedAtUtc, r.RecordedBy, r.Source, r.Phase, r.Note
            }).ToListAsync(ct))
            .Select(r => new Item(r.Id, r.AssetId, r.Key, r.Label, r.UnitOfMeasure, r.SortOrder, r.Value, r.MeasuredAtUtc, r.RecordedAtUtc,
                r.RecordedBy, r.Source.ToString(), r.Phase, r.Note))
            .ToList();

        var today = SiteClock.Today(clock);
        var lastDay = to is { } end && end < today ? end : today;
        var rows = assets.Where(r => r.Status != MonitoringService.Removed).ToList();

        // Until preservation: readings taken before the asset was preserved. Post-preservation: readings taken since.
        var beforeReadings = items.Where(i => i.Phase == MonitoringPhase.During).ToLookup(i => i.AssetId);
        var during = rows.Where(r => r.Phase == "During" || beforeReadings[r.Id].Any()).ToList();
        var postReadings = items.Where(i => i.Phase == MonitoringPhase.Post).ToLookup(i => i.AssetId);
        var post = rows.Where(r => r.Phase == "Post").ToList();

        using var wb = new XLWorkbook();
        var generated = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, SiteClock.Dubai);
        var scope = group is null ? unit.Name : $"{unit.Name} · {group}";
        var period = from is null && to is null ? "all readings" : $"{(from is { } a ? Day(a) : "start")} to {(to is { } b ? Day(b) : "today")}";
        var firstDay = from ?? beforeReadings.SelectMany(x => x).Select(i => (DateOnly?)i.Day).Min();

        UntilPreservation(wb.AddWorksheet("Until preservation"), during, beforeReadings, target, firstDay, lastDay,
            $"Until preservation — {scope}",
            $"Every reading taken while the asset was not marked as preserved: one row per measurement, one column per day. When there are more readings in a day the last one is shown and a note lists them all. Green = N2 blanket target of {Num(target)} barg reached; grey = after it was preserved. Generated {generated:dd MMM yyyy HH:mm} (Dubai), {period}.");
        PostPreservation(wb.AddWorksheet("Post-preservation"), post, postReadings, settings,
            $"Post-preservation monitoring — {scope}",
            $"Readings since the asset was marked as preserved: one row per measurement. Week 1 starts with the first reading after it was preserved and holds up to {FirstWeekReadings} readings of its first 7 days; each later reading is the next week. The note of each cell has its date. Orange = Pressure below the minimum{(settings.MinPressureBarg is { } m ? $" ({Num(m)} barg)" : " (not set)")}. Due every {settings.IntervalDays} days. Generated {generated:dd MMM yyyy HH:mm} (Dubai), {period}.");
        Detail(wb.AddWorksheet("Readings"), assets, items, $"All readings — {scope}", $"One row per reading of every measurement type, {period}. Times in Dubai. E&I assets are not included.");

        var fileName = $"Unit_{unit.Number}_Monitoring_Report_{generated.ToString("dd_MMM_yyyy_HHmm", CultureInfo.InvariantCulture)}.xlsx";
        using var output = new MemoryStream();
        wb.SaveAs(output);
        return new ExportResult(fileName, output.ToArray(), Guid.Empty, rows.Count);
    }

    // ------------------------------------------------------------------ sheets

    private static void UntilPreservation(IXLWorksheet ws, List<MonitoringRow> rows, ILookup<Guid, Item> readings, decimal target,
        DateOnly? firstDay, DateOnly lastDay, string title, string legend)
    {
        string[] fixedHeaders = ["Tag", "Equipment ID", "Description", "Type", "Measurement", "Preserved on"];
        var days = Days(firstDay, lastDay);
        Titles(ws, title, legend);
        Headers(ws, fixedHeaders, days.Select(d => d.ToString("ddd\ndd-MMM-yy", CultureInfo.InvariantCulture)).ToList());

        var row = HeaderRow + 1;
        var assetStarts = new List<int>();
        foreach (var r in rows)
        {
            assetStarts.Add(row);
            var preservedDay = r.PreservedAtUtc is { } at ? SiteClock.DateOf(at) : (DateOnly?)null;
            var measurements = ByMeasurement(readings[r.Id]);
            if (measurements.Count == 0) { Fixed(ws, row, r, null, preservedDay); row++; continue; }
            foreach (var m in measurements)
            {
                Fixed(ws, row, r, m[0].Label, preservedDay);
                var byDay = m.GroupBy(i => i.Day).ToDictionary(x => x.Key, x => x.ToList());
                for (var d = 0; d < days.Count; d++)
                {
                    var cell = ws.Cell(row, fixedHeaders.Length + 1 + d);
                    if (byDay.TryGetValue(days[d], out var inDay))
                    {
                        SetNumber(cell, inDay[^1].Value);
                        cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                        if (inDay.Count > 1)
                        {
                            cell.Style.Font.SetBold();
                            cell.CreateComment().AddText($"{inDay.Count} readings this day, the last one is shown:\n" +
                                string.Join("\n", inDay.Select(i => $"{i.Local.ToString("HH:mm", CultureInfo.InvariantCulture)}: {Num(i.Value)}")));
                        }
                        if (m[0].Key == MonitoringService.N2Key && inDay[^1].Value >= target) cell.Style.Fill.SetBackgroundColor(Reached);
                    }
                    else if (preservedDay is { } pd && days[d] > pd) cell.Style.Fill.SetBackgroundColor(NotApplicable);
                }
                row++;
            }
        }
        Finish(ws, fixedHeaders.Length, days.Count, row - 1, assetStarts, dataWidth: 9.5);
    }

    private static void PostPreservation(IXLWorksheet ws, List<MonitoringRow> rows, ILookup<Guid, Item> readings, MonitoringSettingsDto settings,
        string title, string legend)
    {
        string[] fixedHeaders = ["Tag", "Equipment ID", "Description", "Type", "Measurement", "Preserved on", "Week 1 starts", "Status"];

        // Week 1: from the first reading after preservation (of any measurement of the asset), the readings of its first 7 days, at most 5.
        // Every later reading is one more week.
        var lines = new List<(MonitoringRow Asset, DateOnly? Start, List<Item>? Readings, List<Item> FirstWeek, List<Item> Weeks)>();
        foreach (var r in rows)
        {
            var all = readings[r.Id].ToList();
            DateOnly? start = all.Count > 0 ? all.Min(i => i.Day) : null;
            var measurements = ByMeasurement(all);
            if (measurements.Count == 0) { lines.Add((r, null, null, [], [])); continue; }
            foreach (var m in measurements)
            {
                var firstWeek = m.Where(i => i.Day < start!.Value.AddDays(7)).ToList();
                lines.Add((r, start, m, firstWeek, m.Skip(firstWeek.Count).ToList()));
            }
        }
        var laterWeeks = lines.Count == 0 ? 0 : lines.Max(l => l.Weeks.Count);
        var headers = Enumerable.Range(1, FirstWeekReadings).Select(n => $"Week 1\n#{n}")
            .Concat(Enumerable.Range(2, laterWeeks).Select(w => $"Week {w}")).ToList();

        Titles(ws, title, legend);
        Headers(ws, fixedHeaders, headers);
        var first = fixedHeaders.Length + 1;
        ws.Range(HeaderRow, first, HeaderRow, first + FirstWeekReadings - 1).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#D9D1F0"));

        var row = HeaderRow + 1;
        var assetStarts = new List<int>();
        Guid? previous = null;
        foreach (var (r, start, measurement, firstWeek, weeks) in lines)
        {
            if (r.Id != previous) assetStarts.Add(row);
            previous = r.Id;
            Fixed(ws, row, r, measurement?[0].Label, r.PreservedAtUtc is { } at ? SiteClock.DateOf(at) : null);
            if (start is { } s) SetDate(ws.Cell(row, 7), s);
            var status = r.Status == MonitoringService.Ok ? "OK" : r.Status;
            ws.Cell(row, 8).SetValue(r.LowPressure ? $"{status} · Low pressure" : status);
            if (r.Status != MonitoringService.Ok || r.LowPressure)
                ws.Cell(row, 8).Style.Fill.SetBackgroundColor(r.Status == MonitoringService.Ok ? Low : Missing);

            ws.Range(row, first, row, first + FirstWeekReadings - 1).Style.Fill.SetBackgroundColor(FirstWeekFill);
            for (var n = 0; n < Math.Min(firstWeek.Count, FirstWeekReadings); n++)
            {
                // More than 5 readings in week 1: the 5th cell shows the 5th one and its note lists the rest.
                var extra = n == FirstWeekReadings - 1 ? firstWeek.Skip(FirstWeekReadings).ToList() : [];
                Reading(ws.Cell(row, first + n), firstWeek[n], settings, extra);
            }
            for (var w = 0; w < weeks.Count; w++) Reading(ws.Cell(row, first + FirstWeekReadings + w), weeks[w], settings, []);
            row++;
        }
        Finish(ws, fixedHeaders.Length, headers.Count, row - 1, assetStarts, dataWidth: 10);
        ws.Column(7).Width = 13;
        ws.Column(8).Width = 26;
    }

    /// <summary>A post-preservation reading: its value, and its date in the note.</summary>
    private static void Reading(IXLCell cell, Item i, MonitoringSettingsDto settings, List<Item> extra)
    {
        SetNumber(cell, i.Value);
        cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        if (i.Key == MonitoringService.PressureKey && settings.MinPressureBarg is { } min && i.Value < min) cell.Style.Fill.SetBackgroundColor(Low);
        var note = Stamp(i);
        if (extra.Count > 0)
        {
            cell.Style.Font.SetBold();
            note += $"\n{extra.Count} more in week 1:\n" + string.Join("\n", extra.Select(x => $"{Stamp(x)}: {Num(x.Value)}"));
        }
        cell.CreateComment().AddText(note);
    }

    private static void Detail(IXLWorksheet ws, List<MonitoringRow> rows, List<Item> items, string title, string legend)
    {
        string[] headers = ["Tag", "Equipment ID", "Description", "Type", "Phase", "Measurement", "Value", "Unit", "Measured at", "Recorded at", "Recorded by", "Source", "Note"];
        Titles(ws, title, legend);
        Headers(ws, headers, []);
        var byId = rows.ToDictionary(r => r.Id);
        var row = HeaderRow + 1;
        foreach (var i in items.OrderBy(i => byId[i.AssetId].Tag ?? byId[i.AssetId].Code, NaturalComparer.Instance)
                     .ThenBy(i => i.MeasuredAtUtc).ThenBy(i => i.SortOrder).ThenBy(i => i.Id))
        {
            var a = byId[i.AssetId];
            ws.Cell(row, 1).SetValue(a.Tag ?? a.Code);
            ws.Cell(row, 2).SetValue(a.Code);
            ws.Cell(row, 3).SetValue(a.Description ?? "");
            ws.Cell(row, 4).SetValue(TypeText(a.Type));
            ws.Cell(row, 5).SetValue(i.Phase == MonitoringPhase.Post ? "POST" : "DURING");
            ws.Cell(row, 6).SetValue(i.Label);
            SetNumber(ws.Cell(row, 7), i.Value);
            ws.Cell(row, 8).SetValue(i.Unit ?? "");
            ws.Cell(row, 9).SetValue(i.Local);
            ws.Cell(row, 10).SetValue(TimeZoneInfo.ConvertTimeFromUtc(i.RecordedAtUtc, SiteClock.Dubai));
            ws.Cell(row, 11).SetValue(i.RecordedBy);
            ws.Cell(row, 12).SetValue(i.Source);
            ws.Cell(row, 13).SetValue(i.Note ?? "");
            row++;
        }
        ws.Range(HeaderRow + 1, 9, Math.Max(row - 1, HeaderRow + 1), 10).Style.NumberFormat.SetFormat("dd-mmm-yy hh:mm");
        if (row > HeaderRow + 1) ws.Range(HeaderRow, 1, row - 1, headers.Length).SetAutoFilter();
        ws.Columns(1, headers.Length).AdjustToContents(HeaderRow, Math.Min(row, HeaderRow + 500), 8, 45);
        ws.SheetView.Freeze(HeaderRow, 1);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The readings of an asset by measurement type, in the catalog order, each one oldest first.</summary>
    private static List<List<Item>> ByMeasurement(IEnumerable<Item> readings) =>
        readings.GroupBy(i => i.Key)
            .Select(x => x.OrderBy(i => i.MeasuredAtUtc).ThenBy(i => i.Id).ToList())
            .OrderBy(x => x[0].SortOrder).ThenBy(x => x[0].Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void Titles(IXLWorksheet ws, string title, string legend)
    {
        ws.Cell(1, 1).SetValue(title).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).SetValue(legend).Style.Font.SetFontColor(XLColor.FromHtml("#555555"));
    }

    private static void Headers(IXLWorksheet ws, string[] fixedHeaders, List<string> columns)
    {
        for (var c = 0; c < fixedHeaders.Length; c++) ws.Cell(HeaderRow, c + 1).SetValue(fixedHeaders[c]);
        for (var c = 0; c < columns.Count; c++)
            ws.Cell(HeaderRow, fixedHeaders.Length + 1 + c).SetValue(columns[c]).Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        var header = ws.Range(HeaderRow, 1, HeaderRow, Math.Max(1, fixedHeaders.Length + columns.Count));
        header.Style.Font.SetBold().Fill.SetBackgroundColor(HeaderFill).Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Center)
            .Border.SetBottomBorder(XLBorderStyleValues.Thin);
        ws.Row(HeaderRow).Height = 32;
    }

    /// <summary>Tag, Equipment ID, Description, Type, Measurement and Preserved on: repeated on every row of the asset, so filters keep them.</summary>
    private static void Fixed(IXLWorksheet ws, int row, MonitoringRow r, string? measurement, DateOnly? preservedOn)
    {
        ws.Cell(row, 1).SetValue(r.Tag ?? r.Code).Style.Font.SetBold();
        ws.Cell(row, 2).SetValue(r.Code);
        ws.Cell(row, 3).SetValue(r.Description ?? "");
        ws.Cell(row, 4).SetValue(TypeText(r.Type));
        if (measurement is not null) ws.Cell(row, 5).SetValue(measurement);
        else ws.Cell(row, 5).SetValue("No readings").Style.Font.SetItalic().Font.SetFontColor(XLColor.FromHtml("#777777"));
        if (preservedOn is { } p) SetDate(ws.Cell(row, 6), p);
    }

    private static string TypeText(string type) => type switch { "Pipeline" => "Line", ElectricalInstrument => "E&I", _ => type };

    private static void Finish(IXLWorksheet ws, int fixedColumns, int dataColumns, int lastRow, List<int> assetStarts, double dataWidth)
    {
        if (lastRow <= HeaderRow) ws.Cell(HeaderRow + 1, 1).SetValue("No assets in this sheet.").Style.Font.SetItalic();
        ws.Column(1).Width = 30;
        ws.Column(2).Width = 16;
        ws.Column(3).Width = 34;
        ws.Column(4).Width = 10;
        ws.Column(5).Width = 24;
        for (var c = 6; c <= fixedColumns; c++) ws.Column(c).Width = 13;
        for (var c = fixedColumns + 1; c <= fixedColumns + dataColumns; c++) ws.Column(c).Width = dataWidth;
        if (lastRow > HeaderRow)
        {
            var last = Math.Max(fixedColumns + dataColumns, fixedColumns);
            var table = ws.Range(HeaderRow, 1, lastRow, last);
            table.Style.Border.SetInsideBorder(XLBorderStyleValues.Hair).Border.SetOutsideBorder(XLBorderStyleValues.Thin)
                .Border.SetInsideBorderColor(XLColor.FromHtml("#CCCCCC"));
            // A darker line where each asset starts, so its measurements read as one block.
            foreach (var start in assetStarts.Where(s => s > HeaderRow + 1))
                ws.Range(start, 1, start, last).Style.Border.SetTopBorder(XLBorderStyleValues.Thin).Border.SetTopBorderColor(Separator);
            ws.Range(HeaderRow, 1, lastRow, fixedColumns).SetAutoFilter();
        }
        // Tag and measurement stay visible while scrolling to the right.
        ws.SheetView.Freeze(HeaderRow, 5);
    }

    /// <summary>A number as it was entered: 1 shows "1" (not "1."), 3.5 shows "3.5".</summary>
    private static void SetNumber(IXLCell cell, decimal value)
    {
        cell.SetValue(value);
        cell.Style.NumberFormat.SetFormat(value == decimal.Truncate(value) ? "0" : "0.###");
    }

    private static void SetDate(IXLCell cell, DateOnly day)
    {
        cell.SetValue(day.ToDateTime(TimeOnly.MinValue));
        cell.Style.NumberFormat.SetFormat("dd-mmm-yy");
    }

    /// <summary>The days from <paramref name="first"/> to <paramref name="last"/>, at most the last 1500.</summary>
    private static List<DateOnly> Days(DateOnly? first, DateOnly last)
    {
        if (first is not { } start || start > last) return [];
        var count = Math.Min(MaxDays, last.DayNumber - start.DayNumber + 1);
        return Enumerable.Range(0, count).Select(i => last.AddDays(i - count + 1)).ToList();
    }

    private static DateTime ToUtc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), SiteClock.Dubai);
    private static string Day(DateOnly d) => d.ToString("dd-MMM-yy", CultureInfo.InvariantCulture);
    private static string Stamp(Item i) => i.Local.ToString("ddd dd-MMM-yy HH:mm", CultureInfo.InvariantCulture);
    private static string Num(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
