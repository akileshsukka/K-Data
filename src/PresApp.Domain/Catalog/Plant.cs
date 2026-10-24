namespace PresApp.Domain.Catalog;

public class Plant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Simpro tbl_locationBreakdown.id of this plant.</summary>
    public Guid? SimproLocationId { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Unit> Units { get; set; } = [];
}

public class Unit
{
    public int Id { get; set; }
    public int PlantId { get; set; }
    public Plant Plant { get; set; } = null!;

    /// <summary>Full text as used everywhere, e.g. "Unit 541".</summary>
    public string Name { get; set; } = "";

    /// <summary>Number used in Equipment IDs and Excel (541 → "541", 39 → "039").</summary>
    public int Number { get; set; }

    /// <summary>Simpro tbl_locationBreakdown.id of this unit.</summary>
    public Guid? SimproLocationId { get; set; }
    public bool IsActive { get; set; } = true;
}
