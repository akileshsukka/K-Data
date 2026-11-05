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

    /// <summary>
    /// The line number does not follow the standard (loaded with the format check turned off). Internal control: only roles with
    /// lines.nonStandard see it. Worked out again from the line number every time the number changes.
    /// </summary>
    public bool NonStandardNumber { get; set; }

    /// <summary>For a non-standard number: what could not be read and what was ignored, e.g. "Not read: pipe spec · Ignored: ST(1x1/2)".</summary>
    public string? NumberNote { get; set; }

    public const int CodeLength = 20;
    public const int PidLength = 50;
    public const int NoteLength = 200;

    /// <summary>Takes what was read from the line number. <paramref name="onlyEmpty"/> fills only empty fields (re-reading never overwrites hand-entered values).</summary>
    public void TakeFrom(LineNumberReading r, bool onlyEmpty = false, bool keepSize = false, bool keepSpec = false, bool keepService = false)
    {
        if (r.SizeIn is { } size && !keepSize && (!onlyEmpty || SizeIn is null)) SizeIn = size;
        if (r.Spec is { } spec && !keepSpec && (!onlyEmpty || string.IsNullOrEmpty(PipeSpec))) PipeSpec = spec;
        if (r.Service is { } service && !keepService && (!onlyEmpty || string.IsNullOrEmpty(Service))) Service = service;
        NonStandardNumber = !r.IsStandard;
        NumberNote = r.Note;
    }

    Guid? IFieldAudited.AuditAssetId => AssetId;
}
