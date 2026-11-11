# 0.10.0 설계서: 망가진 쪽 고치기, 텍스트 상자 Acrobat 호환, 주석 회전, 끌기 미리보기

- 날짜: 2026-11-11
- 상태: 사용자 승인(대화), 구현 전
- 관련: `ContentRepair`(0.9.0), `PatchPlanBuilder.FreeTextOps`, `AnnotationReader.ReadFreeText`, `ObjectSelectionController`(0.9.0 회전 끌기), `DuplicateScreen.CreateAsBitmap`

## 1. 목적과 성공 기준

사용자(조선 기자재 엔지니어)는 같은 도면을 Acrobat XI, PDF-XChange, SedolPDF에서 번갈아 연다. 0.10.0은 **프로그램 사이를 오갈 때 모양이 깨지는 문제**를 고치고, 0.9.0 객체 편집에 맞춰 주석도 돌릴 수 있게 한다.

**성공 기준**
1. 0.8.0 이하가 망가뜨린 파일(예: `(오류 발생건).pdf` 4쪽)을 SedolPDF로 열어 저장하면 4쪽이 망가지기 전 원본과 같은 모양이 된다. Acrobat에서도 오류 없이 보인다.
2. SedolPDF로 만든 텍스트 상자(빨간 테두리, 글자 크기 20pt)를 Acrobat에서 글자만 고쳐도 테두리는 빨간색, 글자 크기는 20pt 그대로다.
3. Acrobat에서 만든 텍스트 상자(빨간 테두리, Adobe 명조)를 SedolPDF에서 옮기면 테두리는 빨간색, 글꼴은 명조 그대로다.
4. 주석을 초록 회전 손잡이로 돌릴 수 있다. 저장하고 다시 열면 돌린 모양 그대로다. 실행 취소 한 번으로 되돌아간다.
5. 객체를 끌 때 점선 상자와 함께 객체 그림이 따라온다.

## 2. 범위 (사용자 확정 2026-11-11)

**넣는 것**: 아래 3~8절.
- 고칠 것 5가지:
  1. 망가진 파일
  2. 텍스트 상자 글자 커짐
  3. 텍스트 상자 테두리 검게 변함
  4. Acrobat 상자 옮기면 검게·글꼴 바뀜
  5. 개체를 돌리거나 옮기면 글자 잘림(7a절, 2026-11-11 추가)
- 추가 기능 a(주석 회전), b(끌기 미리보기).

**빼는 것 (다음 버전)**: 시간 재는 테스트 안정화, 서식/정렬 메뉴의 나머지, 폼 XObject 안 글자 편집, 글꼴 유지 2단계.

## 3. 망가진 쪽 고치기

### 3.1 무엇이 망가졌나 (실제 파일로 확인)

`261110_가나해운(한일종합해사)_NO 2 POHAH_VISCO (오류 발생건).pdf` 4쪽의 스트림별 q/Q 깊이:

| 스트림 | 내용 | 시작 깊이 | 증감 | 비고 |
|---|---|---|---|---|
| s0 | `q` | 0 | +1 | PDFium 감싸기 |
| s1 | `q q` | 1 | +2 | PDFium 감싸기 |
| s2 | 다시 쓴 스트림, `… S Q Q .75 0 0 -.75 0 841.92 cm`으로 끝남 | 3 | 0 | 균형. 끝의 `cm`(쪽 뒤집기)은 감싸지 않음 |
| s3 | `S Q q …` | 3 | 0 (최저 −1) | **꼬리 조각**: `S Q`가 s2의 `cm`을 꺼 버린다 → 나머지가 뒤집혀 그려짐 |
| s4 | `10 M .32 w … S Q q …` | 3 | 0 (최저 −1) | 정상 걸침(s3가 끝에 `q`를 열어 둠) |
| s5 | `… S Q Q` | 3 | −2 | 둘째 `Q` = 다시 쓰면서 잃은 쪽 전체 `q`의 짝 |
| s6 | `Q q q … Q Q Q Q Q` | 1 | −2 | SedolPDF가 덧붙인 글. 앞의 `Q` 하나 = PDFium이 감싸기를 닫는 보통 모양 |
| s7 | `Q q q … Q Q Q` | −1 | −1 | 위와 같음 |

망가지기 전 파일(`… 신환_DWG.pdf` 4쪽)은 스트림 하나다: `q .75 0 0 -.75 0 841.92 cm q … Q Q Q`.

지금 규칙(`ContentRepair.RemoveUnmatchedRestores`)은 쪽 전체를 한 줄로 보고 깊이가 0 아래로 내려가는 `Q`만 지운다. 감싸기 `q`(s0·s1)가 s3의 고아 `Q`를 가려서 s7의 `Q`를 지운다. 그래서 뒤집힘은 그대로 남는다.

### 3.2 새 규칙

스트림을 차례로 읽으면서 **열린 `q`마다 어느 스트림에서 열렸는지** 쌓아 둔다(스택). 스트림은 두 종류로 나눈다.
- **감싸기 스트림**: `q`만 있는 스트림(공백 제외). 여기서 연 `q`는 "걸침"으로 치지 않는다.
- **그림 스트림**: 그 밖의 모든 스트림.

각 스트림의 **머리**(첫 `q` 앞의 연산자들)에서 스택을 꺼내는 `Q`마다 판단한다.
1. 스택 맨 위가 **그림 스트림에서 연 `q`**면 → 정상 걸침. 그대로 둔다.
2. 그렇지 않고(감싸기에서 연 `q`이거나 스택이 비었음), 이 `Q` 앞에 머리 안의 `Q`가 아닌 연산자가 하나라도 있으면 → **꼬리 조각**. 스트림 처음부터 이 `Q`까지 지운다.
3. 그렇지 않으면(머리가 `Q`만으로 시작) → PDFium 감싸기 닫기. 그대로 둔다.

꼬리 조각을 지운 뒤에는 기존 규칙(쪽 전체에서 짝 없는 `Q` 지우기)을 한 번 더 돌린다. 4쪽에 적용하면 s3의 `S Q`가 지워지고 s7 앞의 `Q`가 지워져 깊이가 0으로 끝난다.

**규칙이 지우는 것의 성질**: 꼬리 조각의 길(path)은 앞 스트림을 다시 쓸 때 이미 통째로 그려졌다(s2 끝의 `216.8 813.12 m … l S Q`). 남은 `S Q`는 그릴 것이 없고 상태만 꺼낸다. 그래서 지우면 **원래 모양으로 돌아간다**. 0.9.0 규칙과 달리 PDFium·PDF-XChange에서도 모양이 바뀐다(뒤집힘이 고쳐짐). 그러니 "관대한 뷰어의 모양은 같다"는 설명은 이 규칙에 맞지 않는다. 주석을 고친다.

**한계**: 처음부터 여러 스트림이던 쪽에서, 그림 스트림이 쪽 전체 `q`를 열어 둔 채 끝나고 그 뒤 스트림에 꼬리 조각이 있으면 1번 경우로 보아 놓친다. 그때는 기존 규칙만 적용된다(0.9.0과 같음).

### 3.3 코드 모양

- `PageContent.LoadStreams(PdfPage) → Loaded(IReadOnlyList<byte[]>? Streams, string? Failure)`: `/Contents`의 스트림을 따로 돌려준다. 기존 `Load`는 그대로 둔다(다른 호출자가 씀).
- `ContentRepair.RepairStreams(IReadOnlyList<byte[]> streams) → (byte[]? Data, int FragmentsRemoved, int RestoresRemoved)`: 스트림마다 `ContentLexer.Parse`로 연산자를 읽는다. 3.2 규칙을 적용하고, 스트림 사이에 줄바꿈을 넣어 잇는다. 그다음 `RemoveUnmatchedRestores`를 돌린다. 고칠 것이 없거나 어느 스트림이든 해석할 수 없으면 `(null, 0, 0)`. 해석이 실패하면 쪽 전체 규칙만 쓴다(지금과 같은 결과).
- `PdfSharpAnnotationPatcher.RepairUnmatchedRestores`는 `LoadStreams` + `RepairStreams`를 쓴다. **고친 쪽 번호 목록**을 돌려주도록 `IContentRepairer`를 바꾼다: `IReadOnlyList<int> RepairContent(string pdfPath, string? password)`.
- `OutputCopyBuilder.BuildCoreAsync`가 그 목록을 `OutputCopy.RepairedPages`로 넘긴다. `SaveService`는 `LastRepairedPages`로 보여 준다.

### 3.4 화면

저장으로 고친 쪽이 있으면 알림 막대에 "N쪽의 그림 순서를 고쳤습니다(이전 버전 저장으로 망가진 부분). 다시 열면 고친 모양이 보입니다. [다시 열기]"를 띄운다. 쪽이 여러 개면 "4, 7쪽".
- [다시 열기]는 같은 파일을 같은 탭에서 다시 연다. 보던 쪽과 배율은 유지하고, 실행 취소 기록은 비운다.
- 다시 열기 기능이 이미 있으면(다중 창/복구 코드) 그것을 쓴다. 없으면 "닫기 + 열기 + 쪽·배율 복원"으로 만든다.
- 버튼을 누르지 않으면 화면은 저장 전 모양 그대로다. 파일은 이미 고쳐져 있다.

## 4. SedolPDF 텍스트 상자의 Acrobat 호환 (고칠 것 2·3)

### 4.1 근거

사용자가 컴퓨터 제어를 허용하지 않아 Acrobat XI를 직접 돌려 보지는 못했다. 대신 사용자 PC의 PDF에 있는 FreeText 약 320개를 조사했다(`D:\Downloads`, 카카오톡 받은 파일, 바탕화면, 2026-10-15 이후).
- Acrobat이 쓴 상자 중 `/DA`에 `RG`(테두리 색)를 넣은 것은 **하나도 없다**. 테두리 색은 `/DA`의 **`rg`**에 있고, 글자 색은 `/DS`의 `color:`에 있다.
  - `J001B005_ODME_POS.pdf` 6쪽(Acrobat 10.1.5): `/DA (1 0 0 rg /F1 12 Tf)`, `/DS … color:#000000`, AP는 `1 0 0 RG … re B`(빨간 테두리)이고 글자는 검정·파랑이다.
  - `TANK TABLE_H3368.pdf`: `/DA ( 1 1 1 rg /맑은고딕 12 Tf)`, `/DS … color:#FF0000`, AP는 `1 1 1 RG … re S`(흰 테두리)이고 글자는 빨강이다.
  - `TANK TABLE (23).pdf`(Acrobat 11.0.20): `/DA (1 0 0 rg /AdobeMyungjoStd-Medium 12 Tf)`, `/DS (font: 500 'Adobe Myungjo Std',serif 12.0pt; text-align:left; color:#FF0000 )`, `/C [1 1 1]`(배경 흰색).
- SedolPDF 0.9.0은 `/DA (글자 rg 테두리 RG /Helv 크기 Tf)`를 쓰고 `/DS`가 없다. `/RC`에는 서식이 없다.
  - Acrobat은 `rg`(글자색, 보통 검정)를 테두리로 써서 테두리가 검게 된다(고칠 것 3).
  - Acrobat은 `/DS`·`/RC`에 크기가 없으면 12pt로 다시 그린다(고칠 것 2).

**Acrobat XI에서 직접 확인함 (2026-11-11, 사용자 허락 후 컴퓨터 제어)**: 테스트 파일 두 개를 Acrobat XI(11.0.23)에서 글자 하나만 고쳐 저장했다.

| 파일 | 고치기 전 `/DA` | Acrobat이 다시 그린 테두리 |
|---|---|---|
| 0.9.0 방식 | `0 0 0 rg 1 0 0 RG /Helv 20 Tf`, `/DS` 없음 | AP `0 G` → **검정** (고칠 것 3 재현). `/DS`는 `font: Helvetica,sans-serif 20.0pt`로 새로 씀 |
| 4.2 방식 | `1 0 0 rg 1 0 0 RG /Helv 20 Tf` + `/DS`(맑은 고딕 20pt) + 서식 `/RC` | AP `1 0 0 RG` → **빨강 유지**. 글꼴 `/MalgunGothic 20 Tf`, 20pt 유지 |

Acrobat은 한글이 든 `/DS`·`/RC`를 UTF-16 문자열(`<FEFF…>`)로 저장한다. 읽는 쪽은 PDFsharp `GetString`이 BOM을 풀어 주는 것을 전제로 한다(5절 시험에 포함).

### 4.2 쓰는 규칙 (`PatchPlanBuilder.FreeTextOps`)

- `/DA`: `{T} rg{B} /Helv {크기} Tf`
  - 테두리가 있으면(`Color`가 있고 `BorderWidth > 0`) T = 테두리 색, B = ` {테두리 색} RG`.
  - 테두리가 없으면 T = 글자 색, B = 없음.
  - `RG`를 남기는 이유: 0.9.0 이하 SedolPDF와 pypdf 관례가 테두리를 `RG`에서 읽는다. 대가로 0.9.0 이하 SedolPDF는 새 파일의 글자 색을 테두리 색으로 읽는다. 사용자는 최신판만 쓰므로 받아들인다.
- `/DS`(새로 씀): `font-family:{글꼴};font-size:{크기}pt;color:#{글자 RRGGBB};text-align:{left|center|right}` 뒤에 서식이 있을 때만 `;font-weight:bold`, `;font-style:italic`, `;text-decoration:underline` / `line-through`(둘 다면 `underline line-through`)를 붙인다. 크기는 소수 한 자리까지(`12pt`, `10.5pt`).
  - 글꼴: 고딕 → `'Malgun Gothic',sans-serif`, 명조 → `'Adobe Myungjo Std','Batang',serif`. 맑은 고딕은 한국어 Windows에 늘 있고, Adobe 명조는 Acrobat 한국어판이 자기 상자에 쓰는 글꼴이다(TANK TABLE).
- `/RC`(서식 있게): `<?xml version="1.0"?><body xmlns="http://www.w3.org/1999/xhtml" xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/" xfa:APIVersion="SedolPDF:{버전}" xfa:spec="2.0.2" style="{/DS와 같은 선언}"><p dir="ltr">{줄}</p>…</body>`. 빈 줄은 `<p dir="ltr"><br/></p>`. 새 함수 `FreeTextRichText.Build(Annotation)`가 `/DS`·`/RC` 둘 다 만든다. `AnnotationFlusher.MinimalXhtml`은 메모(팝업) 주석용으로 그대로 둔다.
- 나머지 키(`/Border`, `/C` = 배경, `/Q`, `/SedolFont`, `/SedolFmt`)는 지금과 같다.
- `xfa:APIVersion`에 Acrobat을 사칭하지 않는다. 그래서 Acrobat이 `/RC`를 무시할 위험이 있다. 그때를 대비해 `/DS`에도 같은 값을 넣는다. Acrobat은 `/RC`를 못 읽으면 `/DS`를 쓴다. 9절 확인 항목 1이 이를 확인한다.

## 5. Acrobat이 만든 상자를 읽기 (고칠 것 4)

`AnnotationReader.ReadFreeText` / 호출부:
- **`/DS`가 있으면**(Acrobat·PDF-XChange 관례):
  - 테두리 색 = `/DA`의 `RG`, 없으면 `/DA`의 `rg`, 그것도 없으면 검정. 테두리 두께가 0이면 색 없음(지금과 같음).
  - 글자 색 = `/DS`의 `color:#RRGGBB`. 없으면 `/RC` body의 `color:`, 그것도 없으면 `/DA`의 `rg`.
  - 크기 = `/DA`의 `Tf` 크기. 0이거나 없으면 `/DS`의 `font-size` 또는 `font:` 줄임 속성 안의 `NNpt`.
  - 글꼴 가족: `/SedolFont`가 있으면 그것. 없으면 `/DS`·`/RC`의 글꼴 문자열(`font-family`, `font:` 줄임, `/DA` 글꼴 이름)에 다음이 있으면 명조: `serif`(단 `sans-serif`는 아님), `Myungjo`, `Batang`, `Gungsuh`, `Times`, `명조`, `바탕`, `궁서`. 그 밖은 고딕.
- **`/DS`가 없으면**: 지금 규칙 그대로(`rg` = 글자, `RG` = 테두리; `/IC`가 있으면 옛 SedolPDF 규칙).
- 새 `DefaultStyle.Parse(string? ds) → (float? Size, PdfColor? Color, string? Family)`: `DefaultAppearance` 옆, `Annotations/Reading/`. 굵게·기울임은 `/DS`에서 읽지 않는다. SedolPDF 자기 서식은 `/SedolFmt`로 왕복하고, Acrobat의 `font-weight:500`은 굵게가 아니다.
- 0.9.0 이하가 쓴 파일(`/DS` 없음)과 0.10.0이 쓴 파일(`/DS` + `rg`=테두리 + `RG`=테두리) 모두 왕복 테스트로 묶는다.

## 6. 주석 회전 (기능 a)

### 6.1 동작

- 주석 선택 도구에서 회전할 수 있는 주석을 **하나 이상** 고르면, 0.9.0 객체 회전과 같은 초록 회전 손잡이가 선택 경계 위쪽 가운데에 나온다. 위치는 `ObjectSelectionController.RotateHandlePos`와 같은 계산이다.
- 끄는 동안: 중심 표시, 부채꼴, 각도 라벨(소수 한 자리). Shift를 누르면 15° 단위. 그리는 코드는 객체 쪽 코드를 함수로 빼서 같이 쓴다.
- 놓으면 고른 주석 전부를 선택 경계 중심으로 돌린다. 명령 하나(`ChangeAnnotationsCommand`)라서 실행 취소 한 번이면 된다.

| 종류 | 돌리는 방법 |
|---|---|
| 선, 폴리라인, 다각형(구름 다각형 포함), 연필(Ink) | 모든 점을 중심으로 각도만큼 돌린다. `Rect`는 다시 계산한다. 자유 각도. |
| 사각형, 원, 구름 사각형 | 각도를 가장 가까운 90° 배수로 맞춘다. 90°·270°면 중심을 지키고 너비·높이를 바꾼다. 0°·180°면 모양이 같아 위치만 그대로다. |
| 텍스트 상자, 타자기, 스탬프 | 90° 배수로 맞춘다. 상자 중심을 지키며 `Rotation`(`/Rotate`)에 더하고 너비·높이를 바꾼다. |
| 설명선 | 지시선 점(`Vertices`, `Leader2`)과 글 상자를 90° 배수로 함께 돌린다. 글 상자는 텍스트 상자와 같은 방법. |
| 형광펜·밑줄·취소선·메모·캐럿·첨부 | 손잡이 없음. 이것들이 섞인 선택에서는 손잡이를 숨긴다. |

- 90° 단위 종류가 섞인 선택에서 Shift 없이 끌면, 끄는 동안의 미리보기는 자유 각도로 보인다. 놓을 때 각 주석에 위 표의 규칙을 적용한다. 라벨에는 놓았을 때 적용될 각도를 함께 보인다. 예: `37.4° → 90°`(90° 단위 주석이 섞여 있을 때만).
- 쪽 회전(0~3)이 있으면 손잡이 위치와 각도 방향은 객체 회전과 같은 변환을 쓴다.

### 6.2 코드 모양

- `AnnotationRotate.Apply(Annotation a, float angleRad, PdfPointF center) → Annotation?`(Document 층, 순수 함수). 돌릴 수 없는 종류면 null.
- `AnnotationRotate.CanRotate(Annotation)`, `AnnotationRotate.SnapsToQuarter(Annotation)`.
- `SelectionController`에 `HandleKind.Rotate`. `HitTest`가 손잡이를 찾고, `Begin`/`Move`/`End`가 각도를 다룬다. 결과 명령은 기존 `ChangeAnnotationsCommand`.
- 그리기: `DocumentView.Annotate.cs`(주석 선택 그리기)가 객체 쪽에서 빼낸 `RotationGizmo.Draw(ctx, …)`를 부른다.
- 저장: 바뀐 점·Rect·Rotation은 기존 패치 계획이 쓴다. 새 키는 없다.

## 7. 객체 끌기 미리보기 (기능 b)

- `ObjectSelectionController.BeginDrag`(몸통 끌기와 크기 바꾸기)에서, UI가 문서 스레드에 그림을 하나 부탁한다.
  - 고른 객체만 `IsActive = true`, 나머지는 `false`로 둔 채 `RenderRegionTransparent(GroupBounds, scale)`를 부른다. 그리고 원래 `IsActive` 값을 **반드시 되돌린다**(`try/finally`). `DuplicateScreen.CreateAsBitmap`과 같은 방법이다.
  - scale = 지금 화면 배율. 단, 그림 긴 변이 2048px를 넘지 않게 줄인다.
  - 이 작업은 문서 스레드(`s.Thread`)에서 한 번에 끝나므로 타일 렌더와 겹치지 않는다.
- 그림이 오기 전이나 실패하면 지금처럼 점선 상자만 그린다.
- 끄는 동안: 원래 자리 객체는 그대로 둔다. 미리보기 경계(`Preview`, 여러 개면 `DragDelta`만큼 옮긴 경계)에 그림을 불투명도 0.6으로 늘여 그린다. 그 위에 점선 상자를 그린다. 크기 바꾸기도 같은 그림을 늘여 그린다.
- 회전 끌기는 이번 범위가 아니다. 지금처럼 돌아가는 점선 상자만 보인다.
- 끌기가 끝나거나 취소되면 그림을 버린다.
- `IsActive`를 껐다 켜는 일이 저장 결과에 흔적을 남기지 않는지 테스트로 확인한다. 저장 전후 내용 스트림이 같아야 한다.

## 7a. 개체를 돌리거나 옮기면 글자가 잘리는 문제 (2026-11-11 사용자 보고, 0.9.0 버그)

**증상**: `D:\Downloads\TEST\SEDOL Analytics PDF Product Showcase.pdf`(Chromium/Skia가 만든 PDF) 2쪽의 어두운 패널을 개체 편집으로 돌리면 다음이 깨진다.
- 제목 "Multi-Timeframe Algorithmic Overlay"가 중간에서 잘린다.
- 오른쪽 위 글자와 아래 "[PROMO SLOT 2] …" 띠가 사라지거나 잘린다.

**원인(재현 확인)**: `PdfPageObject.TransformBy`는 `FPDFPageObj_SetMatrix`로 개체 행렬만 바꾼다. PDFium은 개체마다 **잘림 경로(clip path)** 를 따로 들고 있는데, 이것은 움직이지 않는다.
- 이 PDF는 2쪽 개체 1,623개 중 824개에 잘림 경로가 있다. Chromium은 글자 덩어리마다 `re W n`으로 상자를 둘러 자른다.
- 그래서 돌린 글자가 원래 자리에 똑바로 선 상자에 잘린다.
- PDFium만으로 패널 개체 468개를 15° 돌려 보았다.
  - 행렬만 바꾸면 사용자 화면과 똑같이 깨진다.
  - `FPDFPageObj_TransformClipPath`로 잘림 경로도 같은 행렬로 바꾸면 바르게 그려진다.
- 옮기기·크기 바꾸기도 같은 원인이다. 크게 옮기면 개체가 잘림 상자 밖으로 나가 통째로 사라진다.

**고치는 방법**
1. `PdfPageObject.TransformBy(m)`는 행렬과 함께 `FPDFPageObj_TransformClipPath(m)`도 부른다. 잘림 경로가 없으면 아무 일도 하지 않는다.
   - `ContentReplayer`의 화면·저장 재생, `DuplicateScreen`, 붙여넣기가 모두 이 함수를 거치므로 한 곳만 고치면 된다.
   - `Matrix = …`를 직접 쓰는 곳(`DuplicateScreen` 글자 `t.Matrix = src.Matrix.Then(then)`)은 새로 만든 개체라 잘림 경로가 없다. 원본의 잘림을 이어받는 경우가 있는지 구현 때 확인한다.
2. 저장 경로 중 **내용 스트림 직접 고쳐 쓰기**(`ContentRewriter`, 변형을 `q cm … Q`로 감쌈)는 바깥에서 건 잘림을 옮기지 못한다.
   - `ContentSegmenter`가 개체마다 "바깥 `q`에서 이어받은 잘림이 있음"(`InheritsClip`)을 기록한다.
   - 그런 개체에 변형 편집이 있으면 `ContentRewriter`가 거절(Fail)한다. 그러면 PDFium 다시 쓰기 경로(잘림까지 옮긴 상태로 씀)로 내려간다. 거절 후 PDFium 경로로 내려가는 구조는 지금 있는 것이다(`IContentRewriteProbe`).
3. 충실도 보호(`FindChangedPages`: 화면 모양 ≡ 저장 모양)는 그대로 둔다. 화면이 바르게 바뀌므로, 저장도 같아야 통과한다.

**시험**: 합성 PDF(`q 100 100 200 20 re W n BT … (Hello) Tj ET Q` 같은 Chromium 모양)로 다음을 확인한다.
- 글 개체를 30° 돌린 화면 그림에 글자가 다 있다(잘림 경로 없는 같은 글을 돌린 그림과 같다).
- 저장 → 다시 열기 → 그림이 같다.
- 200pt 옮겨도 사라지지 않는다.
- 실제 파일(위 `SEDOL Analytics …pdf`)로 Claude가 앱에서 돌리고 저장한 뒤 그림을 확인한다(커밋 안 함).

## 8. 오류 처리

- 스트림 해석 실패: 해당 쪽은 0.9.0 규칙으로만 고친다. 고칠 수 없으면 손대지 않는다. 저장은 실패시키지 않는다.
- `/DS` 해석 실패: `/DS`가 없는 것으로 보고 지금 규칙으로 읽는다.
- 회전 결과가 쪽 밖으로 나가면 그대로 둔다(옮기기와 같은 정책).
- 미리보기 그림 렌더 예외는 삼키고 점선 상자만 그린다. 단, `IsActive` 복원은 `finally`에서 반드시 한다.

## 9. 시험

**자동(xUnit)**
- `ContentRepairTests`: 4쪽 구조를 합성한 스트림 목록(s0 `q`, s1 `q q`, s2 균형 + 끝 `cm`, s3 `S Q q … `, s4 정상 걸침, s5 `… Q Q`, s6/s7 `Q q q … Q`). s3 조각만 지우고 결과 깊이가 0인지 본다. 다음 경우도 따로 시험한다: 앞 `Q`만 있는 머리(그대로), 정상 걸침(그대로), 감싸기 없는 단일 스트림(지금과 같은 결과), 해석 실패(null).
- 렌더 비교: 합성 PDF 한 쪽(뒤집기 `cm` + 다시 쓴 모양 재현)을 고치기 전·후·정답으로 그려 비교한다. 고친 뒤가 정답과 같아야 한다.
- `FreeTextOps`: `/DA`·`/DS`·`/RC` 문자열 스냅숏(테두리 있음/없음, 고딕/명조, 굵게·밑줄, 가운데 정렬, 여러 줄·빈 줄, `&<>` 이스케이프).
- `AnnotationReader`: TANK TABLE 모양의 상자(`/DS` + `rg` 빨강 + `RG` 없음 + Adobe 명조) → 테두리 빨강, 명조, 12pt. H3368 모양 → 테두리 흰색, 글자 빨강. 0.9.0이 쓴 상자 → 지금과 같음. 0.10.0이 쓴 상자 왕복 → 같은 모델.
- `AnnotationRotate`: 종류마다 0/37/90/180/270°. 90° 단위 맞춤, 중심 유지, Rect 재계산, 설명선 지시선.
- `SelectionController` 회전: 손잡이 찾기, Shift 15°, 섞인 선택에서 손잡이 숨김, 실행 취소 한 번.
- 끌기 미리보기: 렌더 뒤 모든 객체의 `IsActive`가 원래대로인지, 저장한 내용 스트림이 미리보기를 안 한 경우와 같은지.

**Claude가 직접 하는 확인 (구현 세션)**
1. 실제 `(오류 발생건).pdf`를 저장하고, 4쪽을 원본(`… 신환_DWG.pdf` 4쪽)과 PDFium 그림으로 비교한다. 차이가 경계선 수준이어야 한다. 알림 막대와 [다시 열기]도 확인한다.
2. Acrobat XI 확인(컴퓨터 제어). 사용자에게 다시 허락을 구한다. 허락하면 다음을 해 보고 키를 덤프한다.
   - SedolPDF가 만든 빨간 테두리·20pt 상자를 Acrobat에서 글자만 고쳐 저장 → 테두리 빨강, 20pt인지.
   - TANK TABLE을 SedolPDF에서 옮겨 저장 → Acrobat에서 테두리 빨강, 명조인지.
   허락하지 않으면 이 두 항목을 수동 시험지(10절)로 넘긴다.
3. 앱을 실행해 주석 회전·끌기 미리보기 화면을 캡처해 확인한다.

## 10. 수동 시험지

`docs/superpowers/notes/2026-11-11-v0100-manual-tests.md`(구현 세션이 작성). 위 2번의 Acrobat 항목, 회전 손잡이 감각(Shift, 각도 라벨, 회전된 쪽), 큰 도면에서 끌기 미리보기 속도.

## 11. 릴리스

0.10.0 마이너 버전. 버전·README·스토어 문구는 출시 세션에서 한꺼번에 바꾼다(릴리스 정책). 이번 문서와 계획은 커밋만 하고 푸시하지 않는다.
