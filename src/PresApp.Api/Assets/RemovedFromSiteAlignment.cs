using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Common;
using PresApp.Domain.Rules;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

/// <summary>One asset whose Current Gate and Asset Availability disagree about Removed from Site.</summary>
public sealed record AlignmentRow(
    string Case, string Unit, string Code, string? Tag, string Type, string Discipline, bool IsActive,
    string? AssetAvailability, string CurrentGate, string HighestCompletedGate, string Action, string Result);

/// <summary>
/// One-off review (and optional fix) of the data that existed before the Asset Availability rule:
/// <list type="bullet">
/// <item>A: Asset Availability = Removed from Site, Current Gate is not → Current Gate is set to Removed from Site.</item>
/// <item>B: Current Gate = Removed from Site, Asset Availability empty → Asset Availability is set to Removed from Site.</item>
/// <item>C: Current Gate = Removed from Site, Asset Availability says something else (Available, TBC…) → review only:
/// in the app, set Asset Availability to Removed from Site (keeps it) or move it from Removed from Site to the right value
/// (Current Gate then follows the gates).</item>
/// </list>
/// Inactive assets are listed but never changed. Every change goes through EF, so it is in the history (source System).
/// </summary>
public sealed class RemovedFromSiteAlignment(PresAppDbContext db, AuditContext audit)
{
    public const string ToolName = "align-removed-from-site";

    public async Task<List<AlignmentRow>> RunAsync(bool apply, CancellationToken ct = default)
    {
        audit.UserName = ToolName;
        audit.Source = ChangeSource.System;

        var assets = await db.Assets
            .Include(a => a.Preservation).Include(a => a.Unit).Include(a => a.Discipline)
            .Where(a => a.Preservation != null)
            .ToListAsync(ct);

        var rows = new List<AlignmentRow>();
        foreach (var a in assets.OrderBy(a => a.Unit.Name, StringComparer.Ordinal).ThenBy(a => a.Tag ?? a.Code, NaturalComparer.Instance))
        {
            var p = a.Preservation!;
            var removedAvailability = PreservationRules.IsRemoved(p.AssetAvailability);
            var removedGate = p.CurrentGate == CurrentGate.RemovedFromSite;
            if (removedAvailability == removedGate) continue;

            string kase, action;
            if (removedAvailability) { kase = "A"; action = "Set Current Gate to Removed from Site"; }
            else if (string.IsNullOrWhiteSpace(p.AssetAvailability)) { kase = "B"; action = "Set Asset Availability to Removed from Site"; }
            else { kase = "C"; action = "Review in the app"; }

            var row = new AlignmentRow(kase, a.Unit.Name, a.Code, a.Tag, a.Type.ToString(), a.Discipline.Name, a.IsActive,
                p.AssetAvailability, FieldValues.CurrentGateText(p.CurrentGate),
                p.HighestCompletedGate() is { } g ? $"G{g}" : "None", action, "");

            string result;
            if (kase == "C") result = "Not changed";
            else if (!a.IsActive) result = "Inactive: not changed";
            else if (!apply) result = "To change";
            else
            {
                if (kase == "A") p.CurrentGate = CurrentGate.RemovedFromSite;
                else p.AssetAvailability = PreservationRules.RemovedFromSite;
                result = "Changed";
            }
            rows.Add(row with { Result = result });
        }

        if (apply) await db.SaveChangesAsync(ct);
        return rows;
    }

    public static string ToCsv(IEnumerable<AlignmentRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Case,Unit,Equipment ID,Tag,Type,Discipline,Active,Asset Availability,Current Gate,Highest Completed Gate,Action,Result");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                r.Case, r.Unit, r.Code, r.Tag, r.Type, r.Discipline, r.IsActive ? "Yes" : "No",
                r.AssetAvailability, r.CurrentGate, r.HighestCompletedGate, r.Action, r.Result
            }.Select(Csv)));
        }
        return sb.ToString();
    }

    private static string Csv(string? v) =>
        v is null ? "" : v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    public static string Summary(IReadOnlyCollection<AlignmentRow> rows) => string.Create(CultureInfo.InvariantCulture,
        $"A (availability removed, gate not): {rows.Count(r => r.Case == "A")}. " +
        $"B (gate removed, availability empty): {rows.Count(r => r.Case == "B")}. " +
        $"C (gate removed, availability says otherwise; review): {rows.Count(r => r.Case == "C")}. " +
        $"Inactive (never changed): {rows.Count(r => !r.IsActive)}.");
}
