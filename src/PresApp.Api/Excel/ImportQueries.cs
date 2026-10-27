using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Imports;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>Read side of imports and the admin issue list.</summary>
public static class ImportQueries
{
    public static async Task<ImportRunDetail> DetailAsync(PresAppDbContext db, long runId, CancellationToken ct)
    {
        var run = await RunsQuery(db).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw ApiProblem.NotFound("Import not found.");
        var changes = await db.ImportChanges.AsNoTracking().Where(c => c.ImportRunId == runId)
            .OrderBy(c => c.RowNumber).ThenBy(c => c.Id).ToListAsync(ct);
        return new ImportRunDetail(await ToDtoAsync(db, run, changes, ct), changes.Select(ToDto).ToList());
    }

    public static async Task<List<ImportRunDto>> RecentAsync(PresAppDbContext db, int? unitId, int take, CancellationToken ct)
    {
        var runs = await RunsQuery(db).Where(r => unitId == null || r.UnitId == unitId)
            .OrderByDescending(r => r.UploadedAtUtc).Take(take).ToListAsync(ct);
        var ids = runs.Select(r => r.Id).ToList();
        var counts = await db.ImportChanges.AsNoTracking().Where(c => ids.Contains(c.ImportRunId))
            .GroupBy(c => new { c.ImportRunId, c.Outcome }).Select(g => new { g.Key.ImportRunId, g.Key.Outcome, Count = g.Count() })
            .ToListAsync(ct);
        var sessions = await SessionsOfAsync(db, runs, ct);
        return runs.Select(r => ToDto(r, CountsOf(counts.Where(c => c.ImportRunId == r.Id).ToDictionary(c => c.Outcome, c => c.Count)),
            r.SessionId is { } s ? sessions.GetValueOrDefault(s) : null)).ToList();
    }

    public static async Task<List<IssueDto>> IssuesAsync(PresAppDbContext db, bool resolved, int? unitId, string? q, int take, CancellationToken ct)
    {
        var state = resolved ? IssueResolution.Resolved : IssueResolution.Open;
        var query = db.ImportChanges.AsNoTracking().Where(c => c.Resolution == state)
            .Where(c => c.ImportRun.Status == ImportStatus.Applied || c.ImportRun.Status == ImportStatus.AppliedWithIssues);
        if (unitId is not null) query = query.Where(c => c.ImportRun.UnitId == unitId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}%";
            query = query.Where(c => EF.Functions.Like(c.AssetTag ?? "", like) || EF.Functions.Like(c.AssetCode ?? "", like) || EF.Functions.Like(c.ImportRun.FileName, like));
        }
        var rows = await query.OrderByDescending(c => c.ImportRun.UploadedAtUtc).ThenBy(c => c.RowNumber).Take(take)
            .Select(c => new { Change = c, c.ImportRun.FileName, c.ImportRun.UnitId, UnitName = c.ImportRun.Unit != null ? c.ImportRun.Unit.Name : null, c.ImportRun.UploadedAtUtc, c.ImportRun.UploadedBy })
            .ToListAsync(ct);
        return rows.Select(r => new IssueDto(ToDto(r.Change), r.FileName, r.UnitId, r.UnitName, r.UploadedAtUtc, r.UploadedBy)).ToList();
    }

    public static Task<int> OpenIssueCountAsync(PresAppDbContext db, CancellationToken ct) =>
        db.ImportChanges.CountAsync(c => c.Resolution == IssueResolution.Open
            && (c.ImportRun.Status == ImportStatus.Applied || c.ImportRun.Status == ImportStatus.AppliedWithIssues), ct);

    public static async Task<ImportChangeDto> ResolveAsync(PresAppDbContext db, long changeId, ResolveIssueRequest request, string user, DateTime now, CancellationToken ct)
    {
        var change = await db.ImportChanges.FirstOrDefaultAsync(c => c.Id == changeId, ct) ?? throw ApiProblem.NotFound("Issue not found.");
        if (change.Resolution == IssueResolution.None) throw ApiProblem.Invalid("This row is not an issue.");
        var note = Domain.Rules.ValueNormalizer.CleanText(request.Note);
        if (note?.Length > 1000) throw ApiProblem.Invalid("The note allows at most 1,000 characters.");
        change.Resolution = request.Resolved ? IssueResolution.Resolved : IssueResolution.Open;
        change.ResolvedBy = request.Resolved ? user : null;
        change.ResolvedAtUtc = request.Resolved ? now : null;
        change.ResolutionNote = note;
        await db.SaveChangesAsync(ct);
        return ToDto(change);
    }

    // ------------------------------------------------------------------ mapping

    private static IQueryable<ImportRun> RunsQuery(PresAppDbContext db) => db.ImportRuns.AsNoTracking().Include(r => r.Unit);

    private static async Task<ImportRunDto> ToDtoAsync(PresAppDbContext db, ImportRun run, List<ImportChange> changes, CancellationToken ct)
    {
        var sessions = await SessionsOfAsync(db, [run], ct);
        return ToDto(run, CountsOf(changes.GroupBy(c => c.Outcome).ToDictionary(g => g.Key, g => g.Count())),
            run.SessionId is { } s ? sessions.GetValueOrDefault(s) : null);
    }

    private static async Task<Dictionary<Guid, ExcelSession>> SessionsOfAsync(PresAppDbContext db, List<ImportRun> runs, CancellationToken ct)
    {
        var ids = runs.Where(r => r.SessionId != null).Select(r => r.SessionId!.Value).Distinct().ToList();
        return await db.ExcelSessions.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new ExcelSession { Id = s.Id, ExportedAtUtc = s.ExportedAtUtc, ExportedBy = s.ExportedBy })
            .ToDictionaryAsync(s => s.Id, ct);
    }

    private static ImportCounts CountsOf(Dictionary<ImportOutcome, int> c) => new(
        c.GetValueOrDefault(ImportOutcome.Applied), c.GetValueOrDefault(ImportOutcome.Derived), c.GetValueOrDefault(ImportOutcome.NoChange),
        c.GetValueOrDefault(ImportOutcome.Invalid), c.GetValueOrDefault(ImportOutcome.Conflict), c.GetValueOrDefault(ImportOutcome.Rejected),
        c.GetValueOrDefault(ImportOutcome.Warning), c.GetValueOrDefault(ImportOutcome.Created));

    private static ImportRunDto ToDto(ImportRun r, ImportCounts counts, ExcelSession? session) => new(
        r.Id, r.Kind.ToString(), r.FileName, r.UnitId, r.Unit?.Name, r.SessionId, r.UploadedBy, r.UploadedAtUtc, r.AppliedAtUtc,
        r.Status.ToString(), r.Message, r.RowsInFile, counts, session?.ExportedAtUtc, session?.ExportedBy);

    public static ImportChangeDto ToDto(ImportChange c) => new(
        c.Id, c.ImportRunId, c.AssetId, c.AssetCode, c.AssetTag, c.RowNumber, c.Field, FieldLabel(c.Field),
        c.BaseValue, c.ExcelValue, c.CurrentValue, c.NewValue, OutcomeText(c.Outcome), c.Reason, c.Resolution.ToString(),
        c.ResolvedBy, c.ResolvedAtUtc, c.ResolutionNote);

    public static string FieldLabel(string field) => field switch
    {
        "(row)" => "Row",
        "(asset)" => "Equipment",
        "(possible match)" => "Same line?",
        "Tag" => "Equip No",
        "Discipline" => "Discipline",
        _ => PreservationFields.LabelOf(field)
    };

    public static string OutcomeText(ImportOutcome o) => o == ImportOutcome.NoChange ? "No change" : o.ToString();
}
