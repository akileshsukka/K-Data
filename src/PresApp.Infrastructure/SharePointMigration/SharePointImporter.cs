using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Domain.Imports;
using PresApp.Domain.Monitoring;
using PresApp.Domain.Rules;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Infrastructure.SharePointMigration;

/// <summary>
/// Loads the CSV exports of the SharePoint lists into PresApp. Until go-live it can run any number
/// of times: each applied run REPLACES all asset data with the CSV content (users and catalogs are
/// merged, never removed). A preview run does everything in a transaction and rolls it back.
/// Disabled for good once the setting "Migration.Locked" is true (set at cut-over).
/// </summary>
public sealed partial class SharePointImporter(PresAppDbContext db, AuditContext audit, ILogger<SharePointImporter> logger)
{
    public const string LockedSetting = "Migration.Locked";
    private static readonly string[] DateTimeFormats = ["M/d/yyyy h:mm tt", "M/d/yyyy H:mm", "M/d/yyyy h:mm:ss tt", "M/d/yyyy"];

    private readonly MigrationReport report = new();
    private Dictionary<string, List<string>> options = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> peopleEmails = new(StringComparer.OrdinalIgnoreCase);

    public async Task<MigrationReport> RunAsync(string folder, bool apply, CancellationToken ct = default)
    {
        var locked = await db.Settings.Where(s => s.Key == LockedSetting).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.Equals(locked, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Migration is locked (Migration.Locked = true): PresApp is already the source of truth.");
        }

        var files = new CsvFiles(folder);
        audit.UserName = "migration";
        audit.Source = ChangeSource.Migration;
        audit.SuppressFieldAudit = true;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ClearAssetDataAsync(ct);
        await MergeOptionsAsync(files.Get("DropdownOptions"), ct);
        await MergeUsersAsync(files.Get("AppAdmins"), ct);
        var units = await EnsureUnitsAsync(files.Get("PlantUnitMappings"), files.Get("Equipments"), ct);
        var disciplines = await EnsureDisciplinesAsync(files.Get("Equipments"), ct);
        await db.SaveChangesAsync(ct);

        var assets = LoadEquipments(files.Get("Equipments"), units, disciplines);
        await db.SaveChangesAsync(ct);
        LoadGateDocumentation(files.TryGet("GateDocumentation"), assets);
        await LoadReadingsAsync(files.Get("Readings"), assets, ct);
        LoadStatusHistory(files.Get("StatusHistory"), assets);
        await LoadExcelHistoryAsync(files, units, assets, ct);
        await SetSequencesAsync(ct);
        await db.SaveChangesAsync(ct);

        await VerifyAsync(files.Get("Equipments"), ct);

        report.Applied = apply;
        if (apply) await tx.CommitAsync(ct);
        else await tx.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        logger.LogInformation("SharePoint migration {Mode}: {Assets} assets", apply ? "applied" : "previewed", assets.Count);
        return report;
    }

    // ------------------------------------------------------------------ clear

    private async Task ClearAssetDataAsync(CancellationToken ct)
    {
        await db.ImportChanges.ExecuteDeleteAsync(ct);
        await db.ImportRuns.ExecuteDeleteAsync(ct);
        await db.ExcelSessions.ExecuteDeleteAsync(ct);
        await db.SimproSyncItems.ExecuteDeleteAsync(ct);
        await db.GroupMembers.ExecuteDeleteAsync(ct);
        await db.Readings.ExecuteDeleteAsync(ct);
        await db.ChangeLogs.ExecuteDeleteAsync(ct);
        await db.GateDocuments.ExecuteDeleteAsync(ct);
        await db.FieldValues.ExecuteDeleteAsync(ct);
        await db.PipelineDetails.ExecuteDeleteAsync(ct);
        await db.Preservations.ExecuteDeleteAsync(ct);
        await db.Assets.ExecuteDeleteAsync(ct);
        await db.EquipmentIdSequences.ExecuteDeleteAsync(ct);
    }

    // ------------------------------------------------------------------ catalogs and users

    private async Task MergeOptionsAsync(CsvTable csv, CancellationToken ct)
    {
        var sets = await db.OptionSets.Include(s => s.Values).ToListAsync(ct);
        foreach (var row in csv.Rows)
        {
            var setKey = csv.Get(row, "Option Set").Trim();
            if (setKey.Length == 0 || setKey.Equals("Discipline", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = ValueNormalizer.CleanText(csv.Get(row, "Title"));
            if (raw is null) continue;
            if (raw.Equals("Completed - Documentation Pending", StringComparison.OrdinalIgnoreCase))
            {
                report.Converted("Option 'Completed - Documentation Pending' dropped");
                continue;
            }
            var value = ValueNormalizer.IsNotApplicable(raw) ? ValueNormalizer.NotApplicable : raw;
            if (value != raw) report.Converted("Option N/A → Not Applicable");
            var active = ParseBool(csv.Get(row, "Is Active"));
            var order = int.TryParse(csv.Get(row, "Sort Order"), out var o) ? o * 10 : 9990;

            var set = sets.FirstOrDefault(s => s.Key.Equals(setKey, StringComparison.OrdinalIgnoreCase));
            if (set is null)
            {
                set = new OptionSet { Key = setKey, Label = setKey };
                db.OptionSets.Add(set);
                sets.Add(set);
            }
            var existing = set.Values.FirstOrDefault(v => ValueNormalizer.ListKey(v.Value) == ValueNormalizer.ListKey(value));
            if (existing is null)
            {
                set.Values.Add(new OptionValue { Value = value, IsActive = active, SortOrder = order });
                report.Count("Options added");
            }
            else if (active)
            {
                // A value listed twice (active and inactive copy) ends up active, with the active copy's order.
                existing.IsActive = true;
                existing.SortOrder = order;
            }
        }
        options = sets.ToDictionary(s => s.Key, s => s.Values.Select(v => v.Value).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private async Task MergeUsersAsync(CsvTable csv, CancellationToken ct)
    {
        var adminRole = await db.Roles.FirstAsync(r => r.Name == RoleNames.Admin, ct);
        var users = await db.Users.ToListAsync(ct);
        foreach (var row in csv.Rows)
        {
            var email = AppUser.NormalizeEmail(csv.Get(row, "Email"));
            if (email.Length == 0 || !ParseBool(csv.Get(row, "Is Active"))) continue;
            var name = ValueNormalizer.CleanText(csv.Get(row, "Title")) ?? email;
            peopleEmails[name] = email;
            if (users.Any(u => u.Email == email)) continue;
            var user = new AppUser
            {
                Email = email, DisplayName = name, RoleId = adminRole.Id, CreatedAtUtc = DateTime.UtcNow, CreatedBy = "migration"
            };
            db.Users.Add(user);
            users.Add(user);
            report.Count("Users added (Admin)");
        }
        foreach (var u in users) peopleEmails.TryAdd(u.DisplayName, u.Email);
    }

    private async Task<Dictionary<string, Unit>> EnsureUnitsAsync(CsvTable mappings, CsvTable equipments, CancellationToken ct)
    {
        var plants = await db.Plants.Include(p => p.Units).ToListAsync(ct);
        var pairs = mappings.Rows.Select(r => (Plant: mappings.Get(r, "Plant").Trim(), Unit: mappings.Get(r, "Unit").Trim()))
            .Concat(equipments.Rows.Select(r => (Plant: equipments.Get(r, "Plant").Trim(), Unit: equipments.Get(r, "Unit").Trim())))
            .Where(p => p.Plant.Length > 0 && p.Unit.Length > 0)
            .Distinct();

        foreach (var (plantName, unitName) in pairs)
        {
            var plant = plants.FirstOrDefault(p => p.Name.Equals(plantName, StringComparison.OrdinalIgnoreCase));
            if (plant is null)
            {
                plant = new Plant { Name = plantName };
                db.Plants.Add(plant);
                plants.Add(plant);
                report.Flag("New plant", null, "Plant", plantName, "Plant not in the catalog: created without Simpro id.");
            }
            if (plants.SelectMany(p => p.Units).Any(u => u.Name.Equals(unitName, StringComparison.OrdinalIgnoreCase))) continue;
            var number = int.TryParse(DigitsRegex().Match(unitName).Value, out var n) ? n : 0;
            plant.Units.Add(new Unit { Name = unitName, Number = number });
            report.Flag("New unit", null, "Unit", unitName, "Unit not in the catalog: created without Simpro id.");
        }
        return plants.SelectMany(p => p.Units).ToDictionary(u => u.Name, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, Discipline>> EnsureDisciplinesAsync(CsvTable equipments, CancellationToken ct)
    {
        var disciplines = await db.Disciplines.ToListAsync(ct);
        foreach (var name in equipments.Rows.Select(r => DisciplineName(equipments.Get(r, "Discipline"))).Distinct())
        {
            if (disciplines.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var d = new Discipline { Name = name, AssetType = AssetType.Equipment, SyncToSimpro = true, SortOrder = 500 };
            db.Disciplines.Add(d);
            disciplines.Add(d);
            report.Flag("New discipline", null, "Discipline", name, "Discipline not in the catalog: created as Equipment.");
        }

        // Prefixes from the existing Equipment IDs (e.g. RT-EMP-541-043 → Rotating = RT), when not set yet.
        var prefixes = equipments.Rows
            .Select(r => (Discipline: DisciplineName(equipments.Get(r, "Discipline")), Match: EquipmentIdRegex().Match(equipments.Get(r, "Equipment ID").Trim())))
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Discipline, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Match.Groups[1].Value).OrderByDescending(x => x.Count()).First().Key,
                StringComparer.OrdinalIgnoreCase);
        foreach (var d in disciplines.Where(d => d.Prefix is null && prefixes.ContainsKey(d.Name)))
        {
            d.Prefix = prefixes[d.Name];
            report.Converted($"Prefix {d.Prefix} set for discipline {d.Name}");
        }
        return disciplines.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ equipment

    private Dictionary<Guid, Asset> LoadEquipments(CsvTable csv, Dictionary<string, Unit> units, Dictionary<string, Discipline> disciplines)
    {
        var assets = new Dictionary<Guid, Asset>();
        var tagsSeen = new Dictionary<(int, string), string>();

        foreach (var row in csv.Rows)
        {
            var code = csv.Get(row, "Equipment ID").Trim();
            if (!Guid.TryParse(csv.Get(row, "EquipmentGUID"), out var id))
            {
                report.Flag("Skipped", code, "EquipmentGUID", csv.Get(row, "EquipmentGUID"), "Row without a valid GUID was not loaded.");
                continue;
            }
            if (assets.ContainsKey(id))
            {
                report.Flag("Skipped", code, "EquipmentGUID", id.ToString(), "Duplicate GUID: second row not loaded.");
                continue;
            }
            if (code.StartsWith("FM", StringComparison.OrdinalIgnoreCase))
            {
                report.Flag("Skipped", code, "Equipment ID", code, "Flange Management (FM) is excluded.");
                continue;
            }

            var unit = units[csv.Get(row, "Unit").Trim()];
            var discipline = disciplines[DisciplineName(csv.Get(row, "Discipline"))];
            var tag = ValueNormalizer.CleanText(csv.Get(row, "EquipmentTag"));
            if (tag is not null && !tag.Equals("Typical Tag", StringComparison.OrdinalIgnoreCase))
            {
                if (tagsSeen.TryGetValue((unit.Id, tag.ToLowerInvariant()), out var other))
                {
                    report.Flag("Duplicate tag", code, "Equipment Tag", tag, $"Same tag as {other} in {unit.Name}. Check whether it is the same equipment.");
                }
                else tagsSeen[(unit.Id, tag.ToLowerInvariant())] = code;
            }

            var asset = new Asset
            {
                Id = id,
                Type = discipline.AssetType,
                Code = code,
                Tag = tag,
                Description = Text(csv, row, "Description", code, 255),
                DisciplineId = discipline.Id,
                Discipline = discipline,
                UnitId = unit.Id,
                Unit = unit,
                Priority = Choice(csv, row, "Priority", OptionSetKeys.Priority, code),
                IsActive = !csv.Has("IsActive") || ParseBool(csv.Get(row, "IsActive")),
                SimproFlangeId = Guid.TryParse(csv.Get(row, "SimproFlangeID"), out var sf) ? sf : null,
                LegacySharePointId = int.TryParse(csv.Get(row, "ID"), out var spId) ? spId : null,
                MigrationNotes = ValueNormalizer.CleanText(csv.Get(row, "MigrationNotes")),
                Preservation = LoadPreservation(csv, row, code)
            };
            if (asset.SimproFlangeId is null)
            {
                report.Flag("Not in Simpro yet", code, null, asset.Tag, "Created in the app; it will be created in Simpro by the first sync.");
            }
            db.Assets.Add(asset);
            assets[id] = asset;
        }
        report.Count("Assets", assets.Count);
        return assets;
    }

    private AssetPreservation LoadPreservation(CsvTable csv, string[] row, string code) => new()
    {
        CurrentGate = ParseCurrentGate(csv.Get(row, "Current Gate"), code),
        AssetAvailability = Choice(csv, row, "AssetAvailability", OptionSetKeys.AssetAvailability, code),
        CurrentActivity = Choice(csv, row, "CurrentActivity", OptionSetKeys.CurrentActivity, code),
        PreservationStatusAdnoc = Choice(csv, row, "PreservationStatusADNOC", OptionSetKeys.PreservationStatus, code),
        N2BlanketStatus = Choice(csv, row, "N2BlanketStatus", OptionSetKeys.N2BlanketStatus, code),
        ThirdPartyInspectionStatus = Choice(csv, row, "ThirdPartyInspectionStatus", OptionSetKeys.ThirdPartyInspection, code),
        EmWalkdownStatus = Choice(csv, row, "EM Walkdown Status", OptionSetKeys.EmWalkdown, code),
        PreservedByEu = Choice(csv, row, "Preserved by EU", OptionSetKeys.PreservedByEu, code),
        ActionBy = Choice(csv, row, "Action By", OptionSetKeys.ActionBy, code),
        OemName = Text(csv, row, "OEM Name", code, AssetPreservation.ShortText),
        G1BulkLiquidDrained = Choice(csv, row, "G1BulkLiquidDrained", OptionSetKeys.G1BulkLiquidDrained, code),
        G5PositiveMechIsolation = Text(csv, row, "G5PositiveMechIsolation", code, AssetPreservation.ShortText),
        G6CleaningMethod = Text(csv, row, "G6CleaningMethod", code, AssetPreservation.ShortText),
        PreservationRemarks = Text(csv, row, "Preservation Remarks", code, AssetPreservation.LongText),
        RemarksAdnoc = Text(csv, row, "RemarksADNOC", code, AssetPreservation.LongText),
        EmComments = Text(csv, row, "EMComments", code, AssetPreservation.LongText),
        CurrentStatusAdnoc = Text(csv, row, "CurrentStatusADNOC", code, AssetPreservation.LongText),
        Response = Text(csv, row, "Electrical5", code, AssetPreservation.LongText),
        G1Status = Gate(csv, row, 1, code), G2Status = Gate(csv, row, 2, code), G3Status = Gate(csv, row, 3, code), G4Status = Gate(csv, row, 4, code),
        G5Status = Gate(csv, row, 5, code), G6Status = Gate(csv, row, 6, code), G7Status = Gate(csv, row, 7, code), G8Status = Gate(csv, row, 8, code),
        G1Comments = Text(csv, row, "G1Comments", code, AssetPreservation.LongText),
        G2Comments = Text(csv, row, "G2Comments", code, AssetPreservation.LongText),
        G3Comments = Text(csv, row, "G3Comments", code, AssetPreservation.LongText),
        G4Comments = Text(csv, row, "G4Comments", code, AssetPreservation.LongText),
        G5Comments = Text(csv, row, "G5Comments", code, AssetPreservation.LongText),
        G6Comments = Text(csv, row, "G6Comments", code, AssetPreservation.LongText),
        G7Comments = Text(csv, row, "G7Comments", code, AssetPreservation.LongText),
        G8Comments = Text(csv, row, "G8Comments", code, AssetPreservation.LongText),
        PressureBarg = Number(csv, row, "PressureBarg", code),
        G2DewPointC = Number(csv, row, "G2DewPointC", code),
        G3O2Pct = Number(csv, row, "G3O2Pct", code),
        G4N2BlanketBarg = Number(csv, row, "G4N2BlanketBarg", code),
        G6H2S = Number(csv, row, "G6H2S", code),
        G6LelPct = Number(csv, row, "G6LELPct", code),
        LastFieldActionDate = DateOnlyOf(csv.Get(row, "LastFieldActionDate"), code, "LastFieldActionDate"),
        PreservationDate = DateOnlyOf(csv.Get(row, "PreservationDate"), code, "PreservationDate"),
        N2BlanketedDate = DateOnlyOf(csv.Get(row, "N2BlanketedDate"), code, "N2BlanketedDate"),
        LastReadingAtUtc = LondonToUtc(csv.Get(row, "LastReadingDate"), code, "LastReadingDate"),
    };

    private CurrentGate ParseCurrentGate(string raw, string code)
    {
        var v = raw.Trim();
        if (v.Length == 0 || v.Equals("New", StringComparison.OrdinalIgnoreCase)) return CurrentGate.New;
        if (v.Equals("Nuevo", StringComparison.OrdinalIgnoreCase))
        {
            report.Converted("Current Gate 'Nuevo' → New");
            return CurrentGate.New;
        }
        if (ValueNormalizer.ListKey(v) == "removedfromsite") return CurrentGate.RemovedFromSite;
        if (v.Length == 2 && v[0] is 'G' or 'g' && v[1] is >= '1' and <= '8') return (CurrentGate)(v[1] - '0');
        report.Flag("Unknown value", code, "Current Gate", v, "Not a known gate: loaded as New.");
        return CurrentGate.New;
    }

    private string? Gate(CsvTable csv, string[] row, int gate, string code) =>
        Choice(csv, row, $"G{gate}Status", OptionSetKeys.GateStatus, code, isGate: true);

    private string? Choice(CsvTable csv, string[] row, string column, string optionSet, string code, bool isGate = false)
    {
        var raw = ValueNormalizer.CleanText(csv.Get(row, column));
        if (raw is null) return null;
        var (value, matched) = ValueNormalizer.Canonical(raw, options.GetValueOrDefault(optionSet) ?? [], isGate);
        if (value != raw)
        {
            report.Converted(ValueNormalizer.IsNotApplicable(raw) ? "N/A → Not Applicable" : $"{raw} → {value}");
        }
        if (!matched) report.Flag("Value not in list", code, column, value, $"Not an option of {optionSet}: kept as is.");
        return value;
    }

    private string? Text(CsvTable csv, string[] row, string column, string code, int max)
    {
        var value = ValueNormalizer.CleanText(csv.Get(row, column));
        if (value is not null && value.Length > max)
        {
            report.Flag("Too long", code, column, value[..60] + "…", $"{value.Length} characters, limit {max}: cut.");
            value = value[..max];
        }
        return value;
    }

    private decimal? Number(CsvTable csv, string[] row, string column, string code)
    {
        var raw = csv.Get(row, column).Trim();
        if (raw.Length == 0) return null;
        if (ValueNormalizer.TryParseDecimal(raw, out var d)) return d;
        report.Flag("Not a number", code, column, raw, "Not loaded.");
        return null;
    }

    // ------------------------------------------------------------------ related lists

    private void LoadGateDocumentation(CsvTable? csv, Dictionary<Guid, Asset> assets)
    {
        var received = new HashSet<(Guid, int)>();
        if (csv is not null)
        {
            foreach (var row in csv.Rows)
            {
                if (!Guid.TryParse(csv.Get(row, "EquipmentGUID"), out var id)) continue;
                for (var g = 1; g <= 8; g++)
                {
                    if (ParseBool(csv.Get(row, $"G{g}"))) received.Add((id, g));
                }
            }
        }

        // A document row is created for every Completed gate (Received when SharePoint said so, otherwise Pending)
        // and for any gate marked received even if not completed.
        var count = 0;
        foreach (var asset in assets.Values)
        {
            for (var g = 1; g <= 8; g++)
            {
                var completed = AssetPreservation.IsCompleted(asset.Preservation!.GetGateStatus(g));
                var isReceived = received.Contains((asset.Id, g));
                if (!completed && !isReceived) continue;
                db.GateDocuments.Add(new GateDocumentation
                {
                    AssetId = asset.Id, Gate = g, Status = isReceived ? DocumentationStatus.Received : DocumentationStatus.Pending
                });
                count++;
            }
        }
        report.Count("Gate documentation rows", count);
    }

    private async Task LoadReadingsAsync(CsvTable csv, Dictionary<Guid, Asset> assets, CancellationToken ct)
    {
        var types = await db.MeasurementTypes.ToListAsync(ct);
        var count = 0;
        foreach (var row in csv.Rows)
        {
            var guidText = csv.Get(row, "Equipment GUID");
            if (!Guid.TryParse(guidText, out var id) || !assets.TryGetValue(id, out var asset))
            {
                report.Flag("Orphan reading", csv.Get(row, "Equipment ID"), "Equipment GUID", guidText, "Reading of an unknown equipment: not loaded.");
                continue;
            }
            var label = csv.Get(row, "Measurement").Trim();
            var type = types.FirstOrDefault(t => ValueNormalizer.ListKey(t.Label) == ValueNormalizer.ListKey(label));
            if (type is null)
            {
                report.Flag("Unknown measurement", asset.Code, "Measurement", label, "Not loaded.");
                continue;
            }
            if (!ValueNormalizer.TryParseDecimal(csv.Get(row, "Value"), out var value))
            {
                report.Flag("Not a number", asset.Code, label, csv.Get(row, "Value"), "Reading not loaded.");
                continue;
            }
            var measured = LondonToUtc(csv.Get(row, "Measured At"), asset.Code, "Measured At");
            if (measured is null) continue;
            var recordedBy = ValueNormalizer.CleanText(csv.Get(row, "Recorded By"))
                             ?? ValueNormalizer.CleanText(csv.Get(row, "Recorded By Name")) ?? "unknown";
            db.Readings.Add(new Reading
            {
                AssetId = asset.Id,
                MeasurementTypeId = type.Id,
                Value = value,
                MeasuredAtUtc = measured.Value,
                RecordedAtUtc = LondonToUtc(csv.Get(row, "Recorded At"), asset.Code, "Recorded At") ?? measured.Value,
                RecordedBy = EmailOf(recordedBy),
                Source = SourceOf(csv.Get(row, "Source")),
                Phase = MonitoringPhase.During,
                Note = ValueNormalizer.CleanText(csv.Get(row, "Note"))
            });
            count++;
        }
        report.Count("Readings", count);
    }

    private void LoadStatusHistory(CsvTable csv, Dictionary<Guid, Asset> assets)
    {
        var count = 0;
        foreach (var row in csv.Rows)
        {
            if (!Guid.TryParse(csv.Get(row, "Equipment GUID"), out var id) || !assets.ContainsKey(id))
            {
                report.Flag("Orphan history", csv.Get(row, "Equipment ID"), "Equipment GUID", csv.Get(row, "Equipment GUID"), "History row of an unknown equipment: not loaded.");
                continue;
            }
            var label = csv.Get(row, "Field").Trim();
            var field = HistoryField(label);
            var changedAt = LondonToUtc(csv.Get(row, "Changed At"), assets[id].Code, "Changed At");
            if (changedAt is null) continue;
            var who = ValueNormalizer.CleanText(csv.Get(row, "Changed By")) ?? ValueNormalizer.CleanText(csv.Get(row, "Changed By Name")) ?? "unknown";
            db.ChangeLogs.Add(new ChangeLog
            {
                EntityType = nameof(AssetPreservation),
                EntityKey = id.ToString(),
                AssetId = id,
                Field = field,
                OldValue = HistoryValue(csv.Get(row, "Old Value")),
                NewValue = HistoryValue(csv.Get(row, "New Value")),
                ChangedAtUtc = changedAt.Value,
                ChangedBy = EmailOf(who),
                Source = SourceOf(csv.Get(row, "Source"))
            });
            count++;
        }
        report.Count("History rows", count);
    }

    private async Task LoadExcelHistoryAsync(CsvFiles files, Dictionary<string, Unit> units, Dictionary<Guid, Asset> assets, CancellationToken ct)
    {
        var sessions = files.TryGet("ExcelSessions");
        if (sessions is not null)
        {
            foreach (var row in sessions.Rows)
            {
                if (!Guid.TryParse(sessions.Get(row, "Title"), out var sid) || !units.TryGetValue(sessions.Get(row, "Unit").Trim(), out var unit)) continue;
                db.ExcelSessions.Add(new ExcelSession
                {
                    Id = sid,
                    UnitId = unit.Id,
                    AssetType = AssetType.Equipment,
                    ExportedAtUtc = LondonToUtc(sessions.Get(row, "ExportedAt"), null, "ExportedAt") ?? DateTime.UtcNow,
                    ExportedBy = sessions.Get(row, "ExportedBy").Trim(),
                    Status = Enum.TryParse<ExcelSessionStatus>(sessions.Get(row, "Status"), true, out var st) ? st : ExcelSessionStatus.Processed,
                    ProcessedAtUtc = LondonToUtc(sessions.Get(row, "ProcessedAt"), null, "ProcessedAt"),
                    FormatVersion = 2
                });
                report.Count("Excel sessions");
            }
        }

        var log = files.TryGet("ImportLog");
        if (log is null) return;
        // SharePoint item ids are not in the export; list rows are numbered 1..n in creation order.
        var runs = new Dictionary<string, ImportRun>();
        for (var i = 0; i < log.Rows.Count; i++)
        {
            var row = log.Rows[i];
            var result = log.Get(row, "Result").Trim();
            var run = new ImportRun
            {
                Kind = ImportKind.Update,
                FileName = Truncate(log.Get(row, "Title").Trim(), 260),
                UnitId = units.TryGetValue(log.Get(row, "Unit").Trim(), out var u) ? u.Id : null,
                SessionId = Guid.TryParse(log.Get(row, "SessionId"), out var s) ? s : null,
                UploadedBy = log.Get(row, "UploadedBy").Trim(),
                UploadedAtUtc = LondonToUtc(log.Get(row, "UploadedAt"), null, "UploadedAt") ?? DateTime.UtcNow,
                Status = result.StartsWith("Applied with", StringComparison.OrdinalIgnoreCase) ? ImportStatus.AppliedWithIssues
                    : result.StartsWith("Applied", StringComparison.OrdinalIgnoreCase) ? ImportStatus.Applied
                    : result.StartsWith("Rejected", StringComparison.OrdinalIgnoreCase) ? ImportStatus.Rejected
                    : ImportStatus.Failed,
                AppliedCount = int.TryParse(log.Get(row, "AppliedCount"), out var a) ? a : 0,
                IssueCount = int.TryParse(log.Get(row, "NotAppliedCount"), out var n) ? n : 0,
                WarningCount = int.TryParse(log.Get(row, "WarningCount"), out var w) ? w : 0,
                Message = ValueNormalizer.CleanText(string.Join(" ", log.Get(row, "Message"), result.Contains("Test", StringComparison.OrdinalIgnoreCase) ? "(test run)" : ""))
            };
            db.ImportRuns.Add(run);
            runs[(i + 1).ToString(CultureInfo.InvariantCulture)] = run;
        }
        report.Count("Import runs", runs.Count);

        var openWarnings = new HashSet<(string, string)>();
        var warnings = files.TryGet("ImportWarnings");
        if (warnings is not null)
        {
            foreach (var row in warnings.Rows.Where(r => warnings.Get(r, "Status").Trim().Equals("Open", StringComparison.OrdinalIgnoreCase)))
            {
                openWarnings.Add((warnings.Get(row, "EquipmentGUID").Trim().ToLowerInvariant(), warnings.Get(row, "Gate").Trim()));
            }
        }

        var changes = files.TryGet("ImportChanges");
        if (changes is null) return;
        var count = 0;
        foreach (var row in changes.Rows)
        {
            if (!runs.TryGetValue(changes.Get(row, "ImportId").Trim(), out var run)) continue;
            var outcome = OutcomeOf(changes.Get(row, "Outcome"));
            var guid = changes.Get(row, "EquipmentGUID").Trim();
            var isIssue = outcome is ImportOutcome.Conflict or ImportOutcome.Invalid or ImportOutcome.Rejected or ImportOutcome.Warning;
            var field = changes.Get(row, "Field").Trim();
            var gate = field.Length >= 2 && field[0] == 'G' && char.IsDigit(field[1]) ? field[..2] : "";
            run.Changes.Add(new ImportChange
            {
                AssetId = Guid.TryParse(guid, out var g) && assets.ContainsKey(g) ? g : null,
                AssetCode = Truncate(changes.Get(row, "EquipmentID").Trim(), 40),
                Field = Truncate(field, 128),
                BaseValue = ValueNormalizer.CleanText(changes.Get(row, "BaseValue")),
                ExcelValue = ValueNormalizer.CleanText(changes.Get(row, "ExcelValue")),
                CurrentValue = ValueNormalizer.CleanText(changes.Get(row, "CurrentValue")),
                Outcome = outcome,
                Reason = ValueNormalizer.CleanText(changes.Get(row, "Reason")),
                Resolution = !isIssue ? IssueResolution.None
                    : outcome == ImportOutcome.Warning && !openWarnings.Contains((guid.ToLowerInvariant(), gate)) ? IssueResolution.Resolved
                    : IssueResolution.Open
            });
            count++;
        }
        report.Count("Import changes", count);
        await Task.CompletedTask;
    }

    private async Task SetSequencesAsync(CancellationToken ct)
    {
        var codes = await db.Assets.Select(a => a.Code).ToListAsync(ct);
        foreach (var g in codes.Select(c => EquipmentIdRegex().Match(c)).Where(m => m.Success)
                     .GroupBy(m => (Prefix: m.Groups[1].Value, Unit: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))))
        {
            db.EquipmentIdSequences.Add(new EquipmentIdSequence
            {
                Prefix = g.Key.Prefix, UnitNumber = g.Key.Unit,
                LastNumber = g.Max(m => int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
            });
        }
    }

    // ------------------------------------------------------------------ verification

    private async Task VerifyAsync(CsvTable equipments, CancellationToken ct)
    {
        var dbUnits = await db.Assets.GroupBy(a => a.Unit.Name).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        foreach (var g in equipments.Rows.GroupBy(r => equipments.Get(r, "Unit").Trim()).OrderBy(g => g.Key))
        {
            var loadable = g.Count(r => Guid.TryParse(equipments.Get(r, "EquipmentGUID"), out _)
                                        && !equipments.Get(r, "Equipment ID").Trim().StartsWith("FM", StringComparison.OrdinalIgnoreCase));
            report.Units.Add(new MigrationReport.UnitCount(g.Key, loadable, dbUnits.FirstOrDefault(u => u.Key == g.Key)?.Count ?? 0));
        }

        var stored = await db.Preservations.AsNoTracking().Include(p => p.Asset).ToListAsync(ct);
        var checks = new (string Csv, Func<AssetPreservation, object?> Get)[]
        {
            ("Description", p => p.Asset.Description), ("Priority", p => p.Asset.Priority),
            ("AssetAvailability", p => p.AssetAvailability), ("CurrentActivity", p => p.CurrentActivity),
            ("PreservationStatusADNOC", p => p.PreservationStatusAdnoc), ("N2BlanketStatus", p => p.N2BlanketStatus),
            ("ThirdPartyInspectionStatus", p => p.ThirdPartyInspectionStatus), ("EM Walkdown Status", p => p.EmWalkdownStatus),
            ("Preserved by EU", p => p.PreservedByEu), ("Action By", p => p.ActionBy), ("OEM Name", p => p.OemName),
            ("Preservation Remarks", p => p.PreservationRemarks), ("RemarksADNOC", p => p.RemarksAdnoc), ("EMComments", p => p.EmComments),
            ("CurrentStatusADNOC", p => p.CurrentStatusAdnoc), ("Electrical5", p => p.Response),
            ("G1BulkLiquidDrained", p => p.G1BulkLiquidDrained),
            ("G1Status", p => p.G1Status), ("G2Status", p => p.G2Status), ("G3Status", p => p.G3Status), ("G4Status", p => p.G4Status),
            ("G5Status", p => p.G5Status), ("G6Status", p => p.G6Status), ("G7Status", p => p.G7Status), ("G8Status", p => p.G8Status),
            ("G1Comments", p => p.G1Comments), ("G2Comments", p => p.G2Comments), ("G3Comments", p => p.G3Comments), ("G4Comments", p => p.G4Comments),
            ("PressureBarg", p => p.PressureBarg), ("G2DewPointC", p => p.G2DewPointC), ("G3O2Pct", p => p.G3O2Pct),
            ("G4N2BlanketBarg", p => p.G4N2BlanketBarg), ("G6H2S", p => p.G6H2S), ("G6LELPct", p => p.G6LelPct),
            ("LastFieldActionDate", p => p.LastFieldActionDate), ("N2BlanketedDate", p => p.N2BlanketedDate), ("PreservationDate", p => p.PreservationDate),
        };
        var codes = stored.Select(p => p.Asset.Code).ToHashSet();
        foreach (var (column, get) in checks)
        {
            if (!equipments.Has(column)) continue;
            var inCsv = equipments.Rows.Count(r => codes.Contains(equipments.Get(r, "Equipment ID").Trim()) && ValueNormalizer.CleanText(equipments.Get(r, column)) is not null);
            var inDb = stored.Count(p => get(p) is { } v && v.ToString() is { Length: > 0 });
            report.Fields.Add(new MigrationReport.FieldCount(column, inCsv, inDb));
        }
    }

    // ------------------------------------------------------------------ helpers

    private DateTime? LondonToUtc(string raw, string? code, string column)
    {
        var v = raw.Trim();
        if (v.Length == 0) return null;
        if (!DateTime.TryParseExact(v, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
        {
            report.Flag("Not a date", code, column, v, "Not loaded.");
            return null;
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), SiteClock.London);
    }

    private DateOnly? DateOnlyOf(string raw, string code, string column)
    {
        var v = raw.Trim();
        if (v.Length == 0) return null;
        if (DateTime.TryParseExact(v, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d))
        {
            return DateOnly.FromDateTime(d);
        }
        report.Flag("Not a date", code, column, v, "Not loaded.");
        return null;
    }

    private string EmailOf(string who) => who.Contains('@') ? AppUser.NormalizeEmail(who) : peopleEmails.GetValueOrDefault(who) ?? who;

    private static string? HistoryValue(string raw)
    {
        var v = ValueNormalizer.CleanText(raw);
        if (v is null) return null;
        if (ValueNormalizer.IsNotApplicable(v)) return ValueNormalizer.NotApplicable;
        return v.Equals("Nuevo", StringComparison.OrdinalIgnoreCase) ? "New" : v;
    }

    private static string HistoryField(string label) => label switch
    {
        "Current Gate" => nameof(AssetPreservation.CurrentGate),
        "Preservation Status (ADNOC)" => nameof(AssetPreservation.PreservationStatusAdnoc),
        "N2 Blanket Status" => nameof(AssetPreservation.N2BlanketStatus),
        _ when label.Length == 9 && label.StartsWith('G') && label.EndsWith(" Status", StringComparison.Ordinal) => $"G{label[1]}Status",
        _ => label
    };

    private static ChangeSource SourceOf(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "APP" => ChangeSource.App,
        "EXCEL" => ChangeSource.Excel,
        "BULK" => ChangeSource.Bulk,
        _ => ChangeSource.HistoryImport
    };

    private static ImportOutcome OutcomeOf(string raw) => ValueNormalizer.ListKey(raw) switch
    {
        "applied" => ImportOutcome.Applied,
        "derived" => ImportOutcome.Derived,
        "nochange" => ImportOutcome.NoChange,
        "invalid" => ImportOutcome.Invalid,
        "conflict" => ImportOutcome.Conflict,
        "rejected" => ImportOutcome.Rejected,
        _ => ImportOutcome.Warning
    };

    private static string DisciplineName(string raw) => ValueNormalizer.CleanText(raw) ?? "SU";

    private static bool ParseBool(string raw) =>
        raw.Trim().ToLowerInvariant() is "true" or "yes" or "1";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^([A-Z]{2})-EMP-(\d{3})-(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EquipmentIdRegex();

    /// <summary>The CSV files of one export folder, found by list name whatever prefix the file has.</summary>
    private sealed class CsvFiles(string folder)
    {
        private readonly string[] paths = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.csv")
            : throw new DirectoryNotFoundException(folder);

        public CsvTable Get(string list) => TryGet(list) ?? throw new FileNotFoundException($"No CSV for the list '{list}' in {folder}.");

        public CsvTable? TryGet(string list)
        {
            var key = list.ToLowerInvariant();
            var path = paths.FirstOrDefault(p => Normalize(Path.GetFileNameWithoutExtension(p)).Contains(key, StringComparison.Ordinal));
            return path is null ? null : CsvTable.Load(path);
        }

        private static string Normalize(string name) => new(name.ToLowerInvariant().Where(char.IsLetter).ToArray());
    }
}
