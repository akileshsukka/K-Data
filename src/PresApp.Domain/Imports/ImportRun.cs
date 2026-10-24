using PresApp.Domain.Catalog;

namespace PresApp.Domain.Imports;

public enum ImportKind
{
    /// <summary>A file exported by PresApp, edited and uploaded back.</summary>
    Update = 0,

    /// <summary>A client master list used to create or complete assets.</summary>
    Onboarding = 1
}

public enum ImportStatus
{
    Previewed = 0,
    Applied = 1,
    AppliedWithIssues = 2,
    Rejected = 3,
    Failed = 4,
    Cancelled = 5
}

public enum ImportOutcome
{
    Applied,
    Derived,
    NoChange,
    Invalid,
    Conflict,
    Rejected,
    Warning,
    Created
}

public enum IssueResolution
{
    /// <summary>Not an issue (Applied, Derived, No change, Created).</summary>
    None = 0,
    Open = 1,
    Resolved = 2
}

public class ImportRun
{
    public long Id { get; set; }
    public ImportKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public Guid? SessionId { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public ImportStatus Status { get; set; }
    public int AppliedCount { get; set; }
    public int IssueCount { get; set; }
    public int WarningCount { get; set; }
    public string? Message { get; set; }
    public List<ImportChange> Changes { get; set; } = [];
}

/// <summary>Result of one cell. Problem outcomes become issues an admin resolves.</summary>
public class ImportChange
{
    public long Id { get; set; }
    public long ImportRunId { get; set; }
    public ImportRun ImportRun { get; set; } = null!;
    public Guid? AssetId { get; set; }
    public string? AssetCode { get; set; }
    public string Field { get; set; } = "";
    public string? BaseValue { get; set; }
    public string? ExcelValue { get; set; }
    public string? CurrentValue { get; set; }
    public string? NewValue { get; set; }
    public ImportOutcome Outcome { get; set; }
    public string? Reason { get; set; }
    public IssueResolution Resolution { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionNote { get; set; }
}
