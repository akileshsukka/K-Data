namespace PresApp.Domain.Assets;

/// <summary>Kind of physical asset. Each type has its own register.</summary>
public enum AssetType
{
    Equipment = 1,
    Pipeline = 2,
    ElectricalInstrument = 3
}

/// <summary>Set of asset types a catalog row applies to.</summary>
[Flags]
public enum AssetTypes
{
    None = 0,
    Equipment = 1,
    Pipeline = 2,
    ElectricalInstrument = 4,
    All = Equipment | Pipeline | ElectricalInstrument
}

public static class AssetTypeExtensions
{
    public static AssetTypes ToFlag(this AssetType type) => type switch
    {
        AssetType.Equipment => AssetTypes.Equipment,
        AssetType.Pipeline => AssetTypes.Pipeline,
        AssetType.ElectricalInstrument => AssetTypes.ElectricalInstrument,
        _ => AssetTypes.None
    };
}
