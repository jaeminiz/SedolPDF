using System.Collections.Immutable;
using Pdf.Native;

namespace PdfEditor.Document.Annotations.Patching;

/// <summary>임시 파일의 주석 딕셔너리에 하는 한 가지 고치기. 키는 슬래시를 붙여 쓴다("/RD").</summary>
public abstract record PatchOp;

public sealed record SetName(string Key, string Value) : PatchOp;
public sealed record SetNumbers(string Key, ImmutableArray<float> Values) : PatchOp;
public sealed record SetNames(string Key, ImmutableArray<string> Values) : PatchOp;
public sealed record SetString(string Key, string Value) : PatchOp;
public sealed record SetInteger(string Key, int Value) : PatchOp;

/// <summary>/BE &lt;&lt;/S /C /I n&gt;&gt; (클라우드 테두리)</summary>
public sealed record SetBorderEffect(float Intensity) : PatchOp;

public sealed record RemoveKeys(ImmutableArray<string> Keys) : PatchOp;

/// <summary>이미 /Popup이 있으면 아무것도 하지 않는다. 없으면 Popup을 끝에 붙이고 서로 연결한다.</summary>
public sealed record EnsurePopup(PdfRectF Rect, bool Open) : PatchOp;

/// <summary>/IRT를 /NM이 ParentNm인 같은 쪽 주석으로 건다(+ /RT).</summary>
public sealed record SetInReplyTo(string ParentNm, string? ReplyType) : PatchOp;

/// <summary>/AP /N을 새 폼으로 교체한다(기존 /Resources는 합친다).</summary>
public sealed record SetAppearanceStream(string Content, PdfRectF BBox) : PatchOp;

public sealed record AddApFont(string ResourceName, FontUse Font) : PatchOp;
public sealed record AddApImage(string ResourceName, string ImageId) : PatchOp;
public sealed record EmbedFile(string FileName, string AttachmentId) : PatchOp;

public sealed record FontUse(ImmutableArray<int> Codepoints, ImmutableArray<GlyphUse> Glyphs);

public readonly record struct GlyphUse(int Cid, int Advance1000, string Text);
