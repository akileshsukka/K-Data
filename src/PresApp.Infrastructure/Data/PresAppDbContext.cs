using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Common;
using PresApp.Domain.Groups;
using PresApp.Domain.Imports;
using PresApp.Domain.Monitoring;
using PresApp.Domain.Reporting;
using PresApp.Domain.Security;
using PresApp.Domain.Simpro;

namespace PresApp.Infrastructure.Data;

public class PresAppDbContext(DbContextOptions<PresAppDbContext> options) : DbContext(options)
{
    // Assets
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetPreservation> Preservations => Set<AssetPreservation>();
    public DbSet<GateDocumentation> GateDocuments => Set<GateDocumentation>();
    public DbSet<PipelineDetail> PipelineDetails => Set<PipelineDetail>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<AssetFieldValue> FieldValues => Set<AssetFieldValue>();
    public DbSet<EquipmentIdSequence> EquipmentIdSequences => Set<EquipmentIdSequence>();

    // Catalogs
    public DbSet<Plant> Plants => Set<Plant>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<OptionSet> OptionSets => Set<OptionSet>();
    public DbSet<OptionValue> OptionValues => Set<OptionValue>();
    public DbSet<Discipline> Disciplines => Set<Discipline>();
    public DbSet<LineCode> LineCodes => Set<LineCode>();
    public DbSet<PipeSpec> PipeSpecs => Set<PipeSpec>();
    public DbSet<MeasurementType> MeasurementTypes => Set<MeasurementType>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    // Groups and monitoring
    public DbSet<GroupType> GroupTypes => Set<GroupType>();
    public DbSet<AssetGroup> Groups => Set<AssetGroup>();
    public DbSet<AssetGroupMember> GroupMembers => Set<AssetGroupMember>();
    public DbSet<Reading> Readings => Set<Reading>();

    // Reporting
    public DbSet<DashboardCategory> DashboardCategories => Set<DashboardCategory>();
    public DbSet<CategoryKeyword> CategoryKeywords => Set<CategoryKeyword>();
    public DbSet<UnitReportConfig> UnitReportConfigs => Set<UnitReportConfig>();

    // Audit, imports, Simpro
    public DbSet<ChangeLog> ChangeLogs => Set<ChangeLog>();
    public DbSet<ExcelSession> ExcelSessions => Set<ExcelSession>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<ImportChange> ImportChanges => Set<ImportChange>();
    public DbSet<ImportFile> ImportFiles => Set<ImportFile>();
    public DbSet<ExcelTemplate> ExcelTemplates => Set<ExcelTemplate>();
    public DbSet<SimproSyncRun> SimproSyncRuns => Set<SimproSyncRun>();
    public DbSet<SimproSyncItem> SimproSyncItems => Set<SimproSyncItem>();
    public DbSet<SimproBeforeImage> SimproBeforeImages => Set<SimproBeforeImage>();
    public DbSet<SimproForcedAsset> SimproForcedAssets => Set<SimproForcedAsset>();

    // Security
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<LoginAudit> LoginAudits => Set<LoginAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PresAppDbContext).Assembly);

        var isSqlServer = Database.IsSqlServer();
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(AuditableEntity).IsAssignableFrom(entity.ClrType)) continue;
            var b = modelBuilder.Entity(entity.ClrType);
            b.Property(nameof(AuditableEntity.CreatedBy)).HasMaxLength(256);
            b.Property(nameof(AuditableEntity.UpdatedBy)).HasMaxLength(256);
            var rowVersion = b.Property(nameof(AuditableEntity.RowVersion));
            if (isSqlServer) rowVersion.IsRowVersion();
            else rowVersion.IsConcurrencyToken();
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Exact decimals everywhere (never float); text defaults to Unicode.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
        configurationBuilder.Properties<ChangeSource>().HaveConversion<string>().HaveMaxLength(20);
    }
}
