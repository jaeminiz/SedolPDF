using PdfEditor.App.Printing;

namespace PdfEditor.App.Tests.Printing;

/// <summary>쪽 범위 계산·모아 찍기 계획·옵션 직렬화(순수 로직).</summary>
public class PrintOptionsLogicTests
{
    private static int[] Resolve(PrintOptions o, int count = 10, int current = 3, int[]? selected = null)
    {
        Assert.True(PrintPageSelection.TryResolve(o, count, current, selected ?? [], out var r));
        return r;
    }

    [Fact]
    public void Range_All_Current_Selected()
    {
        Assert.Equal(Enumerable.Range(0, 10), Resolve(new PrintOptions()));
        Assert.Equal([3], Resolve(new PrintOptions(Range: PrintRangeMode.Current)));
        Assert.Equal([9], Resolve(new PrintOptions(Range: PrintRangeMode.Current), current: 99));
        Assert.Equal([1, 4, 6], Resolve(new PrintOptions(Range: PrintRangeMode.Selected), selected: [6, 1, 4, 4]));
        Assert.Empty(Resolve(new PrintOptions(Range: PrintRangeMode.Selected)));
    }

    [Fact]
    public void Range_Custom_KeepsInputOrder_AndRejectsBadText()
    {
        Assert.Equal([0, 1, 2, 4], Resolve(new PrintOptions(Range: PrintRangeMode.Custom, RangeText: "1-3, 5")));
        Assert.Equal([4, 0, 1], Resolve(new PrintOptions(Range: PrintRangeMode.Custom, RangeText: "5,1-2")));
        foreach (var bad in new[] { "", "  ", "0", "11", "3-1", "a", "1,,2" })
        {
            Assert.False(PrintPageSelection.TryResolve(new PrintOptions(Range: PrintRangeMode.Custom, RangeText: bad), 10, 0, [], out var r), bad);
            Assert.Empty(r);
        }
    }

    [Fact]
    public void Parity_FiltersByPageNumber()
    {
        Assert.Equal([0, 2, 4, 6, 8], Resolve(new PrintOptions(Parity: PrintParity.Odd)));
        Assert.Equal([1, 3, 5, 7, 9], Resolve(new PrintOptions(Parity: PrintParity.Even)));
        // 선택 3·4·5쪽 + 홀수만 = 3쪽·5쪽
        Assert.Equal([2, 4], Resolve(new PrintOptions(Range: PrintRangeMode.Selected, Parity: PrintParity.Odd), selected: [2, 3, 4]));
        Assert.Equal([1, 3], Resolve(new PrintOptions(Range: PrintRangeMode.Custom, RangeText: "2-4", Parity: PrintParity.Even)));
    }

    [Fact]
    public void Plan_NUp_ChunksPages_AndPadsLastSheet()
    {
        var s = PrintPlan.Build([0, 1, 2, 3, 4], 2, 1, true, false);
        Assert.Equal(3, s.Count);
        Assert.Equal([0, 1], s[0].Cells);
        Assert.Equal([4, null], s[2].Cells);
        Assert.Equal(5, PrintPlan.CountPages(s));
        Assert.Equal(2, PrintPlan.Build([0, 1, 2, 3, 4], 4, 1, true, false).Count);
    }

    [Fact]
    public void Plan_SoftwareCopies_CollatedAndUncollated()
    {
        var c = PrintPlan.Build([0, 1], 1, 2, collate: true, duplex: false);
        Assert.Equal([0, 1, 0, 1], c.Select(x => x.Cells[0]!.Value));
        var u = PrintPlan.Build([0, 1], 1, 2, collate: false, duplex: false);
        Assert.Equal([0, 0, 1, 1], u.Select(x => x.Cells[0]!.Value));
    }

    [Fact]
    public void Plan_Duplex_ForcesCollate_AndPadsOddSets()
    {
        var p = PrintPlan.Build([0, 1, 2], 1, 2, collate: false, duplex: true);
        // 3장짜리 한 부 뒤에 빈 장이 끼어 두 번째 부가 새 용지에서 시작한다
        Assert.Equal(7, p.Count);
        Assert.Null(p[3].Cells[0]);
        Assert.Equal([0, 1, 2], p.Skip(4).Select(x => x.Cells[0]!.Value));
        Assert.Equal(6, PrintPlan.CountPages(p));
        Assert.Empty(PrintPlan.Build([], 1, 3, true, true));
    }

    [Fact]
    public void Layout_Grid_AndCells_CoverArea()
    {
        Assert.Equal((2, 1), PrintLayout.Grid(2, true));
        Assert.Equal((1, 2), PrintLayout.Grid(2, false));
        Assert.Equal((2, 2), PrintLayout.Grid(4, false));
        int total = 0;
        for (int i = 0; i < 4; i++) { var c = PrintLayout.Cell(i, 4, false, 1001, 1501); total += c.W * c.H; }
        Assert.Equal(1001 * 1501, total);
    }

    [Fact]
    public void Layout_Orientation_Auto_FollowsFirstPage_AndTwoUpFlips()
    {
        Assert.False(PrintLayout.IsLandscapeSheet(PrintOrientation.Auto, 1, 595, 842));
        Assert.True(PrintLayout.IsLandscapeSheet(PrintOrientation.Auto, 1, 842, 595));
        Assert.True(PrintLayout.IsLandscapeSheet(PrintOrientation.Auto, 2, 595, 842));
        Assert.True(PrintLayout.IsLandscapeSheet(PrintOrientation.Landscape, 1, 595, 842));
        Assert.False(PrintLayout.IsLandscapeSheet(PrintOrientation.Portrait, 1, 842, 595));
    }

    [Fact]
    public void Layout_Percent_And_NoCenter()
    {
        const int PW = 4960, PH = 7016, Dpi = 600;
        var p = PrintLayout.Compute(595.28f, 841.89f, PW, PH, Dpi, Dpi, new PrintOptions(Scaling: PrintScaling.Percent, ScalePercent: 50, Center: false));
        Assert.Equal(0, p.X);
        Assert.Equal(0, p.Y);
        Assert.InRange(p.Width, PW / 2 - 2, PW / 2 + 2);
        var c = PrintLayout.Compute(595.28f, 841.89f, PW, PH, Dpi, Dpi, new PrintOptions(Scaling: PrintScaling.Percent, ScalePercent: 50));
        Assert.InRange(c.X * 2 + c.Width, PW - 2, PW + 2);
    }

    [Fact]
    public void Json_RoundTrips_AllFields()
    {
        var o = new PrintOptions(false, PrintScaling.Percent, false, PrintRangeMode.Custom, "1-3, 5", PrintParity.Odd, "Foo Printer", 3, false,
            PrintDuplex.ShortEdge, PrintOrientation.Landscape, 9, 80, false, 4, true);
        var back = PrintOptionsJson.TryDeserialize(PrintOptionsJson.Serialize(o));
        Assert.Equal(o, back);
        Assert.Contains("ShortEdge", PrintOptionsJson.Serialize(o));
    }

    [Fact]
    public void Json_BadInput_ReturnsNull_AndClampsValues()
    {
        Assert.Null(PrintOptionsJson.TryDeserialize(null));
        Assert.Null(PrintOptionsJson.TryDeserialize("{nope"));
        Assert.Null(PrintOptionsJson.TryDeserialize("{\"Duplex\":\"Sideways\"}"));
        var o = PrintOptionsJson.TryDeserialize("{\"Copies\":5000,\"ScalePercent\":1,\"PagesPerSheet\":3}")!;
        Assert.Equal(PrintOptions.MaxCopies, o.Copies);
        Assert.Equal(PrintOptions.MinPercent, o.ScalePercent);
        Assert.Equal(1, o.PagesPerSheet);
        // 옛 설정(필드 일부만)도 읽힌다
        Assert.Equal(PrintScaling.ActualSize, PrintOptionsJson.TryDeserialize("{\"Scaling\":\"ActualSize\"}")!.Scaling);
    }

    [Fact]
    public void Json_ForRemembering_DropsRangeAndPrinter()
    {
        var r = PrintOptionsJson.ForRemembering(new PrintOptions(Range: PrintRangeMode.Custom, RangeText: "2", Parity: PrintParity.Even, PrinterName: "X", Copies: 2));
        Assert.Equal(PrintRangeMode.All, r.Range);
        Assert.Equal("", r.RangeText);
        Assert.Null(r.PrinterName);
        Assert.Equal(PrintParity.All, r.Parity);
        Assert.Equal(2, r.Copies);
    }
}

public class PrintOptionsStoreTests
{
    [Fact]
    public void Store_RoundTrips_DropsRange_AndSurvivesCorruptFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pos-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(dir, "sub", "print-options.json");
            var store = new PrintOptionsStore(file);
            Assert.Equal(new PrintOptions(), store.Load());   // 파일 없음 → 기본값
            store.Save(new PrintOptions(Copies: 3, Range: PrintRangeMode.Custom, RangeText: "2", PrinterName: "X", Grayscale: true));
            var back = store.Load();
            Assert.Equal(3, back.Copies);
            Assert.True(back.Grayscale);
            Assert.Equal(PrintRangeMode.All, back.Range);
            Assert.Null(back.PrinterName);
            File.WriteAllText(file, "{broken");
            Assert.Equal(new PrintOptions(), store.Load());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
