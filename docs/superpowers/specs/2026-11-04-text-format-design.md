# 글자 형식 보기·바꾸기 설계 (0.6.0)

## 배경
- 속성 패널은 글 객체에 대해 앱 글꼴 줄의 크기·색만 바꾼다. 원본 PDF의 글은 위치·크기(경계)만 보이고 글꼴·굵기·색은 읽지 않는다.
- 사용자 검수(2026-11-04): 글을 고칠 때 굵게가 사라져도 이를 보거나 고칠 방법이 없다. PDF-XChange는 "형식" 탭에서 글꼴·크기·굵게·기울임·색을 보이고 바꾼다.

## 목표
선택한 글 객체(원본·앱 글꼴 줄 모두)의 **글꼴 이름, 크기, 색, 굵게, 기울임**을 속성 패널에서 보고, 아래처럼 바꾼다.

| 속성 | 원본 글 객체 | 앱 글꼴 줄 |
|---|---|---|
| 색 | 원본 글꼴 유지, 색만 바꿈 (새 편집 `SetTextColorEdit`) | 기존 (다시 넣기) |
| 크기 | 원본 글꼴 유지, 기준선 원점 기준 균등 확대 (`TransformEdit`) | 기존 |
| 굵게·기울임·명조 | 원본 글꼴 모양은 못 바꿈 → 앱 글꼴로 줄을 다시 쓰고 알림("글꼴이 바뀝니다") | 합성 굵게·기울임을 바로 적용(다시 넣기) |

## 범위 밖
도형 형식, 객체 정렬, Z 순서, 문단·폼 안 글, 글꼴 목록에서 임의 글꼴 고르기, 설치 글꼴(TrueType) 사용.

## 설계
1. **읽기 모델**: `TextFormatInfo(FontName, SizePt, Color, Bold, Italic, Serif, IsOriginalFont)`를 PDFium 스레드에서 읽는다. 원본은 `PdfPageObject.Font/FontSize/FillColor/TextRenderMode` + `AppFontCatalog.Classify`(이름·플래그) + 겹쳐 찍은 글(`ObjectToolController.ResolveOverlappedAsync`) → 굵게. 앱 글꼴 줄은 `InsertAppTextEdit.Line`에서. 순수 함수 `TextFormatReader`로 두고 PDFium 의존 부분은 얇게.
2. **새 편집 `SetTextColorEdit(long Id, ObjectRef Target, PdfColor Color)`**: `ContentReplayer`(화면·후속 편집), `ContentRewritePlanner`/`OutputCopyBuilder`(저장·인쇄·내보내기)에서 `SetShapeStyleEdit`과 같은 방식으로 처리한다. 원본 객체의 채움색을 바꾼다(렌더 모드 2의 선색도 함께).
3. **크기**: 새 크기/원래 크기 비율 s로 `TransformEdit(ScaleAbout(s, s, 기준선 원점))`. 줄 길이도 s배가 된다.
4. **굵게·기울임·명조**: `TextEditPlanner.PlanReplace`에 선택 인자 `FormatOverride(bool? Bold, bool? Italic, bool? Serif)`를 추가해 카탈로그 선택을 덮어쓴다. 원본 줄을 이렇게 바꾸면 `ReplaceTextAsync`와 같은 경로로 앱 글꼴 줄이 되고, 토스트로 알린다.
5. **UI**: `ObjectPropertiesPanel`의 글 객체 영역에 글꼴 이름(읽기 전용), 크기, 색 견본, B/I 단추, 명조 단추를 둔다. 문구는 ko/en resx. 리본은 건드리지 않는다.
6. **되돌리기**: 변경마다 `ContentEditCommand` 하나(실행 취소 한 번).

## 검증
- `TextFormatReader` 단위 시험: 굵은 글꼴 이름("Arial-BoldMT"), 겹쳐 찍은 글, 렌더 모드 2, 기울임 각도, 명조 이름.
- 편집 시험: 원본 글 객체 색 변경 후 재생·저장 결과의 채움색이 바뀌고 글꼴·글자 모양은 그대로. 크기 2배 후 경계 높이 2배. 굵게 켜면 앱 글꼴 줄 + 토스트.
- 실제 화면: 8372 PDF "PLAN HISTORY"에서 읽은 글꼴·크기가 맞고, 색·크기를 바꿔도 글꼴 유지, 굵게 켜기 동작(검증 필요: 회전 쪽, 저장 후 다시 열기).

## 위험
- 새 편집 종류는 저장·복원·인쇄 경로 모두에서 반영돼야 한다 → 저장 후 다시 열어 색이 유지되는지 시험.
- 원본 글꼴 이름만으로 굵기·기울임 판별이 어긋날 수 있다(드문 글꼴) → 읽은 값은 "추정"으로 표기한다.
