# M1 검증 결과

PDFium 157 (bblanchon/pdfium-binaries, 소스 브랜치 chromium/8100) 기준. 헤더는 웹 조회만 했고 파일은 내려받지 않았다.
출처: https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8100/public/ (fpdfview.h, fpdf_doc.h, fpdf_edit.h, fpdf_save.h)

## V4 — 암호화 설정·메타데이터 쓰기
- 암호화를 새로 거는 함수: **없음** (헤더에서 Encrypt/Security로 찾은 것은 읽기용 `FPDF_GetSecurityHandlerRevision`, `FPDF_GetDocPermissions`뿐).
- 저장 시 보안 제거: `FPDF_SaveAsCopy`/`FPDF_SaveWithVersion`의 플래그 `FPDF_REMOVE_SECURITY` (암호화 해제 사본만 가능).
- 메타데이터 쓰기(`SetMeta` 류): **없음**. 읽기만 `FPDF_GetMetaText`.
- 결론: 암호 걸기·메타데이터 수정은 PDFium으로 불가. PDFsharp 후처리가 필요하며 M1 범위에서는 하지 않는다 (사용자 결정 사항). fpdf_annot.h·fpdf_catalog.h 등 나머지 헤더는 전수 확인하지 못함 — 검증 필요.

## V7 — 레이어(Optional Content) 전환 API
- fpdfview.h, fpdf_doc.h, fpdf_edit.h, fpdf_save.h에서 OC/OCG/Layer/OptionalContent 이름의 함수는 **없음** (fpdf_edit.h의 `FPDFPageObj_GetMarkedContentID`는 마크드 콘텐츠 ID 조회일 뿐 가시성 전환이 아님).
- `V7LayerApiProbeTests`: 후보 이름 10개를 `pdfium.dll` export에서 찾았으나 모두 없음 (대조군 `FPDFPageObj_GetMarkedContentID`만 있음).
- 판정: **PDFium 157은 레이어 가시성 전환 API를 공개하지 않는다.** 따라서 M1 레이어 패널은 "목록 표시만, 전환 버튼 비활성"으로 구현한다 (Task 14). 전환 기능은 사용자 결정 사항 (대안: PDFsharp로 `/OCProperties /D /OFF`를 바꾼 렌더 전용 사본 — 페이지 편집과 동기화 문제로 M1에서는 하지 않음).
- 레이어 목록 표시 자체도 PDFium에 API가 없으므로 PDFsharp로 `/OCProperties`를 읽어야 할 가능성이 높음 — 검증 필요 (Task 14에서 확인).

## V8 — 손상 파일 감지
- `FPDF_DocumentHasValidCrossReferenceTable`로 확인. startxref 오프셋을 깨뜨린 파일은 PDFium이 복구해서 열고(`PageCount` 정상) 함수는 false를 돌려준다. 정상 파일은 true. (DocumentInfoTests로 확인)

## 권한 값 관찰
- 암호화 안 된 문서: SecurityRevision = -1, Raw = 0xFFFFFFFF로 처리.
- 사용자 암호 문서(PDFsharp 기본 설정): rev 4, Raw = 0xFFFFFFFC (권한 전부 허용).
- 소유자 제한 문서는 PDFsharp에서 `PermitAssembleDocument`까지 꺼야 `CanEditPages`가 false가 된다 (CanAssemble = 0x400 또는 CanModify).
