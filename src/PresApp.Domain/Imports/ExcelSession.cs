using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;

namespace PresApp.Domain.Imports;

public enum ExcelSessionStatus
{
    Open = 0,
    Processing = 1,
    Processed = 2
}

/// <summary>One exported unit Excel. A file can be imported back once, within the time limit.</summary>
public class ExcelSession
{
    public Guid Id { get; set; }
    public int UnitId { get; set; }
    public Unit Unit { get; set; } = null!;
    public AssetType AssetType { get; set; }
    public DateTime ExportedAtUtc { get; set; }
    public string ExportedBy { get; set; } = "";
    public ExcelSessionStatus Status { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int FormatVersion { get; set; }

    /// <summary>JSON of the exact table headers and positions, checked before any import.</summary>
    public string StructureSignature { get; set; } = "";
}
