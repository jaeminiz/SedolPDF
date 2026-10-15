using Pdf.Native;
using PdfEditor.Document.Saving;
using PdfEditor.Pages;
using PdfEditor.TestFixtures;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Document.Tests;

/// <summary>V6: PDFium 전체 저장 뒤에도 암호화·권한이 유지되는가. 결과는 docs/validation/2026-10-m1-results.md.</summary>
public class V6EncryptedSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfeditor-v6-" + Guid.NewGuid().ToString("N"));
    private readonly PdfiumThread _t = new();

    public V6EncryptedSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task V6_UserPasswordPdf_SaveAsCopy_StillRequiresPassword()
    {
        var p = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 595, 842, "secret");
        var outp = P("out.pdf");
        await using (var s = await DocumentSession.OpenAsync(_t, p, "secret"))
        {
            await s.Commands.ExecuteAsync(new InsertBlankPageCommand(1, 595, 842));
            await new SaveService([]).SaveAsync(s, outp);
        }
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => DocumentSession.OpenAsync(_t, outp, null));
        await using var again = await DocumentSession.OpenAsync(_t, outp, "secret");
        Assert.Equal(2, again.Pages.Length);
        Assert.True(again.IsEncrypted);
    }

    [Fact]
    public async Task V6_OwnerRestrictedPdf_SaveAsCopy_KeepsPermissions()
    {
        var p = P("own.pdf");
        TestPdfFactory.CreateOwnerRestricted(p, false, false, true);
        var outp = P("out.pdf");
        await using (var s = await DocumentSession.OpenAsync(_t, p, null))
            await new SaveService([]).SaveAsync(s, outp);
        await using var again = await DocumentSession.OpenAsync(_t, outp, null);
        Assert.True(again.IsEncrypted);
        Assert.False(again.Permissions.CanPrint);
    }

    [Fact]
    public async Task V6_PdfSharpModify_OnEncryptedTemp_KeepsEncryption()
    {
        var p = P("enc.pdf");
        TestPdfFactory.CreateEncrypted(p, 1, 595, 842, "secret");
        var outp = P("out.pdf");
        await using (var s = await DocumentSession.OpenAsync(_t, p, "secret"))
            await new SaveService([]).SaveAsync(s, outp);

        // M2 후처리 조사: 사용자 암호만으로는 PDFsharp Modify로 열 수 없다 (PDFium을 거치지 않은 원본도 같음).
        // 소유자 암호(테스트 픽스처: "secret-owner")를 알면 Modify로 열 수 있다.
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(p, "secret", PdfDocumentOpenMode.Modify));
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(outp, "secret", PdfDocumentOpenMode.Modify));

        // 소유자 암호를 알아도 PDFsharp Modify+Save는 암호화를 그대로 유지하지 않는다 (결과 문서 V6 참고).
        // 그래서 M2 후처리는 저장 전에 SecuritySettings(사용자/소유자 암호·권한)를 다시 지정해야 한다.
        var plain = P("plain.pdf");
        using (var doc = PdfReader.Open(outp, "secret-owner", PdfDocumentOpenMode.Modify))
        {
            doc.Info.Title = "changed";
            doc.Save(plain);
        }
        await using (var open = await DocumentSession.OpenAsync(_t, plain, null))
            Assert.False(open.IsEncrypted);

        var reapplied = P("reapplied.pdf");
        using (var doc = PdfReader.Open(outp, "secret-owner", PdfDocumentOpenMode.Modify))
        {
            doc.Info.Title = "changed";
            doc.SecuritySettings.UserPassword = "secret";
            doc.SecuritySettings.OwnerPassword = "secret-owner";
            doc.Save(reapplied);
        }
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => DocumentSession.OpenAsync(_t, reapplied, null));
        await using var again = await DocumentSession.OpenAsync(_t, reapplied, "secret");
        Assert.Single(again.Pages);
    }
}
