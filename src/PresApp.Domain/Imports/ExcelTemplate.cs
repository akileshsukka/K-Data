using PresApp.Domain.Catalog;

namespace PresApp.Domain.Imports;

/// <summary>
/// The client "Master Equipment List – Preservation" workbook of one unit, uploaded by an administrator.
/// Every export starts from a copy of it.
/// </summary>
public class ExcelTemplate
{
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;
    public string FileName { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public DateTime UploadedAtUtc { get; set; }
    public string UploadedBy { get; set; } = "";
}

/// <summary>The uploaded workbook of an import, kept so a preview can be applied later and audited.</summary>
public class ImportFile
{
    public long ImportRunId { get; set; }
    public ImportRun ImportRun { get; set; } = null!;
    public byte[] Content { get; set; } = [];
}
