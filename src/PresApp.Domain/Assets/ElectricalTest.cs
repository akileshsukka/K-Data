using PresApp.Domain.Common;

namespace PresApp.Domain.Assets;

/// <summary>
/// Electrical test of step 7 (E7) of a motor, one per asset (1:1 with <see cref="Asset"/>). Only assets marked
/// <see cref="Required"/> need it; any asset type can be marked. Columns follow the client's E7 test sheet:
/// winding resistance (Ω), insulation resistance at 1 kV (Ω), four DAR tests (30 s / 60 s, GΩ) and six optional PI tests
/// (1 / 10 min, GΩ: U-V, V-W, U-W, U-G, V-G, W-G).
/// The DAR and PI ratios are never stored: they are calculated (<see cref="ElectricalTestSpec"/>).
/// </summary>
public class ElectricalTest : AuditableEntity, IFieldAudited
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    /// <summary>The asset needs this test. Turning it off keeps the values.</summary>
    public bool Required { get; set; }

    /// <summary>Date of the test. Editable in the app; not printed on the report.</summary>
    public DateOnly? TestedOn { get; set; }

    // Winding resistance (DLR ohmmeter), Ω
    public decimal? WrU1U2 { get; set; }
    public decimal? WrV1V2 { get; set; }
    public decimal? WrW1W2 { get; set; }

    // Insulation resistance IR (megger), applied voltage 1 kV
    public decimal? IrUV { get; set; }
    public decimal? IrUW { get; set; }
    public decimal? IrVW { get; set; }
    public decimal? IrUG { get; set; }
    public decimal? IrVG { get; set; }
    public decimal? IrWG { get; set; }

    // DAR tests, GΩ at 30 s and 60 s (the fourth one, E-UVW, is stored as DarUvwToE)
    public decimal? DarUVwe30 { get; set; }
    public decimal? DarUVwe60 { get; set; }
    public decimal? DarVUwe30 { get; set; }
    public decimal? DarVUwe60 { get; set; }
    public decimal? DarWUve30 { get; set; }
    public decimal? DarWUve60 { get; set; }
    public decimal? DarUvwToE30 { get; set; }
    public decimal? DarUvwToE60 { get; set; }

    // PI tests (optional), GΩ at 1 min and 10 min
    public decimal? PiUV1 { get; set; }
    public decimal? PiUV10 { get; set; }
    public decimal? PiVW1 { get; set; }
    public decimal? PiVW10 { get; set; }
    public decimal? PiUW1 { get; set; }
    public decimal? PiUW10 { get; set; }
    public decimal? PiUG1 { get; set; }
    public decimal? PiUG10 { get; set; }
    public decimal? PiVG1 { get; set; }
    public decimal? PiVG10 { get; set; }
    public decimal? PiWG1 { get; set; }
    public decimal? PiWG10 { get; set; }

    Guid? IFieldAudited.AuditAssetId => AssetId;
}

/// <summary>One value of the E7 test sheet.</summary>
public sealed record ElectricalMeasure(string Key, string Label, string Block, bool Required);

/// <summary>A block of the sheet (one colour): its values and, for DAR / PI, the ratio = second value ÷ first value.</summary>
public sealed record ElectricalBlock(string Key, string Title, string? Unit, bool Required, IReadOnlyList<ElectricalMeasure> Measures, string? RatioLabel);

/// <summary>Layout and rules of the E7 test: blocks in the order of the client sheet, required values, ratios.</summary>
public static class ElectricalTestSpec
{
    public static readonly IReadOnlyList<ElectricalBlock> Blocks = Build();

    public static readonly IReadOnlyList<ElectricalMeasure> Measures = Blocks.SelectMany(b => b.Measures).ToList();

    private static readonly Dictionary<string, System.Reflection.PropertyInfo> Properties =
        Measures.ToDictionary(m => m.Key, m => typeof(ElectricalTest).GetProperty(m.Key)!, StringComparer.OrdinalIgnoreCase);

    public static ElectricalMeasure? Find(string key) => Measures.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    public static decimal? Get(ElectricalTest t, string key) => (decimal?)Properties[key].GetValue(t);

    public static void Set(ElectricalTest t, string key, decimal? value) => Properties[key].SetValue(t, value);

    /// <summary>
    /// Ratio of a DAR or PI block (60 s ÷ 30 s, 10 min ÷ 1 min), cut after the second decimal, never rounded
    /// (engineers' rule: 1.5178 → 1.51, 1.5231 → 1.52). Null when a value is missing or the first one is zero.
    /// </summary>
    public static decimal? Ratio(ElectricalTest t, ElectricalBlock block)
    {
        if (block.RatioLabel is null) return null;
        var first = Get(t, block.Measures[0].Key);
        var second = Get(t, block.Measures[1].Key);
        return first is > 0 && second is not null ? Math.Truncate(second.Value / first.Value * 100) / 100 : null;
    }

    /// <summary>Required values not filled in yet (the PI block is optional).</summary>
    public static IEnumerable<ElectricalMeasure> Missing(ElectricalTest? t) =>
        Measures.Where(m => m.Required && (t is null || Get(t, m.Key) is null));

    /// <summary>Every required value is filled in.</summary>
    public static bool IsComplete(ElectricalTest? t) => t is not null && !Missing(t).Any();

    private static List<ElectricalBlock> Build()
    {
        ElectricalBlock Dar(string key, string title, string prefix) => new(key, title, "GΩ", true,
            [new($"{prefix}30", $"{title} 30 s", key, true), new($"{prefix}60", $"{title} 60 s", key, true)], "DAR Ratio");

        ElectricalBlock Pi(string key, string phases) => new(key, $"PI {phases}", "GΩ", false,
            [new($"{key}1", $"PI {phases} 1 min", key, false), new($"{key}10", $"PI {phases} 10 min", key, false)], "PI Ratio");

        return
        [
            new("Winding", "Winding resistance (DLR ohmmeter)", "Ω", true,
            [
                new(nameof(ElectricalTest.WrU1U2), "Winding U1-U2", "Winding", true),
                new(nameof(ElectricalTest.WrV1V2), "Winding V1-V2", "Winding", true),
                new(nameof(ElectricalTest.WrW1W2), "Winding W1-W2", "Winding", true),
            ], null),
            new("Insulation", "Insulation resistance IR (megger) at 1 kV", "Ω", true,
            [
                new(nameof(ElectricalTest.IrUV), "IR U-V", "Insulation", true),
                new(nameof(ElectricalTest.IrUW), "IR U-W", "Insulation", true),
                new(nameof(ElectricalTest.IrVW), "IR V-W", "Insulation", true),
                new(nameof(ElectricalTest.IrUG), "IR U-G", "Insulation", true),
                new(nameof(ElectricalTest.IrVG), "IR V-G", "Insulation", true),
                new(nameof(ElectricalTest.IrWG), "IR W-G", "Insulation", true),
            ], null),
            Dar("DarU", "U-VWE", "DarUVwe"),
            Dar("DarV", "V-UWE", "DarVUwe"),
            Dar("DarW", "W-UVE", "DarWUve"),
            Dar("DarUvw", "E-UVW", "DarUvwToE"),
            Pi("PiUV", "U-V"),
            Pi("PiVW", "V-W"),
            Pi("PiUW", "U-W"),
            Pi("PiUG", "U-G"),
            Pi("PiVG", "V-G"),
            Pi("PiWG", "W-G"),
        ];
    }
}
