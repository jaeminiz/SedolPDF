# 객체 편집: 개체·주석 복사·붙여넣기 설계서 (0.9.0)

- 날짜: 2026-11-10
- 상태: 사용자 승인(대화), 구현 전
- 관련: M4 객체 편집(`ContentEdit`·`ContentReplayer`), M4x 내용 스트림 고쳐 쓰기(`ContentRewritePlanner`·`ContentRewriter`·`PdfSharpAnnotationPatcher`), M5 문단 편집(원래 글꼴 줄 직접 쓰기)

## 1. 목적과 배경

사용자 보고: "객체 편집에서 개체 복사 기능이 안 된다, 되어야 한다." 확인해 보니 기능이 아예 없다.
- `DocumentView.Objects.cs`의 `HandleObjectKey`는 Delete(`DeactivateEdit`)와 방향키(`TransformEdit`)만 처리한다.
- Ctrl+C는 `DocumentView.OnKeyDown`에서 글자 선택 복사(`CopySelection`)에만 연결돼 있다.
- 원본 개체를 하나 더 만드는 편집 종류가 없다.

0.9.0의 1순위 기능이다. 폼 XObject 안 글자 편집, 글꼴 유지 2단계, 정렬·배분 메뉴는 이번 범위가 아니다.

**성공 기준**
1. 객체 편집 도구에서 개체(하나 또는 여러 개)를 고르고 Ctrl+C → Ctrl+V 하면 같은 모양의 개체가 생기고, 그 개체를 다시 옮기기·크기 바꾸기·지우기·복사할 수 있다.
2. 저장한 파일을 다시 열면 붙인 개체가 화면에서 본 자리에 원본과 **같은 모양**으로 있다. 원본 개체 복제는 원본 PDF 명령을 그대로 쓰므로 충실도가 100 %다.
3. 실행 취소 한 번으로 붙여넣기·잘라내기·복제 하나가 통째로 되돌아간다.
4. 할 수 없는 경우에는 조용히 실패하지 않고 알림 문구로 이유를 알린다.

## 2. 범위 (사용자 확정 — "제안대로")

**넣는 것**
- 단축키: Ctrl+C(복사), Ctrl+X(잘라내기), Ctrl+V(붙여넣기), Ctrl+D(복제 = 복사 + 바로 붙여넣기, 클립보드는 건드리지 않음).
- 객체 편집 도구에서 오른쪽 클릭 메뉴: 잘라내기 / 복사 / 붙여넣기 / 복제 / 삭제. 쓸 수 없는 항목은 흐리게 표시.
- 놓는 자리: **같은 쪽**에 붙이면 화면 기준 오른쪽 아래로 10pt 비켜서 놓는다. 같은 클립보드를 거듭 붙이면 10pt씩 더 비킨다. **다른 쪽**에 붙이면 같은 쪽 좌표에 놓고, 쪽 밖으로 나가면 안으로 당긴다.
- 여러 개를 고르면 한꺼번에 처리한다. 겹침 순서는 원래 그리기 순서를 지킨다. 실행 취소는 한 번이다.
- 붙인 뒤에는 새 개체들이 선택된다.

**빼는 것 (다음 버전)**
- 다른 문서·다른 창으로 붙이기. 클립보드는 문서(세션)마다 따로 기억하고, 다른 문서에서 Ctrl+V를 누르면 알림만 보여 준다.
- 시스템 클립보드에 개체를 그림이나 PDF로 내보내기.
- 텍스트 편집 도구에서의 개체 복사(텍스트 편집 도구의 Ctrl+C는 지금처럼 글자 복사).

## 3. 사용자 동작 규칙

| 상황 | 동작 |
|---|---|
| 객체 편집 도구 또는 주석 선택 도구 + 개체가 선택됨 + Ctrl+C | 개체 복사. 글자 선택 복사보다 먼저 처리한다. |
| 개체가 선택되지 않음 + Ctrl+C | 지금처럼 글자 선택 복사(`CopySelection`) |
| Ctrl+V, 이 문서의 개체 클립보드가 있음 | **지금 보고 있는 쪽**(`tab.CurrentPageIndex`)에 붙인다 |
| Ctrl+V, 다른 문서의 클립보드만 있음 | 알림 "다른 문서의 개체는 아직 붙일 수 없습니다" |
| Ctrl+X | 복사 + 원래 개체 지우기. 실행 취소 한 번 |
| Ctrl+D | 고른 개체를 같은 쪽에 비켜서 하나 더 만든다. 클립보드는 그대로 |
| 권한 없음(`CanModify` 또는 `CanCopy`가 거짓) | 복사·잘라내기·붙여넣기·복제 모두 알림 "이 문서는 권한 설정 때문에 개체를 복사할 수 없습니다" |
| 복사할 수 없는 개체가 섞임 | 복사할 수 있는 것만 복사하고 알림 "일부 개체는 복사하지 못했습니다" (전부 안 되면 알림만) |

**쪽 회전:** 비키는 방향은 "화면 기준 오른쪽 아래"다. 쪽 회전(0/90/180/270)에 맞춰 쪽 좌표의 이동량으로 바꾼다. 복사한 개체의 모양은 쪽 좌표 행렬 그대로이므로, 회전된 쪽에서도 원래 개체와 같은 방향으로 보인다. 다른 쪽에 붙일 때 두 쪽의 회전이 다르면 쪽 좌표를 그대로 쓴다(사용자에게 보이는 방향이 다를 수 있다 — 한계로 적는다).

**권한:** 문서 안에서 개체를 복제하는 것은 내용을 바꾸는 일이므로 `CanModify`가 필요하다. PDF 권한 bit 5(복사·추출)도 "그래픽 추출"을 포함하므로 보수적으로 `CanCopy`까지 요구한다.

## 4. 구조

### 4.1 무엇을 복사하나 — 클립보드 항목

클립보드(`ObjectClipboard`, 앱 전체에 하나, 세션별 항목)는 **편집 기록을 만들 재료**를 갖는다. 복사 시점의 모양을 고정해 두므로, 복사한 뒤 원래 개체를 옮기거나 지워도 붙이는 결과는 바뀌지 않는다.

```
ClipItem =
  | OriginalClip(PageId SourcePage, int SourceIndex, PdfMatrix Then, PdfColor? TextColor, PdfRectF Bounds)
      // 원본 개체: 복사 시점까지 그 개체에 쌓인 TransformEdit를 접은 행렬, 글자 색 편집이 있으면 그 색
  | InsertedClip(ContentEdit Recipe, PdfMatrix Then, PdfRectF Bounds)
      // 앱이 넣은 개체: 넣기 편집(도형은 최신 스타일을 반영한 사본), 그 개체에 쌓인 TransformEdit를 접은 행렬
ObjectClipboardEntry(DocumentSession Session, PageId FromPage, ImmutableArray<ClipItem> Items, int PasteCount)
```

- `OriginalClip`의 원천이 이미 다른 복제(`DuplicateObjectEdit`)라면, 그 복제의 원천(SourcePage·SourceIndex)과 합친 행렬로 접는다. 복제의 복제도 결국 원본 하나를 가리킨다.
- Items는 원래 그리기 순서(쪽 객체 목록 순서)로 정렬한다.
- **복사할 수 없는 개체(v1):** 문단 편집 줄(`ReplaceParagraphEdit`의 줄), 찾아 바꾸기 조각(`ReplaceTextSpanEdit`), 원래 글꼴로 다시 쓴 줄(`InsertTextLikeEdit`), 그리고 화면 객체를 만들 수 없는 원본 개체. 원래 글꼴 줄은 저장 때 원본 글 객체에서 상태를 다시 읽는 구조라서 복제를 따로 설계해야 한다.

### 4.2 새 편집 종류

`src/PdfEditor.Document/Content/ContentEdit.cs`에 추가:

```csharp
/// 원본 개체(SourcePage 쪽의 원본 SourceIndex번)를 Then(쪽 좌표 행렬)만큼 옮긴 사본을 이 쪽 맨 위에 넣는다.
/// 가리킬 때는 ObjectRef.Inserted(Id). 원본은 세션 동안 바뀌지 않으므로 원본 쪽이 다른 편집을 받아도 사본은 같다.
public sealed record DuplicateObjectEdit(long Id, PageId SourcePage, int SourceIndex, PdfMatrix Then) : ContentEdit(Id);
```

- 사본은 원본의 **현재 상태가 아니라 원래 모양**에 Then을 적용한다. 원래 개체에 쌓인 옮기기는 클립보드가 Then에 이미 접어 두었다.
- 글자 색을 바꾼 원본을 복사하면 `SetTextColorEdit(새Id, Inserted(사본Id), 색)`을 함께 쌓는다.
- 앱이 넣은 개체는 새 편집 종류 없이 기존 넣기 편집을 새 Id로 다시 쌓는다.
  - 도형: `InsertShapeEdit(새Id, 최신 스타일을 반영한 ShapeSpec)` + `TransformEdit(새Id, Then·비킴)`
  - 그림: `InsertImageEdit(새Id, 같은 AssetId, Placement·Then·비킴)` (같은 문서라 자산을 같이 쓴다)
  - 앱 글꼴 글자: `InsertAppTextEdit(새Id, 같은 AppTextSpec)` + `TransformEdit(새Id, Then·비킴)`

### 4.3 붙여넣기 → 명령

`ObjectPasteBuilder`(Document 계층, Avalonia 없음): `(ClipboardEntry, 대상 PageId, 비킴 행렬) → ContentEdit[]`. 결과는 한 쪽에 대한 `ContentEditCommand` 하나다(실행 취소 한 번).
- 잘라내기: 같은 쪽의 `DeactivateEdit`들과 함께 `ContentEditCommand` 하나로 만든다. 클립보드는 명령이 성공했을 때만 바꾼다.
- 비킴: 같은 쪽이면 `(+10·k, −10·k)`를 화면 방향에서 쪽 좌표로 바꾼 이동(k = PasteCount+1). 다른 쪽이면 이동 없음에, 묶음 경계가 쪽 상자 밖이면 안으로 당기는 이동.
- 회전 변환은 `DocumentView`가 아는 `PageGeom.Rotation`을 써서 App 계층이 계산하고, 쪽 좌표 이동량으로 넘긴다.

### 4.4 화면 (`ContentReplayer`, ReplayMode.Screen)

PDFium 공개 API에는 개체를 그대로 복제하는 함수가 없다. 그래서 종류별로 비슷한 화면 객체를 만든다. 만든 객체는 쪽 맨 위에 넣고 `inserted[Id]`에 등록한다. 그러면 기존 옮기기·지우기·선택이 그대로 동작한다.

| 원본 종류 | 화면 객체 | 비고 |
|---|---|---|
| 글자(Text) | `PdfPage.CreateTextLike(원본, 원본 글)` + 행렬·Then | 같은 글꼴 핸들(문서 단위라 다른 쪽에서도 쓸 수 있음). 자간(Tc)·TJ 간격은 못 그린다 — 저장본이 정확 |
| 경로(Path) | `ReadPathGeometry` → `CreatePath` 다시 그리기 + 채움·선 색, 선 굵기, 그리기 모드 | 점선·선 끝 모양은 화면에서만 빠질 수 있음 |
| 그림(Image) | 새 바인딩 `FPDFImageObj_GetRenderedBitmap` → `CreateImage(bgra, 행렬)` | 마스크 포함 렌더 결과 |
| 폼(Form)·셰이딩(Shading) | **원본 쪽을 따로 한 번 더 불러와** 그 개체만 켠 채 개체 경계를 투명 비트맵으로 렌더 → `CreateImage` | 화면에서만 그림. 확대하면 흐릴 수 있음 — 저장본은 벡터 그대로 |

- 다른 쪽이 원천이면 replay가 원천 쪽 객체를 얻어야 한다. `ContentReplayer.Apply`에 `Func<PageId, int, PdfPageObject?> sourceResolver`(세션이 `PageHandleCache`로 원천 쪽 원본 객체를 돌려줌)를 넘긴다. 같은 쪽이면 `originals[SourceIndex]`를 쓴다.
- 원천 객체는 **원래 모양**이어야 한다. 원천 쪽 replay가 그 객체를 옮기거나 껐을 수 있다. 그래서 resolver는 원천 쪽을 **편집을 적용하지 않은 채 따로 한 번 더 불러온** 핸들(같은 쪽이라도)에서 원본 객체를 꺼내고, replay가 끝나면 그 핸들을 닫는다. 끈 원본도 복제 재료로는 쓸 수 있다.
- 화면 객체를 못 만들면 `skipped++`. 붙여넣기 단계에서 미리 같은 검사(`CanDuplicate`)를 하므로 정상 흐름에서는 생기지 않는다.

### 4.5 저장 (내용 스트림 고쳐 쓰기)

복제가 있는 쪽은 **반드시 내용 스트림 고쳐 쓰기(M4x)로 저장**한다. PDFium `GenerateContent` 경로는 화면용 근사 객체를 쓰게 되므로 쓰지 않는다. 고쳐 쓰기를 못 하면(문서가 패치 불가, 객체 번호 대응 실패 등) 저장을 막고 이유를 알린다. 문단 편집 쪽과 같은 처리다(`OutputCopyBuilder.RegenerateContent`의 `paraPages` 규칙에 복제 쪽을 더한다).

**① 계획(`ContentRewritePlanner`)**: `DuplicateObjectEdit`는 새 조각 종류 `'D'`가 된다. 뒤따르는 `TransformEdit`/`DeactivateEdit`/`SetTextColorEdit`(사본을 가리킴)는 그 조각에 접는다(M·Disabled·Color). `NewSnippet`에 `DuplicateSnippet(PageId Source, int SourceIndex, Mat M, PdfColor? TextColor)`를 단다. 조각 순서는 화면과 같다(넣은 순서, 앱 글꼴 줄은 맨 위).

**② 조각 바이트(`PdfSharpAnnotationPatcher.RewritePage`)**: 원천 쪽의 내용 스트림을 해석·분할(`ContentLexer`·`ContentSegmenter`)하고 `ObjectMap.Verify`로 원천 쪽 번호 대응을 확인한다. 그다음 원천 객체 하나를 **혼자 서는 조각**으로 만든다.

```
q
  <M> cm             % 쪽 공간에서 옮기기(Then·비킴을 접은 것). 먼저 써야 바깥쪽에 적용된다
  <원천 객체 시점 CTM> cm   % 객체 좌표 → 쪽 공간 (점 p → p·CTM·M)
  <상태 재현 연산자: w J j M d ri i gs, 채움·선 색 공간과 색>
  <객체 연산자>
Q
```
- 경로: 첫 경로 구성 연산자부터 칠하기 연산자까지 그대로(경로 안에 W/W*가 있거나 구성 외 연산자가 있으면 거절 — 지금 `TransformObject`와 같은 규칙).
- 글자: `TextCopy`와 같은 방식(글꼴 `Tf`, Tc·Tw·Tz·TL·Ts·Tr, `BT Tm … ET`). `Movable`이 거짓이거나 Tr ≥ 4(클립 글자)면 거절. 색을 바꿨으면 상태 재현 뒤에 `rg`(Tr이 선을 쓰면 `RG`도)를 덧쓴다.
- 그림·폼: `/이름 Do`. 인라인 그림: `BI … EI` 그대로. 셰이딩: `/이름 sh`.
- `ContentSegmenter`는 글자에만 상태 재현 연산자(`TextSnapshot.ReplayOps`)를 남긴다. 이것을 모든 `PageObject`에 남기도록 넓힌다(`PageObject.ReplayOps`).
- 원천 쪽은 편집이 없어도 해석해야 한다. `PageContentRewrite`에 원천 쪽 목록(쪽 번호 + 원본 객체 경계 `Reference`)을 더하고, `OutputCopyBuilder.ReadRewrites`가 원천 쪽 경계도 읽는다.

**③ 자원 옮기기(다른 쪽에 붙일 때)**: 조각이 쓰는 이름(Font·XObject·ExtGState·ColorSpace·Pattern·Shading·Properties)을 원천 쪽 `/Resources`에서 찾아 대상 쪽 `/Resources`에 넣는다. 같은 이름이 같은 객체를 가리키면 그대로 쓴다. 이름이 겹치는데 다른 객체면 `SedolR1`, `SedolR2`… 로 새 이름을 붙이고 조각 안의 이름 피연산자를 바꾼다. 연산자 단위로 바꾸므로 문자열 안의 우연한 일치는 바뀌지 않는다. 같은 쪽이면 자원을 옮기지 않는다. 쪽이 자원을 부모 `/Pages`에서 물려받는 경우도 원천의 실제 자원(상속 포함)에서 찾는다.

**④ 검증**: 지금처럼 결과를 다시 해석해 q/Q·BT/ET 균형을 확인한다. 저장 뒤 "시험 적용(dry run)"으로 화면 모양과 저장 모양을 비교하는 기존 장치에서, 복제 글자와 폼·셰이딩 화면 근사 영역은 비교에서 뺀다(`MaskRect`와 같은 방식).

### 4.6 원천 쪽 지우기

다른 쪽에 붙인 사본은 저장 때 원천 쪽의 내용과 자원이 필요하다. 그래서 **복제의 원천인 쪽은 그 사본이 살아 있는 동안 지울 수 없다.** 쪽 지우기 명령은 거절되고, 알림 "이 쪽에서 복사해 붙인 개체가 다른 쪽에 있어 지울 수 없습니다. 붙인 개체를 먼저 지우세요."가 뜬다. 같은 쪽 안의 복제는 쪽과 함께 지워지므로 상관없다. 쪽 순서 바꾸기·회전·쪽 추출은 `PageId`로 가리키므로 영향이 없다. 단, 쪽 추출(`PageExtractor`) 결과에 원천 쪽이 빠지는 경우를 구현 때 확인한다.

### 4.7 주석 복사·붙여넣기 (2026-11-10 사용자 추가 요청: "주석도 복사 붙여넣기가 안되네")

주석도 지금은 복사·붙여넣기가 없다(`HandleAnnotKey`는 Delete만 처리한다). 같은 단축키, 같은 메뉴, 같은 놓는 자리 규칙으로 넣는다. 주석은 모델(`Annotation` 레코드)이 그대로 저장되므로 개체보다 훨씬 단순하다.
- **복사:** 주석 선택 도구에서 고른 주석(`tab.Selection`)의 `Annotation` 레코드를 클립보드 항목(`AnnotationClip`)에 담는다. 텍스트 바꾸기 묶음(StrikeOut + Caret, `GroupOf`)은 묶음째 복사한다. 답글·검토 상태는 복사하지 않는다.
- **붙여넣기:** 새 `AnnotId`(`Annotations.NewId`), 이름(`/NM`)은 새로 만들고, 작성자는 현재 사용자, 만든·고친 시각은 지금으로 바꾼다. 위치는 `AnnotationGeometry.Translate`로 옮긴다. 묶음 안의 `InReplyTo`는 새 Id로 다시 잇는다. 하나의 `CompositeCommand`(`ChangeAnnotationsCommand.Add`들)로 실행 취소 한 번이다.
- **복사하지 않는 주석:** 글자에 붙는 표시(Highlight·Underline·StrikeOut 단독·Squiggly — 글자와 떨어지면 뜻이 없다), 다른 앱이 만든 읽기 전용 주석(`PageAnnotations.ReadOnly`), 링크·폼 필드·팝업. 섞여 있으면 되는 것만 붙이고 "일부 개체는 복사하지 못했습니다"를 띄운다.
- **자산:** 그림 스탬프(`StampSpec.ImageId`)·첨부 파일(`AttachmentId`)은 같은 문서 안이므로 같은 자산 키를 같이 쓴다.
- **회전:** FreeText·Stamp의 `Rotation`은 그대로 둔다. 다른 쪽의 회전이 다르면 붙인 쪽의 회전에 맞춰 `AnnotationRotation.ForPage`로 다시 정한다(새로 만들 때와 같은 규칙).
- **권한:** `CanAnnotate`가 필요하다. 개체와 달리 `CanCopy`는 요구하지 않는다. 주석은 사용자 자신의 메모이고 문서 내용을 추출하지 않기 때문이다.
- **개체와 주석이 함께 선택된 경우**(주석 선택 도구는 둘을 함께 고를 수 있다): 둘 다 복사한다. 붙이면 주석 명령과 개체 명령이 하나씩 실행되므로 실행 취소가 두 번 필요하다. `CompositeCommand`는 주석 명령만 묶을 수 있기 때문이다. 이것을 한계 6으로 적는다.

## 5. 오류 처리

| 실패 | 처리 |
|---|---|
| 붙일 쪽이 그사이 지워짐 | 명령 실패 → 아무것도 바뀌지 않음. 기존 `RunObjectEditAsync`처럼 조용히 무시 |
| 화면 객체를 만들 수 없는 개체 | 복사 단계에서 빼고 "일부 개체는 복사하지 못했습니다" |
| 저장 때 고쳐 쓰기 실패(복제 쪽) | `ContentRegenerationException`. 저장 거절 문구에 쪽 번호와 이유. 원본 파일은 그대로 |
| 원천 쪽 번호 대응(`ObjectMap.Verify`) 실패 | 위와 같음 |
| 자원 이름을 찾지 못함 | 위와 같음("resource /Fx not found on source page") |

## 6. 알려진 한계 (v1)

1. 원본 개체에 걸린 클립(잘라내기 영역)은 복사하지 않는다. 클립으로 일부만 보이던 그림을 복사하면 전체가 보일 수 있다. 화면과 저장본이 같게 보이므로 사용자가 바로 알 수 있다.
2. 저장 전 화면에서 복사한 글자는 자간·TJ 간격이 빠진다. 폼·셰이딩은 그림으로 보인다. 저장한 파일은 정확하다.
3. 다른 쪽에 붙일 때 두 쪽의 회전이 다르면 쪽 좌표를 그대로 써서 보이는 방향이 다를 수 있다.
4. 문단 편집 줄, 찾아 바꾸기 조각, 원래 글꼴로 다시 쓴 줄은 복사할 수 없다.
5. 다른 문서로는 붙일 수 없다.
6. 개체와 주석을 함께 붙이면 실행 취소가 두 번 필요하다.

## 7. 시험

- **단위(ContentStream.Tests)**: 분할기가 경로·그림·셰이딩에도 상태 재현 연산자를 남김. 조각 생성기(경로/글자/Do/BI/sh) 결과가 다시 해석되고 객체가 정확히 하나 늘어남. 자원 이름 바꾸기(겹침·같은 객체·상속 자원).
- **단위(Appearance.Tests)**: `ContentRewritePlanner`가 `DuplicateObjectEdit` + 뒤따르는 옮기기·끄기·색을 접음. 잘못된 참조는 실패.
- **단위(Document.Tests)**: `ObjectPasteBuilder` — 같은 쪽 비킴(회전 0/90/180/270), 다른 쪽 같은 자리·쪽 안으로 당기기, 복제의 복제가 원본 하나를 가리킴, 앱 넣기 개체는 새 Id 넣기 편집. `ContentReplayer`가 종류별 화면 객체를 만들고 `Resolve(Inserted(Id))`가 됨. 원천 쪽 지우기 거절.
- **왕복(Document.Tests, 고정 PDF)**: 글자·경로·그림·폼·셰이딩이 있는 쪽에서 같은 쪽 복제 / 다른 쪽 붙이기 → 저장 → 다시 열기 → 객체 수 +N, 사본 경계가 기대 위치, 쪽 렌더가 원본 개체를 옮겨 그린 것과 픽셀 비교 일치.
- **Document.Tests(주석)**: `AnnotationPasteBuilder` — 새 Id·이름·시각, 묶음 InReplyTo 다시 잇기, 글자 표시·읽기 전용 제외, 다른 회전 쪽에서 Rotation 다시 정함.
- **App.Tests**: 단축키 연결(개체 선택 시 Ctrl+C는 개체 복사, 아니면 글자 복사), 권한 없음 알림.
- **손 시험표**: `docs/superpowers/notes/2026-11-10-object-copy-paste-manual-tests.md` (구현 마지막 단계에서 작성).

## 8. 의존성

새 외부 의존성 없음. PDFsharp(MIT), PDFium(BSD/Apache) 기존 것만 쓴다. 새 PDFium 바인딩은 `FPDFImageObj_GetRenderedBitmap`과, 폼·셰이딩 렌더용으로 이미 있는 렌더 함수다.
