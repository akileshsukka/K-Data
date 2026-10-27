using PresApp.Domain.Catalog;

namespace PresApp.Domain.Assets;

public enum FieldDataType
{
    Text = 0,
    Number = 1,
    Date = 2,
    List = 3,
    YesNo = 4
}

/// <summary>
/// A field an admin adds to an asset type without code changes (base of the E&amp;I register).
/// Values live in <see cref="AssetFieldValue"/>.
/// </summary>
public class FieldDefinition
{
    public int Id { get; set; }
    public AssetType AssetType { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldDataType DataType { get; set; }
    public int? OptionSetId { get; set; }
    public OptionSet? OptionSet { get; set; }
    public int? MaxLength { get; set; }
    public bool IsRequired { get; set; }
    public string? Section { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Shown as a column of the register of its asset type.</summary>
    public bool ShowInRegister { get; set; }
    public bool IsActive { get; set; } = true;

    public const int DefaultTextLength = 500;
}

/// <summary>Value of a dynamic field for one asset. Only the column matching the data type is used.</summary>
public class AssetFieldValue : Common.AuditableEntity, Common.IFieldAudited
{
    public long Id { get; set; }
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public int FieldDefinitionId { get; set; }
    public FieldDefinition FieldDefinition { get; set; } = null!;
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateOnly? ValueDate { get; set; }

    Guid? Common.IFieldAudited.AuditAssetId => AssetId;
}
