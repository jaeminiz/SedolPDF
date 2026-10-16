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

## 화면 차이 (Task 10, 앱 셸 vs 시안 `ui-mockup-v2.html` Windows 11) — 사용자 검수 항목
캡처: 실행 중인 창을 화면에서 직접 캡처(저장소에 커밋하지 않음). 홈 탭·주석 탭·파일 메뉴 확인. Mica 실제 적용 여부는 캡처만으로 판단 불가(배경이 단색처럼 보임) — **검증 필요**.

| 항목 | 시안 | 현재 구현 | 비고 |
|---|---|---|---|
| 창 제목 글자 | 없음 | 왼쪽 위에 "PDF 에디터"가 그려짐 | Avalonia 12가 창 장식(제목·버튼)을 직접 그림. 빠른 실행 버튼을 100px 오른쪽으로 밀어 겹침을 피함. 숨기는 방법 검증 필요 |
| 오른쪽 위 "⤡" 버튼 | 없음(최소화·최대화·닫기 3개) | 최소화 왼쪽에 버튼 1개 더 있음 | Avalonia 12 그려진 창 장식의 전체 화면 버튼으로 추정(검증 필요). 제거 방법 검증 필요 |
| 아이콘 | Fluent 스타일 | 시안의 손으로 그린 선 아이콘(자리 표시용). 일부 명령은 비슷한 모양으로 대체(폭 맞춤·페이지 맞춤·실제 크기는 사각형, 삭제·앞으로·뒤로·위치 지정은 아이콘 없음→기본 사각형) | Fluent UI System Icons는 사용자 승인 후 내려받아 같은 키로 교체 |
| 리본 항목 수 | 탭당 10~15개 | 주석 탭은 설계서 5.3절 버튼 전부(약 30개)라 폭 1280에서 오른쪽이 잘려 가로 스크롤 막대가 생김 | 설계서 5.1절 "10~15개, 나머지 더보기"와 5.3절이 충돌 — 결정 필요 |
| 큰/작은 버튼 | 그룹마다 섞어 씀 | 그룹 단위로 큰 버튼 또는 작은 버튼(텍스트 마크업·그리기·주석 관리만 작게, 세로 2~3줄) | |
| 도구 선택 강조 | 선택된 도구는 연한 파랑 | 아직 없음(도구 연결은 Task 12 이후) | |
| 패널 크기 | 왼쪽 200 / 오른쪽 220 고정 | 같음. 끌어서 폭 조절 없음 | |
| 상태 표시줄 | 페이지 이동 `|‹ ‹ [3/12] › ›|` | 이동 단추 4개는 비활성(Task 12에서 연결), 번호 입력칸은 문서가 있을 때만 | |
| 비활성 단추 | — | 모든 문서 명령이 아직 비활성(자리 표시용)이라 회색 | Task 11~17에서 연결 |
| 창 이동 | — | 빈 제목 영역을 끌어 이동하는지 **검증 필요**(캡처로는 확인 못 함) | |

## 인쇄 (Task 16)
`PrintJobTests`(Microsoft Print to PDF, DOCINFO 출력 파일 = 임시 경로. 물리 프린터로는 보내지 않음), 2026-10-16.
- **HDC를 만든 스레드와 다른 스레드에서 쓰기: 성공.** 테스트 스레드에서 `CreateDCW("WINSPOOL","Microsoft Print to PDF")`로 만든 HDC로, PdfiumThread에서 `StartDocW` → 쪽마다 `StartPage`/`FPDF_RenderPage`/`EndPage` → `EndDoc`을 호출해 선택한 2쪽(3쪽 중 [0,2])이 든 PDF가 만들어졌다. 대안(비트맵 렌더 + `StretchDIBits`)은 필요 없었다. 검증 필요: 다른 프린터 드라이버(실제 PCL/PS 프린터)에서도 같은지는 확인하지 못함.
- **취소:** 1쪽 뒤 취소하면 `AbortDoc` 후 `OperationCanceledException`, 완성된 3쪽 PDF는 남지 않는다.
- **주석 인쇄 규칙:** `FPDF_PRINTING` 플래그를 쓰면 주석 플래그의 **Print 비트(4)가 없는 주석은 PDFium이 그리지 않는다**(같은 파일을 화면용으로 렌더하면 그려짐). 앱이 만드는 주석(M2~)은 반드시 Print 플래그를 켜야 인쇄된다. 주석 제외 옵션은 `FPDF_ANNOT`을 빼서 구현했고 흰 쪽이 나온다.
- **A1 도면을 A4(Microsoft Print to PDF 기본 용지)에 맞춤 인쇄:** 가로형 A1(2383.9×1683.8pt)이 세로 A4(595.3×841.9pt)에서 자동 회전(Rotate=1)되어 가로·세로 모두 용지에 꽉 맞고(비율 유지, 가운데) 잘림 없음. 주석 포함 시 스티커 메모 아이콘이 회전된 위치(왼쪽 위)에 보이고, 제외 시 사라짐. 실제 크기(ActualSize)는 A1 가운데 부분만 A4에 잘려 나온다(설계대로).
- **검증 필요(사용자 수동 확인):** 실제 Win32 인쇄 대화상자(`PrintDlgExW`: 프린터 선택, 페이지 범위·현재 페이지, 매수/한 부씩, 파일로 인쇄 체크)와 실제 프린터 출력은 자동 테스트·합성 입력 없이 확인하지 못했다. 쪽 범위·현재 쪽 해석(`PD_PAGENUMS`/`PD_CURRENTPAGE`)은 코드만 작성됨.
