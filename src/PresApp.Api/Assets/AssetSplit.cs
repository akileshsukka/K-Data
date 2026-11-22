using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Documents;
using PresApp.Domain.GateForms;
using PresApp.Domain.Groups;
using PresApp.Domain.Monitoring;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

/// <summary>One gate of the asset as it is now (preview) or as one part gets it (request).</summary>
public sealed record SplitGateDto(int Gate, string? Status, string? Comments);

public sealed record SplitReadingDto(long Id, string Measurement, decimal Value, string? UnitOfMeasure, DateTime MeasuredAtUtc, string RecordedBy, string? Note);

/// <summary>
/// What a split would start from. <see cref="Stamp"/> is sent back with the split: if the asset changed in between, the
/// split is refused and the preview must be reloaded. <see cref="Blocked"/> says why the asset cannot be split (null: it can).
/// </summary>
public sealed record SplitPreview(
    Guid Id, string Code, string? Tag, string? ClientTag, string? Description, string Unit, string Type, bool InSimpro,
    string CurrentGate, IReadOnlyList<string> SuggestedTags, IReadOnlyList<SplitGateDto> Gates, IReadOnlyList<string> StatusOptions,
    IReadOnlyList<SplitReadingDto> Readings, int Documents, int DocumentedGates, int CustomFields, int Groups, bool ElectricalTestRequired,
    string Stamp, string? Blocked);

/// <summary>One resulting asset: its tag and its 8 gates (status and comments). The first part is the existing asset.</summary>
public sealed record SplitPartRequest(string? Tag, IReadOnlyList<SplitGateDto>? Gates);

/// <summary>Where a reading goes: the index of a part (0 = stays on the existing asset), or null to copy it to every part.</summary>
public sealed record SplitReadingTarget(long Id, int? Part);

public sealed record SplitAssetRequest(string? Stamp, IReadOnlyList<SplitPartRequest>? Parts, IReadOnlyList<SplitReadingTarget>? Readings);

public sealed record SplitPartResult(Guid Id, string Code, string Tag, string CurrentGate, bool New);

public sealed record SplitResult(string ClientTag, IReadOnlyList<SplitPartResult> Parts, int ReadingsMoved, int ReadingsCopied);

/// <summary>
/// Splits one asset that the client lists under a grouped tag (e.g. "P-101A/B") into separate assets that are worked on
/// separately (P-101A, P-101B). The existing asset becomes the first part: it keeps its Id, Equipment ID, Simpro flange,
/// history and readings. Each other part is a new asset with its own Equipment ID (from the counter, as any new asset) and
/// a copy of the preservation data, custom fields, groups, gate documentation and document links. The engineer sets the
/// gates of every part in the preview, and says for each reading which part it belongs to (or copies it to all of them).
/// Every part gets the client's tag in <see cref="Asset.ClientTag"/>; the client Excel joins them back into one row.
/// One transaction; the audit interceptor records every field, plus one "(split)" row per part.
/// </summary>
public sealed partial class AssetSplit(PresAppDbContext db, AssetQueries queries, AuditContext audit, TimeProvider clock)
{
    public const int MinParts = 2;
    public const int MaxParts = 6;
    public const string SplitField = "(split)";

    public async Task<SplitPreview> PreviewAsync(Guid id, CancellationToken ct)
    {
        var asset = await LoadAsync(id, ct);
        var p = asset.Preservation!;
        var readings = await ReadingsAsync(id, ct);
        var options = await AssetEditor.LoadActiveOptionsAsync(db, ct);
        var statusField = PreservationFields.Find("G1Status")!;
        var documents = await db.DocumentLinks.CountAsync(l => l.AssetId == id && l.RemovedAtUtc == null, ct);
        var source = asset.ClientTag ?? asset.Tag;

        return new SplitPreview(
            asset.Id, asset.Code, asset.Tag, asset.ClientTag, asset.Description, asset.Unit.Name, asset.Type.ToString(), asset.SimproFlangeId is not null,
            FieldValues.CurrentGateText(p.CurrentGate), SuggestTags(asset.Tag),
            Enumerable.Range(1, 8).Select(g => new SplitGateDto(g, p.GetGateStatus(g), GateComments(p, g))).ToList(),
            AssetEditor.OptionsFor(statusField, options, asset.Type),
            readings.Select(r => new SplitReadingDto(r.Id, r.MeasurementType.Label, r.Value, r.MeasurementType.UnitOfMeasure, r.MeasuredAtUtc, r.RecordedBy, r.Note)).ToList(),
            documents, asset.GateDocuments.Count, asset.FieldValues.Count, asset.GroupMemberships.Count, asset.ElectricalTest?.Required == true,
            Stamp(asset, readings),
            !asset.IsActive ? "The asset is inactive. Reactivate it first."
            : asset.Type == AssetType.Pipeline ? "Lines are not split: each line is already its own asset."
            : string.IsNullOrWhiteSpace(source) ? "The asset has no tag."
            : null);
    }

    public async Task<SplitResult> SplitAsync(Guid id, SplitAssetRequest request, string user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var asset = await LoadAsync(id, ct);
        var p = asset.Preservation!;
        var readings = await ReadingsAsync(id, ct, tracking: true);
        if (!asset.IsActive) throw ApiProblem.Invalid("The asset is inactive. Reactivate it first.");
        if (asset.Type == AssetType.Pipeline) throw ApiProblem.Invalid("Lines are not split: each line is already its own asset.");
        if (request.Stamp != Stamp(asset, readings))
            throw ApiProblem.Conflict("The asset changed since the preview was opened (a reading, a gate or another field). Nothing was split; reload the preview and check it again.");

        var clientTag = asset.ClientTag ?? asset.Tag ?? throw ApiProblem.Invalid("The asset has no tag.");
        var parts = request.Parts ?? [];
        if (parts.Count is < MinParts or > MaxParts) throw ApiProblem.Invalid($"Split into {MinParts} to {MaxParts} assets.");

        // Tags: present, short enough, different from each other and from the client tag, and not used in the unit.
        var tags = parts.Select(x => ValueNormalizer.CleanText(x.Tag)).ToList();
        if (tags.Any(t => t is null)) throw ApiProblem.Invalid("Enter the tag of every part.");
        if (tags.FirstOrDefault(t => t!.Length > Asset.TagMaxLength) is { } tooLong) throw ApiProblem.Invalid($"{tooLong} is longer than {Asset.TagMaxLength} characters.");
        if (tags.GroupBy(t => t!, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice) throw ApiProblem.Invalid($"{twice.Key} is given to two parts.");
        if (tags.FirstOrDefault(t => string.Equals(t, clientTag, StringComparison.OrdinalIgnoreCase)) is { } same)
            throw ApiProblem.Invalid($"{same} is the client's tag; give each part its own tag (e.g. {string.Join(", ", SuggestTags(clientTag).DefaultIfEmpty(clientTag + "A"))}).");
        var lowered = tags.Select(t => t!.ToLower()).ToList();
        var taken = await db.Assets.Where(a => a.UnitId == asset.UnitId && a.IsActive && a.Id != asset.Id && a.Tag != null && lowered.Contains(a.Tag.ToLower()))
            .Select(a => new { a.Tag, a.Code }).ToListAsync(ct);
        if (taken.Count > 0) throw ApiProblem.Conflict($"{taken[0].Tag} is already the tag of {taken[0].Code} in {asset.Unit.Name}.");

        // Gates: every part sends its 8 gates; values are checked as on the asset page.
        var options = await AssetEditor.LoadActiveOptionsAsync(db, ct);
        var gates = new List<(string? Status, string? Comments)[]>();
        foreach (var (part, i) in parts.Select((x, i) => (x, i)))
        {
            var row = new (string? Status, string? Comments)[8];
            for (var g = 1; g <= 8; g++)
            {
                var sent = part.Gates?.FirstOrDefault(x => x.Gate == g) ?? throw ApiProblem.Invalid($"{tags[i]}: gate {g} is missing.");
                row[g - 1] = (Prepare($"G{g}Status", sent.Status, asset.Type, options, tags[i]!), Prepare($"G{g}Comments", sent.Comments, asset.Type, options, tags[i]!));
            }
            gates.Add(row);
        }

        // Readings: each one listed once, belonging to the asset; a part index in range or null (all parts).
        var targets = request.Readings ?? [];
        if (targets.Select(t => t.Id).Distinct().Count() != targets.Count) throw ApiProblem.Invalid("A reading is listed twice.");
        var byId = readings.ToDictionary(r => r.Id);
        if (targets.Any(t => !byId.ContainsKey(t.Id))) throw ApiProblem.Invalid("A reading does not belong to this asset. Reload the preview.");
        if (targets.Any(t => t.Part is { } k && (k < 0 || k >= parts.Count))) throw ApiProblem.Invalid("A reading is given to a part that does not exist.");
        if (readings.Count != targets.Count) throw ApiProblem.Invalid("Say where every reading goes (a part, or all of them).");
        var target = targets.ToDictionary(t => t.Id, t => t.Part);

        // Build the new parts, copies of the existing asset.
        var now = clock.GetUtcNow().UtcDateTime;
        var assets = new List<Asset> { asset };
        var originalTag = asset.Tag;
        foreach (var tag in tags.Skip(1))
        {
            var b = new Asset
            {
                Id = Guid.NewGuid(), Type = asset.Type, Code = await AssetEditor.NextCodeAsync(db, asset.Discipline.Prefix ?? throw ApiProblem.Invalid(
                    $"The discipline {asset.Discipline.Name} has no Equipment ID prefix. An administrator must set it first."), asset.Unit.Number, ct),
                Tag = tag, ClientTag = clientTag, Description = asset.Description,
                DisciplineId = asset.DisciplineId, UnitId = asset.UnitId, Priority = asset.Priority,
            };
            var copy = new AssetPreservation();
            db.Entry(copy).CurrentValues.SetValues(db.Entry(p).CurrentValues);
            copy.AssetId = b.Id;
            copy.RowVersion = [];
            b.Preservation = copy;
            foreach (var d in asset.GateDocuments)
                b.GateDocuments.Add(new GateDocumentation { AssetId = b.Id, Gate = d.Gate, Status = d.Status, ReceivedOn = d.ReceivedOn, Reference = d.Reference, Note = d.Note });
            foreach (var v in asset.FieldValues)
                b.FieldValues.Add(new AssetFieldValue { AssetId = b.Id, FieldDefinitionId = v.FieldDefinitionId, ValueText = v.ValueText, ValueNumber = v.ValueNumber, ValueDate = v.ValueDate });
            foreach (var m in asset.GroupMemberships)
                b.GroupMemberships.Add(new AssetGroupMember { GroupId = m.GroupId, AssetId = b.Id, AddedAtUtc = now, AddedBy = user });
            // The E7 test is done per motor: the new part only inherits that it needs one, never the values.
            if (asset.ElectricalTest?.Required == true) b.ElectricalTest = new ElectricalTest { AssetId = b.Id, Required = true };
            db.Assets.Add(b);
            assets.Add(b);
        }
        asset.Tag = tags[0];
        asset.ClientTag = clientTag;

        // Same signed documents (the file is not copied, only linked) and the same gate form header, except the Record No.:
        // each part has its own (the first part keeps it; the others get theirs when their form is printed).
        var links = await db.DocumentLinks.Where(l => l.AssetId == id && l.RemovedAtUtc == null).ToListAsync(ct);
        var form = await db.GateFormEntries.AsNoTracking().FirstOrDefaultAsync(f => f.AssetId == id, ct);
        foreach (var b in assets.Skip(1))
        {
            foreach (var l in links) db.DocumentLinks.Add(new DocumentLink { DocumentId = l.DocumentId, AssetId = b.Id, Gate = l.Gate, LinkedAtUtc = now, LinkedBy = user });
            if (form is not null)
                db.GateFormEntries.Add(new GateFormEntry { AssetId = b.Id, RecordNo = null, Date = form.Date, Section = form.Section, WorkPack = form.WorkPack, UnitIdText = form.UnitIdText });
        }

        // Gates as set in the preview. Current Gate follows them when they changed (the engineer approved them in the preview).
        var today = SiteClock.Today(clock);
        foreach (var (a, i) in assets.Select((a, i) => (a, i)))
        {
            var pp = a.Preservation!;
            var changed = false;
            for (var g = 1; g <= 8; g++)
            {
                var (status, comments) = gates[i][g - 1];
                changed |= !string.Equals(pp.GetGateStatus(g), status, StringComparison.Ordinal);
                pp.SetGateStatus(g, status);
                SetGateComments(pp, g, comments);
            }
            if (!changed) continue;
            var outcome = PreservationRules.Apply(pp, gateStatusChanged: true, fieldActionHappened: false, today, n2Blanket: a.Type != AssetType.ElectricalInstrument);
            if (outcome.ProposedCurrentGate is { } next) pp.CurrentGate = next;
            AssetEditor.EnsureDocumentRows(a);
        }

        // Readings: moved to their part, or copied to every other part (marked as copied). Never edited otherwise.
        int moved = 0, copied = 0;
        var copyNote = $"Copied from {originalTag} when it was split.";
        foreach (var r in readings)
        {
            if (target[r.Id] is { } k)
            {
                if (k == 0) continue;
                r.AssetId = assets[k].Id;
                moved++;
                continue;
            }
            foreach (var b in assets.Skip(1))
            {
                db.Readings.Add(new Reading
                {
                    AssetId = b.Id, MeasurementTypeId = r.MeasurementTypeId, Value = r.Value, MeasuredAtUtc = r.MeasuredAtUtc, RecordedAtUtc = r.RecordedAtUtc,
                    RecordedBy = r.RecordedBy, Source = r.Source, Phase = r.Phase,
                    Note = Truncate(string.IsNullOrWhiteSpace(r.Note) ? copyNote : $"{copyNote} {r.Note}", 4000),
                });
                copied++;
            }
        }

        // Latest readings of each part from its own readings. A measurement that never had readings (migrated values) keeps its copy.
        var hadReadings = readings.Select(r => r.MeasurementType.Key).ToHashSet();
        var keys = await db.MeasurementTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Key, ct);
        var all = readings.Select(r => (Reading: r, Part: target[r.Id])).ToList();
        foreach (var (a, i) in assets.Select((a, i) => (a, i)))
        {
            var mine = all.Where(x => x.Part is null || x.Part == i).Select(x => x.Reading).ToList();
            var pp = a.Preservation!;
            foreach (var key in hadReadings)
            {
                var latest = mine.Where(r => keys[r.MeasurementTypeId] == key).OrderByDescending(r => r.MeasuredAtUtc).ThenByDescending(r => r.Id).FirstOrDefault();
                AssetEditor.SetLatest(pp, key, latest?.Value);
            }
            if (hadReadings.Count > 0) pp.LastReadingAtUtc = mine.Count > 0 ? mine.Max(r => r.MeasuredAtUtc) : null;
        }

        await db.SaveChangesAsync(ct);

        var summary = string.Join(", ", assets.Select(a => $"{a.Tag} ({a.Code})"));
        var batch = Guid.NewGuid();
        foreach (var a in assets)
        {
            db.ChangeLogs.Add(new ChangeLog
            {
                EntityType = nameof(Asset), EntityKey = a.Id.ToString(), AssetId = a.Id, Field = SplitField,
                OldValue = $"{originalTag} ({asset.Code})", NewValue = Truncate($"Split into {summary}", 4000),
                ChangedAtUtc = now, ChangedBy = audit.UserName, Source = ChangeSource.App, BatchId = batch,
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new SplitResult(clientTag,
            assets.Select((a, i) => new SplitPartResult(a.Id, a.Code, a.Tag!, FieldValues.CurrentGateText(a.Preservation!.CurrentGate), i > 0)).ToList(),
            moved, copied);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Asset> LoadAsync(Guid id, CancellationToken ct)
    {
        var asset = await queries.LoadAsync(id, tracking: true, ct);
        await db.Entry(asset).Collection(a => a.FieldValues).LoadAsync(ct);
        return asset;
    }

    private Task<List<Reading>> ReadingsAsync(Guid id, CancellationToken ct, bool tracking = false)
    {
        var q = db.Readings.Include(r => r.MeasurementType).Where(r => r.AssetId == id).OrderByDescending(r => r.MeasuredAtUtc).ThenByDescending(r => r.Id);
        return (tracking ? q : q.AsNoTracking()).ToListAsync(ct);
    }

    /// <summary>Changes when anything the preview shows changes: the asset, its preservation row, a gate documentation row or a reading.</summary>
    private static string Stamp(Asset a, List<Reading> readings) => string.Join(".",
        a.UpdatedAtUtc.Ticks, a.Preservation!.UpdatedAtUtc.Ticks,
        a.GateDocuments.Select(d => d.UpdatedAtUtc.Ticks).DefaultIfEmpty(0).Max(), a.GateDocuments.Count,
        readings.Count, readings.Select(r => r.Id).DefaultIfEmpty(0).Max());

    private static string? Prepare(string key, string? raw, AssetType type, Dictionary<string, List<string>> options, string tag)
    {
        var field = PreservationFields.Find(key)!;
        var error = FieldValues.Prepare(field, type, raw, options, out var value);
        if (error is not null) throw ApiProblem.Invalid($"{tag}, {field.Label}: {error}");
        return FieldValues.Format(value);
    }

    private static string? GateComments(AssetPreservation p, int gate) => gate switch
    {
        1 => p.G1Comments, 2 => p.G2Comments, 3 => p.G3Comments, 4 => p.G4Comments,
        5 => p.G5Comments, 6 => p.G6Comments, 7 => p.G7Comments, 8 => p.G8Comments,
        _ => throw new ArgumentOutOfRangeException(nameof(gate)),
    };

    private static void SetGateComments(AssetPreservation p, int gate, string? value)
    {
        switch (gate)
        {
            case 1: p.G1Comments = value; break;
            case 2: p.G2Comments = value; break;
            case 3: p.G3Comments = value; break;
            case 4: p.G4Comments = value; break;
            case 5: p.G5Comments = value; break;
            case 6: p.G6Comments = value; break;
            case 7: p.G7Comments = value; break;
            case 8: p.G8Comments = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(gate));
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// The tags the parts most likely have, read from a grouped tag, written as the client writes it:
    /// "P-101A/B" → P-101A, P-101B; "541-E-116 A1/A2" → 541-E-116 A1, 541-E-116 A2; "P-101A/P-101B" → P-101A, P-101B;
    /// serial numbers in brackets split only inside them: "541-E-119 (S/N 043&amp;044)" → 541-E-119 (S/N 043), 541-E-119 (S/N 044).
    /// Empty when the tag does not look grouped (the engineer types them).
    /// </summary>
    public static IReadOnlyList<string> SuggestTags(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return [];
        tag = tag.Trim();
        List<string> result;
        if (BracketRegex().Match(tag) is { Success: true } m)
        {
            // Inside the brackets first; otherwise the part before them, each keeping the brackets ("P-101A/B (spare)").
            var inner = Pieces(m.Groups["inner"].Value);
            var open = tag.IndexOf('(');
            result = inner.Count > 0
                ? inner.Select(p => m.Groups["head"].Value + p + m.Groups["tail"].Value).ToList()
                : Pieces(tag[..open]).Select(p => $"{p} {tag[open..]}").ToList();
        }
        else
            result = Pieces(tag);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Count && result.All(r => r.Length <= Asset.TagMaxLength) ? result : [];
    }

    private static List<string> Pieces(string text)
    {
        var pieces = SeparatorRegex().Split(text.Trim()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (pieces.Count is < MinParts or > MaxParts) return [];
        var first = pieces[0];
        // A short piece replaces the same number of characters at the end of the first one: P-101A + B → P-101B, …-047 + 048 → …-048.
        return [first, .. pieces.Skip(1).Select(p => p.Length < first.Length && p.Length <= 3 ? first[..^p.Length] + p : p)];
    }

    [GeneratedRegex(@"\s*(?:/|&|,|\+|\bAND\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SeparatorRegex();

    /// <summary>"head (S/N: inner) tail": the S/N label belongs to the head, so its slash is not a separator.</summary>
    [GeneratedRegex(@"^(?<head>[^()]*\(\s*(?:S\s*/\s*N\s*[:.]?\s*)?)(?<inner>[^()]*)(?<tail>\).*)$", RegexOptions.IgnoreCase)]
    private static partial Regex BracketRegex();
}
