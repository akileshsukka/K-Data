namespace PresApp.Api.Assets;

public sealed record NamedRef(int Id, string Name);
public sealed record UnitRef(int Id, string Name, int Number, string Plant);

/// <summary>One row of a register table (light: only what the table shows and filters on).</summary>
public sealed record RegisterRow(
    Guid Id, string Code, string? Tag, string? Description, string Discipline, string? Priority,
    string CurrentGate, IReadOnlyList<string?> Gates, string? AssetAvailability, string? PreservationStatus,
    string? ThirdPartyInspection, string? EmWalkdown, string? PreservedByEu, string? ActionBy, string? N2BlanketStatus,
    string? CurrentActivity, string? PreservationRemarks, DateOnly? LastFieldActionDate, DateTime? PreservedAtUtc,
    int DocumentsPending, bool IsActive, DateTime UpdatedAtUtc, LineInfo? Line, IReadOnlyList<string>? Groups = null, string? Circuit = null,
    IReadOnlyDictionary<string, string?>? Custom = null, string? BulkLiquidDrained = null);

/// <summary>Pipeline columns of the register.</summary>
public sealed record LineInfo(decimal? SizeIn, string? PipeSpec, string? PressureClass, string? FlangeType, string? Gasket, string? Service, string? PidNumber, decimal? LengthM);

public sealed record GateDocDto(int Gate, string Status, DateOnly? ReceivedOn, string? Reference, string? Note, string UpdatedBy, DateTime UpdatedAtUtc);
public sealed record GroupRefDto(int Id, string Code, string Name, string Type);

/// <summary>Everything the asset profile shows. Field values are strings keyed by field key.</summary>
public sealed record AssetProfile(
    Guid Id, string Code, string Type, NamedRef Discipline, UnitRef Unit, bool IsActive, Guid? SimproFlangeId,
    string CurrentGate, string SuggestedCurrentGate, IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<GateDocDto> GateDocuments, IReadOnlyList<GroupRefDto> Groups,
    DateTime? PreservedAtUtc, string? PreservedBy, string UpdatedBy, DateTime UpdatedAtUtc,
    AssetCategoryDto? DashboardCategory = null);

/// <summary>Hold / N/A dashboard category of an asset (template v4).</summary>
public sealed record AssetCategoryDto(int Id, string? Code, string Label, string Section);

/// <summary>Sets the asset's category (null = none). Expected = the category id the user saw.</summary>
public sealed record SetCategoryRequest(int? CategoryId, int? Expected);

public sealed record FieldChange(string Field, string? From, string? To);
public sealed record PatchRequest(IReadOnlyList<FieldChange> Changes);
public sealed record Proposals(string? CurrentGate, DateOnly? LastFieldActionDate);
public sealed record PatchResponse(AssetProfile Asset, Proposals Proposals);
public sealed record FieldConflict(string Field, string Label, string? Expected, string? Current);

public sealed record SetCurrentGateRequest(string Value, string? Expected);
public sealed record MarkPreservedRequest(DateOnly? PreservedOn);
public sealed record GateDocRequest(string Status, DateOnly? ReceivedOn, string? Reference, string? Note);
public sealed record CreateAssetRequest(string Type, int UnitId, int DisciplineId, string? Tag, string? Description, string? Priority, decimal? LengthM = null, string? PidNumber = null, bool AllowSimilar = false);

public sealed record BulkLinesRequest(string Text);
public sealed record BulkLineRow(int Row, string LineNumber, decimal? SizeIn, string? PipeSpec, string? PressureClass, string? FlangeType, string? Service, decimal? LengthM,
    string Outcome, string? Reason, Guid? AssetId, string? Code, string? Warning = null);
public sealed record BulkLinesResult(int ToCreate, int Existing, int Invalid, List<BulkLineRow> Rows);

public sealed record ReadingDto(long Id, string Measurement, string Label, decimal Value, string? UnitOfMeasure,
    DateTime MeasuredAtUtc, DateTime RecordedAtUtc, string RecordedBy, string Source, string Phase, string? Note);
public sealed record ReadingValue(int MeasurementTypeId, decimal Value);
public sealed record AddReadingsRequest(DateTime MeasuredAtUtc, string? Note, IReadOnlyList<ReadingValue> Values);
public sealed record AddReadingsResponse(int Saved, Proposals Proposals, AssetProfile Asset);

public sealed record HistoryEntry(DateTime AtUtc, Guid? AssetId, string? AssetCode, string? AssetTag, string Field, string Label,
    string? OldValue, string? NewValue, string By, string? Source);

public sealed record SearchHit(Guid Id, string Code, string? Tag, string? Description, string Unit, string Type, bool IsActive);

public sealed record BulkRequest(IReadOnlyList<Guid> AssetIds, string Field, string? Value);
public sealed record BulkRow(Guid AssetId, string Code, string? Tag, string? Current, string? New, string Outcome, string? Reason);
public sealed record BulkResult(int Changed, int Unchanged, int Skipped, IReadOnlyList<BulkRow> Rows);

public sealed record UnitSummary(int Equipment, int ElectricalInstrument, int Pipelines, int Inactive,
    IReadOnlyDictionary<string, int> EquipmentByCurrentGate, int DocumentsPending);
