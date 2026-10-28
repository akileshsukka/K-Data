using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PresApp.Domain.Common;
using PresApp.Domain.Groups;
using PresApp.Domain.Imports;
using PresApp.Domain.Monitoring;
using PresApp.Domain.Reporting;
using PresApp.Domain.Security;
using PresApp.Domain.Simpro;

namespace PresApp.Infrastructure.Data.Configurations;

internal sealed class GroupTypeConfiguration : IEntityTypeConfiguration<GroupType>
{
    public void Configure(EntityTypeBuilder<GroupType> b)
    {
        b.ToTable("GroupType");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class AssetGroupConfiguration : IEntityTypeConfiguration<AssetGroup>
{
    public void Configure(EntityTypeBuilder<AssetGroup> b)
    {
        b.ToTable("AssetGroup");
        b.Property(x => x.Code).HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasOne(x => x.GroupType).WithMany().HasForeignKey(x => x.GroupTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AssetGroupMemberConfiguration : IEntityTypeConfiguration<AssetGroupMember>
{
    public void Configure(EntityTypeBuilder<AssetGroupMember> b)
    {
        b.ToTable("AssetGroupMember");
        b.HasKey(x => new { x.GroupId, x.AssetId });
        b.Property(x => x.AddedBy).HasMaxLength(256);
        b.HasIndex(x => x.AssetId);
        b.HasOne(x => x.Group).WithMany(x => x.Members).HasForeignKey(x => x.GroupId);
        b.HasOne(x => x.Asset).WithMany(x => x.GroupMemberships).HasForeignKey(x => x.AssetId);
    }
}

internal sealed class ReadingConfiguration : IEntityTypeConfiguration<Reading>
{
    public void Configure(EntityTypeBuilder<Reading> b)
    {
        b.ToTable("Reading");
        b.Property(x => x.RecordedBy).HasMaxLength(256);
        b.Property(x => x.Phase).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Note).HasMaxLength(4000);
        b.HasIndex(x => new { x.AssetId, x.MeasuredAtUtc });
        b.HasIndex(x => new { x.MeasurementTypeId, x.MeasuredAtUtc });
        b.HasOne(x => x.Asset).WithMany(x => x.Readings).HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.MeasurementType).WithMany().HasForeignKey(x => x.MeasurementTypeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DashboardCategoryConfiguration : IEntityTypeConfiguration<DashboardCategory>
{
    public void Configure(EntityTypeBuilder<DashboardCategory> b)
    {
        b.ToTable("DashboardCategory");
        b.Property(x => x.Section).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Label).HasMaxLength(DashboardCategory.ShortText).IsRequired();
        b.Property(x => x.Code).HasMaxLength(DashboardCategory.CodeMaxLength);
        b.Property(x => x.ActionBy).HasMaxLength(DashboardCategory.ShortText);
        b.Property(x => x.EmailAction).HasMaxLength(DashboardCategory.ShortText);
        b.Property(x => x.Recommendation).HasMaxLength(DashboardCategory.LongText);
        b.Property(x => x.Note).HasMaxLength(DashboardCategory.LongText);
        b.HasIndex(x => new { x.UnitId, x.Label }).IsUnique();
        // Hold Cat1 and N/A Cat1 can coexist; rows without a code yet are not checked.
        b.HasIndex(x => new { x.UnitId, x.Section, x.Code }).IsUnique().HasFilter("[Code] IS NOT NULL");
        b.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId);
    }
}

internal sealed class CategoryKeywordConfiguration : IEntityTypeConfiguration<CategoryKeyword>
{
    public void Configure(EntityTypeBuilder<CategoryKeyword> b)
    {
        b.ToTable("CategoryKeyword");
        b.Property(x => x.Keyword).HasMaxLength(200).IsRequired();
        b.HasOne(x => x.Category).WithMany(x => x.Keywords).HasForeignKey(x => x.CategoryId);
    }
}

internal sealed class WorkbookTemplateConfiguration : IEntityTypeConfiguration<WorkbookTemplate>
{
    public void Configure(EntityTypeBuilder<WorkbookTemplate> b)
    {
        b.ToTable("WorkbookTemplate");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(50);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.Property(x => x.UploadedBy).HasMaxLength(256);
    }
}

internal sealed class PipingNoteConfiguration : IEntityTypeConfiguration<PipingNote>
{
    public void Configure(EntityTypeBuilder<PipingNote> b)
    {
        b.ToTable("PipingNote");
        b.Property(x => x.Text).HasMaxLength(PipingNote.TextLength).IsRequired();
        b.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId);
        b.HasIndex(x => new { x.UnitId, x.NoteDate });
    }
}

internal sealed class UnitReportConfigConfiguration : IEntityTypeConfiguration<UnitReportConfig>
{
    public void Configure(EntityTypeBuilder<UnitReportConfig> b)
    {
        b.ToTable("UnitReportConfig");
        b.HasKey(x => x.UnitId);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.UnitLabel).HasMaxLength(50);
        b.Property(x => x.ExtraColumnsJson).HasMaxLength(1000);
        b.HasOne(x => x.Unit).WithOne().HasForeignKey<UnitReportConfig>(x => x.UnitId);
    }
}

internal sealed class ChangeLogConfiguration : IEntityTypeConfiguration<ChangeLog>
{
    public void Configure(EntityTypeBuilder<ChangeLog> b)
    {
        b.ToTable("ChangeLog");
        b.Property(x => x.EntityType).HasMaxLength(64);
        b.Property(x => x.EntityKey).HasMaxLength(100);
        b.Property(x => x.Field).HasMaxLength(128);
        b.Property(x => x.ChangedBy).HasMaxLength(256);
        b.HasIndex(x => new { x.AssetId, x.ChangedAtUtc });
        b.HasIndex(x => x.ChangedAtUtc);
        b.HasIndex(x => x.BatchId);
    }
}

internal sealed class ExcelSessionConfiguration : IEntityTypeConfiguration<ExcelSession>
{
    public void Configure(EntityTypeBuilder<ExcelSession> b)
    {
        b.ToTable("ExcelSession");
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.AssetType).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExportedBy).HasMaxLength(256);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.HasIndex(x => new { x.UnitId, x.ExportedAtUtc });
        b.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExcelTemplateConfiguration : IEntityTypeConfiguration<ExcelTemplate>
{
    public void Configure(EntityTypeBuilder<ExcelTemplate> b)
    {
        b.ToTable("ExcelTemplate");
        b.HasKey(x => x.UnitId);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.Property(x => x.UploadedBy).HasMaxLength(256);
        b.HasOne(x => x.Unit).WithOne().HasForeignKey<ExcelTemplate>(x => x.UnitId);
    }
}

internal sealed class ImportFileConfiguration : IEntityTypeConfiguration<ImportFile>
{
    public void Configure(EntityTypeBuilder<ImportFile> b)
    {
        b.ToTable("ImportFile");
        b.HasKey(x => x.ImportRunId);
        b.HasOne(x => x.ImportRun).WithOne().HasForeignKey<ImportFile>(x => x.ImportRunId);
    }
}

internal sealed class ImportRunConfiguration : IEntityTypeConfiguration<ImportRun>
{
    public void Configure(EntityTypeBuilder<ImportRun> b)
    {
        b.ToTable("ImportRun");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.Property(x => x.UploadedBy).HasMaxLength(256);
        b.Property(x => x.Message).HasMaxLength(4000);
        b.HasIndex(x => x.UploadedAtUtc);
        b.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ImportChangeConfiguration : IEntityTypeConfiguration<ImportChange>
{
    public void Configure(EntityTypeBuilder<ImportChange> b)
    {
        b.ToTable("ImportChange");
        b.Property(x => x.AssetCode).HasMaxLength(40);
        b.Property(x => x.AssetTag).HasMaxLength(55);
        b.Property(x => x.Field).HasMaxLength(128);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Resolution).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.ResolvedBy).HasMaxLength(256);
        b.Property(x => x.ResolutionNote).HasMaxLength(1000);
        b.HasIndex(x => new { x.Resolution, x.ImportRunId });
        b.HasIndex(x => x.AssetId);
        b.HasOne(x => x.ImportRun).WithMany(x => x.Changes).HasForeignKey(x => x.ImportRunId);
    }
}

internal sealed class SimproSyncRunConfiguration : IEntityTypeConfiguration<SimproSyncRun>
{
    public void Configure(EntityTypeBuilder<SimproSyncRun> b)
    {
        b.ToTable("SimproSyncRun");
        b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.TriggeredBy).HasMaxLength(256);
        b.Property(x => x.Message).HasMaxLength(4000);
        b.HasIndex(x => x.StartedAtUtc);
    }
}

internal sealed class SimproSyncItemConfiguration : IEntityTypeConfiguration<SimproSyncItem>
{
    public void Configure(EntityTypeBuilder<SimproSyncItem> b)
    {
        b.ToTable("SimproSyncItem");
        b.Property(x => x.AssetCode).HasMaxLength(40);
        b.Property(x => x.Field).HasMaxLength(128);
        b.Property(x => x.AssetTag).HasMaxLength(55);
        b.Property(x => x.Outcome).HasMaxLength(30);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.HasIndex(x => x.AssetId);
        b.HasOne(x => x.Run).WithMany(x => x.Items).HasForeignKey(x => x.RunId);
    }
}

internal sealed class SimproForcedAssetConfiguration : IEntityTypeConfiguration<SimproForcedAsset>
{
    public void Configure(EntityTypeBuilder<SimproForcedAsset> b)
    {
        b.ToTable("SimproForcedAsset");
        b.HasKey(x => x.AssetId);
        b.Property(x => x.RequestedBy).HasMaxLength(256);
    }
}

internal sealed class SimproBeforeImageConfiguration : IEntityTypeConfiguration<SimproBeforeImage>
{
    public void Configure(EntityTypeBuilder<SimproBeforeImage> b)
    {
        b.ToTable("SimproBeforeImage");
        b.Property(x => x.TableName).HasMaxLength(128);
        b.Property(x => x.RowKey).HasMaxLength(100);
        b.HasIndex(x => x.RunId);
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ToTable("AppUser");
        b.Property(x => x.Email).HasMaxLength(256).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.Property(x => x.EmergencyPasswordHash).HasMaxLength(500);
        b.HasIndex(x => x.Email).IsUnique();
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Role");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("RolePermission");
        b.HasKey(x => new { x.RoleId, x.Permission });
        b.Property(x => x.Permission).HasMaxLength(64);
        b.HasOne(x => x.Role).WithMany(x => x.Permissions).HasForeignKey(x => x.RoleId);
    }
}

internal sealed class LoginAuditConfiguration : IEntityTypeConfiguration<LoginAudit>
{
    public void Configure(EntityTypeBuilder<LoginAudit> b)
    {
        b.ToTable("LoginAudit");
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Detail).HasMaxLength(500);
        b.HasIndex(x => x.AtUtc);
    }
}
