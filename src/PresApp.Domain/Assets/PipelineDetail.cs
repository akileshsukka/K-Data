using PresApp.Domain.Common;

namespace PresApp.Domain.Assets;

/// <summary>Pipeline-only data (1:1 with <see cref="Asset"/>). Size, spec and service are read from the line number; class and flange type come from the pipe spec catalogue.</summary>
public class PipelineDetail : AuditableEntity, IFieldAudited
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    /// <summary>Length in metres.</summary>
    public decimal? LengthM { get; set; }

    /// <summary>Nominal size in inches (3/4" = 0.75).</summary>
    public decimal? SizeIn { get; set; }

    /// <summary>Pipe spec code, free text (e.g. 11010). Later it can point to the pipe spec catalogue (class, flange type).</summary>
    public string? PipeSpec { get; set; }

    /// <summary>Service (fluid) code of the line number, e.g. LS, N2, FAG.</summary>
    public string? Service { get; set; }

    /// <summary>P&amp;ID drawing number, entered by hand.</summary>
    public string? PidNumber { get; set; }

    public const int CodeLength = 20;
    public const int PidLength = 50;

    Guid? IFieldAudited.AuditAssetId => AssetId;
}
