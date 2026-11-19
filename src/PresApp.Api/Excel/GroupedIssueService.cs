using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Imports;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Excel;

/// <summary>
/// The decision on a changed cell of a grouped row (<see cref="ImportOutcome.Grouped"/>): each part of the split item has
/// its own issue, and applying it writes the value of the file to that part only. "Apply to both" is applying both
/// issues; "none" is marking them resolved. The value is applied only while the part still has the value it had when
/// the file was imported; otherwise it is a conflict and nothing is written. Same rules as an applied Excel cell
/// (validation, Current Gate moving forward with a completed gate, documentation rows), recorded as an Excel change.
/// </summary>
public sealed class GroupedIssueService(PresAppDbContext db, AssetQueries queries, AuditContext audit, TimeProvider clock)
{
    public async Task<ImportChangeDto> ApplyAsync(long changeId, ClaimsPrincipal user, CancellationToken ct)
    {
        var change = await db.ImportChanges.Include(c => c.ImportRun).FirstOrDefaultAsync(c => c.Id == changeId, ct) ?? throw ApiProblem.NotFound("Issue not found.");
        if (change.Outcome != ImportOutcome.Grouped) throw ApiProblem.Invalid("Only a change of a grouped row can be applied from here.");
        if (change.Resolution != IssueResolution.Open) throw ApiProblem.Invalid("This issue is already resolved.");
        if (change.ImportRun.Status is not (ImportStatus.Applied or ImportStatus.AppliedWithIssues)) throw ApiProblem.Invalid("The import of this change was not applied.");
        if (change.AssetId is not { } assetId) throw ApiProblem.Invalid("This change has no equipment.");

        var asset = await queries.LoadAsync(assetId, tracking: true, ct);
        if (!asset.IsActive) throw ApiProblem.Invalid("The equipment is inactive: it cannot be edited.");
        var p = asset.Preservation!;
        var tag = asset.Tag ?? asset.Code;
        audit.Source = ChangeSource.Excel;

        if (change.Field == TemplateV4.CategoryField)
        {
            var current = TemplateV4.Token(p.DashboardCategory);
            if (current != change.CurrentValue) throw Changed(tag, current);
            var wanted = change.NewValue is null ? null
                : (await db.DashboardCategories.Where(c => c.UnitId == asset.UnitId && c.IsActive && c.Code != null && c.Scope == Domain.Reporting.CategoryScopes.Of(asset.Type)).ToListAsync(ct))
                    .FirstOrDefault(c => TemplateV4.Token(c) == change.NewValue)
                  ?? throw ApiProblem.Invalid($"The category {change.NewValue} is no longer an active category of this unit.");
            p.DashboardCategoryId = wanted?.Id;
            p.DashboardCategory = wanted;
        }
        else
        {
            var field = PreservationFields.Find(change.Field);
            if (field is null || !field.IsEditable) throw ApiProblem.Invalid("This field cannot be edited.");
            if (!user.HasPermission(field.Permission)) throw ApiProblem.Forbidden($"Your role cannot edit {field.Label}.");
            var current = FieldValues.Read(field, asset);
            if (!FieldValues.Same(field, current, change.CurrentValue)) throw Changed(tag, current);
            var error = FieldValues.Prepare(field, asset.Type, change.NewValue, await AssetEditor.LoadActiveOptionsAsync(db, ct), out var value);
            if (error is not null) throw ApiProblem.Invalid(error);
            var availabilityBefore = p.AssetAvailability;
            FieldValues.Write(field, asset, value);

            // As an applied Excel cell: milestones, and Current Gate moves forward to the highest completed gate.
            var gate = PreservationFields.IsGateStatus(field);
            var outcome = PreservationRules.Apply(p, gate, gate && AssetPreservation.IsCompleted(FieldValues.Format(value)), SiteClock.Today(clock),
                field.Key == nameof(AssetPreservation.AssetAvailability), availabilityBefore, n2Blanket: asset.Type != AssetType.ElectricalInstrument);
            if (outcome.ProposedCurrentGate is { } next && (int)next > (int)p.CurrentGate) p.CurrentGate = next;
            if (outcome.ProposedLastFieldActionDate is { } day) p.LastFieldActionDate = day;
            AssetEditor.EnsureDocumentRows(asset);
        }

        change.Resolution = IssueResolution.Resolved;
        change.ResolvedBy = user.Identity?.Name ?? "unknown";
        change.ResolvedAtUtc = clock.GetUtcNow().UtcDateTime;
        change.ResolutionNote = $"Applied to {tag}.";
        await db.SaveChangesAsync(ct);
        return ImportQueries.ToDto(change);
    }

    private static ApiProblem Changed(string tag, string? current) => ApiProblem.Conflict(
        $"{tag} was changed in the app after this file was imported (now \"{current ?? "empty"}\"). Nothing was applied: check which value is right and set it in the app.");
}
