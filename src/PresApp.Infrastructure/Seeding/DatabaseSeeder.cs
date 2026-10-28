using PresApp.Domain.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PresApp.Domain.Catalog;
using PresApp.Domain.Groups;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Infrastructure.Seeding;

/// <summary>
/// Adds missing catalog rows at startup. Never changes or re-enables rows an admin already edited,
/// so it is safe to run on every start. One exception: the built-in roles always keep "assets.view"
/// (reading the registers and the Unit overview), because without it a role sees nothing at all.
/// </summary>
public sealed class DatabaseSeeder(PresAppDbContext db, ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(string? bootstrapAdminEmail, CancellationToken ct = default)
    {
        await SeedRolesAsync(ct);
        await SeedOptionSetsAsync(ct);
        await SeedDisciplinesAsync(ct);
        await SeedLineCodesAsync(ct);
        await SeedPlantsAsync(ct);
        await SeedMeasurementsAsync(ct);
        await SeedGroupTypesAsync(ct);
        await SeedSettingsAsync(ct);
        await db.SaveChangesAsync(ct);
        await SeedPipingFieldsAsync(ct);
        await SeedBootstrapAdminAsync(bootstrapAdminEmail, ct);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        await MergeSpanishRolesAsync(ct);
        var existing = await db.Roles.Select(r => r.Name).ToListAsync(ct);
        foreach (var (name, description, permissions) in SeedData.Roles.Where(r => !existing.Contains(r.Role)))
        {
            db.Roles.Add(new Role
            {
                Name = name,
                Description = description,
                IsSystem = true,
                Permissions = permissions.Select(p => new RolePermission { Permission = p }).ToList()
            });
        }

        // Every built-in role can at least read: registers, the Unit overview (piping cards included), history and the PDF.
        var builtIn = SeedData.Roles.Select(r => r.Role).ToList();
        foreach (var role in await db.Roles.Include(r => r.Permissions).Where(r => builtIn.Contains(r.Name)).ToListAsync(ct))
        {
            if (role.Permissions.All(x => x.Permission != Permissions.AssetsView))
                role.Permissions.Add(new RolePermission { Permission = Permissions.AssetsView });
        }

        // Admin always holds every permission, including ones added in later versions.
        var admin = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == RoleNames.Admin, ct);
        if (admin is not null)
        {
            foreach (var p in Permissions.All.Where(p => admin.Permissions.All(x => x.Permission != p.Key)))
            {
                admin.Permissions.Add(new RolePermission { Permission = p.Key });
            }
        }
    }

    /// <summary>
    /// Databases created before the translation hold the Spanish system roles. If the English role does not
    /// exist yet the Spanish one is renamed (keeping its permissions); otherwise its users move to the
    /// English role and the Spanish one is removed.
    /// </summary>
    private async Task MergeSpanishRolesAsync(CancellationToken ct)
    {
        var spanish = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Consulta"] = RoleNames.Viewer,
            ["Técnico de campo"] = RoleNames.FieldTechnician,
            ["Tecnico de campo"] = RoleNames.FieldTechnician,
            ["Ingeniero"] = RoleNames.Engineer,
            ["Administrador"] = RoleNames.Admin,
        };
        var roles = await db.Roles.ToListAsync(ct);
        foreach (var old in roles.Where(r => spanish.ContainsKey(r.Name)).ToList())
        {
            var target = roles.FirstOrDefault(r => string.Equals(r.Name, spanish[old.Name], StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                old.Name = spanish[old.Name];
                old.IsSystem = true;
                await db.SaveChangesAsync(ct);
                continue;
            }
            await db.Users.Where(u => u.RoleId == old.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.RoleId, target.Id), ct);
            await db.Set<RolePermission>().Where(p => p.RoleId == old.Id).ExecuteDeleteAsync(ct);
            await db.Roles.Where(r => r.Id == old.Id).ExecuteDeleteAsync(ct);
            logger.LogInformation("Merged role {Old} into {New}", old.Name, target.Name);
        }
    }

    private async Task SeedOptionSetsAsync(CancellationToken ct)
    {
        var sets = await db.OptionSets.Include(s => s.Values).ToListAsync(ct);
        foreach (var (key, label, active, inactive) in SeedData.OptionSets)
        {
            var set = sets.FirstOrDefault(s => s.Key == key);
            if (set is null)
            {
                set = new OptionSet { Key = key, Label = label };
                db.OptionSets.Add(set);
            }

            var order = 10;
            foreach (var value in active)
            {
                AddValue(set, value, order, isActive: true);
                order += 10;
            }
            foreach (var value in inactive)
            {
                AddValue(set, value, order, isActive: false);
                order += 10;
            }
        }
    }

    private static void AddValue(OptionSet set, string value, int order, bool isActive)
    {
        if (set.Values.Any(v => string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase))) return;
        set.Values.Add(new OptionValue { Value = value, SortOrder = order, IsActive = isActive });
    }

    private async Task SeedDisciplinesAsync(CancellationToken ct)
    {
        var existing = await db.Disciplines.Select(d => d.Name).ToListAsync(ct);
        // Pipelines need an Equipment ID prefix (PL) where an older database has none.
        var piping = await db.Disciplines.FirstOrDefaultAsync(d => d.AssetType == AssetType.Pipeline && d.Prefix == null, ct);
        if (piping is not null) piping.Prefix = "PL";
        foreach (var d in SeedData.Disciplines.Where(d => !existing.Contains(d.Name)))
        {
            db.Disciplines.Add(new Discipline
            {
                Name = d.Name, AssetType = d.Type, Prefix = d.Prefix, SyncToSimpro = d.Simpro, SortOrder = d.Sort
            });
        }
    }

    private async Task SeedLineCodesAsync(CancellationToken ct)
    {
        var existing = (await db.LineCodes.Select(c => new { c.Kind, c.Code }).ToListAsync(ct)).Select(c => (c.Kind, c.Code)).ToHashSet();
        foreach (var c in SeedData.LineCodes.Where(c => !existing.Contains((c.Kind, c.Code))))
            db.LineCodes.Add(new LineCode { Kind = c.Kind, Code = c.Code, Description = c.Description });
    }

    private async Task SeedPlantsAsync(CancellationToken ct)
    {
        var plants = await db.Plants.Include(p => p.Units).ToListAsync(ct);
        var unitNames = await db.Units.Select(u => u.Name).ToListAsync(ct);
        foreach (var (plantName, plantId, units) in SeedData.Plants)
        {
            var plant = plants.FirstOrDefault(p => p.Name == plantName);
            if (plant is null)
            {
                plant = new Plant { Name = plantName, SimproLocationId = plantId };
                db.Plants.Add(plant);
            }
            foreach (var (unitName, number, unitId) in units.Where(u => !unitNames.Contains(u.Unit)))
            {
                plant.Units.Add(new Unit { Name = unitName, Number = number, SimproLocationId = unitId });
            }
        }
    }

    private async Task SeedMeasurementsAsync(CancellationToken ct)
    {
        var existing = await db.MeasurementTypes.Select(m => m.Key).ToListAsync(ct);
        foreach (var m in SeedData.Measurements.Where(m => !existing.Contains(m.Key)))
        {
            db.MeasurementTypes.Add(new MeasurementType
            {
                Key = m.Key, Label = m.Label, UnitOfMeasure = m.UnitOfMeasure, Gate = m.Gate,
                EvidenceForGate = m.EvidenceForGate, MinValue = m.MinValue, MaxValue = m.MaxValue,
                AppliesTo = m.AppliesTo, SortOrder = m.SortOrder
            });
        }
    }

    private async Task SeedGroupTypesAsync(CancellationToken ct)
    {
        // An early version seeded Spanish names: their groups move to the English type and the Spanish type is removed.
        var spanish = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Paquete de trabajo"] = "Workpack", ["Paquete"] = "Workpack", ["Circuito"] = "Circuit", ["Sistema"] = "System"
        };
        var types = await db.GroupTypes.ToListAsync(ct);
        foreach (var name in SeedData.GroupTypes.Where(n => !types.Any(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))))
        {
            var t = new GroupType { Name = name };
            db.GroupTypes.Add(t);
            types.Add(t);
        }
        await db.SaveChangesAsync(ct);
        foreach (var old in types.Where(t => spanish.ContainsKey(t.Name)).ToList())
        {
            var target = types.First(t => t.Name == spanish[old.Name]);
            await db.Groups.Where(g => g.GroupTypeId == old.Id).ExecuteUpdateAsync(u => u.SetProperty(g => g.GroupTypeId, target.Id), ct);
            db.GroupTypes.Remove(old);
        }
    }

    /// <summary>
    /// Custom fields of the Pipeline Register with a fixed key (Inspection Done, Cleaning Required, Cleaned).
    /// A field an admin already created with the same name is adopted (its key is fixed); existing ones are never changed.
    /// </summary>
    private async Task SeedPipingFieldsAsync(CancellationToken ct)
    {
        var defs = await db.FieldDefinitions.Include(f => f.OptionSet).ThenInclude(o => o!.Values)
            .Where(f => f.AssetType == AssetType.Pipeline).ToListAsync(ct);
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        foreach (var (key, label, kind, options, sort) in SeedData.PipingFields)
        {
            var def = defs.FirstOrDefault(d => d.Key == key)
                      ?? defs.FirstOrDefault(d => Norm(d.Label) == Norm(label) && d.DataType == kind);
            if (def is not null)
            {
                if (def.Key != key && !defs.Any(d => d.Key == key)) def.Key = key;
                continue;
            }
            def = new FieldDefinition
            {
                AssetType = AssetType.Pipeline, Key = key, Label = label, DataType = kind, Section = "Inspection and cleaning",
                SortOrder = sort, ShowInRegister = false, IsActive = true
            };
            if (kind == FieldDataType.List)
            {
                def.OptionSet = new OptionSet { Key = $"CF.{AssetType.Pipeline}.{key}", Label = label };
                for (var i = 0; i < options.Length; i++) def.OptionSet.Values.Add(new OptionValue { Value = options[i], SortOrder = i, IsActive = true });
            }
            db.FieldDefinitions.Add(def);
            defs.Add(def);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        var existing = await db.Settings.Select(s => s.Key).ToListAsync(ct);
        foreach (var (key, value, description) in SeedData.Settings.Where(s => !existing.Contains(s.Key)))
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value, Description = description });
        }
    }

    /// <summary>Creates the first admin from configuration when no admin exists yet.</summary>
    private async Task SeedBootstrapAdminAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var adminRole = await db.Roles.FirstAsync(r => r.Name == RoleNames.Admin, ct);
        if (await db.Users.AnyAsync(u => u.RoleId == adminRole.Id && u.IsActive, ct)) return;

        var normalized = AppUser.NormalizeEmail(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
        {
            db.Users.Add(new AppUser
            {
                Email = normalized,
                DisplayName = normalized,
                RoleId = adminRole.Id,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "bootstrap"
            });
        }
        else
        {
            user.RoleId = adminRole.Id;
            user.IsActive = true;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Bootstrap admin {Email} is ready", normalized);
    }
}
