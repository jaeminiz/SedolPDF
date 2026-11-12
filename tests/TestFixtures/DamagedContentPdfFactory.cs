using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfEditor.TestFixtures;

/// <summary>
/// 0.10.0 Task 2 시험용: 0.8.0 이전 저장이 망가뜨린 쪽(2026-11-10 사용자 PDF 4쪽 모양)과 멀쩡한 같은 그림.
/// 망가진 쪽은 스트림 여섯 개로 나뉜다. 셋째 스트림은 자기 그림을 q…Q로 마치고 나머지 쪽을 위한 쪽 뒤집기 `cm`으로 끝난다.
/// 넷째 스트림 앞의 `S Q`는 이미 그려진 길의 꼬리 조각이라 그 `cm`을 꺼 버린다 → 파란 선이 뒤집히지 않고 그려진다.
/// </summary>
public static class DamagedContentPdfFactory
{
    public static void Create(string path, bool damaged)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        string[] streams = damaged
            ?
            [
                "q",
                "q q",
                "q 1 0 0 RG 2 w .75 0 0 -.75 0 841.92 cm 100 100 m 500 100 l S Q .75 0 0 -.75 0 841.92 cm",
                "S Q q 0 0 1 RG 2 w 100 300 m 500 300 l S Q",
                "Q",
                "Q Q",
            ]
            : ["q .75 0 0 -.75 0 841.92 cm q 1 0 0 RG 2 w 100 100 m 500 100 l S Q q 0 0 1 RG 2 w 100 300 m 500 300 l S Q Q"];
        foreach (var s in streams) page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(s));
        doc.Save(path);
    }
}
