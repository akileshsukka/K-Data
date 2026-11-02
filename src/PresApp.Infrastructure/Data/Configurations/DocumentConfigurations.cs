using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PresApp.Domain.Documents;

namespace PresApp.Infrastructure.Data.Configurations;

internal sealed class StoredDocumentConfiguration : IEntityTypeConfiguration<StoredDocument>
{
    public void Configure(EntityTypeBuilder<StoredDocument> b)
    {
        b.ToTable("Document");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FileName).HasMaxLength(StoredDocument.FileNameMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.StoragePath).HasMaxLength(400).IsRequired();
        b.Property(x => x.Note).HasMaxLength(StoredDocument.NoteMaxLength);
        b.Property(x => x.DeletedBy).HasMaxLength(256);
        b.HasIndex(x => x.Sha256);
    }
}

internal sealed class DocumentLinkConfiguration : IEntityTypeConfiguration<DocumentLink>
{
    public void Configure(EntityTypeBuilder<DocumentLink> b)
    {
        b.ToTable("DocumentLink", t => t.HasCheckConstraint("CK_DocumentLink_Gate", "[Gate] IS NULL OR [Gate] BETWEEN 1 AND 8"));
        b.HasKey(x => x.Id);
        b.Ignore(x => x.IsActive);
        b.Property(x => x.LinkedBy).HasMaxLength(256);
        b.Property(x => x.RemovedBy).HasMaxLength(256);
        b.HasIndex(x => new { x.AssetId, x.Gate });
        b.HasIndex(x => x.DocumentId);
        b.HasOne(x => x.Document).WithMany(x => x.Links).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
    }
}
