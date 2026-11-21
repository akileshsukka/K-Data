using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.GateForms;

public sealed record GateFormDocumentationRequest(string? Type, IReadOnlyList<Guid>? AssetIds);

/// <summary>
/// Excel of the assets shown in a register (usually filtered to "Documentation ready"): tag, description, and the date and
/// work pack last printed on the asset's gate form, plus whether the signed file of the last gate (G8 / E8) is uploaded.
/// One signed document covers every gate of the asset, so the last gate is the one that counts. The list grows as more
/// documents are signed: it is always built from what the register shows at that moment. A report only, never imported.
/// </summary>
public sealed class GateFormDocumentationList(PresAppDbContext db, TimeProvider clock)
{
    public const int MaxAssets = 10000;
    public const int LastGate = 8;
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<(string FileName, byte[] Content)> ExportAsync(int unitId, GateFormDocumentationRequest request, CancellationToken ct)
    {
        var type = Enum.TryParse<AssetType>(request.Type, ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : throw ApiProblem.Invalid("Unknown register.");
        var ids = (request.AssetIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw ApiProblem.Invalid("There are no assets on screen to list.");
        if (ids.Count > MaxAssets) throw ApiProblem.Invalid($"At most {MaxAssets} assets can be listed at once.");
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit");

        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id) && a.UnitId == unitId && a.Type == type)
            .Select(a => new { a.Id, a.Code, a.Tag, a.Description, Pid = a.Pipeline == null ? null : a.Pipeline.PidNumber })
            .ToDictionaryAsync(a => a.Id, ct);
        var entries = await db.GateFormEntries.AsNoTracking().Where(e => ids.Contains(e.AssetId))
            .Select(e => new { e.AssetId, e.Date, e.WorkPack }).ToDictionaryAsync(e => e.AssetId, ct);
        var withFile = (await db.DocumentLinks.AsNoTracking()
            .Where(l => ids.Contains(l.AssetId) && l.Gate == LastGate && l.RemovedAtUtc == null && !l.Document.IsDeleted)
            .Select(l => l.AssetId).Distinct().ToListAsync(ct)).ToHashSet();

        var step = type == AssetType.ElectricalInstrument ? "E" : "G";
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Documentation");
        string[] headers = ["Tag", "Description", "Gate form date", "Work pack / P&ID rev.", $"{step}{LastGate} signed file uploaded"];
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).SetValue(headers[c]);
        var header = ws.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8E4F3");

        var row = 2;
        foreach (var id in ids)
        {
            if (!assets.TryGetValue(id, out var a)) continue;
            var entry = entries.GetValueOrDefault(id);
            ws.Cell(row, 1).SetValue(string.IsNullOrWhiteSpace(a.Tag) ? a.Code : a.Tag.Trim());
            ws.Cell(row, 2).SetValue(a.Description ?? "");
            if (entry?.Date is { } d)
            {
                ws.Cell(row, 3).SetValue(d.ToDateTime(TimeOnly.MinValue));
                ws.Cell(row, 3).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            // What the form printed: the work pack typed, else the line's P&ID number (saved only when it differs).
            ws.Cell(row, 4).SetValue(entry?.WorkPack ?? GateFormService.WorkPackFromPid(a.Pid) ?? "");
            var uploaded = withFile.Contains(id);
            ws.Cell(row, 5).SetValue(uploaded ? "Yes" : "No");
            if (!uploaded) ws.Cell(row, 5).Style.Font.FontColor = XLColor.FromHtml("#B42318");
            row++;
        }
        if (row == 2) throw ApiProblem.Invalid("None of the assets on screen belongs to this register.");

        ws.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents(1, Math.Min(row - 1, 500), 10, 60);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return ($"Documentation - {unit.Name} - {SiteClock.Today(clock):yyyy-MM-dd}.xlsx", stream.ToArray());
    }
}
