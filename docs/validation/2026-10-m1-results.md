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

## V6 — 암호 PDF 저장 (Task 8)
`tests/PdfEditor.Document.Tests/V6EncryptedSaveTests.cs`, PDFium `FPDF_SaveAsCopy`(플래그 0) + 임시 파일 → 검증 → 교체 파이프라인으로 확인.
- `V6_UserPasswordPdf_SaveAsCopy_StillRequiresPassword`: **통과.** 사용자 암호 PDF를 암호로 열어 쪽을 추가해 저장하면, 저장본은 암호 없이 열 때 `PdfPasswordRequiredException`, 같은 암호로는 열리고 쪽 수가 맞다.
- `V6_OwnerRestrictedPdf_SaveAsCopy_KeepsPermissions`: **통과.** 소유자 제한 PDF(인쇄 금지)를 저장해도 `IsEncrypted` true, `CanPrint` false 유지.
- 판정: **PDFium 저장은 암호화(사용자 암호·권한)를 유지한다** → `SavePolicy.PreservesEncryption = true`. 암호 PDF도 덮어쓰기 저장·복구 파일 허용. (검증 필요: 암호 알고리즘 등급 — 예: AES-256 원본이 저장 후 같은 등급인지 — 은 확인하지 않음. 테스트 픽스처는 PDFsharp 기본 설정, rev 4.)
- `V6_PdfSharpModify_OnEncryptedTemp_KeepsEncryption` (M2 후처리 설계용 조사):
  - 사용자 암호만으로 `PdfReader.Open(..., Modify)`는 `PdfReaderException` ("owner password is required"). PDFium을 거치지 않은 원본도 같다 (PDFsharp 제약).
  - 소유자 암호로 Modify로 열어 `Info.Title`만 바꿔 저장하면 **암호화가 사라진다**(PDFium에서 암호 없이 열림). `SecuritySettings.UserPassword/OwnerPassword`를 다시 지정하면 암호화가 유지된다.
  - M2 후처리 설계로 넘길 사항: (1) 암호 PDF에서 PDFsharp 후처리를 쓰려면 소유자 암호가 필요한데 앱은 사용자 암호만 알 수 있다 → 소유자 암호 없이 열린 암호 PDF는 후처리를 못 하므로 대책 필요(예: 후처리를 건너뛰고 PDFium만으로 저장하거나, 해당 문서의 주석 저장을 막고 안내). (2) 소유자 암호를 아는 경우에도 사용자/소유자 암호와 권한을 다시 지정해야 하고, 원래 권한 비트를 PDFsharp 설정으로 되돌리는 매핑이 필요하다(검증 필요).
