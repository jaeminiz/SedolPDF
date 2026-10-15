namespace Pdf.Native;

/// <summary>
/// 렌더 옵션. HiddenAnnotSubtypes에 든 유형의 주석은 렌더하는 동안에만 Hidden(2) 플래그를 붙여 그리지 않는다
/// (우리 모델이 직접 그리는 유형을 PDFium이 중복해서 그리지 않게 함, 설계서 4.2-4).
/// </summary>
public sealed record RenderOptions(bool WithAnnotations, IReadOnlySet<int>? HiddenAnnotSubtypes = null);
