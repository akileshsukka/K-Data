using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PresApp.Domain.Catalog;

namespace PresApp.Infrastructure.Data.Configurations;

internal sealed class PlantConfiguration : IEntityTypeConfiguration<Plant>
{
    public void Configure(EntityTypeBuilder<Plant> b)
    {
        b.ToTable("Plant");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> b)
    {
        b.ToTable("Unit");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.HasOne(x => x.Plant).WithMany(x => x.Units).HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OptionSetConfiguration : IEntityTypeConfiguration<OptionSet>
{
    public void Configure(EntityTypeBuilder<OptionSet> b)
    {
        b.ToTable("OptionSet");
        b.Property(x => x.Key).HasMaxLength(64).IsRequired();
        b.Property(x => x.Label).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class OptionValueConfiguration : IEntityTypeConfiguration<OptionValue>
{
    public void Configure(EntityTypeBuilder<OptionValue> b)
    {
        b.ToTable("OptionValue");
        b.Property(x => x.Value).HasMaxLength(255).IsRequired();
        b.HasIndex(x => new { x.OptionSetId, x.Value }).IsUnique();
        b.HasOne(x => x.OptionSet).WithMany(x => x.Values).HasForeignKey(x => x.OptionSetId);
    }
}

internal sealed class DisciplineConfiguration : IEntityTypeConfiguration<Discipline>
{
    public void Configure(EntityTypeBuilder<Discipline> b)
    {
        b.ToTable("Discipline");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Prefix).HasMaxLength(4);
        b.Property(x => x.AssetType).HasConversion<string>().HasMaxLength(30);
        b.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class MeasurementTypeConfiguration : IEntityTypeConfiguration<MeasurementType>
{
    public void Configure(EntityTypeBuilder<MeasurementType> b)
    {
        b.ToTable("MeasurementType");
        b.Property(x => x.Key).HasMaxLength(64).IsRequired();
        b.Property(x => x.Label).HasMaxLength(128).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> b)
    {
        b.ToTable("AppSetting");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Value).HasMaxLength(4000);
        b.Property(x => x.Description).HasMaxLength(500);
    }
}

internal sealed class LineCodeConfiguration : IEntityTypeConfiguration<LineCode>
{
    public void Configure(EntityTypeBuilder<LineCode> b)
    {
        b.ToTable("LineCode");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Code).HasMaxLength(LineCode.CodeLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(LineCode.DescriptionLength).IsRequired();
        b.HasIndex(x => new { x.Kind, x.Code }).IsUnique();
    }
}

internal sealed class PipeSpecConfiguration : IEntityTypeConfiguration<PipeSpec>
{
    public void Configure(EntityTypeBuilder<PipeSpec> b)
    {
        b.ToTable("PipeSpec");
        b.Property(x => x.Code).HasMaxLength(PipeSpec.CodeLength).IsRequired();
        b.Property(x => x.PressureClass).HasMaxLength(PipeSpec.ClassLength);
        b.Property(x => x.FlangeType).HasMaxLength(PipeSpec.FlangeLength);
        b.Property(x => x.Gasket).HasMaxLength(PipeSpec.GasketLength);
        b.Property(x => x.Description).HasMaxLength(PipeSpec.DescriptionLength);
        b.HasIndex(x => x.Code).IsUnique();
    }
}
