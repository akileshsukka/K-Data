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
/// <list type="number">
/// <item><b>Until preservation</b>: assets not marked as preserved, and preserved ones that had N2 blanket readings before.
/// One column per ISO week with the last G4 - N2 Blanket reading of the week, green once it reaches the target.</item>
/// <item><b>Post-preservation</b>: preserved assets, with their current monitoring status first, then one column per ISO week with
/// the last Pressure (barg) reading of the week: orange below the minimum, MISSING for a past week without a reading.</item>
/// <item><b>Readings</b>: every reading, one per row, for filters and pivot tables.</item>
/// </list>
/// Weeks are ISO weeks (Monday to Sunday) in Dubai time. Assets removed from site only appear in Readings.
/// </summary>
public sealed class MonitoringReportWorkbook(PresAppDbContext db, MonitoringService monitoring, TimeProvider clock)
{
    private const int HeaderRow = 4;
    private const int MaxWeeks = 160;
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E8E3F5");
    private static readonly XLColor Reached = XLColor.FromHtml("#D7F0DD");
    private static readonly XLColor Low = XLColor.FromHtml("#FCE3C4");
    private static readonly XLColor Missing = XLColor.FromHtml("#F8D7D7");
    private static readonly XLColor NotApplicable = XLColor.FromHtml("#EEEEEE");

    private sealed record Item(long Id, Guid AssetId, string Key, string Label, string? Unit, decimal Value, DateTime MeasuredAtUtc,
        DateTime RecordedAtUtc, string RecordedBy, string Source, MonitoringPhase Phase, string? Note)
    {
        public DateTime Local => TimeZoneInfo.ConvertTimeFromUtc(MeasuredAtUtc, SiteClock.Dubai);
        public DateOnly Week => WeekOf(DateOnly.FromDateTime(Local));
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
        var ids = view.Rows.Select(r => r.Id).ToList();

        var query = db.Readings.AsNoTracking().Where(r => ids.Contains(r.AssetId));
        if (from is { } f) query = query.Where(r => r.MeasuredAtUtc >= ToUtc(f));
        if (to is { } t) query = query.Where(r => r.MeasuredAtUtc < ToUtc(t.AddDays(1)));
        var items = (await query.Select(r => new
            {
                r.Id, r.AssetId, r.MeasurementType.Key, r.MeasurementType.Label, r.MeasurementType.UnitOfMeasure, r.Value, r.MeasuredAtUtc,
                r.RecordedAtUtc, r.RecordedBy, r.Source, r.Phase, r.Note
            }).ToListAsync(ct))
            .Select(r => new Item(r.Id, r.AssetId, r.Key, r.Label, r.UnitOfMeasure, r.Value, r.MeasuredAtUtc, r.RecordedAtUtc, r.RecordedBy,
                r.Source.ToString(), r.Phase, r.Note))
            .ToList();

        var today = SiteClock.Today(clock);
        var lastWeek = WeekOf(to is { } end && end < today ? end : today);
        var rows = view.Rows.Where(r => r.Status != MonitoringService.Removed).ToList();

        // Until preservation: N2 blanket readings taken before the asset was preserved.
        var n2 = items.Where(i => i.Key == MonitoringService.N2Key && i.Phase == MonitoringPhase.During).ToLookup(i => i.AssetId);
        var during = rows.Where(r => r.Phase == "During" || n2[r.Id].Any()).ToList();
        // Post-preservation: Pressure readings taken since.
        var pressure = items.Where(i => i.Key == MonitoringService.PressureKey && i.Phase == MonitoringPhase.Post).ToLookup(i => i.AssetId);
        var post = rows.Where(r => r.Phase == "Post").ToList();

        using var wb = new XLWorkbook();
        var generated = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, SiteClock.Dubai);
        var scope = group is null ? unit.Name : $"{unit.Name} · {group}";
        var period = from is null && to is null ? "all readings" : $"{(from is { } a ? Day(a) : "start")} to {(to is { } b ? Day(b) : "today")}";

        UntilPreservation(wb.AddWorksheet("Until preservation"), during, n2, target, FirstWeek(from, n2.SelectMany(x => x)), lastWeek,
            $"Until preservation — {scope}", $"G4 N2 blanket while the asset is not marked as preserved. Last reading of each ISO week (Monday to Sunday); green = target of {Num(target)} barg reached. Generated {generated:dd MMM yyyy HH:mm} (Dubai), {period}.");
        PostPreservation(wb.AddWorksheet("Post-preservation"), post, pressure, settings, from, lastWeek, today,
            $"Post-preservation monitoring — {scope}",
            $"Pressure (barg) since the asset was marked as preserved. Last reading of each ISO week; orange = below the minimum{(settings.MinPressureBarg is { } m ? $" ({Num(m)} barg)" : " (not set)")}; MISSING = a full week without a reading; grey = before it was preserved. Due every {settings.IntervalDays} days. Generated {generated:dd MMM yyyy HH:mm} (Dubai), {period}.");
        Detail(wb.AddWorksheet("Readings"), view.Rows, items, $"All readings — {scope}", $"One row per reading, {period}. Times in Dubai.");

        var fileName = $"Unit_{unit.Number}_Monitoring_Report_{generated.ToString("dd_MMM_yyyy_HHmm", CultureInfo.InvariantCulture)}.xlsx";
        using var output = new MemoryStream();
        wb.SaveAs(output);
        return new ExportResult(fileName, output.ToArray(), Guid.Empty, rows.Count);
    }

    // ------------------------------------------------------------------ sheets

    private static void UntilPreservation(IXLWorksheet ws, List<MonitoringRow> rows, ILookup<Guid, Item> n2, decimal target,
        DateOnly? firstWeek, DateOnly lastWeek, string title, string legend)
    {
        string[] fixedHeaders = ["Tag", "Equipment ID", "Description", "Type", "Workpack", "Target (barg)", "Reached", "Reached on", "Preserved on", "Last N2 (barg)"];
        var weeks = Weeks(firstWeek, lastWeek);
        Titles(ws, title, legend);
        Headers(ws, fixedHeaders, weeks);

        var row = HeaderRow + 1;
        foreach (var r in rows)
        {
            var readings = n2[r.Id].OrderBy(i => i.MeasuredAtUtc).ToList();
            var reached = readings.FirstOrDefault(i => i.Value >= target);
            Identity(ws, row, r);
            ws.Cell(row, 6).SetValue(target);
            ws.Cell(row, 7).SetValue(reached is not null ? "Yes" : readings.Count == 0 ? "No reading" : "No");
            if (reached is not null) { ws.Cell(row, 7).Style.Fill.SetBackgroundColor(Reached); SetDate(ws.Cell(row, 8), reached.Local); }
            if (r.PreservedAtUtc is { } p) SetDate(ws.Cell(row, 9), TimeZoneInfo.ConvertTimeFromUtc(p, SiteClock.Dubai));
            if (readings.LastOrDefault() is { } last) ws.Cell(row, 10).SetValue(last.Value);

            var preservedWeek = r.PreservedAtUtc is { } at ? WeekOf(SiteClock.DateOf(at)) : (DateOnly?)null;
            var byWeek = readings.GroupBy(i => i.Week).ToDictionary(x => x.Key, x => x.ToList());
            for (var w = 0; w < weeks.Count; w++)
            {
                var cell = ws.Cell(row, fixedHeaders.Length + 1 + w);
                if (byWeek.TryGetValue(weeks[w], out var inWeek))
                {
                    WeekValue(cell, inWeek);
                    if (inWeek[^1].Value >= target) cell.Style.Fill.SetBackgroundColor(Reached);
                }
                else if (preservedWeek is { } pw && weeks[w] > pw) cell.Style.Fill.SetBackgroundColor(NotApplicable);
            }
            row++;
        }
        Finish(ws, fixedHeaders.Length, weeks.Count, row - 1, numberColumns: [6, 10]);
    }

    private static void PostPreservation(IXLWorksheet ws, List<MonitoringRow> rows, ILookup<Guid, Item> pressure, MonitoringSettingsDto settings,
        DateOnly? from, DateOnly lastWeek, DateOnly today, string title, string legend)
    {
        string[] fixedHeaders = ["Tag", "Equipment ID", "Description", "Type", "Workpack", "Preserved on", "Last pressure (barg)", "Last reading", "Days since", "Status"];
        var preservedWeeks = rows.Where(r => r.PreservedAtUtc is not null).Select(r => WeekOf(SiteClock.DateOf(r.PreservedAtUtc!.Value))).ToList();
        DateOnly? first = from is { } f ? WeekOf(f) : preservedWeeks.Count > 0 ? preservedWeeks.Min() : null;
        var weeks = Weeks(first, lastWeek);
        var currentWeek = WeekOf(today);
        Titles(ws, title, legend);
        Headers(ws, fixedHeaders, weeks);

        var row = HeaderRow + 1;
        foreach (var r in rows)
        {
            Identity(ws, row, r);
            var preservedLocal = TimeZoneInfo.ConvertTimeFromUtc(r.PreservedAtUtc!.Value, SiteClock.Dubai);
            SetDate(ws.Cell(row, 6), preservedLocal);
            if (r.DaysSincePressure is not null && r.PressureBarg is { } v)
            {
                ws.Cell(row, 7).SetValue(v);
                if (r.LowPressure) ws.Cell(row, 7).Style.Fill.SetBackgroundColor(Low);
                SetDate(ws.Cell(row, 8), TimeZoneInfo.ConvertTimeFromUtc(r.PressureAtUtc!.Value, SiteClock.Dubai));
                ws.Cell(row, 9).SetValue(r.DaysSincePressure.Value);
            }
            var status = r.Status == MonitoringService.Ok ? "OK" : r.Status;
            ws.Cell(row, 10).SetValue(r.LowPressure ? $"{status} · Low pressure" : status);
            if (r.Status != MonitoringService.Ok || r.LowPressure)
                ws.Cell(row, 10).Style.Fill.SetBackgroundColor(r.Status == MonitoringService.Ok ? Low : Missing);

            var preservedWeek = WeekOf(DateOnly.FromDateTime(preservedLocal));
            var byWeek = pressure[r.Id].OrderBy(i => i.MeasuredAtUtc).GroupBy(i => i.Week).ToDictionary(x => x.Key, x => x.ToList());
            for (var w = 0; w < weeks.Count; w++)
            {
                var cell = ws.Cell(row, fixedHeaders.Length + 1 + w);
                if (byWeek.TryGetValue(weeks[w], out var inWeek))
                {
                    WeekValue(cell, inWeek);
                    if (settings.MinPressureBarg is { } min && inWeek[^1].Value < min) cell.Style.Fill.SetBackgroundColor(Low);
                }
                else if (weeks[w] < preservedWeek) cell.Style.Fill.SetBackgroundColor(NotApplicable);
                // The week it was preserved and the current week are not over: an empty cell there is not missing yet.
                else if (weeks[w] > preservedWeek && weeks[w] < currentWeek)
                {
                    cell.SetValue("MISSING");
                    cell.Style.Fill.SetBackgroundColor(Missing).Font.SetFontColor(XLColor.FromHtml("#9B1C1C")).Font.SetBold();
                }
            }
            row++;
        }
        Finish(ws, fixedHeaders.Length, weeks.Count, row - 1, numberColumns: [7, 9]);
    }

    private static void Detail(IXLWorksheet ws, List<MonitoringRow> rows, List<Item> items, string title, string legend)
    {
        string[] headers = ["Tag", "Equipment ID", "Description", "Phase", "Measurement", "Value", "Unit", "Measured at", "Recorded at", "Recorded by", "Source", "Note"];
        Titles(ws, title, legend);
        Headers(ws, headers, []);
        var byId = rows.ToDictionary(r => r.Id);
        var row = HeaderRow + 1;
        foreach (var i in items.OrderBy(i => byId[i.AssetId].Tag ?? byId[i.AssetId].Code, NaturalComparer.Instance).ThenBy(i => i.MeasuredAtUtc).ThenBy(i => i.Id))
        {
            var a = byId[i.AssetId];
            ws.Cell(row, 1).SetValue(a.Tag ?? a.Code);
            ws.Cell(row, 2).SetValue(a.Code);
            ws.Cell(row, 3).SetValue(a.Description ?? "");
            ws.Cell(row, 4).SetValue(i.Phase == MonitoringPhase.Post ? "POST" : "DURING");
            ws.Cell(row, 5).SetValue(i.Label);
            ws.Cell(row, 6).SetValue(i.Value);
            ws.Cell(row, 7).SetValue(i.Unit ?? "");
            ws.Cell(row, 8).SetValue(i.Local);
            ws.Cell(row, 9).SetValue(TimeZoneInfo.ConvertTimeFromUtc(i.RecordedAtUtc, SiteClock.Dubai));
            ws.Cell(row, 10).SetValue(i.RecordedBy);
            ws.Cell(row, 11).SetValue(i.Source);
            ws.Cell(row, 12).SetValue(i.Note ?? "");
            row++;
        }
        ws.Range(HeaderRow + 1, 8, Math.Max(row - 1, HeaderRow + 1), 9).Style.NumberFormat.SetFormat("dd-mmm-yy hh:mm");
        ws.Range(HeaderRow + 1, 6, Math.Max(row - 1, HeaderRow + 1), 6).Style.NumberFormat.SetFormat("0.###");
        if (row > HeaderRow + 1) ws.Range(HeaderRow, 1, row - 1, headers.Length).SetAutoFilter();
        ws.Columns(1, headers.Length).AdjustToContents(HeaderRow, Math.Min(row, HeaderRow + 500), 8, 45);
        ws.SheetView.Freeze(HeaderRow, 1);
    }

    // ------------------------------------------------------------------ helpers

    private static void Titles(IXLWorksheet ws, string title, string legend)
    {
        ws.Cell(1, 1).SetValue(title).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).SetValue(legend).Style.Font.SetFontColor(XLColor.FromHtml("#555555"));
    }

    private static void Headers(IXLWorksheet ws, string[] fixedHeaders, List<DateOnly> weeks)
    {
        for (var c = 0; c < fixedHeaders.Length; c++) ws.Cell(HeaderRow, c + 1).SetValue(fixedHeaders[c]);
        for (var w = 0; w < weeks.Count; w++)
        {
            var monday = weeks[w].ToDateTime(TimeOnly.MinValue);
            var cell = ws.Cell(HeaderRow, fixedHeaders.Length + 1 + w);
            cell.SetValue($"W{ISOWeek.GetWeekOfYear(monday):00} {ISOWeek.GetYear(monday)}\n{Day(weeks[w])}");
            cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        }
        var header = ws.Range(HeaderRow, 1, HeaderRow, Math.Max(1, fixedHeaders.Length + weeks.Count));
        header.Style.Font.SetBold().Fill.SetBackgroundColor(HeaderFill).Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Center)
            .Border.SetBottomBorder(XLBorderStyleValues.Thin);
        ws.Row(HeaderRow).Height = 32;
    }

    private static void Identity(IXLWorksheet ws, int row, MonitoringRow r)
    {
        ws.Cell(row, 1).SetValue(r.Tag ?? r.Code).Style.Font.SetBold();
        ws.Cell(row, 2).SetValue(r.Code);
        ws.Cell(row, 3).SetValue(r.Description ?? "");
        ws.Cell(row, 4).SetValue(r.Type switch { "Pipeline" => "Line", "ElectricalInstrument" => "E&I", _ => r.Type });
        ws.Cell(row, 5).SetValue(string.Join(", ", r.Groups));
    }

    /// <summary>The last reading of the week; when there were more, a note lists them all.</summary>
    private static void WeekValue(IXLCell cell, List<Item> inWeek)
    {
        cell.SetValue(inWeek[^1].Value);
        cell.Style.NumberFormat.SetFormat("0.###").Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        if (inWeek.Count < 2) return;
        cell.Style.Font.SetBold();
        cell.CreateComment().AddText($"{inWeek.Count} readings this week, the last one is shown:\n" +
            string.Join("\n", inWeek.Select(i => $"{i.Local.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)}: {Num(i.Value)}")));
    }

    private static void Finish(IXLWorksheet ws, int fixedColumns, int weekColumns, int lastRow, int[] numberColumns)
    {
        if (lastRow <= HeaderRow) ws.Cell(HeaderRow + 1, 1).SetValue("No assets in this sheet.").Style.Font.SetItalic();
        foreach (var c in numberColumns) ws.Range(HeaderRow + 1, c, Math.Max(lastRow, HeaderRow + 1), c).Style.NumberFormat.SetFormat("0.###");
        ws.Column(1).Width = 30;
        ws.Column(2).Width = 16;
        ws.Column(3).Width = 34;
        ws.Column(4).Width = 10;
        ws.Column(5).Width = 14;
        for (var c = 6; c <= fixedColumns; c++) ws.Column(c).Width = 13;
        for (var c = fixedColumns + 1; c <= fixedColumns + weekColumns; c++) ws.Column(c).Width = 11;
        if (lastRow > HeaderRow)
        {
            var table = ws.Range(HeaderRow, 1, lastRow, fixedColumns + weekColumns);
            table.Style.Border.SetInsideBorder(XLBorderStyleValues.Hair).Border.SetOutsideBorder(XLBorderStyleValues.Thin)
                .Border.SetInsideBorderColor(XLColor.FromHtml("#CCCCCC"));
            ws.Range(HeaderRow, 1, lastRow, fixedColumns).SetAutoFilter();
        }
        // Tag stays visible while scrolling to the weeks; the newest week is at the right end.
        ws.SheetView.Freeze(HeaderRow, 1);
    }

    private static void SetDate(IXLCell cell, DateTime local)
    {
        cell.SetValue(local.Date);
        cell.Style.NumberFormat.SetFormat("dd-mmm-yy");
    }

    private static DateOnly? FirstWeek(DateOnly? from, IEnumerable<Item> items)
    {
        if (from is { } f) return WeekOf(f);
        var first = items.Select(i => (DateOnly?)i.Week).Min();
        return first;
    }

    /// <summary>The ISO weeks (their Mondays) from <paramref name="first"/> to <paramref name="last"/>, at most the last 160.</summary>
    private static List<DateOnly> Weeks(DateOnly? first, DateOnly last)
    {
        if (first is not { } start || start > last) return [];
        var count = Math.Min(MaxWeeks, (last.DayNumber - start.DayNumber) / 7 + 1);
        return Enumerable.Range(0, count).Select(i => last.AddDays(-7 * (count - 1 - i))).ToList();
    }

    /// <summary>Monday of the ISO week of a day.</summary>
    internal static DateOnly WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateTime ToUtc(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), SiteClock.Dubai);
    private static string Day(DateOnly d) => d.ToString("dd-MMM-yy", CultureInfo.InvariantCulture);
    private static string Num(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
