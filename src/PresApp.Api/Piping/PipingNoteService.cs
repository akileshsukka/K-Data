using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Piping;

public sealed record PipingNoteDto(int Id, DateOnly? Date, string Text, string UpdatedBy, DateTime UpdatedAtUtc);

/// <summary>Date null = the general note. <c>Expected</c> = the text the user saw (null for a new note).</summary>
public sealed record SavePipingNoteRequest(DateOnly? Date, string? Text, string? Expected);

/// <summary>
/// Notes under the Summary of the piping line list. Written in the app or in the Excel; in the Excel each dated note
/// starts with a line "27 Oct:" and the general note comes first. Notes are deactivated, never deleted.
/// </summary>
public sealed partial class PipingNoteService(PresAppDbContext db)
{
    public async Task<List<PipingNoteDto>> ListAsync(int unitId, CancellationToken ct) =>
        (await db.PipingNotes.AsNoTracking().Where(n => n.UnitId == unitId && n.IsActive).ToListAsync(ct))
        .OrderBy(n => n.NoteDate.HasValue).ThenBy(n => n.NoteDate)
        .Select(n => new PipingNoteDto(n.Id, n.NoteDate, n.Text, n.UpdatedBy, n.UpdatedAtUtc)).ToList();

    /// <summary>Creates, changes or (empty text) removes the note of that date.</summary>
    public async Task<List<PipingNoteDto>> SaveAsync(int unitId, SavePipingNoteRequest r, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit");
        var text = Clean(r.Text);
        if (text?.Length > PipingNote.TextLength) throw ApiProblem.Invalid($"A note allows at most {PipingNote.TextLength} characters.");
        var note = await db.PipingNotes.FirstOrDefaultAsync(n => n.UnitId == unitId && n.IsActive && n.NoteDate == r.Date, ct);
        if (!Same(note?.Text, r.Expected))
            throw ApiProblem.Conflict("Someone else changed this note while you were editing. Check it and try again.");
        if (text is null)
        {
            if (note is not null) note.IsActive = false;
        }
        else if (note is null) db.PipingNotes.Add(new PipingNote { UnitId = unitId, NoteDate = r.Date, Text = text });
        else note.Text = text;
        await db.SaveChangesAsync(ct);
        return await ListAsync(unitId, ct);
    }

    // ------------------------------------------------------------------ text rules shared with the Excel

    /// <summary>Line breaks kept, spaces trimmed, empty lines dropped. Empty → null.</summary>
    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => ValueNormalizer.CleanText(l)).OfType<string>().ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    public static bool Same(string? a, string? b) => (Clean(a) ?? "") == (Clean(b) ?? "");

    /// <summary>Key of a note in the export snapshot: "" for the general note, yyyy-MM-dd otherwise.</summary>
    public static string Key(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public static DateOnly? FromKey(string key) =>
        key.Length == 0 ? null : DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"27 Oct:" as the engineers write it.</summary>
    public static string Header(DateOnly date) => date.ToString("d MMM", CultureInfo.InvariantCulture) + ":";

    public static string Label(DateOnly? date) => date is { } d ? $"Summary note {d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}" : "Summary note (general)";

    /// <summary>
    /// Reads the lines under the Summary: a line "26 Oct:" (or "26 Oct 2026:", or a date cell) starts the note of that date;
    /// lines before the first date are the general note. The year is the one of <paramref name="reference"/>, or the year
    /// before when that would put the note more than two months after it.
    /// </summary>
    public static Dictionary<string, string> Parse(IEnumerable<string> lines, DateOnly reference)
    {
        var notes = new Dictionary<string, List<string>>();
        var current = "";
        foreach (var raw in lines)
        {
            var line = ValueNormalizer.CleanText(raw);
            if (line is null) continue;
            if (TryDate(line, reference, out var date))
            {
                current = Key(date);
                notes.TryAdd(current, []);
                continue;
            }
            if (!notes.TryGetValue(current, out var list)) notes[current] = list = [];
            list.Add(line);
        }
        return notes.Where(n => n.Value.Count > 0).ToDictionary(n => n.Key, n => string.Join("\n", n.Value));
    }

    /// <summary>
    /// Notes written under the Summary sheet of a piping line list: the lines of column B after the first empty row
    /// under "All lines". Null when the workbook has no such Summary.
    /// </summary>
    public static Dictionary<string, string>? ReadSummary(XLWorkbook wb, DateOnly reference)
    {
        // A file downloaded from K Data marks where the notes start (name KD_Notes); it moves with inserted rows.
        if (wb.DefinedNames.TryGetValue(Excel.PipingTemplate.NotesName, out var name) && name.Ranges.FirstOrDefault()?.FirstCell() is { } start)
        {
            var sheet = start.Worksheet;
            var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
            var column = start.Address.ColumnNumber;
            return Parse(Enumerable.Range(start.Address.RowNumber, Math.Max(0, last - start.Address.RowNumber + 1))
                .Select(r => Excel.WorkbookFormat.CellText(sheet.Cell(r, column))), reference);
        }
        if (!wb.TryGetWorksheet(Excel.PipingLayout.SummarySheet, out var summary)) return null;
        var used = summary.LastRowUsed()?.RowNumber() ?? 0;
        var row = 1;
        while (row <= used && !string.Equals(Excel.WorkbookFormat.CellText(summary.Cell(row, 2)).Trim(), "All lines", StringComparison.OrdinalIgnoreCase)) row++;
        if (row > used) return null;
        while (row <= used && Excel.WorkbookFormat.CellText(summary.Cell(row, 2)).Trim().Length > 0) row++;
        return Parse(Enumerable.Range(row, Math.Max(0, used - row + 1)).Select(r => Excel.WorkbookFormat.CellText(summary.Cell(r, 2))), reference);
    }

    private static bool TryDate(string line, DateOnly reference, out DateOnly date)
    {
        date = default;
        if (DateOnly.TryParseExact(line.TrimEnd(':', ' '), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        var m = DateHeader().Match(line);
        if (!m.Success) return false;
        var text = $"{m.Groups["day"].Value} {m.Groups["month"].Value} {(m.Groups["year"].Success ? m.Groups["year"].Value : reference.Year.ToString(CultureInfo.InvariantCulture))}";
        string[] formats = ["d MMM yyyy", "d MMMM yyyy", "d MMM yy", "d MMMM yy"];
        if (!DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return false;
        if (!m.Groups["year"].Success && date > reference.AddMonths(2)) date = date.AddYears(-1);
        return true;
    }

    // "26 Oct:", "26-Oct:", "26 October 2026 :" — the colon is required so a sentence is never read as a date.
    [GeneratedRegex(@"^(?<day>\d{1,2})[\s\-/]*(?<month>[A-Za-z]{3,9})[\s\-/,]*(?<year>\d{4}|\d{2})?\s*:\s*$")]
    private static partial Regex DateHeader();
}
