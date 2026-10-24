using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PresApp.Domain.Assets;

namespace PresApp.Infrastructure.Data.Configurations;

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> b)
    {
        b.ToTable("Asset");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Code).HasMaxLength(Asset.CodeMaxLength).IsRequired();
        b.Property(x => x.Tag).HasMaxLength(55);
        b.Property(x => x.Description).HasMaxLength(255);
        b.Property(x => x.Priority).HasMaxLength(40);
        b.Property(x => x.MigrationNotes).HasMaxLength(4000);

        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.UnitId, x.Type, x.IsActive });
        b.HasIndex(x => x.Tag);
        b.HasIndex(x => x.DisciplineId);

        b.HasOne(x => x.Discipline).WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Preservation).WithOne(x => x.Asset).HasForeignKey<AssetPreservation>(x => x.AssetId);
        b.HasOne(x => x.Pipeline).WithOne(x => x.Asset).HasForeignKey<PipelineDetail>(x => x.AssetId);
    }
}

internal sealed class AssetPreservationConfiguration : IEntityTypeConfiguration<AssetPreservation>
{
    public void Configure(EntityTypeBuilder<AssetPreservation> b)
    {
        b.ToTable("AssetPreservation");
        b.HasKey(x => x.AssetId);
        b.Property(x => x.CurrentGate).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AssetAvailability).HasMaxLength(50);

        foreach (var name in new[]
                 {
                     nameof(AssetPreservation.ThirdPartyInspectionStatus), nameof(AssetPreservation.PreservationStatusAdnoc),
                     nameof(AssetPreservation.PreservedByEu), nameof(AssetPreservation.EmWalkdownStatus),
                     nameof(AssetPreservation.ActionBy), nameof(AssetPreservation.OemName),
                     nameof(AssetPreservation.CurrentActivity), nameof(AssetPreservation.N2BlanketStatus),
                     nameof(AssetPreservation.G1BulkLiquidDrained), nameof(AssetPreservation.G5PositiveMechIsolation),
                     nameof(AssetPreservation.G6CleaningMethod),
                     "G1Status", "G2Status", "G3Status", "G4Status", "G5Status", "G6Status", "G7Status", "G8Status"
                 })
        {
            b.Property(name).HasMaxLength(AssetPreservation.ShortText);
        }

        foreach (var name in new[]
                 {
                     nameof(AssetPreservation.PreservationRemarks), nameof(AssetPreservation.RemarksAdnoc),
                     nameof(AssetPreservation.EmComments), nameof(AssetPreservation.Response),
                     nameof(AssetPreservation.CurrentStatusAdnoc),
                     "G1Comments", "G2Comments", "G3Comments", "G4Comments", "G5Comments", "G6Comments", "G7Comments", "G8Comments"
                 })
        {
            b.Property(name).HasMaxLength(AssetPreservation.LongText);
        }

        b.Property(x => x.PreservedBy).HasMaxLength(256);
        b.HasIndex(x => x.CurrentGate);
        for (var g = 1; g <= 8; g++) b.HasIndex($"G{g}Status");
    }
}

internal sealed class GateDocumentationConfiguration : IEntityTypeConfiguration<GateDocumentation>
{
    public void Configure(EntityTypeBuilder<GateDocumentation> b)
    {
        b.ToTable("GateDocumentation", t => t.HasCheckConstraint("CK_GateDocumentation_Gate", "[Gate] BETWEEN 1 AND 8"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reference).HasMaxLength(255);
        b.Property(x => x.Note).HasMaxLength(1000);
        b.HasIndex(x => new { x.AssetId, x.Gate }).IsUnique();
        b.HasOne(x => x.Asset).WithMany(x => x.GateDocuments).HasForeignKey(x => x.AssetId);
    }
}

internal sealed class PipelineDetailConfiguration : IEntityTypeConfiguration<PipelineDetail>
{
    public void Configure(EntityTypeBuilder<PipelineDetail> b)
    {
        b.ToTable("PipelineDetail");
        b.HasKey(x => x.AssetId);
        b.Property(x => x.LengthM).HasPrecision(12, 3);
        b.Property(x => x.SizeIn).HasPrecision(8, 3);
        b.Property(x => x.PipeSpec).HasMaxLength(50);
        b.HasIndex(x => x.PipeSpec);
    }
}

internal sealed class FieldDefinitionConfiguration : IEntityTypeConfiguration<FieldDefinition>
{
    public void Configure(EntityTypeBuilder<FieldDefinition> b)
    {
        b.ToTable("FieldDefinition");
        b.Property(x => x.AssetType).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.DataType).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Key).HasMaxLength(64).IsRequired();
        b.Property(x => x.Label).HasMaxLength(128).IsRequired();
        b.Property(x => x.Section).HasMaxLength(64);
        b.HasIndex(x => new { x.AssetType, x.Key }).IsUnique();
        b.HasOne(x => x.OptionSet).WithMany().HasForeignKey(x => x.OptionSetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AssetFieldValueConfiguration : IEntityTypeConfiguration<AssetFieldValue>
{
    public void Configure(EntityTypeBuilder<AssetFieldValue> b)
    {
        b.ToTable("AssetFieldValue");
        b.Property(x => x.ValueText).HasMaxLength(4000);
        b.Property(x => x.ValueNumber).HasPrecision(18, 6);
        b.HasIndex(x => new { x.AssetId, x.FieldDefinitionId }).IsUnique();
        b.HasIndex(x => new { x.FieldDefinitionId, x.ValueText });
        b.HasOne(x => x.Asset).WithMany(x => x.FieldValues).HasForeignKey(x => x.AssetId);
        b.HasOne(x => x.FieldDefinition).WithMany().HasForeignKey(x => x.FieldDefinitionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EquipmentIdSequenceConfiguration : IEntityTypeConfiguration<EquipmentIdSequence>
{
    public void Configure(EntityTypeBuilder<EquipmentIdSequence> b)
    {
        b.ToTable("EquipmentIdSequence");
        b.HasKey(x => new { x.Prefix, x.UnitNumber });
        b.Property(x => x.Prefix).HasMaxLength(4);
    }
}
