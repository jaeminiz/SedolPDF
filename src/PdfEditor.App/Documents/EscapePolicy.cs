namespace PdfEditor.App.Documents;

public enum EscapeAction
{
    /// <summary>Esc를 가로채지 않는다(키는 그대로 다른 곳으로 간다).</summary>
    None,
    /// <summary>그리고 있던 것(끌기·점 모으기·붙이기)만 취소한다. 도구와 선택은 그대로.</summary>
    CancelDrawing,
    /// <summary>선택(주석·쪽 객체)을 해제한다. 도구는 그대로.</summary>
    ClearSelection,
    /// <summary>주석 선택 도구로 돌아간다.</summary>
    SwitchToAnnotSelect,
}

/// <summary>
/// 문서 화면의 Esc 규칙 한 곳 (0.4.0, 결정 D6): 그리는 중이면 취소만 → 선택이 있으면 해제 → 다른 도구면 주석 선택 도구.
/// 편집 창·IME 조합·대화상자·찾아 바꾸기 패널 입력칸에서는 가로채지 않는다
/// (그 입력칸들은 키를 먼저 받아 스스로 처리하므로, 포커스가 입력칸이면 여기서도 <see cref="EscapeAction.None"/>).
/// </summary>
public static class EscapePolicy
{
    public static EscapeAction Decide(ToolKind tool, bool drawingInProgress, bool hasSelection, bool textEditorFocused)
    {
        if (textEditorFocused) return EscapeAction.None;
        if (drawingInProgress) return EscapeAction.CancelDrawing;
        if (hasSelection) return EscapeAction.ClearSelection;
        return tool == ToolKind.AnnotSelect ? EscapeAction.None : EscapeAction.SwitchToAnnotSelect;
    }
}
