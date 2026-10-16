using System.Reflection;
using PdfEditor.App.Panels;
using PdfEditor.Document;

namespace PdfEditor.App.Tests.Viewer.Annotations;

public class ThumbnailAnnotationLayerTests
{
    [Fact]
    public void DataContextChange_ResetsCachedGeometry()
    {
        HeadlessUi.Run(() =>
        {
            var layer = new ThumbnailAnnotationLayer();
            var t = typeof(ThumbnailAnnotationLayer);
            var geom = t.GetField("_geom", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var req = t.GetField("_geomRequested", BindingFlags.NonPublic | BindingFlags.Instance)!;
            layer.DataContext = new ThumbnailItem(new PageId(1));
            geom.SetValue(layer, (System.Drawing.RectangleF.FromLTRB(0, 0, 10, 10), 1, new PageId(1), 0));
            req.SetValue(layer, (new PageId(1), 0));
            layer.DataContext = new ThumbnailItem(new PageId(2));   // 컨테이너 재사용
            Assert.Null(geom.GetValue(layer));
            Assert.Null(req.GetValue(layer));
            return 0;
        });
    }
}
