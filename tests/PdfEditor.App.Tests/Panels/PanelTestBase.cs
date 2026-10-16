using Pdf.Native;
using PdfEditor.App.Documents;
using PdfEditor.Document;

namespace PdfEditor.App.Tests.Panels;

/// <summary>패널 모델 테스트 공통: 전용 스레드, 임시 폴더, 세션을 열어 탭을 만든다.</summary>
public abstract class PanelTestBase : IAsyncLifetime
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "pdfeditor-pn-" + Guid.NewGuid().ToString("N"));
    protected readonly PdfiumThread Thread = new();
    private readonly List<DocumentSession> _sessions = [];

    public Task InitializeAsync() { Directory.CreateDirectory(Dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions) await s.DisposeAsync();
        Thread.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }

    protected async Task<DocumentTabViewModel> OpenTab(string path, string? password = null)
    {
        var s = await DocumentSession.OpenAsync(Thread, path, password);
        _sessions.Add(s);
        return new DocumentTabViewModel(s, s.DisplayName, null);
    }
}
