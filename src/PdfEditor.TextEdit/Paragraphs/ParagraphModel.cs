using System.Collections.Immutable;
using Pdf.Native;
using PdfEditor.Document.Annotations;
using PdfEditor.Document.Content;

namespace PdfEditor.TextEdit.Paragraphs;

/// <summary>
/// 문단 추정의 모든 상수(M5 계획 C3·C4, Task 1 확정값). 시험(<c>ParagraphOptionsTests</c>)이 기본값을 고정한다.
/// 길이는 글자 크기(em) 배수, 신뢰도 가감은 0~1 점수 단위.
/// </summary>
public sealed record ParagraphOptions
{
    // ---- 안전 문턱 (C4, V17: 합성 0.60~0.65 구간이 정밀도 100 %·표 병합 0, 실제 도면 목록 오탐 최대 0.59)
    public double Threshold { get; init; } = 0.62;

    // ---- 줄 조립 (C3 ①)
    public double BaselineTolEm { get; init; } = 0.15;      // 같은 기준선
    public double SpaceGapEm { get; init; } = 0.25;         // 글 객체 사이 틈 ≥ 이 값이면 공백 (0.12는 낱말을 쪼갰음)
    public double CellGapEm { get; init; } = 1.0;           // 이보다 큰 틈 = 다른 칸
    public double BulletGapEm { get; init; } = 3.0;         // 글머리표 마커 뒤 틈 허용
    public int BulletMaxChars { get; init; } = 3;           // 글머리표 마커 최대 글자 수
    public double DuplicateTolEm { get; init; } = 0.12;     // 겹쳐 찍은 같은 글 판정 거리
    public double SizeTol { get; init; } = 0.03;            // 같은 글자 크기 ±3 %
    public double TzTol { get; init; } = 0.01;              // 같은 가로 비율(1 %p/비율 차)
    public double VerticalRuleMaxWidth { get; init; } = 2.5;// 가는 세로선 최대 두께(pt)
    public double TableColumnEm { get; init; } = 12;        // 표 행 판정: 같은 열 글 폭 중앙값 ≤ 12 em
    public int TableColumnMinSamples { get; init; } = 3;

    // ---- 문단 묶기 (C3 ②)
    public double MinPitchEm { get; init; } = 1.0;
    public double MaxPitchEm { get; init; } = 2.2;
    public double PitchTol { get; init; } = 0.25;           // 첫 간격 대비 ±25 %
    public double NeighborOverlapFrac { get; init; } = 0.3; // 이웃 줄 가로 겹침 비율(짧은 줄 폭 대비)
    public double NeighborMinPitchEm { get; init; } = 0.3;  // 같은 기준선으로 보는 최소 세로 거리
    public double AlignTolEm { get; init; } = 0.5;
    public double FirstLineIndentMaxEm { get; init; } = 4;  // 들여쓴 첫 줄 허용 폭
    public double IndentStartsParagraphEm { get; init; } = 0.8; // 이보다 들여쓰면 새 문단 첫 줄
    public double ShortLineFrac { get; init; } = 0.5;       // 열 폭 이하 비율인 짧은 줄 = 문단 끝
    public double ShortLineMinColumnEm { get; init; } = 12; // 열 폭이 이보다 좁으면 짧은 줄 규칙 적용 안 함
    public double ShortLastLineEm { get; init; } = 2.5;     // 마지막(첫) 줄이 본문 줄 끝보다 이만큼 짧으면 문단 끝
    public double ColumnNearEm { get; init; } = 4;          // "같은 열" 왼쪽 끝 거리

    // ---- 신뢰도 점수 (C4)
    public double ScoreLines2 { get; init; } = 0.55;
    public double ScoreLines3 { get; init; } = 0.72;
    public double ScoreLines4 { get; init; } = 0.80;
    public double ScoreLines5Plus { get; init; } = 0.85;
    public double PitchCvTight { get; init; } = 0.02;
    public double PitchCvLoose { get; init; } = 0.06;
    public double BonusPitchTight { get; init; } = 0.07;
    public double BonusPitchLoose { get; init; } = 0.03;
    public double PenaltyPitchUneven { get; init; } = 0.10;
    public double PitchOkMinEm { get; init; } = 1.1;
    public double PitchOkMaxEm { get; init; } = 1.9;
    public double PenaltyPitchOutOfRange { get; init; } = 0.10;
    public double TwoLinePitchMaxEm { get; init; } = 1.6;
    public double PenaltyTwoLineWidePitch { get; init; } = 0.15;
    public double BonusJustified { get; init; } = 0.08;
    public double BonusFullLines { get; init; } = 0.10;     // × 열 폭을 채운 앞줄 비율
    public double FullLineSlackEm { get; init; } = 1.2;
    public double ShortAvgEm { get; init; } = 10;
    public double PenaltyShortAvg { get; init; } = 0.25;
    public double NarrowAvgEm { get; init; } = 15;
    public double PenaltyNarrowAvg { get; init; } = 0.10;
    public int FewWordsMax { get; init; } = 3;
    public double PenaltyFewWords { get; init; } = 0.10;
    public double ListCapitalFrac { get; init; } = 0.6;     // 이음 줄이 대문자로 시작하는 라틴 글 비율
    public double AllCapsFrac { get; init; } = 0.8;
    public double PenaltyListLike { get; init; } = 0.25;
    public int CenterListMinLines { get; init; } = 4;
    public double PenaltyCenterList { get; init; } = 0.30;
    public double TinyAvgEm { get; init; } = 4;
    public double TinyAvgCap { get; init; } = 0.3;
    // Task 2에서 실제 6~10쪽을 보고 추가한 목록 특징(항목 나열이 문단으로 묶이는 오탐: 매개변수 목록·"2 sets …" 목록·연락처 블록)
    public int RepeatedPrefixMinLines { get; init; } = 3;   // 줄이 이 수 이상일 때
    public int RepeatedPrefixMinCount { get; init; } = 2;   // 같은 첫 낱말이 최소 이만큼
    public double RepeatedPrefixFrac { get; init; } = 0.6;  // 줄 첫 낱말(숫자는 #로 본 모양)이 같은 줄의 비율
    public int PrefixTokenMaxChars { get; init; } = 12;
    public double PenaltyRepeatedPrefix { get; init; } = 0.55;
    public double LabelLineFrac { get; init; } = 0.5;       // "라벨 : 값" 꼴로 시작하는 줄의 비율
    public int LabelMaxChars { get; init; } = 40;           // 라벨(콜론 앞) 최대 글자 수
    public double PenaltyLabelLines { get; init; } = 0.5;

    // ---- 아래 성장 한계 (C6)
    public double GrowthIgnoreBelowEm { get; init; } = 0.5; // 마지막 줄 기준선보다 이만큼 이상 아래 있는 장애물만
}

/// <summary><see cref="ParagraphModel.LowConfidenceReason"/> 코드. 화면 문구는 UI가 리소스에서 고른다.</summary>
public static class ParagraphReasons
{
    public const string TableRow = "table-row";            // 같은 기준선에 칸이 여럿 → 표 안의 글
    public const string MixedStyle = "mixed-style";        // 한 줄에 글꼴·크기·색이 섞임
    public const string LowConfidence = "low-confidence";  // 묶을 수는 있으나 확신이 문턱 미만
}

public enum Alignment { Left, Center, Right, Justified }

/// <summary>문단의 글 스타일(첫 줄 첫 글 객체 기준). 글꼴·크기·색·가로 비율이 모두 같은 줄만 한 문단이 된다.</summary>
public sealed record ParagraphStyle(string FontKey, float SizePt, PdfColor Color, float Tz);

/// <summary>
/// 추정된 문단. <see cref="Lines"/>는 위→아래 순서. 한 줄짜리면 한 줄 편집(M4)으로 되돌아가라는 뜻이고,
/// 이유가 있으면 <see cref="LowConfidenceReason"/>에 코드가 들어 있다(없으면 이웃이 없는 외톨이 줄).
/// <see cref="MaxBottom"/>은 줄 방향 기준 로컬 세로 좌표(v, 위쪽이 큼)에서 이 문단 아래에 처음 만나는 장애물의 위쪽 끝
/// (없으면 <see cref="float.NegativeInfinity"/>) — C6(겹침 허용)이므로 막지 않고 알림에만 쓴다. 0° 글이면 쪽 y와 같다.
/// </summary>
public sealed record ParagraphModel(ImmutableArray<TextLine> Lines, PdfRectF Box, ParagraphStyle Style, Alignment Align,
    float FirstLineIndent, float Leading, float MaxBottom, double Confidence, string? LowConfidenceReason)
{
    /// <summary>두 줄 이상이고 문턱을 넘은(또는 사용자가 줄을 더해 만든) 진짜 문단인가.</summary>
    public bool IsParagraph => Lines.Length >= 2 && LowConfidenceReason is null;

    public IEnumerable<ObjectRef> AllRefs => Lines.SelectMany(l => l.AllRefs);
}
