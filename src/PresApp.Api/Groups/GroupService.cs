using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Assets;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Groups;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Groups;

public sealed record GroupTypeDto(int Id, string Name, string Prefix);

public sealed record GroupProgress(int Members, int Equipment, int Lines, int ElectricalInstrument, int[] GatesCompleted, int Preserved, int RemovedFromSite);

public sealed record GroupDto(int Id, string Code, string Name, string? Description, int TypeId, string Type, int? UnitId, string? UnitName,
    bool IsActive, GroupProgress Progress, string UpdatedBy, DateTime UpdatedAtUtc);

public sealed record GroupMemberDto(Guid Id, string Code, string? Tag, string? Description, string Type, string Discipline, string CurrentGate,
    IReadOnlyList<string?> Gates, DateTime? PreservedAtUtc, bool IsActive, DateTime AddedAtUtc, string AddedBy);

public sealed record GroupDetail(GroupDto Group, List<GroupMemberDto> Members);

public sealed record SaveGroupRequest(int TypeId, string? Code, string Name, string? Description, int? UnitId, bool IsActive = true);
public sealed record MembersRequest(IReadOnlyList<Guid> AssetIds);
public sealed record MembersResult(int Added, int AlreadyIn, int Removed, GroupDetail Detail);
public sealed record PasteMembersRequest(string? Text, bool Apply);
public sealed record PasteMembersRow(int Row, string Text, Guid? AssetId, string? Code, string? Tag, string Outcome, string? Note);
public sealed record PasteMembersResult(int ToAdd, int AlreadyIn, int NotFound, List<PasteMembersRow> Rows, GroupDetail? Detail);

/// <summary>
/// Internal groups of assets of any type (Workpack, Circuit, System…). They never go to the client Excel nor to Simpro.
/// A group belongs to one unit (or to none, to span units); its code is unique, e.g. WP-039-001.
/// </summary>
public sealed class GroupService(PresAppDbContext db, TimeProvider clock)
{
    public static string PrefixOf(string typeName)
    {
        var letters = new string(typeName.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        var words = typeName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])))
            : letters.Length >= 2 ? (letters == "WORKPACK" ? "WP" : letters[..2]) : "GR";
    }

    public async Task<List<GroupTypeDto>> TypesAsync(CancellationToken ct) =>
        (await db.GroupTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Id).ToListAsync(ct))
        .Select(t => new GroupTypeDto(t.Id, t.Name, PrefixOf(t.Name))).ToList();

    public async Task<List<GroupDto>> ListAsync(int? unitId, bool includeInactive, CancellationToken ct)
    {
        var q = db.Groups.AsNoTracking().Where(g => includeInactive || g.IsActive);
        if (unitId is { } u) q = q.Where(g => g.UnitId == u || g.UnitId == null);
        var groups = await q.Include(g => g.GroupType).Include(g => g.Unit).ToListAsync(ct);
        var progress = await ProgressAsync(groups.Select(g => g.Id).ToList(), ct);
        return groups.OrderBy(g => g.GroupType.Name).ThenBy(g => g.Code, NaturalComparer.Instance)
            .Select(g => ToDto(g, progress.GetValueOrDefault(g.Id) ?? Empty)).ToList();
    }

    public async Task<GroupDetail> DetailAsync(int id, CancellationToken ct)
    {
        var g = await db.Groups.AsNoTracking().Include(x => x.GroupType).Include(x => x.Unit).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw ApiProblem.NotFound("Group");
        var members = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == id)
            .Select(m => new { m.AddedAtUtc, m.AddedBy, A = m.Asset, Discipline = m.Asset.Discipline.Name, P = m.Asset.Preservation })
            .ToListAsync(ct);
        var progress = await ProgressAsync([id], ct);
        return new GroupDetail(ToDto(g, progress.GetValueOrDefault(id) ?? Empty), members
            .OrderBy(m => m.A.Type).ThenBy(m => m.A.Tag ?? m.A.Code, NaturalComparer.Instance)
            .Select(m => new GroupMemberDto(m.A.Id, m.A.Code, m.A.Tag, m.A.Description, m.A.Type.ToString(), m.Discipline,
                FieldValues.CurrentGateText(m.P?.CurrentGate ?? CurrentGate.New), Enumerable.Range(1, 8).Select(i => m.P?.GetGateStatus(i)).ToList(),
                m.P?.PreservedAtUtc, m.A.IsActive, m.AddedAtUtc, m.AddedBy)).ToList());
    }

    public async Task<GroupDetail> CreateAsync(SaveGroupRequest r, CancellationToken ct)
    {
        var type = await db.GroupTypes.FirstOrDefaultAsync(t => t.Id == r.TypeId && t.IsActive, ct) ?? throw ApiProblem.Invalid("Choose the kind of group.");
        var unit = r.UnitId is { } uid ? await db.Units.FirstOrDefaultAsync(u => u.Id == uid, ct) ?? throw ApiProblem.NotFound("Unit") : null;
        var code = ValueNormalizer.CleanText(r.Code)?.ToUpperInvariant();
        if (code is null)
        {
            var prefix = $"{PrefixOf(type.Name)}-{(unit is null ? "ALL" : unit.Number.ToString("000"))}-";
            var used = await db.Groups.Where(g => g.Code.StartsWith(prefix)).Select(g => g.Code).ToListAsync(ct);
            var next = used.Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            code = $"{prefix}{next:000}";
        }
        var g = new AssetGroup { GroupTypeId = type.Id, UnitId = unit?.Id };
        await FillAsync(g, r with { Code = code }, ct);
        db.Groups.Add(g);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(g.Id, ct);
    }

    public async Task<GroupDetail> UpdateAsync(int id, SaveGroupRequest r, CancellationToken ct)
    {
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiProblem.NotFound("Group");
        if (!await db.GroupTypes.AnyAsync(t => t.Id == r.TypeId, ct)) throw ApiProblem.Invalid("Choose the kind of group.");
        g.GroupTypeId = r.TypeId;
        await FillAsync(g, r with { Code = ValueNormalizer.CleanText(r.Code)?.ToUpperInvariant() ?? g.Code }, ct);
        g.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return await DetailAsync(id, ct);
    }

    private async Task FillAsync(AssetGroup g, SaveGroupRequest r, CancellationToken ct)
    {
        var name = ValueNormalizer.CleanText(r.Name);
        if (name is null) throw ApiProblem.Invalid("Enter the name of the group.");
        if (name.Length > 200) throw ApiProblem.Invalid("The name allows at most 200 characters.");
        var description = ValueNormalizer.CleanText(r.Description);
        if (description?.Length > 1000) throw ApiProblem.Invalid("The description allows at most 1,000 characters.");
        if (r.Code!.Length > 50) throw ApiProblem.Invalid("The code allows at most 50 characters.");
        if (await db.Groups.AnyAsync(x => x.Id != g.Id && x.Code == r.Code, ct)) throw new ApiProblem(409, "Already used", $"The code {r.Code} is already used by another group.");
        g.Code = r.Code;
        g.Name = name;
        g.Description = description;
    }

    /// <summary>Deletes the group and its memberships. The assets themselves are not touched.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiProblem.NotFound("Group");
        await db.GroupMembers.Where(m => m.GroupId == id).ExecuteDeleteAsync(ct);
        db.Groups.Remove(g);
        await db.SaveChangesAsync(ct);
    }

    public async Task<GroupDetail> SetActiveAsync(int id, bool active, CancellationToken ct)
    {
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiProblem.NotFound("Group");
        g.IsActive = active;
        await db.SaveChangesAsync(ct);
        return await DetailAsync(id, ct);
    }

    public async Task<MembersResult> AddMembersAsync(int id, IReadOnlyList<Guid> assetIds, ClaimsPrincipal user, CancellationToken ct)
    {
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiProblem.NotFound("Group");
        if (!g.IsActive) throw ApiProblem.Invalid("The group is inactive. Activate it first.");
        var ids = (assetIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw ApiProblem.Invalid("Choose at least one asset.");
        if (ids.Count > 2000) throw ApiProblem.Invalid("Add at most 2,000 assets at a time.");
        var assets = await db.Assets.Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.UnitId, a.IsActive }).ToListAsync(ct);
        if (assets.Count != ids.Count) throw ApiProblem.NotFound("One of the assets");
        if (g.UnitId is { } u && assets.Any(a => a.UnitId != u)) throw ApiProblem.Invalid("This group belongs to one unit; all its assets must be from that unit.");
        var existing = (await db.GroupMembers.Where(m => m.GroupId == id && ids.Contains(m.AssetId)).Select(m => m.AssetId).ToListAsync(ct)).ToHashSet();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var a in assets.Where(a => !existing.Contains(a.Id)))
            db.GroupMembers.Add(new AssetGroupMember { GroupId = id, AssetId = a.Id, AddedAtUtc = now, AddedBy = user.Identity?.Name ?? "" });
        g.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return new MembersResult(assets.Count - existing.Count, existing.Count, 0, await DetailAsync(id, ct));
    }

    /// <summary>
    /// Adds the assets of a pasted list (line numbers, tags or Equipment IDs, one per line, e.g. a column copied from Excel)
    /// without picking them one by one. Lines are also found when the number is written differently (insulation code,
    /// 1-1/2" or 1.5") or when they hold two numbers. A circuit holds each line once: lines in another circuit are moved.
    /// Preview first (<c>Apply = false</c>), then apply.
    /// </summary>
    public async Task<PasteMembersResult> PasteMembersAsync(int id, PasteMembersRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var g = await db.Groups.Include(x => x.GroupType).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiProblem.NotFound("Group");
        if (!g.IsActive) throw ApiProblem.Invalid("The group is inactive. Activate it first.");
        var tokens = (request.Text ?? "").Replace("\r", "").Replace("\"\"", "\"").Split(['\n', '\t', ';'])
            .Select(t => ValueNormalizer.CleanText(t.Trim().Trim('"')))
            .OfType<string>().Where(t => !ValueNormalizer.ListKey(t).StartsWith("lineno", StringComparison.Ordinal) && ValueNormalizer.ListKey(t) is not ("tagno" or "equipno"))
            .ToList();
        if (tokens.Count == 0) throw ApiProblem.Invalid("Paste at least one line number, tag or Equipment ID.");
        if (tokens.Count > 2000) throw ApiProblem.Invalid("Paste at most 2,000 at a time.");

        var assets = await db.Assets.AsNoTracking().Where(a => a.IsActive && (g.UnitId == null || a.UnitId == g.UnitId))
            .Select(a => new { a.Id, a.Code, a.Tag, a.Type }).ToListAsync(ct);
        // Every number of a tag (a line cell can hold two), by exact text and by line identity.
        var byText = new Dictionary<string, HashSet<Guid>>();
        var byIdentity = new Dictionary<string, HashSet<Guid>>();
        void Index(Dictionary<string, HashSet<Guid>> d, string key, Guid assetId) { if (!d.TryGetValue(key, out var set)) d[key] = set = []; set.Add(assetId); }
        foreach (var a in assets)
        {
            Index(byText, ValueNormalizer.ListKey(a.Code), a.Id);
            if (a.Tag is null) continue;
            Index(byText, ValueNormalizer.ListKey(a.Tag), a.Id);
            foreach (var part in a.Tag.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                Index(byText, ValueNormalizer.ListKey(part), a.Id);
                if (LineNumber.Parse(part) is { } parts) Index(byIdentity, LineNumber.IdentityKey(parts), a.Id);
            }
        }
        var info = assets.ToDictionary(a => a.Id);
        var members = (await db.GroupMembers.Where(m => m.GroupId == id).Select(m => m.AssetId).ToListAsync(ct)).ToHashSet();
        var isCircuit = string.Equals(g.GroupType.Name, "Circuit", StringComparison.OrdinalIgnoreCase);
        var otherCircuits = isCircuit
            ? await db.GroupMembers.Where(m => m.GroupId != id && m.Group.GroupTypeId == g.GroupTypeId).Select(m => new { m.AssetId, m.GroupId, m.Group.Code }).ToListAsync(ct)
            : [];

        var rows = new List<PasteMembersRow>();
        var toAdd = new List<Guid>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var found = byText.GetValueOrDefault(ValueNormalizer.ListKey(t))
                        ?? (LineNumber.Parse(t) is { } p ? byIdentity.GetValueOrDefault(LineNumber.IdentityKey(p)) : null);
            if (found is null || found.Count == 0) { rows.Add(new(i + 1, t, null, null, null, "Not found", g.UnitId is null ? null : "Not an active asset of this unit.")); continue; }
            if (found.Count > 1)
            {
                rows.Add(new(i + 1, t, null, null, null, "Several", $"Matches {string.Join(", ", found.Select(f => info[f].Tag ?? info[f].Code))}. Paste the exact line number."));
                continue;
            }
            var a = info[found.First()];
            if (members.Contains(a.Id) || toAdd.Contains(a.Id)) { rows.Add(new(i + 1, t, a.Id, a.Code, a.Tag, "Already in", null)); continue; }
            var moving = otherCircuits.Where(o => o.AssetId == a.Id).Select(o => o.Code).ToList();
            rows.Add(new(i + 1, t, a.Id, a.Code, a.Tag, request.Apply ? "Added" : "Add", moving.Count > 0 ? $"Moved from {string.Join(", ", moving)}." : null));
            toAdd.Add(a.Id);
        }

        GroupDetail? detail = null;
        if (request.Apply && toAdd.Count > 0)
        {
            if (isCircuit)
            {
                var old = await db.GroupMembers.Where(m => m.GroupId != id && m.Group.GroupTypeId == g.GroupTypeId && toAdd.Contains(m.AssetId)).ToListAsync(ct);
                db.GroupMembers.RemoveRange(old);
            }
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var assetId in toAdd)
                db.GroupMembers.Add(new AssetGroupMember { GroupId = id, AssetId = assetId, AddedAtUtc = now, AddedBy = user.Identity?.Name ?? "" });
            g.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        if (request.Apply) detail = await DetailAsync(id, ct);
        return new PasteMembersResult(toAdd.Count, rows.Count(r => r.Outcome == "Already in"), rows.Count(r => r.Outcome is "Not found" or "Several"), rows, detail);
    }

    public async Task<MembersResult> RemoveMembersAsync(int id, IReadOnlyList<Guid> assetIds, CancellationToken ct)
    {
        var g = await db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiProblem.NotFound("Group");
        var ids = (assetIds ?? []).Distinct().ToList();
        var members = await db.GroupMembers.Where(m => m.GroupId == id && ids.Contains(m.AssetId)).ToListAsync(ct);
        db.GroupMembers.RemoveRange(members);
        g.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new MembersResult(0, 0, members.Count, await DetailAsync(id, ct));
    }

    private static readonly GroupProgress Empty = new(0, 0, 0, 0, new int[8], 0, 0);

    private async Task<Dictionary<int, GroupProgress>> ProgressAsync(List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var rows = await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.GroupId) && m.Asset.IsActive)
            .Select(m => new { m.GroupId, m.Asset.Type, P = m.Asset.Preservation }).ToListAsync(ct);
        // Gates and preserved count only the members still on site (Removed from Site is shown apart).
        return rows.GroupBy(r => r.GroupId).ToDictionary(g => g.Key, g =>
        {
            var onSite = g.Where(r => r.P?.CurrentGate != CurrentGate.RemovedFromSite).ToList();
            return new GroupProgress(
                g.Count(), g.Count(r => r.Type == AssetType.Equipment), g.Count(r => r.Type == AssetType.Pipeline),
                g.Count(r => r.Type == AssetType.ElectricalInstrument),
                Enumerable.Range(1, 8).Select(i => onSite.Count(r => AssetPreservation.IsCompleted(r.P?.GetGateStatus(i)))).ToArray(),
                onSite.Count(r => r.P?.PreservedAtUtc != null), g.Count() - onSite.Count);
        });
    }

    private static GroupDto ToDto(AssetGroup g, GroupProgress p) => new(g.Id, g.Code, g.Name, g.Description, g.GroupTypeId, g.GroupType.Name,
        g.UnitId, g.Unit?.Name, g.IsActive, p, g.UpdatedBy, g.UpdatedAtUtc);
}
