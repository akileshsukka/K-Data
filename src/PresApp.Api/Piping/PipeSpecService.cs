using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Excel;
using PresApp.Api.Hosting;
using PresApp.Domain.Catalog;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Piping;

/// <summary>Active pipe specs by code, to give each line its class and flange type.</summary>
public sealed class PipeSpecLookup(Dictionary<string, PipeSpec> byCode)
{
    public static async Task<PipeSpecLookup> LoadAsync(PresAppDbContext db, CancellationToken ct) =>
        new((await db.PipeSpecs.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct))
            .ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase));

    public string? Gasket(string? code) => code is not null && byCode.TryGetValue(code, out var s) ? s.Gasket : null;
    public bool Has(string? code) => code is not null && byCode.ContainsKey(code);
    public string? Class(string? code) => code is not null && byCode.TryGetValue(code, out var s) ? s.PressureClass : null;
    public string? Flange(string? code) => code is not null && byCode.TryGetValue(code, out var s) ? s.FlangeType : null;
}

public sealed record PipeSpecDto(int Id, string Code, string? PressureClass, string? FlangeType, string? Gasket, string? Description, bool IsActive, int Lines);
public sealed record SavePipeSpecRequest(string Code, string? PressureClass, string? FlangeType, string? Gasket, string? Description, bool IsActive = true);
public sealed record PipeSpecImportRow(int Row, string Code, string? PressureClass, string? FlangeType, string? Gasket, string? Description, string Outcome, string? Reason, string? Before);
public sealed record PipeSpecImportResult(string? Sheet, IReadOnlyList<string> Columns, int New, int Updated, int Unchanged, int Invalid, List<PipeSpecImportRow> Rows);

/// <summary>
/// The pipe spec catalogue: kept by hand (add, correct, deactivate) or loaded from the client's pipe spec Excel
/// (preview first; new specs are added, known ones get the non-empty values of the file, nothing is deleted).
/// </summary>
public sealed class PipeSpecService(PresAppDbContext db)
{
    public async Task<List<PipeSpecDto>> ListAsync(CancellationToken ct)
    {
        var used = await db.PipelineDetails.AsNoTracking().Where(p => p.PipeSpec != null && p.Asset.IsActive)
            .GroupBy(p => p.PipeSpec!).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, StringComparer.OrdinalIgnoreCase, ct);
        return (await db.PipeSpecs.AsNoTracking().ToListAsync(ct))
            .OrderBy(s => s.Code, Assets.NaturalComparer.Instance)
            .Select(s => ToDto(s, used.GetValueOrDefault(s.Code))).ToList();
    }

    public async Task<PipeSpecDto> SaveAsync(int? id, SavePipeSpecRequest r, CancellationToken ct)
    {
        var spec = id is { } x ? await db.PipeSpecs.FirstOrDefaultAsync(s => s.Id == x, ct) ?? throw ApiProblem.NotFound("Pipe spec") : new PipeSpec();
        var code = Clean(r.Code, PipeSpec.CodeLength, "spec code")?.ToUpperInvariant() ?? throw ApiProblem.Invalid("Enter the spec code, e.g. 61055.");
        if (await db.PipeSpecs.AnyAsync(s => s.Id != spec.Id && s.Code == code, ct)) throw new ApiProblem(409, "Already used", $"The spec {code} is already in the list.");
        spec.Code = code;
        spec.PressureClass = Rating(Clean(r.PressureClass, PipeSpec.ClassLength, "rating"));
        spec.FlangeType = Face(Clean(r.FlangeType, PipeSpec.FlangeLength, "face"));
        spec.Gasket = Clean(r.Gasket, PipeSpec.GasketLength, "gasket");
        spec.Description = Clean(r.Description, PipeSpec.DescriptionLength, "description");
        spec.IsActive = r.IsActive;
        if (id is null) db.PipeSpecs.Add(spec);
        await db.SaveChangesAsync(ct);
        var lines = await db.PipelineDetails.CountAsync(p => p.PipeSpec == spec.Code && p.Asset.IsActive, ct);
        return ToDto(spec, lines);
    }

    /// <summary>RFD and RFC (raised face, as printed in the pipe class summary) are kept as RF; RTJ and FF as they are.</summary>
    public static string? Face(string? value)
    {
        var v = ValueNormalizer.CleanText(value)?.ToUpperInvariant();
        return v is null ? null : v.StartsWith("RF", StringComparison.Ordinal) ? "RF" : v;
    }

    /// <summary>"150" or "150.0" → "150#"; anything else as written.</summary>
    public static string? Rating(string? value)
    {
        var v = ValueNormalizer.CleanText(value);
        if (v is null) return null;
        return decimal.TryParse(v.TrimEnd('#').Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) ? $"{n:0.##}#" : v;
    }

    private static string? Clean(string? value, int max, string what)
    {
        var v = ValueNormalizer.CleanText(value);
        if (v?.Length > max) throw ApiProblem.Invalid($"The {what} allows at most {max} characters.");
        return v;
    }

    // ------------------------------------------------------------------ Excel

    private enum Col { Code, Class, Flange, Gasket, Description }

    private static Col? ColumnFor(string header)
    {
        var k = ExcelColumns.Norm(header);
        // Headers like "Face (exacta PDF)" or "Gasket (PDF)"; "Grupo face" is only a filter group and is not read.
        if (k.StartsWith("face", StringComparison.Ordinal) || k.StartsWith("flangeface", StringComparison.Ordinal)) return Col.Flange;
        if (k.StartsWith("gasket", StringComparison.Ordinal)) return Col.Gasket;
        if (k.StartsWith("rating", StringComparison.Ordinal)) return Col.Class;
        return ByName(k);
    }

    private static Col? ByName(string k) => k switch
    {
        "pipespec" or "spec" or "pipingspec" or "pipingspecification" or "specification" or "pipeclass" or "pipingclass" or "speccode" or "linespec" or "pipespecification" => Col.Code,
        "class" or "rating" or "pressureclass" or "pressurerating" or "ansiclass" or "ansirating" or "classrating" or "flangerating" or "flangeclass" => Col.Class,
        "flange" or "flangetype" or "flangefacing" or "facing" or "flangeface" or "typeofflange" => Col.Flange,
        "gaskettype" or "gasketmaterial" => Col.Gasket,
        "description" or "remarks" or "service" or "notes" => Col.Description,
        _ => null
    };

    public async Task<PipeSpecImportResult> ImportAsync(Stream file, bool apply, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > ExcelImportService.MaxFileBytes) throw ApiProblem.Invalid("Upload an .xlsx file of at most 20 MB.");
        XLWorkbook wb;
        try { wb = new XLWorkbook(new MemoryStream(buffer.ToArray())); }
        catch (Exception) { throw ApiProblem.Invalid("The file could not be opened as an Excel workbook (.xlsx)."); }

        using (wb)
        {
            var (sheet, columns, rows) = FindList(wb) ?? throw ApiProblem.Invalid(
                "No list with a pipe class column (\"Pipe Class\", \"Pipe Spec\" or \"Spec\") and a rating or face column was found in the first 20 rows of the visible sheets.");
            var existing = await db.PipeSpecs.ToDictionaryAsync(s => s.Code, StringComparer.OrdinalIgnoreCase, ct);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<PipeSpecImportRow>();
            foreach (var (number, cells) in rows)
            {
                string? Get(Col c) => columns.TryGetValue(c, out var i) && i < cells.Count ? ValueNormalizer.CleanText(cells[i]) : null;
                var code = Get(Col.Code)?.ToUpperInvariant();
                if (code is null) continue;
                var (cls, flange, gasket, description) = (Rating(Get(Col.Class)), Face(Get(Col.Flange)), Get(Col.Gasket), Get(Col.Description));
                string? problem = code.Length > PipeSpec.CodeLength ? $"The code is longer than {PipeSpec.CodeLength} characters."
                    : cls?.Length > PipeSpec.ClassLength ? "The rating is too long."
                    : flange?.Length > PipeSpec.FlangeLength ? "The face is too long."
                    : !seen.Add(code) ? "Appears twice in the file; only the first row is used."
                    : null;
                gasket = gasket?.Length > PipeSpec.GasketLength ? gasket[..PipeSpec.GasketLength] : gasket;
                description = description?.Length > PipeSpec.DescriptionLength ? description[..PipeSpec.DescriptionLength] : description;
                if (problem is not null) { result.Add(new(number, code, cls, flange, gasket, description, "Invalid", problem, null)); continue; }

                if (!existing.TryGetValue(code, out var spec))
                {
                    result.Add(new(number, code, cls, flange, gasket, description, apply ? "Added" : "New", null, null));
                    if (apply) db.PipeSpecs.Add(new PipeSpec { Code = code, PressureClass = cls, FlangeType = flange, Gasket = gasket, Description = description });
                    continue;
                }
                var before = $"{spec.PressureClass ?? "—"} · {spec.FlangeType ?? "—"}";
                var changed = (cls is not null && cls != spec.PressureClass) || (flange is not null && flange != spec.FlangeType)
                              || (gasket is not null && gasket != spec.Gasket) || (description is not null && description != spec.Description);
                if (!changed) { result.Add(new(number, code, cls, flange, gasket, description, "Unchanged", null, null)); continue; }
                result.Add(new(number, code, cls ?? spec.PressureClass, flange ?? spec.FlangeType, gasket ?? spec.Gasket, description ?? spec.Description, apply ? "Updated" : "Update", null, before));
                if (apply)
                {
                    spec.PressureClass = cls ?? spec.PressureClass;
                    spec.FlangeType = flange ?? spec.FlangeType;
                    spec.Gasket = gasket ?? spec.Gasket;
                    spec.Description = description ?? spec.Description;
                }
            }
            if (apply) await db.SaveChangesAsync(ct);
            return new PipeSpecImportResult(sheet, columns.OrderBy(c => c.Value).Select(c => c.Key.ToString()).ToList(),
                result.Count(r => r.Outcome is "New" or "Added"), result.Count(r => r.Outcome is "Update" or "Updated"),
                result.Count(r => r.Outcome == "Unchanged"), result.Count(r => r.Outcome == "Invalid"), result);
        }
    }

    /// <summary>The first visible sheet with a header row (in its first 20 rows) holding a spec column and a class or flange column.</summary>
    private static (string Sheet, Dictionary<Col, int> Columns, List<(int Row, List<string> Cells)> Rows)? FindList(XLWorkbook wb)
    {
        foreach (var ws in wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible))
        {
            var used = ws.RangeUsed();
            if (used is null) continue;
            var first = used.FirstColumn().ColumnNumber();
            var last = used.LastColumn().ColumnNumber();
            for (var r = used.FirstRow().RowNumber(); r <= Math.Min(used.FirstRow().RowNumber() + 20, used.LastRow().RowNumber()); r++)
            {
                var columns = new Dictionary<Col, int>();
                for (var c = first; c <= last; c++)
                    if (ColumnFor(WorkbookFormat.CellText(ws.Cell(r, c))) is { } col) columns.TryAdd(col, c - first);
                if (!columns.ContainsKey(Col.Code) || !(columns.ContainsKey(Col.Class) || columns.ContainsKey(Col.Flange))) continue;
                var rows = new List<(int, List<string>)>();
                for (var d = r + 1; d <= used.LastRow().RowNumber(); d++)
                    rows.Add((d, Enumerable.Range(first, last - first + 1).Select(c => WorkbookFormat.CellText(ws.Cell(d, c))).ToList()));
                return (ws.Name, columns, rows);
            }
        }
        return null;
    }

    private static PipeSpecDto ToDto(PipeSpec s, int lines) => new(s.Id, s.Code, s.PressureClass, s.FlangeType, s.Gasket, s.Description, s.IsActive, lines);
}
