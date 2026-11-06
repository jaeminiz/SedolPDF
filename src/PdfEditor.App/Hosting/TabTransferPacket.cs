using PdfEditor.App.Settings;
using PdfEditor.Document;

namespace PdfEditor.App.Hosting;

/// <summary>다른 창으로 옮기는 탭: 닫지 않은 세션 그대로와 보던 쪽·배율(0.8.0 여러 창 설계 5).</summary>
public sealed record TabTransferPacket(DocumentSession Session, ViewStateEntry? View);
