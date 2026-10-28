using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Imports;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>
/// Client templates per unit. When a unit gets its first template and has no dashboard categories yet,
/// the categories are taken from the template (Categories table + the Action By / Email Action / note
/// columns of the dashboard), so the app becomes their single place from then on.
/// </summary>
public sealed record PipingTemplateDto(bool IsDefault, string FileName, DateTime? UploadedAtUtc, string? UploadedBy, List<string> Notes);

public sealed partial class ExcelTemplateService(PresAppDbContext db, TimeProvider clock)
{
    public async Task<List<TemplateDto>> ListAsync(CancellationToken ct)
    {
        var units = await db.Units.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Number)
            .Select(u => new { u.Id, u.Name }).ToListAsync(ct);
        var templates = await db.ExcelTemplates.AsNoTracking()
            .Select(t => new { t.UnitId, t.FileName, t.UploadedAtUtc, t.UploadedBy }).ToDictionaryAsync(t => t.UnitId, ct);
        var categories = await db.DashboardCategories.AsNoTracking().Where(c => c.IsActive)
            .GroupBy(c => c.UnitId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return units.Select(u =>
        {
            templates.TryGetValue(u.Id, out var t);
            return new TemplateDto(u.Id, u.Name, t?.FileName, t?.UploadedAtUtc, t?.UploadedBy, categories.GetValueOrDefault(u.Id));
        }).ToList();
    }

    public async Task<TemplateDto> UploadAsync(int unitId, Stream file, string fileName, string user, CancellationToken ct)
    {
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit not found.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > ExcelImportService.MaxFileBytes) throw ApiProblem.Invalid("Upload an .xlsx file of at most 20 MB.");
        var bytes = buffer.ToArray();

        List<DashboardCategory> fromTemplate = [];
        List<TemplateCategory> v4Categories = [];
        bool v4;
        using (var wb = UnitWorkbookExporter.OpenTemplate(bytes))
        {
            v4 = TemplateV4.IsV4(wb);
            if (v4) v4Categories = TemplateV4.ReadCategories(wb);
            else fromTemplate = ReadCategories(wb, unitId);
        }

        var template = await db.ExcelTemplates.FirstOrDefaultAsync(t => t.UnitId == unitId, ct);
        if (template is null) db.ExcelTemplates.Add(template = new ExcelTemplate { UnitId = unitId });
        template.FileName = Path.GetFileName(fileName);
        template.Content = bytes;
        template.UploadedAtUtc = clock.GetUtcNow().UtcDateTime;
        template.UploadedBy = user;

        if (v4) await MergeV4CategoriesAsync(unitId, v4Categories, ct);
        else if (!await db.DashboardCategories.AnyAsync(c => c.UnitId == unitId, ct))
        {
            Reporting.UnitDashboard.AssignCodes(fromTemplate);
            db.DashboardCategories.AddRange(fromTemplate);
        }
        await db.SaveChangesAsync(ct);
        return (await ListAsync(ct)).First(t => t.UnitId == unit.Id);
    }

    // ------------------------------------------------------------------ piping line list template (one for the whole project)

    public async Task<PipingTemplateDto> PipingAsync(CancellationToken ct, List<string>? notes = null)
    {
        var t = await db.WorkbookTemplates.AsNoTracking().Where(x => x.Key == WorkbookTemplate.PipingKey)
            .Select(x => new { x.FileName, x.UploadedAtUtc, x.UploadedBy }).FirstOrDefaultAsync(ct);
        return new PipingTemplateDto(t is null, t?.FileName ?? PipingTemplate.DefaultFileName, t?.UploadedAtUtc, t?.UploadedBy, notes ?? []);
    }

    /// <summary>Checks the template before it replaces the current one; the notes say what K Data will and will not fill.</summary>
    public async Task<PipingTemplateDto> UploadPipingAsync(Stream file, string fileName, string user, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > ExcelImportService.MaxFileBytes) throw ApiProblem.Invalid("Upload an .xlsx file of at most 20 MB.");
        var bytes = buffer.ToArray();
        List<string> notes;
        using (var wb = PipingWorkbookExporter.Open(bytes)) notes = PipingTemplate.Describe(wb);

        var template = await db.WorkbookTemplates.FirstOrDefaultAsync(t => t.Key == WorkbookTemplate.PipingKey, ct);
        if (template is null) db.WorkbookTemplates.Add(template = new WorkbookTemplate { Key = WorkbookTemplate.PipingKey });
        template.FileName = Path.GetFileName(fileName);
        template.Content = bytes;
        template.UploadedAtUtc = clock.GetUtcNow().UtcDateTime;
        template.UploadedBy = user;
        await db.SaveChangesAsync(ct);
        return await PipingAsync(ct, notes);
    }

    /// <summary>The template in use: the uploaded one, or the built-in default to start a new one from.</summary>
    public async Task<(string FileName, byte[] Content)> DownloadPipingAsync(CancellationToken ct)
    {
        var info = await PipingAsync(ct);
        return (info.FileName, await PipingWorkbookExporter.TemplateAsync(db, ct));
    }

    /// <summary>Back to the built-in default.</summary>
    public async Task<PipingTemplateDto> ResetPipingAsync(CancellationToken ct)
    {
        await db.WorkbookTemplates.Where(t => t.Key == WorkbookTemplate.PipingKey).ExecuteDeleteAsync(ct);
        return await PipingAsync(ct);
    }

    public async Task<(string FileName, byte[] Content)> DownloadAsync(int unitId, CancellationToken ct)
    {
        var t = await db.ExcelTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.UnitId == unitId, ct)
                ?? throw ApiProblem.NotFound("This unit has no template.");
        return (t.FileName, t.Content);
    }

    /// <summary>
    /// A v4 template moves the unit to category codes. The app stays the source of truth:
    /// <list type="bullet">
    /// <item>a unit without categories takes the template's list as it is;</item>
    /// <item>otherwise existing categories are kept: their label loses the "Cat 2 :" prefix (the code is now its own
    /// column), empty notes are filled from the template's row with the same section and code, and codes the unit
    /// does not have yet are added.</item>
    /// </list>
    /// </summary>
    private async Task MergeV4CategoriesAsync(int unitId, List<TemplateCategory> fromTemplate, CancellationToken ct)
    {
        var existing = await db.DashboardCategories.Where(c => c.UnitId == unitId).ToListAsync(ct);
        foreach (var c in existing)
        {
            var stripped = StripCodePrefix(c.Label);
            if (stripped != c.Label && !existing.Any(o => o != c && string.Equals(o.Label, stripped, StringComparison.OrdinalIgnoreCase))) c.Label = stripped;
        }
        Reporting.UnitDashboard.AssignCodes(existing);

        var order = existing.Select(c => c.SortOrder).DefaultIfEmpty(0).Max();
        foreach (var t in fromTemplate)
        {
            var match = existing.FirstOrDefault(c => c.Section == t.Section && c.Code == t.Code);
            if (match is not null)
            {
                match.ActionBy ??= t.ActionBy;
                match.EmailAction ??= t.EmailAction;
                match.Recommendation ??= t.Recommendation;
                match.Note ??= t.Note;
                continue;
            }
            var label = existing.Any(c => string.Equals(c.Label, t.Description, StringComparison.OrdinalIgnoreCase)) ? $"{t.Description} ({t.Code})" : t.Description;
            var created = new DashboardCategory
            {
                UnitId = unitId, Section = t.Section, Code = t.Code, Label = Truncate(label, DashboardCategory.ShortText)!,
                Priority = 100 + t.Order, SortOrder = existing.Count == 0 ? t.Order : ++order,
                ActionBy = t.ActionBy, EmailAction = t.EmailAction, Recommendation = t.Recommendation, Note = t.Note
            };
            db.DashboardCategories.Add(created);
            existing.Add(created);
        }
    }

    /// <summary>"Cat 2 :Level transferred" → "Level transferred"; a label that is only the prefix stays as it is.</summary>
    public static string StripCodePrefix(string label)
    {
        var m = CodePrefix().Match(label);
        if (!m.Success) return label;
        var rest = label[m.Length..].Trim();
        return rest.Length == 0 ? label : rest;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*Cat\s*\d+\s*[:\-–.]?\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CodePrefix();

    /// <summary>Categories defined in a template: one per Category label, with all its keywords.</summary>
    public static List<DashboardCategory> ReadCategories(XLWorkbook wb, int unitId)
    {
        var table = WorkbookFormat.FindTable(wb, ExcelColumns.CategoriesTable);
        if (table?.DataRange is null) return [];
        var headers = table.HeadersRow().Cells().Select(c => ExcelColumns.Norm(WorkbookFormat.CellText(c))).ToList();
        int priority = headers.IndexOf("priority") + 1, category = headers.IndexOf("category") + 1,
            keyword = headers.IndexOf("keyword") + 1, section = headers.IndexOf("section") + 1;
        if (category == 0 || keyword == 0) return [];

        var notes = DashboardNotes(wb);
        var result = new Dictionary<string, DashboardCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.DataRange.Rows())
        {
            var label = ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(category)));
            var kw = ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(keyword)));
            if (label is null) continue;
            if (!result.TryGetValue(label, out var c))
            {
                notes.TryGetValue(label, out var n);
                var sectionText = section > 0 ? ExcelColumns.Norm(WorkbookFormat.CellText(row.Cell(section))) : "";
                c = new DashboardCategory
                {
                    UnitId = unitId, Label = Truncate(label, 200)!,
                    Priority = priority > 0 && int.TryParse(WorkbookFormat.CellText(row.Cell(priority)), out var p) ? p : result.Count + 1,
                    Section = sectionText is "na" or "notapplicable" || n?.Section == CategorySection.NotApplicable ? CategorySection.NotApplicable : CategorySection.Hold,
                    SortOrder = n?.Order ?? 100 + result.Count,
                    ActionBy = Truncate(n?.ActionBy, 200), EmailAction = Truncate(n?.EmailAction, 200), Note = Truncate(n?.Note, 1000)
                };
                result[label] = c;
            }
            if (kw is not null && !c.Keywords.Any(k => string.Equals(k.Keyword, kw, StringComparison.OrdinalIgnoreCase)))
                c.Keywords.Add(new CategoryKeyword { Keyword = Truncate(kw, 200)! });
        }
        return result.Values.ToList();
    }

    private sealed record DashboardNote(CategorySection Section, int Order, string? ActionBy, string? EmailAction, string? Note);

    /// <summary>Action By / Email Action / note written next to each category on the Dashboard sheet.</summary>
    private static Dictionary<string, DashboardNote> DashboardNotes(XLWorkbook wb)
    {
        var notes = new Dictionary<string, DashboardNote>(StringComparer.OrdinalIgnoreCase);
        if (!wb.TryGetWorksheet("Dashboard", out var ws)) return notes;
        CategorySection? section = null;
        var order = 0;
        for (var r = 1; r <= (ws.LastRowUsed()?.RowNumber() ?? 0); r++)
        {
            var k = ExcelColumns.Norm(WorkbookFormat.CellText(ws.Cell(r, 2)));
            if (k.StartsWith("holdcategor", StringComparison.Ordinal)) { section = CategorySection.Hold; continue; }
            if (k.StartsWith("nacategor", StringComparison.Ordinal)) { section = CategorySection.NotApplicable; continue; }
            if (section is null || k.Length == 0) continue;
            if (k.StartsWith("total", StringComparison.Ordinal) || k.StartsWith("gran", StringComparison.Ordinal) || k.StartsWith("available", StringComparison.Ordinal))
            {
                if (section == CategorySection.NotApplicable) break;
                continue;
            }
            var label = ValueNormalizer.CleanText(WorkbookFormat.CellText(ws.Cell(r, 2)))!;
            notes[label] = new DashboardNote(section.Value, ++order, Text(ws.Cell(r, 4)), Text(ws.Cell(r, 5)), Text(ws.Cell(r, 6)));
        }
        return notes;
    }

    private static string? Text(IXLCell cell) => cell.HasFormula ? null : ValueNormalizer.CleanText(WorkbookFormat.CellText(cell));

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
