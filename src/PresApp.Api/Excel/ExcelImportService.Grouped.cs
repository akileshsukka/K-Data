using ClosedXML.Excel;
using PresApp.Api.Assets;
using PresApp.Domain.Assets;
using PresApp.Domain.Imports;
using PresApp.Domain.Reporting;
using PresApp.Domain.Rules;

namespace PresApp.Api.Excel;

/// <summary>
/// Import of a grouped row of the unit file: the parts of a split item (P-101A and P-101B shown as the client's
/// P-101A/B, see <see cref="GroupedRows"/>). A cell that still reads as the row was written is not a change. A changed
/// cell is never applied: the file does not say which part it is for. Each part gets an entry
/// (<see cref="ImportOutcome.Grouped"/>) with the value it would take, and the engineer decides in Import issues which
/// parts get it. Values are checked as for any row (lists, dates…); a part that already has the value is "No change".
/// </summary>
public sealed partial class ExcelImportService
{
    private List<ImportChange> GroupedRowChanges(int number, IReadOnlyList<BaseRow> parts, IXLTableRow row, ListColumns list,
        Dictionary<Guid, Asset> assets, Dictionary<string, List<string>> options, List<DashboardCategory> unitCategories)
    {
        var changes = new List<ImportChange>();
        var clientTag = parts[0].Tag;
        string TagOf(BaseRow p) => assets.GetValueOrDefault(p.Key)?.Tag ?? p.Code;
        var names = string.Join(" and ", parts.Select(TagOf));
        ImportChange Change(BaseRow part, string field, ImportOutcome outcome, string? reason, string? baseValue, string? excel, string? current = null, string? newValue = null) => new()
        {
            AssetId = part.Key, AssetCode = assets.GetValueOrDefault(part.Key)?.Code ?? part.Code, AssetTag = Truncate(TagOf(part), 55), RowNumber = number,
            Field = field, BaseValue = baseValue, ExcelValue = excel, CurrentValue = current, NewValue = newValue, Outcome = outcome, Reason = reason
        };
        string Pending(string? value) =>
            $"Row {clientTag} stands for {names}. Not applied: in Import issues, apply \"{value ?? "empty"}\" to the parts it is for, or close it.";

        if (list.TagColumn is { } tcol)
        {
            var tagInFile = ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(tcol)));
            if (!string.Equals(tagInFile, ValueNormalizer.CleanText(clientTag), StringComparison.Ordinal))
                changes.Add(Change(parts[0], "Tag", ImportOutcome.Warning, "EQUIP NO was changed in the file. Tags are changed in the app; the row was matched by its hidden Key.",
                    clientTag, tagInFile));
        }

        foreach (var col in list.Editable)
        {
            var field = col.Field!;
            if (!parts.All(p => p.Values.ContainsKey(field.Key))) continue;
            var shown = GroupedRows.Combine(field, parts.Select(p => (TagOf(p), p.Values[field.Key])).ToList());
            var excel = WorkbookFormat.CellText(row.Cell(col.Index + 1));
            if (FieldValues.Same(field, excel, shown)) continue;   // the engineer did not change this cell
            var excelClean = ValueNormalizer.CleanText(excel);

            foreach (var part in parts)
            {
                var baseValue = part.Values[field.Key];
                if (!assets.TryGetValue(part.Key, out var asset)) { changes.Add(Change(part, field.Key, ImportOutcome.Rejected, "The equipment no longer exists.", baseValue, excelClean)); continue; }
                var current = FieldValues.Read(field, asset);
                if (!asset.IsActive) { changes.Add(Change(part, field.Key, ImportOutcome.Rejected, "Inactive equipment: it cannot be edited.", baseValue, excelClean, current)); continue; }
                var error = FieldValues.Prepare(field, asset.Type, excel, options, out var value);
                if (error is not null) { changes.Add(Change(part, field.Key, ImportOutcome.Invalid, error, baseValue, excelClean, current)); continue; }
                var newText = FieldValues.Format(value);
                changes.Add(FieldValues.Same(field, current, newText)
                    ? Change(part, field.Key, ImportOutcome.NoChange, "The app already has this value.", baseValue, excelClean, current, newText)
                    : Change(part, field.Key, ImportOutcome.Grouped, Pending(newText), baseValue, excelClean, current, newText));
            }
        }

        // ---- template v4: Hold / N/A category, compared with what the row showed
        if ((list.Hold is not null || list.Na is not null) && parts.All(p => p.Values.ContainsKey(TemplateV4.CategoryField)))
        {
            var shown = GroupedRows.Category(parts.Select(p => p.Values[TemplateV4.CategoryField]).ToList(), t => t.StartsWith("Hold:", StringComparison.Ordinal));
            var hold = list.Hold is null ? null : ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(list.Hold.Index + 1)));
            var na = list.Na is null ? null : ValueNormalizer.CleanText(WorkbookFormat.CellText(row.Cell(list.Na.Index + 1)));
            var excelText = hold is not null && na is not null ? $"Hold:{hold} / N/A:{na}" : hold is not null ? $"Hold:{hold}" : na is not null ? $"N/A:{na}" : null;
            var section = hold is not null ? CategorySection.Hold : CategorySection.NotApplicable;
            var code = Reporting.UnitDashboard.NormalizeCode(hold ?? na);
            var first = assets.GetValueOrDefault(parts[0].Key);
            var scope = first is null ? (CategoryScope?)null : CategoryScopes.Of(first.Type);
            var wanted = code is null ? null : unitCategories.FirstOrDefault(c => c.Section == section && c.Code == code && (scope is null || c.Scope == scope));
            var wantedToken = excelText is null ? null : wanted is null ? excelText : TemplateV4.Token(wanted);
            if (wantedToken != shown)
            {
                foreach (var part in parts)
                {
                    var baseValue = part.Values[TemplateV4.CategoryField];
                    if (!assets.TryGetValue(part.Key, out var asset)) { changes.Add(Change(part, TemplateV4.CategoryField, ImportOutcome.Rejected, "The equipment no longer exists.", baseValue, excelText)); continue; }
                    var current = TemplateV4.Token(asset.Preservation?.DashboardCategory);
                    if (!asset.IsActive) changes.Add(Change(part, TemplateV4.CategoryField, ImportOutcome.Rejected, "Inactive equipment: it cannot be edited.", baseValue, excelText, current));
                    else if (hold is not null && na is not null)
                        changes.Add(Change(part, TemplateV4.CategoryField, ImportOutcome.Invalid, "Both Hold Category and N/A Category are filled. Keep only one.", baseValue, excelText, current));
                    else if (excelText is not null && wanted is null)
                        changes.Add(Change(part, TemplateV4.CategoryField, ImportOutcome.Invalid,
                            $"\"{hold ?? na}\" is not an active {TemplateV4.SectionText(section)} category of this unit. Categories are created in K Data (Dashboard categories).",
                            baseValue, excelText, current));
                    else if (current == wantedToken)
                        changes.Add(Change(part, TemplateV4.CategoryField, ImportOutcome.NoChange, "The app already has this value.", baseValue, excelText, current, wantedToken));
                    else
                        changes.Add(Change(part, TemplateV4.CategoryField, ImportOutcome.Grouped, Pending(wantedToken), baseValue, excelText, current, wantedToken));
                }
            }
        }
        return changes;
    }
}
