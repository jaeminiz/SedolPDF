# M2 검증 결과

PDFium 157 (bblanchon/pdfium-binaries, chromium/8100) 기준. 측정은 `tests/Pdf.Native.Tests`의 탐침 테스트(`V9AnnotWriteProbeTests`)로 했다.

## V9 — PDFium만으로 주석 AP(겉모양)를 써도 되는가 (결정 D1의 전제)

확인한 것: PDFium의 `FPDFAnnot_SetAP`로 우리가 만든 그리기 명령을 넣고 저장했을 때, 투명도·좌표·재저장이 의도대로 되는가.

- **투명도가 붙는다 (통과).** 사각형 주석에 불투명도 0.5(`/CA`)와 AP 내용 `q /GS gs ... Q`를 넣고 저장하면, 저장 파일의 AP 스트림에 `/Resources /ExtGState /GS`가 생기고 `/CA`, `/ca` 둘 다 0.502(= 128/255)로 들어간다. 즉 PDFium이 `/GS` 이름을 알아서 연결해 준다. 그러므로 **D1(M2는 PDFium만으로 저장, PDFsharp 후처리 없음)이 성립한다.**
- **좌표는 페이지 좌표 그대로다 (통과).** AP 스트림의 `/BBox`가 주석 `/Rect`와 같고(`[100 600 300 650]`), `/Matrix`는 없다. 그래서 AP 내용은 페이지 좌표로 쓰면 되고, 렌더하면 사각형 선 위 픽셀이 붉은 계열(255,127,127 — 반투명 빨강), 사각형 밖은 흰색이다.
- **우리가 쓴 AP가 그대로 남는다 (통과).** AP를 쓴 주석을 렌더하고, 저장하고, 다시 열어도 `GetAppearance()`가 우리가 쓴 문자열과 똑같다.
- **주의:** AP가 이미 있는 주석에는 `SetColor`가 실패한다. `Pdf.Native`는 이때 `InvalidOperationException`을 던진다. 색을 바꿔야 하면 AP를 먼저 지운다(`SetAppearance(null)`). AP가 없을 때 `GetAppearance()`는 null이다(PDFium은 빈 문자열 길이 2바이트를 돌려주므로 이를 null로 처리).
- **쿼드(강조 영역)는 줄일 수 없다.** PDFium에 삭제 API가 없어서 기존 개수보다 적게 `SetQuads`하면 `NotSupportedException`이다. 줄여야 하면 주석을 다시 만든다.
- **검증 필요:** Acrobat 등 다른 뷰어에서 우리가 쓴 AP가 같은 모양으로 보이는지는 아직 확인하지 않았다(M2 마무리 검토에서 확인).

## V2 — AP 없는 주석을 PDFium이 어떻게 다루는가

확인한 것: AP 없이 사각형 주석만 만들고(색·테두리만) 렌더 → 저장했을 때 AP가 남는가.

- **PDFium이 AP를 스스로 만들어 저장 파일에 남긴다.** 렌더하면 빨간 테두리가 보이고(빨강 픽셀 1,464개), 저장한 파일의 주석에 `/AP`가 들어 있다. 저장 → 다시 열기 → 다시 저장해도 같다(빨강 픽셀 1,464개 동일).
- **모르는 키가 붙는다.** 이때 주석 딕셔너리에 PDFium 전용 키 `/PDFIUM_HasGeneratedAP`가 함께 들어간다(키 목록: `/AP /Border /C /CA /PDFIUM_HasGeneratedAP /Rect /Subtype /Type`). 다른 뷰어는 이 키를 무시하겠지만, 파일에 우리가 모르는 흔적이 남는다.
- **결론:** 계획의 방침("우리가 만들거나 고친 주석은 Flush 때 반드시 `SetAP`로 AP를 직접 쓴다", PDFium 자동 생성에 기대지 않음)을 그대로 유지한다. 자동 생성 AP는 모양을 우리가 통제할 수 없고 `/PDFIUM_HasGeneratedAP` 키도 남기 때문이다. 이 키가 우리가 직접 쓴 AP 주석에도 붙는지는 이번에 확인하지 않았다 — **검증 필요** (Task 7 저장 테스트에서 확인).

## 원본 주석 읽기 — 암호·권한 문서 (Task 3)

확인한 것: PDFsharp 6.2.4를 읽기 전용(Import 모드)으로 열어 원본 주석을 읽을 때, 암호가 있는 문서가 열리는가. `LayerReader`에 남아 있던 "사용자 암호로 Import 모드가 열리는지는 검증 필요"와 같은 질문이며, `AnnotationReaderTests`의 두 탐침 테스트로 실측했다(2026-10-16).

- **사용자(열기) 암호 문서:** 올바른 암호를 주면 열리고 주석이 읽힌다(사각형·스티커 메모는 편집 가능, Popup은 `UnsupportedType`). 암호를 주지 않으면 `PdfReaderException`이 나며, `AnnotationReader`는 이를 `AnnotationReadException`으로 감싸 던진다. 틀린 암호도 같다.
- **소유자 암호만 있는 문서(열기 암호 없음):** 주석 권한이 꺼져 있든(`PermitAnnotations = false`) 켜져 있든 똑같이 열리고 주석이 읽힌다. 읽기는 권한과 무관하다.
- **앱 동작에 주는 뜻:** 암호 문서도 사용자가 연 암호를 넘기면 원본 주석을 편집 가능한 상태로 읽을 수 있다. 읽기가 실패(`AnnotationReadException`)하면 그 문서의 원본 주석은 모두 읽기 전용으로 둔다(Task 6·15). 소유자 제한 문서에서 주석 도구를 끄는 것은 읽기가 아니라 권한 값(PDFium `Permissions`)으로 판단한다.
- **검증 필요:** 소유자 암호만 있고 AES 256비트 등 다른 암호화 방식을 쓰는 문서, 암호화된 문서의 /Annots 안 간접 객체가 깨진 경우는 확인하지 않았다.
