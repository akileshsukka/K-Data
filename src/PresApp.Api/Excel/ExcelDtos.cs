namespace PresApp.Api.Excel;

public sealed record ImportCounts(int Applied, int Derived, int NoChange, int Invalid, int Conflict, int Rejected, int Warning, int Created)
{
    public int Issues => Invalid + Conflict + Rejected + Warning;
}

public sealed record ImportRunDto(
    long Id, string Kind, string FileName, int? UnitId, string? UnitName, Guid? SessionId,
    string UploadedBy, DateTime UploadedAtUtc, DateTime? AppliedAtUtc, string Status, string? Message,
    int RowsInFile, ImportCounts Counts, DateTime? ExportedAtUtc, string? ExportedBy);

public sealed record ImportChangeDto(
    long Id, long RunId, Guid? AssetId, string? AssetCode, string? AssetTag, int? RowNumber,
    string Field, string FieldLabel, string? BaseValue, string? ExcelValue, string? CurrentValue, string? NewValue,
    string Outcome, string? Reason, string Resolution, string? ResolvedBy, DateTime? ResolvedAtUtc, string? ResolutionNote);

public sealed record ImportRunDetail(ImportRunDto Run, List<ImportChangeDto> Changes);

public sealed record ExcelSessionDto(Guid Id, string FileName, DateTime ExportedAtUtc, string ExportedBy, string Status, int RowCount, DateTime? ProcessedAtUtc, bool Legacy);

public sealed record UnitExcelOverview(bool HasTemplate, string? TemplateFileName, DateTime? TemplateUploadedAtUtc, int MaxAgeHours,
    List<ExcelSessionDto> Sessions, List<ImportRunDto> Runs);

public sealed record IssueDto(ImportChangeDto Change, string FileName, int? UnitId, string? UnitName, DateTime UploadedAtUtc, string UploadedBy);

public sealed record ResolveIssueRequest(bool Resolved, string? Note);

public sealed record TemplateDto(int UnitId, string UnitName, string? FileName, DateTime? UploadedAtUtc, string? UploadedBy, int Categories);
