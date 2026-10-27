using ClosedXML.Excel;

namespace PresApp.Tests.Support;

/// <summary>A small template with the same structure as the client ones (MasterList, Dashboard block, Categories).</summary>
public static class TestWorkbooks
{
    public static readonly string[] Headers =
    [
        "SR", "OPERATING UNIT", "Discipline", "EQUIP NO", "EQUIP DESCRIPTION", "Third Party Inspection Status", "Pre-Assessment By OEM",
        "Preservation", "OEM Name", "Remark", "Gap Anaysis Remark", "Walkdown EM/PF/CPY", "G1-Status", "G2-Status", "G3-Status", "G4-Status",
        "Preserved by EU", "Preservation ", "Action By", "Expected Dispatch date", "Response", "Category"
    ];

    private const string CategoryFormula =
        "IFERROR(INDEX(Categories[Category],MATCH(_xlfn.AGGREGATE(15,6,Categories[Priority]/(ISNUMBER(SEARCH(Categories[Keyword],TRIM($S4)))*(Categories[Keyword]<>\"\")),1),Categories[Priority],0)),\"\")";

    private const string Count =
        "COUNTIFS(MasterList[Discipline],\"<>Electrical\",MasterList[Discipline],\"<>Instrumentation\",MasterList[Discipline],\"<>E&I\",MasterList[Category],TRIM(B{0}))";

    public static byte[] Template()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Master_List__{UNIT}");
        ws.Cell("B2").Value = "Master Equipment list- Preservation-Unit-{UNIT}      Date: {DATE}";
        for (var i = 0; i < Headers.Length; i++) ws.Cell(3, 2 + i).Value = Headers[i];
        ws.Cell(4, 2 + Headers.Length - 1).FormulaA1 = CategoryFormula;
        ws.Range(3, 2, 4, 1 + Headers.Length).CreateTable("MasterList");
        ws.Cell("B6").Value = "Notes:";

        var d = wb.AddWorksheet("Dashboard");
        d.TabSelected = true;   // like the client templates: saved with the dashboard selected too
        d.Cell("B2").Value = "UNIT-{UNIT} PRESERVATION DASHBOARD - DATED:{DATE}";
        d.Cell("B5").Value = "Walkdown";
        d.Cell("G5").FormulaA1 = "COUNTIFS(MasterList[Discipline],\"<>Electrical\",MasterList[Discipline],\"<>Instrumentation\",MasterList[Discipline],\"<>E&I\",MasterList[EQUIP NO],\"<>\")";
        d.Cell("B18").Value = "Hold Category "; d.Cell("C18").Value = "Tag "; d.Cell("D18").Value = "Action By"; d.Cell("E18").Value = "Email Action ";
        d.Cell("B19").Value = "Cat1 :Conflict Between DNV and EnerMech"; d.Cell("C19").FormulaA1 = string.Format(Count, 19); d.Cell("D19").Value = "PF/DNV"; d.Cell("E19").Value = "Done(16 Oct 26)";
        d.Cell("B20").Value = "Cat 4 Under integrity Testing /Hydro Test"; d.Cell("C20").FormulaA1 = string.Format(Count, 20); d.Cell("D20").Value = "CPY Ops/PF";
        d.Cell("B21").Value = "Total -1"; d.Cell("C21").FormulaA1 = "SUM(C19:C20)";
        d.Cell("B22").Value = "N/A Category "; d.Cell("C22").Value = "Tag ";
        d.Cell("B23").Value = "Cat1 :Removed from Site"; d.Cell("C23").FormulaA1 = string.Format(Count, 23); d.Cell("D23").Value = "Info";
        d.Cell("B24").Value = "Total -2"; d.Cell("C24").FormulaA1 = "SUM(C23:C23)";
        d.Cell("B25").Value = "Grant Total "; d.Cell("C25").FormulaA1 = "C21+C24";
        d.Cell("B26").Value = "Total Equipment Tags(Static and Rotating)"; d.Cell("C26").FormulaA1 = "G5";
        d.Cell("B27").Value = "Available for Preservation "; d.Cell("C27").FormulaA1 = "C26-C25";
        d.Cell("B18").Style.Font.Bold = true;

        var c = wb.AddWorksheet("Categories");
        string[] ch = ["Priority", "Category", "Keyword", "Section"];
        for (var i = 0; i < ch.Length; i++) c.Cell(9, 2 + i).Value = ch[i];
        object[][] rows =
        [
            [1, "Cat1 :Removed from Site", "Removed from site", "N/A"],
            [3, "Cat1 :Conflict Between DNV and EnerMech", "Conflict", "Hold"],
            [6, "Cat 4 Under integrity Testing /Hydro Test", "hydro test", "Hold"],
            [6, "Cat 4 Under integrity Testing /Hydro Test", "Integrity testing", "Hold"],
        ];
        for (var r = 0; r < rows.Length; r++)
            for (var i = 0; i < 4; i++) c.Cell(10 + r, 2 + i).Value = XLCellValue.FromObject(rows[r][i]);
        c.Range(9, 2, 9 + rows.Length, 5).CreateTable("Categories");

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Opens a workbook, lets the test edit it and returns the saved bytes.</summary>
    public static byte[] Edit(byte[] content, Action<XLWorkbook> edit)
    {
        using var wb = new XLWorkbook(new MemoryStream(content));
        edit(wb);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static IXLTable Master(XLWorkbook wb) => wb.Worksheets.SelectMany(w => w.Tables).First(t => t.Name == "MasterList");

    /// <summary>The data row whose EQUIP NO is <paramref name="tag"/>.</summary>
    public static IXLTableRow Row(XLWorkbook wb, string tag) =>
        Master(wb).DataRange.Rows().First(r => r.Field("EQUIP NO").GetString() == tag);
}
