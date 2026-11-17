using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.GateForms;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.GateForms;

public sealed record GateFormUnitSettingDto(int UnitId, string UnitName, string PlantName, string? MechanicalSource, string? ElectricalSource, string? DefaultUnitId);
public sealed record SaveGateFormUnitSetting(string? MechanicalSource, string? ElectricalSource, string? DefaultUnitId);

public sealed record GateFormTemplateVersionDto(int Id, string FileName, DateTime UploadedAtUtc, string UploadedBy, bool IsActive, List<string> Fields);
public sealed record GateFormTemplateSlotDto(int PlantId, string PlantName, string Kind, string Units, bool IsBuiltIn, string FileName, List<string> Fields, List<GateFormTemplateVersionDto> Versions);
public sealed record GateFormUploadResult(GateFormTemplateVersionDto Version, List<string> Notes);

public sealed record GateFormPrepareRequest(string? Kind, IReadOnlyList<Guid>? AssetIds);
public sealed record GateFormRowDto(Guid AssetId, string Code, string? Tag, string? Description, string? RecordNo, DateOnly? Date,
    string Section, string SectionDefault, string? WorkPack, string? WorkPackDefault, string? UnitId, string? UnitIdDefault);
public sealed record GateFormSkippedDto(string Code, string? Tag, string Reason);
public sealed record GateFormPrepareDto(string Kind, string TemplateName, bool IsBuiltIn, List<string> Fields, string? Source, bool CanSave,
    List<GateFormRowDto> Rows, List<GateFormSkippedDto> Skipped);
public sealed record GateFormGenerateRow(Guid AssetId, string? RecordNo, DateOnly? Date, string? Section, string? WorkPack, string? UnitId);
public sealed record GateFormGenerateRequest(string? Kind, IReadOnlyList<GateFormGenerateRow>? Rows);

/// <summary>
/// Printable section acceptance gate forms (Word), one page per asset:
/// <list type="bullet">
/// <item><b>Mechanical</b> (G1–G8): equipment and lines. <b>Electrical</b> (E1–E8): E&amp;I items of discipline Electrical (motors).</item>
/// <item>Template per plant and kind: the active uploaded version, or the built-in one (the client forms of Nov 2026).</item>
/// <item>Unit values (Admin → Gate forms): source document of each form and the default Unit ID.</item>
/// <item>Asset values: Record No., date, and overrides of section (tag / line number), work pack (P&amp;ID number of a line)
/// and Unit ID. They are saved with the asset so the next print starts from them.</item>
/// </list>
/// </summary>
public sealed class GateFormService(PresAppDbContext db, TimeProvider clock)
{
    public const string ElectricalDiscipline = "Electrical";
    public const int MaxAssets = 500;
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly ConcurrentDictionary<string, byte[]> BuiltInCache = new();

    public static GateFormKind ParseKind(string? kind) =>
        Enum.TryParse<GateFormKind>(kind, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : throw ApiProblem.Invalid("Unknown gate form.");

    public static string FormTitle(GateFormKind kind) => kind == GateFormKind.Electrical ? "Gate electrical verification" : "Gate verification";

    // ------------------------------------------------------------------ unit settings

    public async Task<List<GateFormUnitSettingDto>> SettingsAsync(CancellationToken ct)
    {
        var settings = await db.GateFormUnitSettings.AsNoTracking().ToDictionaryAsync(s => s.UnitId, ct);
        var units = await db.Units.AsNoTracking().Where(u => u.IsActive && u.Plant.IsActive).OrderBy(u => u.Plant.Name).ThenBy(u => u.Number)
            .Select(u => new { u.Id, u.Name, Plant = u.Plant.Name }).ToListAsync(ct);
        return units.Select(u =>
        {
            settings.TryGetValue(u.Id, out var s);
            return new GateFormUnitSettingDto(u.Id, u.Name, u.Plant, s?.MechanicalSource, s?.ElectricalSource, s?.DefaultUnitId);
        }).ToList();
    }

    public async Task<GateFormUnitSettingDto> SaveSettingAsync(int unitId, SaveGateFormUnitSetting request, CancellationToken ct)
    {
        if (!await db.Units.AnyAsync(u => u.Id == unitId, ct)) throw ApiProblem.NotFound("Unit");
        var s = await db.GateFormUnitSettings.FirstOrDefaultAsync(x => x.UnitId == unitId, ct);
        if (s is null) db.GateFormUnitSettings.Add(s = new GateFormUnitSetting { UnitId = unitId });
        s.MechanicalSource = Clean(request.MechanicalSource, "Source");
        s.ElectricalSource = Clean(request.ElectricalSource, "Source");
        s.DefaultUnitId = Clean(request.DefaultUnitId, "Unit ID");
        await db.SaveChangesAsync(ct);
        return (await SettingsAsync(ct)).First(x => x.UnitId == unitId);
    }

    // ------------------------------------------------------------------ templates

    public async Task<List<GateFormTemplateSlotDto>> TemplatesAsync(CancellationToken ct)
    {
        var plants = await db.Plants.AsNoTracking().Where(p => p.IsActive && p.Units.Any(u => u.IsActive)).OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, Units = p.Units.Where(u => u.IsActive).OrderBy(u => u.Number).Select(u => u.Number).ToList() }).ToListAsync(ct);
        var versions = await db.GateFormTemplates.AsNoTracking().OrderByDescending(t => t.UploadedAtUtc).ToListAsync(ct);
        var result = new List<GateFormTemplateSlotDto>();
        foreach (var plant in plants)
        {
            foreach (var kind in Enum.GetValues<GateFormKind>())
            {
                var mine = versions.Where(v => v.PlantId == plant.Id && v.Kind == kind).ToList();
                var active = mine.FirstOrDefault(v => v.IsActive);
                var (name, content) = active is null ? BuiltIn(kind, plant.Name) : (active.FileName, active.Content);
                result.Add(new GateFormTemplateSlotDto(plant.Id, plant.Name, kind.ToString(), string.Join(", ", plant.Units), active is null, name,
                    GateFormDocument.Inspect(content).Fields, mine.Select(ToDto).ToList()));
            }
        }
        return result;
    }

    /// <summary>Checks the template and keeps it as a new version, not active yet: try its sample first, then activate it.</summary>
    public async Task<GateFormUploadResult> UploadAsync(int plantId, GateFormKind kind, Stream file, string fileName, string user, CancellationToken ct)
    {
        if (!await db.Plants.AnyAsync(p => p.Id == plantId, ct)) throw ApiProblem.NotFound("Plant");
        if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) throw ApiProblem.Invalid("Upload a Word .docx file.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > MaxFileBytes) throw ApiProblem.Invalid("Upload a .docx file of at most 10 MB.");
        var bytes = buffer.ToArray();
        var check = GateFormDocument.Inspect(bytes);
        if (check.Errors.Count > 0) throw ApiProblem.Invalid(string.Join(" ", check.Errors), new { errors = check.Errors });

        var version = new GateFormTemplate
        {
            Kind = kind, PlantId = plantId, FileName = Path.GetFileName(fileName), Content = bytes, IsActive = false,
            UploadedAtUtc = clock.GetUtcNow().UtcDateTime, UploadedBy = user
        };
        db.GateFormTemplates.Add(version);
        await db.SaveChangesAsync(ct);
        return new GateFormUploadResult(ToDto(version), check.Notes);
    }

    public async Task ActivateAsync(int plantId, GateFormKind kind, int versionId, CancellationToken ct)
    {
        var mine = await db.GateFormTemplates.Where(t => t.PlantId == plantId && t.Kind == kind).ToListAsync(ct);
        if (mine.All(t => t.Id != versionId)) throw ApiProblem.NotFound("Template version");
        foreach (var t in mine) t.IsActive = t.Id == versionId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Back to the built-in template; the uploaded versions stay in the list.</summary>
    public async Task UseBuiltInAsync(int plantId, GateFormKind kind, CancellationToken ct) =>
        await db.GateFormTemplates.Where(t => t.PlantId == plantId && t.Kind == kind && t.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false), ct);

    public async Task DeleteVersionAsync(int plantId, GateFormKind kind, int versionId, CancellationToken ct)
    {
        var t = await db.GateFormTemplates.FirstOrDefaultAsync(x => x.Id == versionId && x.PlantId == plantId && x.Kind == kind, ct)
                ?? throw ApiProblem.NotFound("Template version");
        if (t.IsActive) throw ApiProblem.Invalid("This version is in use. Activate another one or the built-in template first.");
        db.GateFormTemplates.Remove(t);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A version (or, without one, the template in use) as it was uploaded.</summary>
    public async Task<(string FileName, byte[] Content)> TemplateFileAsync(int plantId, GateFormKind kind, int? versionId, CancellationToken ct)
    {
        var (_, name, content) = await TemplateAsync(plantId, kind, versionId, ct);
        return (name, content);
    }

    /// <summary>
    /// The template filled with example values, to check it before activating it: a first page with every field filled
    /// (unit and source of the plant's first unit) and a second page with the fields left empty.
    /// </summary>
    public async Task<(string FileName, byte[] Content)> SampleAsync(int plantId, GateFormKind kind, int? versionId, CancellationToken ct)
    {
        var (_, name, content) = await TemplateAsync(plantId, kind, versionId, ct);
        var unit = await db.Units.AsNoTracking().Where(u => u.PlantId == plantId && u.IsActive).OrderBy(u => u.Number).FirstOrDefaultAsync(ct)
                   ?? throw ApiProblem.NotFound("Unit of this plant");
        var setting = await db.GateFormUnitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UnitId == unit.Id, ct);
        var unitValues = UnitValues(unit, setting, kind);
        var filled = new Dictionary<string, string?>
        {
            [GateFormDocument.RecordNo] = "REC-0001",
            [GateFormDocument.Date] = FormatDate(SiteClock.Today(clock)),
            [GateFormDocument.Section] = $"{unit.Number}-V-101",
            [GateFormDocument.WorkPack] = $"{unit.Number}-00-30-201",
            [GateFormDocument.UnitId] = setting?.DefaultUnitId ?? unit.Number.ToString(CultureInfo.InvariantCulture),
        };
        var bytes = GateFormDocument.Fill(content, unitValues, [filled, new Dictionary<string, string?>()]);
        return ($"Sample - {Path.GetFileNameWithoutExtension(name)}.docx", bytes);
    }

    // ------------------------------------------------------------------ forms of the assets chosen in a register

    public async Task<GateFormPrepareDto> PrepareAsync(int unitId, GateFormPrepareRequest request, bool canSave, CancellationToken ct)
    {
        var kind = ParseKind(request.Kind);
        var unit = await UnitAsync(unitId, ct);
        var setting = await db.GateFormUnitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UnitId == unitId, ct);
        var (assets, skipped) = await AssetsAsync(unitId, kind, request.AssetIds, ct);
        var (isBuiltIn, templateName, content) = await TemplateAsync(unit.PlantId, kind, null, ct);
        var unitIdDefault = setting?.DefaultUnitId;

        var rows = assets.Select(a =>
        {
            var sectionDefault = SectionDefault(a);
            var workPackDefault = WorkPackDefault(a);
            var e = a.Entry;
            return new GateFormRowDto(a.Id, a.Code, a.Tag, a.Description, e?.RecordNo, e?.Date,
                e?.Section ?? sectionDefault, sectionDefault, e?.WorkPack ?? workPackDefault, workPackDefault, e?.UnitIdText ?? unitIdDefault, unitIdDefault);
        }).ToList();
        return new GateFormPrepareDto(kind.ToString(), templateName, isBuiltIn, GateFormDocument.Inspect(content).Fields, SourceOf(setting, kind), canSave, rows, skipped);
    }

    /// <summary>
    /// Saves the values typed for each asset (when the role may edit) and returns the Word file, one page per asset in the
    /// order of the request. An empty field prints the blank line of the form.
    /// </summary>
    public async Task<(string FileName, byte[] Content)> GenerateAsync(int unitId, GateFormGenerateRequest request, bool save, CancellationToken ct)
    {
        var kind = ParseKind(request.Kind);
        var unit = await UnitAsync(unitId, ct);
        var rows = (request.Rows ?? []).GroupBy(r => r.AssetId).Select(g => g.First()).ToList();
        if (rows.Count == 0) throw ApiProblem.Invalid("Select at least one asset.");
        var (assets, _) = await AssetsAsync(unitId, kind, rows.Select(r => r.AssetId).ToList(), ct);
        var byId = assets.ToDictionary(a => a.Id);
        rows = rows.Where(r => byId.ContainsKey(r.AssetId)).ToList();
        if (rows.Count == 0) throw ApiProblem.Invalid("None of the selected assets has this form.");

        var setting = await db.GateFormUnitSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UnitId == unitId, ct);
        var pages = new List<IReadOnlyDictionary<string, string?>>();
        var rowIds = rows.Select(r => r.AssetId).ToList();
        var entries = save ? await db.GateFormEntries.Where(e => rowIds.Contains(e.AssetId)).ToDictionaryAsync(e => e.AssetId, ct) : [];
        foreach (var r in rows)
        {
            var a = byId[r.AssetId];
            var recordNo = Clean(r.RecordNo, "Record No.");
            var section = Clean(r.Section, "System / circuit / section");
            var workPack = Clean(r.WorkPack, "Work pack");
            var unitIdText = Clean(r.UnitId, "Unit ID");
            pages.Add(new Dictionary<string, string?>
            {
                [GateFormDocument.RecordNo] = recordNo,
                [GateFormDocument.Date] = r.Date is { } d ? FormatDate(d) : null,
                [GateFormDocument.Section] = section ?? SectionDefault(a),
                [GateFormDocument.WorkPack] = workPack ?? WorkPackDefault(a),
                [GateFormDocument.UnitId] = unitIdText ?? setting?.DefaultUnitId,
            });
            if (!save) continue;

            // Only what differs from what the app fills by itself is kept, so a later change of tag or P&ID still shows.
            var sectionOverride = section is null || section == SectionDefault(a) ? null : section;
            var workPackOverride = workPack is null || workPack == WorkPackDefault(a) ? null : workPack;
            var unitIdOverride = unitIdText is null || unitIdText == setting?.DefaultUnitId ? null : unitIdText;
            entries.TryGetValue(a.Id, out var entry);
            if (entry is null)
            {
                if (recordNo is null && r.Date is null && sectionOverride is null && workPackOverride is null && unitIdOverride is null) continue;
                db.GateFormEntries.Add(entry = new GateFormEntry { AssetId = a.Id });
            }
            entry.RecordNo = recordNo;
            entry.Date = r.Date;
            entry.Section = sectionOverride;
            entry.WorkPack = workPackOverride;
            entry.UnitIdText = unitIdOverride;
        }
        if (save) await db.SaveChangesAsync(ct);

        var (_, _, content) = await TemplateAsync(unit.PlantId, kind, null, ct);
        var bytes = GateFormDocument.Fill(content, UnitValues(unit, setting, kind), pages);
        return ($"{FormTitle(kind)} - {unit.Name} - {SiteClock.Today(clock):yyyy-MM-dd}.docx", bytes);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record FormAsset(Guid Id, string Code, string? Tag, string? Description, string? PidNumber, GateFormEntry? Entry);

    /// <summary>The requested assets that have this form, in the order asked; the others with the reason.</summary>
    private async Task<(List<FormAsset> Assets, List<GateFormSkippedDto> Skipped)> AssetsAsync(int unitId, GateFormKind kind, IReadOnlyList<Guid>? requested, CancellationToken ct)
    {
        var ids = (requested ?? []).Distinct().ToList();
        if (ids.Count == 0) throw ApiProblem.Invalid("Select at least one asset.");
        if (ids.Count > MaxAssets) throw ApiProblem.Invalid($"At most {MaxAssets} assets at once.");
        var found = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new
            {
                a.Id, a.Code, a.Tag, a.Description, a.UnitId, a.Type, a.IsActive, Discipline = a.Discipline.Name,
                Pid = a.Pipeline == null ? null : a.Pipeline.PidNumber
            }).ToDictionaryAsync(a => a.Id, ct);
        var entries = await db.GateFormEntries.AsNoTracking().Where(e => ids.Contains(e.AssetId)).ToDictionaryAsync(e => e.AssetId, ct);

        var assets = new List<FormAsset>();
        var skipped = new List<GateFormSkippedDto>();
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var a)) continue;
            var reason = a.UnitId != unitId ? "It belongs to another unit."
                : !a.IsActive ? "It is inactive."
                : kind == GateFormKind.Mechanical && a.Type is not (AssetType.Equipment or AssetType.Pipeline) ? "E&I items use the electrical form."
                : kind == GateFormKind.Electrical && a.Type != AssetType.ElectricalInstrument ? "Only E&I items use the electrical form."
                : kind == GateFormKind.Electrical && !string.Equals(a.Discipline, ElectricalDiscipline, StringComparison.OrdinalIgnoreCase)
                    ? $"Only discipline {ElectricalDiscipline} (motors) has this form; this item is {a.Discipline}."
                : null;
            if (reason is null) assets.Add(new FormAsset(a.Id, a.Code, a.Tag, a.Description, a.Pid, entries.GetValueOrDefault(a.Id)));
            else skipped.Add(new GateFormSkippedDto(a.Code, a.Tag, reason));
        }
        return (assets, skipped);
    }

    private async Task<Unit> UnitAsync(int unitId, CancellationToken ct) =>
        await db.Units.AsNoTracking().Include(u => u.Plant).FirstOrDefaultAsync(u => u.Id == unitId, ct) ?? throw ApiProblem.NotFound("Unit");

    private async Task<(bool IsBuiltIn, string FileName, byte[] Content)> TemplateAsync(int plantId, GateFormKind kind, int? versionId, CancellationToken ct)
    {
        var plant = await db.Plants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == plantId, ct) ?? throw ApiProblem.NotFound("Plant");
        var query = db.GateFormTemplates.AsNoTracking().Where(t => t.PlantId == plantId && t.Kind == kind);
        var t = versionId is { } v
            ? await query.FirstOrDefaultAsync(x => x.Id == v, ct) ?? throw ApiProblem.NotFound("Template version")
            : await query.FirstOrDefaultAsync(x => x.IsActive, ct);
        if (t is not null) return (false, t.FileName, t.Content);
        var (name, content) = BuiltIn(kind, plant.Name);
        return (true, name, content);
    }

    /// <summary>Built-in template: the client's forms of Nov 2026 with markers in place of the blanks.</summary>
    internal static (string FileName, byte[] Content) BuiltIn(GateFormKind kind, string plantName)
    {
        var resource = kind == GateFormKind.Electrical ? "GateForm_Electrical.docx"
            : string.Equals(plantName, GateFormDefaults.UnitIdLayoutPlant, StringComparison.OrdinalIgnoreCase) ? "GateForm_UnitId.docx"
            : "GateForm_WorkPack.docx";
        var content = BuiltInCache.GetOrAdd(resource, r =>
        {
            using var stream = typeof(GateFormService).Assembly.GetManifestResourceStream(r) ?? throw new InvalidOperationException($"Missing resource {r}.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
        return (resource, content);
    }

    private static Dictionary<string, string?> UnitValues(Unit unit, GateFormUnitSetting? setting, GateFormKind kind) => new()
    {
        [GateFormDocument.Unit] = unit.Number.ToString(CultureInfo.InvariantCulture),
        [GateFormDocument.Source] = SourceOf(setting, kind),
    };

    private static string? SourceOf(GateFormUnitSetting? s, GateFormKind kind) => kind == GateFormKind.Electrical ? s?.ElectricalSource : s?.MechanicalSource;

    private static string SectionDefault(FormAsset a) => string.IsNullOrWhiteSpace(a.Tag) ? a.Code : a.Tag.Trim();

    /// <summary>The P&amp;ID number of a line (several, one per line, become "a, b"); equipment has none yet.</summary>
    private static string? WorkPackDefault(FormAsset a)
    {
        if (string.IsNullOrWhiteSpace(a.PidNumber)) return null;
        return string.Join(", ", a.PidNumber.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string FormatDate(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string? Clean(string? value, string label)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length > GateFormEntry.TextMaxLength) throw ApiProblem.Invalid($"{label} can have at most {GateFormEntry.TextMaxLength} characters.");
        return s;
    }

    private static GateFormTemplateVersionDto ToDto(GateFormTemplate t) =>
        new(t.Id, t.FileName, t.UploadedAtUtc, t.UploadedBy, t.IsActive, GateFormDocument.Inspect(t.Content).Fields);
}
