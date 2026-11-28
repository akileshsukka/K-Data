using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PresApp.Domain.GateForms;

namespace PresApp.Infrastructure.Data.Configurations;

internal sealed class GateFormTemplateConfiguration : IEntityTypeConfiguration<GateFormTemplate>
{
    public void Configure(EntityTypeBuilder<GateFormTemplate> b)
    {
        b.ToTable("GateFormTemplate");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.Property(x => x.UploadedBy).HasMaxLength(256);
        b.HasOne(x => x.Plant).WithMany().HasForeignKey(x => x.PlantId);
        b.HasIndex(x => new { x.PlantId, x.Kind, x.IsActive });
    }
}

internal sealed class GateFormUnitSettingConfiguration : IEntityTypeConfiguration<GateFormUnitSetting>
{
    public void Configure(EntityTypeBuilder<GateFormUnitSetting> b)
    {
        b.ToTable("GateFormUnitSetting");
        b.HasKey(x => x.UnitId);
        b.Property(x => x.MechanicalSource).HasMaxLength(GateFormEntry.TextMaxLength);
        b.Property(x => x.ElectricalSource).HasMaxLength(GateFormEntry.TextMaxLength);
        b.Property(x => x.ElectricalDocumentsNo).HasMaxLength(GateFormEntry.TextMaxLength);
        b.HasOne(x => x.Unit).WithOne().HasForeignKey<GateFormUnitSetting>(x => x.UnitId);
    }
}

internal sealed class GateFormEntryConfiguration : IEntityTypeConfiguration<GateFormEntry>
{
    public void Configure(EntityTypeBuilder<GateFormEntry> b)
    {
        b.ToTable("GateFormEntry");
        b.HasKey(x => x.AssetId);
        b.Property(x => x.RecordNo).HasMaxLength(GateFormEntry.TextMaxLength);
        b.Property(x => x.Section).HasMaxLength(GateFormEntry.TextMaxLength);
        b.Property(x => x.WorkPack).HasMaxLength(GateFormEntry.TextMaxLength);
        b.HasOne(x => x.Asset).WithOne().HasForeignKey<GateFormEntry>(x => x.AssetId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.RecordNo);
    }
}

internal sealed class RecordNoSequenceConfiguration : IEntityTypeConfiguration<RecordNoSequence>
{
    public void Configure(EntityTypeBuilder<RecordNoSequence> b)
    {
        b.ToTable("RecordNoSequence");
        b.HasKey(x => new { x.Code, x.UnitNumber });
        b.Property(x => x.Code).HasMaxLength(RecordNoFormat.CodeMaxLength);
        // Two prints at once must not take the same number: the second one is refused and can be repeated.
        b.Property(x => x.LastNumber).IsConcurrencyToken();
    }
}
