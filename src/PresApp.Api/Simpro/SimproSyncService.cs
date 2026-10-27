using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Rules;
using PresApp.Domain.Simpro;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Simpro;

/// <summary>
/// K Data → Simpro, the automatic version of SharePoint_to_Simpro_Sync.sql v5.
/// <list type="bullet">
/// <item><b>Preview</b>: reads both sides and shows what would happen; writes nothing anywhere.</item>
/// <item><b>Apply</b> (button or the daily schedule): same comparison inside one Simpro transaction with the rows locked,
/// writes like Simpro does (header, custom values, one history event with header and custom snapshots, workpack item
/// for new equipment), checks the result, commits, and keeps a before-image so the run can be undone.</item>
/// </list>
/// Equipment changed in Simpro, or on both sides, is held (no write at all) until the values agree or an
/// administrator decides that K Data wins. Simpro is never the source of truth.
/// </summary>
public sealed class SimproSyncService(PresAppDbContext db, SimproStore store, IOptions<SimproOptions> options, AuditContext audit,
    TimeProvider clock, ILogger<SimproSyncService> logger)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private readonly SimproOptions o = options.Value;

    public bool IsConfigured => store.IsConfigured;

    public async Task<SimproRunDetail> RunAsync(SyncMode mode, string triggeredBy, CancellationToken ct)
    {
        if (!store.IsConfigured) throw ApiProblem.Invalid("The Simpro connection is not configured (ConnectionStrings:Simpro).");
        if (!await OneAtATime.WaitAsync(0, ct)) throw ApiProblem.Conflict("A Simpro sync is already running. Wait for it to finish.");
        var run = new SimproSyncRun { Mode = mode, Status = SyncStatus.Running, TriggeredBy = triggeredBy, StartedAtUtc = Now() };
        db.SimproSyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
        try
        {
            if (mode == SyncMode.Preview) await PreviewAsync(run, ct);
            else await ApplyAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Simpro sync {RunId} failed", run.Id);
            db.ChangeTracker.Clear();
            var failed = await db.SimproSyncRuns.FirstAsync(r => r.Id == run.Id, CancellationToken.None);
            failed.Status = SyncStatus.Failed;
            failed.FinishedAtUtc = Now();
            failed.Message = Truncate($"Nothing was written to Simpro. {ex.Message}", 4000);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            OneAtATime.Release();
        }
        return await SimproQueries.DetailAsync(db, run.Id, ct);
    }

    private async Task PreviewAsync(SimproSyncRun run, CancellationToken ct)
    {
        await using var c = await store.OpenAsync(ct);
        var sim = await store.LoadAsync(c, null, ct);
        var plan = await PlanAsync(sim, ct);
        Record(run, plan, applied: false);
        run.Status = SyncStatus.Previewed;
        run.FinishedAtUtc = Now();
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyAsync(SimproSyncRun run, CancellationToken ct)
    {
        var now = Now();
        var images = new List<SimproBeforeImage>();
        SyncPlan plan;

        await using (var c = await store.OpenAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            await store.LockAsync(c, tx, ct);
            var sim = await store.LoadAsync(c, tx, ct);
            plan = await PlanAsync(sim, ct);
            var counts = (Flanges: await Count(c, tx, "tbl_flange"), History: await Count(c, tx, "tbl_flangeHistory"),
                Changes: await Count(c, tx, "tbl_flangeChanges"), Workpack: await Count(c, tx, "tbl_workpackItem"));
            var events = 0;
            var created = 0;

            foreach (var a in plan.Assets.Where(a => a.WillCreate))
            {
                var header = a.Fields.Where(f => f.Field.Kind == SimproKind.Header).ToDictionary(f => f.Field.Key, f => f.AppOut);
                await store.CreateFlangeAsync(c, tx, a.Asset.Id, header, now, ct);
                images.Add(Image("tbl_flange", a.Asset.Id, null));
                foreach (var f in a.Fields.Where(f => f.Field.Kind == SimproKind.Custom && f.AppOut is not null))
                {
                    var id = await store.InsertCustomAsync(c, tx, a.Asset.Id, f.Field.FieldId!.Value, f.AppOut!, ct);
                    images.Add(Image("tbl_flangeCustomFieldValue", id, null));
                }
                var (pkid, record) = await store.WriteEventAsync(c, tx, a.Asset.Id, created: true, 1, o.NewWorkpackId, now, ct);
                images.Add(Image("tbl_flangeHistory", pkid, record.ToString()));
                events++;
                created++;
            }

            foreach (var a in plan.Assets.Where(a => a.Status == PlanStatus.Existing && a.Writes.Any()))
            {
                var writes = a.Writes.ToList();
                var headerChanges = writes.Where(f => f.Field.Kind == SimproKind.Header).ToDictionary(f => f.Field.Key, f => f.AppOut);
                images.Add(Image("tbl_flange", a.Asset.Id, Json(await store.ReadRowAsync(c, tx, "tbl_flange", "flangeId", a.Asset.Id, ct))));
                await store.UpdateHeaderAsync(c, tx, a.Asset.Id, headerChanges, now, ct);
                foreach (var f in writes.Where(f => f.Field.Kind == SimproKind.Custom))
                {
                    if (f.CustomRowId is { } rowId)
                    {
                        images.Add(Image("tbl_flangeCustomFieldValue", rowId, Json(new Dictionary<string, object?> { ["value"] = f.SimRaw })));
                        await store.UpdateCustomAsync(c, tx, rowId, f.AppOut ?? "", ct);
                    }
                    else if (f.AppOut is not null)
                    {
                        var id = await store.InsertCustomAsync(c, tx, a.Asset.Id, f.Field.FieldId!.Value, f.AppOut, ct);
                        images.Add(Image("tbl_flangeCustomFieldValue", id, null));
                    }
                }
                var history = sim.History.GetValueOrDefault(a.Asset.Id) ?? [];
                var last = history.OrderByDescending(h => h.Sequence).ThenByDescending(h => h.Created).FirstOrDefault();
                var (pkid, record) = await store.WriteEventAsync(c, tx, a.Asset.Id, created: false, (last?.Sequence ?? 0) + 1,
                    last?.WorkpackId ?? sim.Flanges[a.Asset.Id].WorkpackId, now, ct);
                images.Add(Image("tbl_flangeHistory", pkid, record.ToString()));
                events++;
            }

            // ---- checks before commit (rolled back on any failure)
            var problems = new List<string>();
            if (await Count(c, tx, "tbl_flange") - counts.Flanges != created) problems.Add("tbl_flange rows added ≠ new equipment");
            if (await Count(c, tx, "tbl_workpackItem") - counts.Workpack != created) problems.Add("workpack items added ≠ new equipment");
            if (await Count(c, tx, "tbl_flangeHistory") - counts.History != events) problems.Add("history events ≠ equipment written");
            if (await Count(c, tx, "tbl_flangeChanges") - counts.Changes != events) problems.Add("header snapshots ≠ events");
            if (events > 0)
            {
                var after = await store.LoadAsync(c, tx, ct);
                foreach (var a in plan.Assets.Where(a => a.WillCreate || a.Status == PlanStatus.Existing && a.Writes.Any()))
                {
                    var row = after.Flanges.GetValueOrDefault(a.Asset.Id);
                    if (row is null) { problems.Add($"{a.Asset.Code} not found after writing"); continue; }
                    foreach (var f in a.WillCreate ? a.Fields.Where(f => f.AppOut is not null) : a.Writes)
                    {
                        var stored = f.Field.Kind == SimproKind.Header
                            ? row.Values[f.Field.Key]
                            : after.CustomValues.GetValueOrDefault(a.Asset.Id)?.Where(v => v.FieldId == f.Field.FieldId).Select(v => v.Value).SingleOrDefault();
                        var expected = f.Field.Kind == SimproKind.Custom ? f.AppOut ?? "" : f.AppOut;
                        var same = f.Field.Type is SimproType.Plant or SimproType.Unit
                            ? string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(stored, expected, StringComparison.Ordinal);
                        if (!same) problems.Add($"{a.Asset.Code} {f.Field.Label}: stored \"{stored}\", expected \"{expected}\"");
                    }
                }
            }
            if (problems.Count > 0)
            {
                await tx.RollbackAsync(ct);
                throw new InvalidOperationException("Checks failed, the transaction was rolled back: " + string.Join("; ", problems.Take(5)));
            }
            await tx.CommitAsync(ct);
        }

        // ---- K Data: record the run, link new equipment, consume "K Data wins" decisions
        audit.Source = ChangeSource.Simpro;
        audit.UserName = run.TriggeredBy;
        Record(run, plan, applied: true);
        foreach (var image in images) image.RunId = run.Id;
        db.SimproBeforeImages.AddRange(images);
        var linked = plan.Assets.Where(a => a.WillCreate || a.Status == PlanStatus.Existing && a.Asset.SimproFlangeId is null).Select(a => a.Asset.Id).ToList();
        if (linked.Count > 0)
            await db.Assets.Where(x => linked.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.SimproFlangeId, x => x.Id), ct);
        var forcedUsed = plan.Assets.Where(a => a.Forced && a.Writes.Any()).Select(a => a.Asset.Id).ToList();
        await db.SimproForcedAssets.Where(f => forcedUsed.Contains(f.AssetId)).ExecuteDeleteAsync(ct);
        run.Status = run.HeldCount > 0 ? SyncStatus.HeldForConflicts : SyncStatus.Applied;
        run.FinishedAtUtc = Now();
        await db.SaveChangesAsync(ct);
    }

    private async Task<SyncPlan> PlanAsync(SimproData sim, CancellationToken ct)
    {
        var assets = await db.Assets.AsNoTracking()
            .Include(a => a.Preservation).Include(a => a.Discipline).Include(a => a.Unit).ThenInclude(u => u.Plant)
            .ToListAsync(ct);
        var allOptions = (await db.OptionSets.AsNoTracking()
                .Select(s => new { s.Key, Values = s.Values.OrderBy(v => !v.IsActive).ThenBy(v => v.SortOrder).Select(v => v.Value).ToList() })
                .ToListAsync(ct))
            .ToDictionary(s => s.Key, s => s.Values, StringComparer.OrdinalIgnoreCase);
        var forced = (await db.SimproForcedAssets.AsNoTracking().Select(f => f.AssetId).ToListAsync(ct)).ToHashSet();
        var planner = new SimproPlanner(o, allOptions);
        return planner.Build(assets.OrderBy(a => a.Unit.Number).ThenBy(a => a.Code, NaturalComparer.Instance), sim, forced, SiteClock.Today(clock));
    }

    /// <summary>Stores the result: one item per field that is written, held, invalid or changed by a rule.</summary>
    private static void Record(SimproSyncRun run, SyncPlan plan, bool applied)
    {
        foreach (var a in plan.Assets)
        {
            void Add(string field, string outcome, string? reason, string? simpro = null, string? newValue = null, string? synced = null) => run.Items.Add(new SimproSyncItem
            {
                AssetId = a.Asset.Id, AssetCode = a.Asset.Code, AssetTag = Truncate(a.Asset.Tag, 55), UnitId = a.Asset.UnitId, Field = field,
                SimproValue = simpro, NewValue = newValue, LastSyncedValue = synced, Outcome = outcome, Reason = Truncate(reason, 1000)
            });

            if (a.Status == PlanStatus.New)
            {
                Add("(equipment)", a.Problem is null ? (applied ? "Created" : "To create") : "Not created", a.Problem ?? $"{a.Fields.Count(f => f.AppOut is not null)} fields");
                continue;
            }
            if (a.Status != PlanStatus.Existing) continue;
            foreach (var f in a.Fields)
            {
                var sim = Show(f, f.SimRaw);
                var synced = Show(f, f.BaseRaw);
                var reason = f.Derived ?? (f.FromReading && f.Class == FieldClass.Write ? "Latest reading" : null);
                switch (f.Class)
                {
                    case FieldClass.Write:
                        Add(f.Field.Key, applied ? "Written" : "To write",
                            reason ?? (a.Forced && a.HasBase ? "K Data wins (decided by an administrator)" : a.HasBase ? null : "No sync snapshot yet: K Data wins"),
                            sim, f.AppShow, synced);
                        break;
                    case FieldClass.SimproChanged:
                        Add(f.Field.Key, "Changed in Simpro", "Edited in Simpro only. Copy the Simpro value into K Data, or let K Data win.", sim, f.AppShow, synced);
                        break;
                    case FieldClass.Conflict:
                        Add(f.Field.Key, "Conflict", "Changed in K Data and in Simpro. Decide which value is right.", sim, f.AppShow, synced);
                        break;
                    case FieldClass.Held:
                        Add(f.Field.Key, "Waiting", "Waits until the held fields of this equipment are decided.", sim, f.AppShow, synced);
                        break;
                    case FieldClass.Invalid:
                        Add(f.Field.Key, "Invalid", f.Bad, sim, f.AppShow);
                        break;
                    case FieldClass.GateBack:
                        Add(f.Field.Key, "Gate goes back", $"Simpro has {sim}, K Data has {f.AppShow}. The gate never goes back on its own: use the Simpro gate, or let K Data win.", sim, f.AppShow, synced);
                        break;
                    case FieldClass.Reference:
                        Add(f.Field.Key, "Reference", "Identifiers are never written; check which one is right.", sim, f.AppShow);
                        break;
                }
                if (f.Derived is not null && f.Class != FieldClass.Write)
                    Add(f.Field.Key, "Rule", f.Derived + (f.Class == FieldClass.Same ? " (Simpro already has it)" : ""), sim, f.AppShow);
            }
        }

        var existing = plan.Assets.Where(a => a.Status == PlanStatus.Existing).ToList();
        run.CreatedCount = plan.Assets.Count(a => a.WillCreate);
        run.UpdatedCount = existing.Count(a => a.Writes.Any());
        run.FieldCount = existing.Sum(a => a.Writes.Count()) + plan.Assets.Where(a => a.WillCreate).Sum(a => a.Fields.Count(f => f.AppOut is not null));
        run.HeldCount = existing.Count(a => a.NeedsDecision && !a.Forced);
        run.RejectedCount = existing.Sum(a => a.Fields.Count(f => f.Class == FieldClass.Invalid)) + plan.Assets.Count(a => a.Status == PlanStatus.New && a.Problem is not null);
        run.NotInAppCount = plan.SimproOnly.Count;
        run.Message = Truncate(string.Join(" ", new[]
        {
            $"{existing.Count} equipment compared.",
            existing.Count(a => !a.HasBase) is var noBase and > 0 ? $"{noBase} without a previous sync snapshot (K Data wins for them)." : null,
            plan.SimproOnly.Count > 0 ? $"{plan.SimproOnly.Count} equipment are in Simpro but not in K Data (never touched)." : null,
        }.Where(s => s is not null)), 4000);
    }

    private static string? Show(FieldPlan f, string? raw) => f.Field.Type switch
    {
        SimproType.Gate when int.TryParse(raw, out var code) => SimproFields.GateOfCode(code) is { } g ? FieldValues.CurrentGateText(g) : raw,
        SimproType.Bit => raw switch { "1" => "Yes", "0" => "No", _ => raw },
        _ => raw
    };

    // ------------------------------------------------------------------ undo

    /// <summary>Puts back the Simpro rows a run changed, if nobody edited those equipment in Simpro since.</summary>
    public async Task<SimproRunDetail> UndoAsync(long runId, string by, CancellationToken ct)
    {
        if (!store.IsConfigured) throw ApiProblem.Invalid("The Simpro connection is not configured.");
        var original = await db.SimproSyncRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw ApiProblem.NotFound("Run not found.");
        if (original.Status is not (SyncStatus.Applied or SyncStatus.HeldForConflicts)) throw ApiProblem.Invalid("Only an applied run can be undone.");
        if (await db.SimproSyncRuns.AnyAsync(r => r.Id > runId && (r.Status == SyncStatus.Applied || r.Status == SyncStatus.HeldForConflicts), ct))
            throw ApiProblem.Invalid("A later sync was applied after this one. Only the most recent run can be undone.");
        var images = await db.SimproBeforeImages.AsNoTracking().Where(i => i.RunId == runId).OrderByDescending(i => i.Id).ToListAsync(ct);
        if (!await OneAtATime.WaitAsync(0, ct)) throw ApiProblem.Conflict("A Simpro sync is running. Try again when it finishes.");
        var undo = new SimproSyncRun { Mode = SyncMode.Undo, Status = SyncStatus.Running, TriggeredBy = by, StartedAtUtc = Now(), UndoOfRunId = runId };
        db.SimproSyncRuns.Add(undo);
        await db.SaveChangesAsync(ct);
        try
        {
            await using var c = await store.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await store.LockAsync(c, tx, ct);
            var sim = await store.LoadAsync(c, tx, ct);
            var touched = images.Where(i => i.TableName == "tbl_flange").Select(i => Guid.Parse(i.RowKey)).ToList();
            var editedSince = touched.Where(f => (sim.History.GetValueOrDefault(f) ?? []).Any(h => h.Created >= original.StartedAtUtc && !IsOurEvent(h, images))).ToList();
            if (editedSince.Count > 0)
                throw ApiProblem.Conflict($"{editedSince.Count} equipment were edited in Simpro after this run; the run cannot be undone automatically.");

            foreach (var image in images)
            {
                var key = Guid.Parse(image.RowKey);
                switch (image.TableName)
                {
                    case "tbl_flangeHistory":
                        var record = Guid.Parse(image.RowJson);
                        await store.DeleteAsync(c, tx, "tbl_flangeCustomFieldChange", "ownerId", record, ct);
                        await store.DeleteAsync(c, tx, "tbl_flangeChanges", "activityRecordId", record, ct);
                        await store.DeleteAsync(c, tx, "tbl_flangeHistory", "pkid", key, ct);
                        break;
                    case "tbl_flangeCustomFieldValue" when image.RowJson.Length == 0:
                        await store.DeleteAsync(c, tx, "tbl_flangeCustomFieldValue", "id", key, ct);
                        break;
                    case "tbl_flangeCustomFieldValue":
                        var old = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(image.RowJson)!;
                        await store.UpdateCustomAsync(c, tx, key, old["value"].ValueKind == JsonValueKind.Null ? "" : old["value"].GetString()!, ct);
                        break;
                    case "tbl_flange" when image.RowJson.Length == 0:
                        await store.DeleteAsync(c, tx, "tbl_flangeCustomFieldValue", "ownerId", key, ct);
                        await store.DeleteAsync(c, tx, "tbl_workpackItem", "itemId", key, ct);
                        await store.DeleteAsync(c, tx, "tbl_flange", "flangeId", key, ct);
                        break;
                    case "tbl_flange":
                        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(image.RowJson)!;
                        var values = SimproFields.Headers.Where(f => f.Type != SimproType.Reference)
                            .ToDictionary(f => f.Key, f => Text(row.GetValueOrDefault(f.Column!)));
                        var lastUpdated = row.TryGetValue("lastUpdated", out var lu) && lu.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(lu.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var lud)
                            ? lud : Now();
                        await store.UpdateHeaderAsync(c, tx, key, values, lastUpdated, ct);
                        break;
                }
            }
            await tx.CommitAsync(ct);

            original.Status = SyncStatus.Undone;
            undo.Status = SyncStatus.Applied;
            undo.FinishedAtUtc = Now();
            undo.Message = $"Run {runId} undone: {touched.Count} equipment restored.";
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            undo.Status = SyncStatus.Failed;
            undo.FinishedAtUtc = Now();
            logger.LogError(ex, "Undo of Simpro run {RunId} failed", runId);
            undo.Message = Truncate($"Nothing was changed in Simpro. {ex.Message}", 4000);
            await db.SaveChangesAsync(CancellationToken.None);
            if (ex is ApiProblem) throw;
        }
        finally
        {
            OneAtATime.Release();
        }
        return await SimproQueries.DetailAsync(db, undo.Id, ct);
    }

    private static bool IsOurEvent(SimproHistoryEvent h, List<SimproBeforeImage> images) =>
        images.Any(i => i.TableName == "tbl_flangeHistory" && i.RowJson == h.ActivityRecordId.ToString());

    private static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Number => e.GetRawText(),
        _ => e.GetString()
    };

    // ------------------------------------------------------------------ decisions on held equipment

    public async Task ForceAppAsync(Guid assetId, bool force, string by, CancellationToken ct)
    {
        var existing = await db.SimproForcedAssets.FirstOrDefaultAsync(f => f.AssetId == assetId, ct);
        if (force && existing is null) db.SimproForcedAssets.Add(new SimproForcedAsset { AssetId = assetId, RequestedBy = by, RequestedAtUtc = Now() });
        if (!force && existing is not null) db.SimproForcedAssets.Remove(existing);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"K Data wins" for every equipment held in this run (e.g. the first sync, where K Data is the reference). Returns how many.</summary>
    public async Task<int> ForceAllHeldAsync(long runId, string by, CancellationToken ct)
    {
        string[] held = ["Changed in Simpro", "Conflict", "Gate goes back", "Waiting"];
        var ids = await db.SimproSyncItems.Where(i => i.RunId == runId && held.Contains(i.Outcome)).Select(i => i.AssetId).Distinct().ToListAsync(ct);
        var already = (await db.SimproForcedAssets.Where(f => ids.Contains(f.AssetId)).Select(f => f.AssetId).ToListAsync(ct)).ToHashSet();
        foreach (var id in ids.Where(i => !already.Contains(i)))
            db.SimproForcedAssets.Add(new SimproForcedAsset { AssetId = id, RequestedBy = by, RequestedAtUtc = Now() });
        await db.SaveChangesAsync(ct);
        return ids.Count;
    }

    /// <summary>Copies the value Simpro has into K Data (the Simpro edit was right), audited as source Simpro.</summary>
    public async Task<AssetProfile> CopyFromSimproAsync(long itemId, AssetQueries queries, CancellationToken ct)
    {
        var item = await db.SimproSyncItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw ApiProblem.NotFound("Item not found.");
        var field = SimproFields.ByKey.GetValueOrDefault(item.Field) ?? throw ApiProblem.Invalid("This field cannot be copied.");
        if (field.AppField is null || field.Type is SimproType.Number or SimproType.Reference)
            throw ApiProblem.Invalid($"{field.Label} cannot be copied from Simpro (readings and identifiers are entered in K Data).");

        var asset = await queries.LoadAsync(item.AssetId, tracking: true, ct);
        audit.Source = ChangeSource.Simpro;
        if (field.Type == SimproType.Gate)
        {
            if (!FieldValues.TryParseCurrentGate(item.SimproValue, out var gate) || item.SimproValue is null)
                throw ApiProblem.Invalid("The Simpro gate could not be read.");
            asset.Preservation!.CurrentGate = gate;
        }
        else
        {
            var def = PreservationFields.Find(field.AppField) ?? throw ApiProblem.Invalid("This field cannot be copied.");
            var options = await AssetEditor.LoadActiveOptionsAsync(db, ct);
            var error = FieldValues.Prepare(def, item.SimproValue, AssetEditor.OptionsFor(def, options), out var value);
            if (error is not null) throw ApiProblem.Invalid($"The Simpro value cannot be used: {error}");
            FieldValues.Write(def, asset, value);
            if (PreservationFields.IsGateStatus(def)) AssetEditor.EnsureDocumentRows(asset);
        }
        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(item.AssetId, ct);
    }

    // ------------------------------------------------------------------ helpers

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private async Task<long> Count(DbConnection c, DbTransaction tx, string table) =>
        Convert.ToInt64(await store.CountAsync(c, tx, table, CancellationToken.None), CultureInfo.InvariantCulture);

    private static SimproBeforeImage Image(string table, Guid key, string? json) => new() { TableName = table, RowKey = key.ToString(), RowJson = json ?? "" };

    private static string Json(Dictionary<string, object?> row) => JsonSerializer.Serialize(row.ToDictionary(k => k.Key, k => k.Value switch
    {
        byte[] b when b.Length == 16 => (object?)new Guid(b).ToString(),
        Guid g => g.ToString(),
        DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag,
        long or int or short or decimal or double => k.Value,
        null => null,
        _ => Convert.ToString(k.Value, CultureInfo.InvariantCulture)
    }));

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}
