using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace Pdf.Native.Tests;

public class DocumentInfoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public DocumentInfoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Unencrypted_PermissionsAllTrue_NotEncrypted()
    {
        TestPdfFactory.CreateBlank(P("a.pdf"), 1, 595, 842);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("a.pdf"));
            Assert.False(doc.IsEncrypted);
            Assert.Equal(0xFFFFFFFFu, doc.Permissions.Raw);
            Assert.True(doc.Permissions.CanPrint);
            Assert.True(doc.Permissions.CanModify);
            Assert.True(doc.Permissions.CanCopy);
            Assert.NotNull(doc.FileVersion);
        });
    }

    [Fact]
    public async Task OwnerRestricted_NoPrintNoModify()
    {
        TestPdfFactory.CreateOwnerRestricted(P("o.pdf"), false, false, true);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("o.pdf"));
            Assert.True(doc.IsEncrypted);
            Assert.False(doc.Permissions.CanPrint);
            Assert.False(doc.Permissions.CanEditPages);
            Assert.True(doc.Permissions.CanCopy);
        });
    }

    [Fact]
    public async Task UserPassword_OpenedWithUserPassword_Permissions()
    {
        TestPdfFactory.CreateEncrypted(P("u.pdf"), 1, 595, 842, "pw1234");
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("u.pdf"), "pw1234");
            Assert.True(doc.IsEncrypted);
            Console.WriteLine($"[V] user-password doc: Raw=0x{doc.Permissions.Raw:X8} rev={doc.Permissions.SecurityRevision}");
        });
    }

    [Fact]
    public async Task ValidFile_HasValidXref_True()
    {
        TestPdfFactory.CreateBlank(P("a.pdf"), 1, 595, 842);
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("a.pdf"));
            Assert.True(doc.HasValidCrossReferenceTable);
        });
    }

    [Fact]
    public async Task CorruptedXref_OpensRepaired_HasValidXrefFalse()
    {
        TestPdfFactory.CreateBlank(P("c.pdf"), 2, 595, 842);
        TestPdfFactory.CorruptXref(P("c.pdf"));
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("c.pdf"));
            Assert.Equal(2, doc.PageCount);
            Assert.False(doc.HasValidCrossReferenceTable);
        });
    }

    [Fact]
    public async Task MetaText_Title_RoundTrips()
    {
        using (var d = new PdfSharp.Pdf.PdfDocument())
        {
            d.AddPage();
            d.Info.Title = "도면 제목";
            d.Save(P("m.pdf"));
        }
        await _t.InvokeAsync(() =>
        {
            using var doc = PdfDocument.Open(_t, P("m.pdf"));
            Assert.Equal("도면 제목", doc.GetMetaText("Title"));
            Assert.Equal("", doc.GetMetaText("Author"));
        });
    }
}
