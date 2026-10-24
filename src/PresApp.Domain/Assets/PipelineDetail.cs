using PresApp.Domain.Common;

namespace PresApp.Domain.Assets;

/// <summary>Pipeline-only data (1:1 with <see cref="Asset"/>). Size and spec are read from the line number.</summary>
public class PipelineDetail : AuditableEntity, IFieldAudited
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    /// <summary>Length in metres.</summary>
    public decimal? LengthM { get; set; }

    /// <summary>Nominal size in inches (3/4" = 0.75).</summary>
    public decimal? SizeIn { get; set; }

    /// <summary>Pipe spec code, free text (e.g. 11010).</summary>
    public string? PipeSpec { get; set; }

    Guid? IFieldAudited.AuditAssetId => AssetId;
}
