using PdfEditor.App.Documents;

namespace PdfEditor.App.Tests.Documents;

public sealed class ErrorLogTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfeditor-errlog-" + Guid.NewGuid().ToString("N"));
    private readonly string _saved = ErrorLog.Path;

    public void Dispose()
    {
        ErrorLog.Path = _saved;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Write_AppendsAreaAndException_CreatingTheFolder()
    {
        ErrorLog.Path = System.IO.Path.Combine(_dir, "sub", "error.log");
        ErrorLog.Write("reduce", new InvalidOperationException("boom-1"));
        ErrorLog.Write("estimate", new InvalidOperationException("boom-2"));
        string text = File.ReadAllText(ErrorLog.Path);
        Assert.Contains("reduce:", text);
        Assert.Contains("boom-1", text);
        Assert.Contains("estimate:", text);
        Assert.Contains("boom-2", text);
    }

    [Fact]
    public void Write_ToAnUnwritablePath_DoesNotThrow()
    {
        ErrorLog.Path = System.IO.Path.Combine(_dir, "con:", "x?.log");
        ErrorLog.Write("reduce", new InvalidOperationException("ignored"));
    }
}
