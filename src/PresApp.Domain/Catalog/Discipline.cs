using PresApp.Domain.Assets;

namespace PresApp.Domain.Catalog;

public class Discipline
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AssetType AssetType { get; set; }

    /// <summary>Two letters used to build Equipment IDs (ST, EL, EI, PL…). Configurable by admin.</summary>
    public string? Prefix { get; set; }

    public bool SyncToSimpro { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
