using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Domain.Security;

namespace PresApp.Infrastructure.Seeding;

/// <summary>
/// Initial catalogs, taken from the SharePoint lists and the handover (Oct 2026).
/// "N/A" is written as "Not Applicable" everywhere (rule of 24-Oct-2026).
/// "Completed - Documentation Pending" no longer exists: documentation is tracked per gate.
/// </summary>
internal static class SeedData
{
    public const string NotApplicable = "Not Applicable";

    public static readonly (string Role, string Description, string[] Permissions)[] Roles =
    [
        (RoleNames.Viewer, "Read-only, can download Excel",
            [Permissions.AssetsView, Permissions.ExcelExport]),
        (RoleNames.FieldTechnician, "Readings, gate statuses and comments",
            [Permissions.AssetsView, Permissions.ExcelExport, Permissions.ReadingsWrite, Permissions.GatesEdit]),
        (RoleNames.Engineer, "Full preservation editing and bulk edit",
            [Permissions.AssetsView, Permissions.ExcelExport, Permissions.ReadingsWrite, Permissions.GatesEdit,
             Permissions.AssetsEdit, Permissions.AssetsMarkPreserved, Permissions.BulkEdit]),
        (RoleNames.Admin, "Everything, including Excel uploads, Simpro, lists and users",
            Permissions.All.Select(p => p.Key).ToArray()),
    ];

    /// <summary>(key, label, active values in order, inactive legacy values).</summary>
    public static readonly (string Key, string Label, string[] Active, string[] Inactive)[] OptionSets =
    [
        (OptionSetKeys.Priority, "Priority", ["Priority 1", "Priority 2", "Priority 3"], []),
        (OptionSetKeys.CurrentActivity, "Current Activity",
            [NotApplicable, "P-V Cycle", "Construction Activity", "Draining", "Drained out", "Drying", "Hydrotest",
             "Mechanical Work", "Maintenance"], []),
        (OptionSetKeys.PreservationStatus, "Preservation Status (ADNOC)",
            ["Yes", "No", "Yes with inspection remark", "Not available", "TBC"], []),
        (OptionSetKeys.N2BlanketStatus, "N2 Blanket Status",
            ["N2 Blanket Required", "N2 Blanketed", "TBC", NotApplicable], []),
        (OptionSetKeys.ThirdPartyInspection, "Third Party Inspection Status",
            ["L1 Passed", "L1 Passed with minor repair requirement", "L1 Failed – Level 2 Required",
             "Inspection completed, reconditioning in progress", "Under refurbishment", "Equipment to be replaced",
             "New Equipment", "Pending", "Not required", "Not done", "Not available", "Not Found / Not Assessed", "TBC"], []),
        (OptionSetKeys.G1BulkLiquidDrained, "G1 - Bulk liquid drained",
            ["Completed", "Ongoing", "Draining is required", "Not Confirmed", NotApplicable], []),
        (OptionSetKeys.GateStatus, "Gate Status",
            ["Completed", "Ongoing", "Hold", NotApplicable],
            ["Not Started", "Open", "Pending", "Not Verified", "Not Confirmed"]),
        (OptionSetKeys.AssetAvailability, "Asset Availability", ["Available", "TBC", "Removed from Site"], []),
        (OptionSetKeys.PreservedByEu, "Preserved by EU", ["Yes", "No", NotApplicable, "TBC"], []),
        (OptionSetKeys.EmWalkdown, "EM Walkdown", ["Completed", "Ongoing", NotApplicable], []),
        // Exact spellings of the remaining values come with the SharePoint migration.
        (OptionSetKeys.ActionBy, "Action By", ["ADNOC", "Enermech", "Petrofac"], []),
        (OptionSetKeys.ReadyForPreservation, "Ready For Preservation", ["Yes", "No"], []),
    ];

    /// <summary>(name, asset type, prefix, sync to Simpro, sort).</summary>
    public static readonly (string Name, AssetType Type, string? Prefix, bool Simpro, int Sort)[] Disciplines =
    [
        ("Static", AssetType.Equipment, "ST", true, 10),
        ("Rotating", AssetType.Equipment, null, true, 20),
        ("Package Mechanical", AssetType.Equipment, null, true, 30),
        ("Rotating / Mechanical", AssetType.Equipment, null, true, 40),
        ("Mechanical / Utility", AssetType.Equipment, null, true, 50),
        ("SU", AssetType.Equipment, null, true, 60),
        ("Electrical", AssetType.ElectricalInstrument, "EL", true, 70),
        ("E&I", AssetType.ElectricalInstrument, "EI", true, 80),
        ("Instrumentation", AssetType.ElectricalInstrument, null, true, 90),
        ("Piping", AssetType.Pipeline, "PL", false, 100),
    ];

    /// <summary>Plants and units with their Simpro tbl_locationBreakdown ids (schema export 24-Oct-2026).</summary>
    public static readonly (string Plant, Guid PlantId, (string Unit, int Number, Guid Id)[] Units)[] Plants =
    [
        ("Habshan - 0", Guid.Parse("32A13B4C-73F8-4783-831C-BED263EF738D"),
        [
            ("Unit 31", 31, Guid.Parse("8318C465-84DD-43D3-8970-BA467F2333F4")),
            ("Unit 32", 32, Guid.Parse("872ECD5D-59B7-49E0-AEE2-81DE5ADD4D4C")),
            ("Unit 39", 39, Guid.Parse("CAF9A94E-C285-48FE-A30F-6C8C28045EDA")),
            ("Unit 41", 41, Guid.Parse("9A0BE16C-4789-4829-AA1A-BEA173CF5697")),
            ("Unit 42", 42, Guid.Parse("7B94D04B-4046-4109-BCC0-E3CBF4F89719")),
        ]),
        ("Habshan - 5", Guid.Parse("D4BB74B6-911D-4371-8260-BD31CC0A7AAF"),
        [
            ("Unit 541", 541, Guid.Parse("8AAE2772-9EA8-4C38-B3F4-194067D96008")),
        ]),
    ];

    public static readonly MeasurementType[] Measurements =
    [
        new() { Key = "PressureBarg", Label = "Pressure (barg)", UnitOfMeasure = "barg", SortOrder = 10 },
        new() { Key = "G2DewPointC", Label = "G2 - Dew Point (C)", UnitOfMeasure = "°C", Gate = 2, EvidenceForGate = 2, SortOrder = 20 },
        new() { Key = "G3O2Pct", Label = "G3 - O2 (%)", UnitOfMeasure = "%", Gate = 3, EvidenceForGate = 3, MinValue = 0, MaxValue = 100, SortOrder = 30 },
        new() { Key = "G4N2BlanketBarg", Label = "G4 - N2 Blanket (barg)", UnitOfMeasure = "barg", Gate = 4, EvidenceForGate = 4, MinValue = 0, MaxValue = 100, SortOrder = 40 },
        new() { Key = "G6H2S", Label = "G6 - H2S", UnitOfMeasure = "ppm", Gate = 6, MinValue = 0, SortOrder = 50 },
        new() { Key = "G6LelPct", Label = "G6 - LEL (%)", UnitOfMeasure = "%", Gate = 6, MinValue = 0, MaxValue = 100, SortOrder = 60 },
    ];

    public static readonly string[] GroupTypes = ["Workpack", "Circuit", "System"];

    public static readonly (string Key, string Value, string Description)[] Settings =
    [
        (AppSettingKeys.ExcelMaxAgeHours, "48", "Maximum hours between exporting an Excel file and uploading it back"),
        (AppSettingKeys.SimproScheduleEnabled, "false", "Daily automatic sync to Simpro"),
        (AppSettingKeys.SimproScheduleTime, "05:00", "Local time (Dubai) of the daily sync"),
    ];

    /// <summary>Line numbering standard of the client (service designation).</summary>
    public static readonly (LineCodeKind Kind, string Code, string Description)[] LineCodes =
    [
        (LineCodeKind.Service, "ACS", "Chilled water supply"),
        (LineCodeKind.Service, "ACR", "Chilled water return"),
        (LineCodeKind.Service, "AD", "Area drain (surface drainage)"),
        (LineCodeKind.Service, "AG", "Acid gas"),
        (LineCodeKind.Service, "AGS", "Acid gas, sulphur"),
        (LineCodeKind.Service, "AGX", "Acid gas, sour"),
        (LineCodeKind.Service, "AV", "Ammonia"),
        (LineCodeKind.Service, "BA", "Breathing air"),
        (LineCodeKind.Service, "BD", "Blowdown"),
        (LineCodeKind.Service, "BFW", "Boiler feedwater"),
        (LineCodeKind.Service, "BG", "Blanket gas"),
        (LineCodeKind.Service, "BWS", "Boiler water sewer"),
        (LineCodeKind.Service, "CAC", "Caustic concentrated"),
        (LineCodeKind.Service, "CAD", "Caustic, dilute"),
        (LineCodeKind.Service, "CBA", "Combustion air"),
        (LineCodeKind.Service, "CCD", "Caustic closed drain"),
        (LineCodeKind.Service, "CD", "Closed drain"),
        (LineCodeKind.Service, "CDC", "Closed drain cryogenic"),
        (LineCodeKind.Service, "CG", "Chlorine gas"),
        (LineCodeKind.Service, "CH", "Chemical"),
        (LineCodeKind.Service, "CL", "Sodium hypochlorite"),
        (LineCodeKind.Service, "CW", "Cooling water"),
        (LineCodeKind.Service, "CWR", "Cooling water return"),
        (LineCodeKind.Service, "CWS", "Cooling water supply"),
        (LineCodeKind.Service, "DF", "Diesel fuel"),
        (LineCodeKind.Service, "DMW", "Demineralised water"),
        (LineCodeKind.Service, "FAG", "Acid gas flare"),
        (LineCodeKind.Service, "FD", "Fire deluge"),
        (LineCodeKind.Service, "FGL", "Low pressure fuel gas"),
        (LineCodeKind.Service, "FGM", "Medium pressure fuel gas"),
        (LineCodeKind.Service, "FGX", "Fuel gas sour"),
        (LineCodeKind.Service, "FL", "Flare"),
        (LineCodeKind.Service, "FLC", "Cold flare"),
        (LineCodeKind.Service, "FW", "Fire water"),
        (LineCodeKind.Service, "GD", "Glycol drain"),
        (LineCodeKind.Service, "GL", "Glycol"),
        (LineCodeKind.Service, "GLX", "Glycol, sour"),
        (LineCodeKind.Service, "HC", "Hydrocarbon"),
        (LineCodeKind.Service, "HCX", "Hydrocarbon, sour"),
        (LineCodeKind.Service, "HR", "HC drain to solvent recover"),
        (LineCodeKind.Service, "IA", "Instrument air"),
        (LineCodeKind.Service, "IW", "Irrigation water"),
        (LineCodeKind.Service, "LN", "Liquid nitrogen"),
        (LineCodeKind.Service, "LO", "Lube oil"),
        (LineCodeKind.Service, "LS", "Lean solvent (lean amine)"),
        (LineCodeKind.Service, "LSM", "Lean solvent (lean MDEA)"),
        (LineCodeKind.Service, "MOH", "Methanol"),
        (LineCodeKind.Service, "N2", "Nitrogen"),
        (LineCodeKind.Service, "OD", "Open drain"),
        (LineCodeKind.Service, "PA", "Plant air"),
        (LineCodeKind.Service, "PW", "Potable water"),
        (LineCodeKind.Service, "RF", "Refrigerant (propane)"),
        (LineCodeKind.Service, "RG", "Regeneration gas"),
        (LineCodeKind.Service, "RGX", "Regeneration gas (hot & wet/sour)"),
        (LineCodeKind.Service, "RSM", "Rich solvent, sour (rich MDEA)"),
        (LineCodeKind.Service, "RSX", "Rich solvent, sour (rich amine)"),
        (LineCodeKind.Service, "RW", "Raw water"),
        (LineCodeKind.Service, "SAC", "Sulphuric acid, concentrated"),
        (LineCodeKind.Service, "SAD", "Sulphuric acid, diluted"),
        (LineCodeKind.Service, "SC", "Steam condensate (saturated)"),
        (LineCodeKind.Service, "SCH", "High pressure steam condensate (cold)"),
        (LineCodeKind.Service, "SCL", "Low pressure steam condensate (cold)"),
        (LineCodeKind.Service, "SCM", "Medium pressure steam condensate"),
        (LineCodeKind.Service, "SD", "Solvent drain"),
        (LineCodeKind.Service, "SDM", "Solvent drain, MDEA"),
        (LineCodeKind.Service, "SE", "Sewage"),
        (LineCodeKind.Service, "SL", "Sulphur, liquid"),
        (LineCodeKind.Service, "SLH", "Superheated low pressure steam"),
        (LineCodeKind.Service, "SM", "Fresh solvent (fresh MDEA)"),
        (LineCodeKind.Service, "SO", "Seal oil"),
        (LineCodeKind.Service, "SSM", "SRU solvent"),
        (LineCodeKind.Service, "STH", "High pressure steam"),
        (LineCodeKind.Service, "STL", "Low pressure steam"),
        (LineCodeKind.Service, "STM", "Medium pressure steam"),
        (LineCodeKind.Service, "STS", "Superheated high pressure steam"),
        (LineCodeKind.Service, "SW", "Sour water"),
        (LineCodeKind.Service, "TAG", "Treated acid gas"),
        (LineCodeKind.Service, "TW", "Treated water"),
        (LineCodeKind.Service, "UW", "Utility water"),
        (LineCodeKind.Service, "UWR", "Utility water return"),
        (LineCodeKind.Service, "UWS", "Utility water supply"),
        (LineCodeKind.Service, "VE", "Vent"),
        (LineCodeKind.Service, "VT", "Vessel trim"),
        (LineCodeKind.Service, "WD", "Water disposal"),
        (LineCodeKind.Service, "WDX", "Water disposal, sour"),
        (LineCodeKind.Service, "WF", "Well fluid"),
        (LineCodeKind.Service, "WFX", "Well fluid, sour"),
        (LineCodeKind.Service, "WS", "Sweet water"),
    ];
}
