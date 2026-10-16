# M1 검증 결과

PDFium 157 (bblanchon/pdfium-binaries, 소스 브랜치 chromium/8100) 기준. 헤더는 웹 조회만 했고 파일은 내려받지 않았다.
출처: https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8100/public/ (fpdfview.h, fpdf_doc.h, fpdf_edit.h, fpdf_save.h)

## V4 — 암호화 설정·메타데이터 쓰기
- 암호화를 새로 거는 함수: **없음** (헤더에서 Encrypt/Security로 찾은 것은 읽기용 `FPDF_GetSecurityHandlerRevision`, `FPDF_GetDocPermissions`뿐).
- 저장 시 보안 제거: `FPDF_SaveAsCopy`/`FPDF_SaveWithVersion`의 플래그 `FPDF_REMOVE_SECURITY` (암호화 해제 사본만 가능).
- 메타데이터 쓰기(`SetMeta` 류): **없음**. 읽기만 `FPDF_GetMetaText`.
- 결론: 암호 걸기·메타데이터 수정은 PDFium으로 불가. PDFsharp 후처리가 필요하며 M1 범위에서는 하지 않는다 (사용자 결정 사항). (나머지 헤더 확인은 아래 "V4 확정"에서 끝남.)

### V4 확정 (Task 18, 2026-10-16)
Task 3 때 웹으로만 본 헤더 4개 외에, **NuGet 패키지에 들어 있는 헤더 24개 전체**(`~/.nuget/packages/bblanchon.pdfium.win32/157.0.8100/build/native/include/pdfium/*.h`, `cpp/*.h`)를 직접 검색했다.
- `Encrypt|SetSecurity|SetMeta|SetInfo|SetPassword|SetDocPerm|SetPermission|SetUserPass|SetOwnerPass|AddMeta|WriteMeta|SetTitle|SetAuthor` 검색: **일치 0건.**
- 보안 관련은 `FPDF_REMOVE_SECURITY`(저장 플래그, `fpdf_save.h`)와 읽기용 함수뿐. OC/OCG/Layer 이름도 0건(V7 재확인).
- **판정: PDFium 157에는 암호 설정·메타데이터 쓰기 API가 없다.** 암호 걸기·문서 정보(제목·작성자) 수정은 PDFsharp 후처리로만 가능하며 M1 범위 밖이다(사용자 결정 사항으로 유지).

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
| 창 제목 글자 | 없음 | **해결(마무리 검토)**: Avalonia가 그리던 제목 글자를 숨김 | `App.axaml` 스타일, "M1 마무리 검토" 절 |
| 오른쪽 위 "⤡" 버튼 | 없음(최소화·최대화·닫기 3개) | **해결(마무리 검토)**: 전체 화면 버튼(`PART_FullScreenButton`)을 숨김 | 캡처로 확인 |
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

## V5 — PDFium 배포 조건 (완료, 2026-10-16)
**판정: 재배포 가능. 라이선스 원문을 앱과 함께 배포하고, 문서에 크레딧 2줄(FreeType, IJG)을 넣는다.**

방법: 사용자 승인을 받아 상위 세션이 pdfium-binaries 릴리스 `chromium/8100`의 `pdfium-win-x64.tgz`(3.9 MB)를 작업용 임시 폴더에 내려받았다. 라이선스 파일만 풀어서 읽었고, 실행 파일은 실행하지 않았다.

1. **같은 바이너리:** 릴리스의 `bin/pdfium.dll`과 앱이 쓰는 NuGet `bblanchon.PDFium.Win32` 157.0.8100의 `runtimes/win-x64/native/pdfium.dll`은 SHA-256이 같다(`ae913f407e8d3f5cdbf08a9869564d647b13e132e01eb376c0a5a33b7a227a45`). 그래서 릴리스의 `licenses/`가 앱의 dll에 그대로 적용된다.
2. **실제로 포함된 구성 요소(16개)와 라이선스:**
   - PDFium: BSD-3 + Apache-2.0
   - abseil: Apache-2.0
   - AGG 2.3: 저작권 표시만 남기면 되는 허용형
   - dragonbox: Apache-2.0 LLVM 예외 또는 Boost
   - fast_float: MIT
   - FreeType: FTL
   - HarfBuzz: Old MIT
   - ICU: Unicode V3
   - lcms: MIT
   - libjpeg-turbo: IJG + BSD-3 + zlib
   - OpenJPEG: BSD-2
   - libpng: libpng v2
   - LLVM libc: Apache-2.0 LLVM 예외
   - simdutf: MIT
   - zlib: zlib
   - pdfium-binaries 빌드 스크립트: MIT

   이전에 후보로 적었던 libtiff, bigint, highway 등은 이 빌드에 **없다**.
3. **모두 허용형이다.** GPL·AGPL은 없고, 기업에서도 무료로 쓰고 재배포할 수 있다. FTL·IJG·zlib·libpng·Unicode·Boost는 이름은 MIT·BSD·Apache와 다르지만 같은 계열의 허용형이다. CLAUDE.md의 "MIT·BSD·Apache·OFL 계열" 조건에 맞는다고 판단했다.
4. **반영한 것:**
   - 원문 18개 파일을 `assets/licenses/pdfium/`에 넣었다. 빌드하면 앱 출력 폴더의 `licenses/pdfium/`으로 복사된다(`PdfEditor.App.csproj`).
   - `THIRD-PARTY-NOTICES.md`의 PDFium 절을 구성 요소 표로 바꿨다.
   - FreeType·IJG 크레딧 문구를 넣었다.
5. **남은 일:** 배포용 설치 파일(아직 없음)을 만들 때 `licenses/` 폴더와 `THIRD-PARTY-NOTICES.md`가 들어가는지 확인한다. 매뉴얼을 쓸 때(M5 이후) 크레딧 2줄을 넣는다.

## 성능 측정 (Task 18, 2026-10-16)
> 아래는 Task 18 당시 값이다. 마무리 검토에서 원인을 고치고 다시 잰 값은 "M1 마무리 검토" 절에 있다(두 미달 항목 모두 통과).
`PdfEditor.App.exe --bench <pdf> --out <json>` (Release, 이 PC: Windows 11, 12 논리 코어, 기본 창 크기 → 뷰포트 782×581 px). 입력 장치는 쓰지 않고, 문서 화면이 프레임을 그릴 때마다(`DocumentView.RenderCount` 증가) 스크롤 위치를 직접 바꾸며 `LastCoverage`(아무 그림으로 덮인 비율)와 `LastExactCoverage`(현재 배율 타일로만 덮인 비율)를 읽는다. 원본 JSON은 `spikes/out/m1-bench-a1*.json`, `m1-bench-300*.json`(저장소에 커밋되지 않음). 목표는 조정하지 않았다.

| 항목 | 목표 | A1 도면 (3회) | 300쪽 (2회) | 판정 |
|---|---|---|---|---|
| 512 타일 렌더 p95 | ≤ 100 ms | 46.8 / 49.8 / 48.0 ms (p50 약 40, 최대 47~50) | 3.4 ms (p50 2.4, 최대 5.3) | 통과 |
| A1 첫 화면(현재 배율 타일로 다 덮임) | ≤ 0.5 s | **0.70 / 0.71 / 0.71 s** | (참고) 0.16 s | **미달** |
| 300쪽 100%로 끝까지 | ≤ 3 s | — | 1/180 길이씩 프레임마다: **3.02 s** (181프레임, 회색 0%); 뷰포트 1장씩: 7.35 s; 뷰포트 1/4씩: 29.3 s | 경계 (아래 설명) |
| 최대 작업 집합 | < 400 MB | 286 MB | **424 MB** (423.9 / 423.9) | **미달 (6%)** |
| 프레임 평균 회색 비율 | ≤ 2 % | (참고) 100%에서 스크롤 34%, 한 장씩 47% | 세 방식 모두 **0 %** | 통과(300쪽) |
| 회색 연속 최대 | ≤ 250 ms | (참고) 100 ms / 33 ms | **0 ms** | 통과(300쪽) |

- 회색 비율은 프레임의 "뷰포트 안 페이지 면적 중 아무 그림으로도 안 덮인 비율"(1 − `LastCoverage`)이고, 8px 격자 표본이다. "현재 배율 타일로만" 기준(흐릿한 대체 그림도 회색으로 침)은 300쪽 1/4 스크롤 0 %, 한 장씩 0 %, 3초 속도 1.1 %.
- **A1 첫 화면 0.7 s (미달)의 내역:** 열기 약 70 ms → 첫 쪽이 화면에 그려짐 약 145 ms → 첫 타일 렌더 완료 약 300 ms(PDFium 페이지 로드 약 180 ms + 타일 약 40 ms) → 나머지 보이는 타일을 한 장씩(약 40 ms) 순서대로 그려 약 700 ms에 완료. 타일마다 페이지 내용 전체를 다시 해석하고 PDFium은 한 스레드뿐이라 타일 수에 비례한다.
  - **"썸네일로 먼저 덮이는 시간"**: 앱에서 직접 재지 못했다(타일이 썸네일보다 우선이라 썸네일이 먼저 나오지 않고, "아무 그림으로 덮임" = "현재 배율로 덮임" 시각이 같게 나옴). 참고 수치는 S4 스파이크: A1 한 쪽 전체를 0.25배로 그리는 데 309 ms → 썸네일을 먼저 그려도 0.5 s 안에 덮이긴 하나(페이지 로드 180 ms + 309 ms ≈ 0.49 s) 여유가 거의 없다. **검증 필요.**
  - 대안(조정하지 않고 제안만): ① 첫 화면은 뷰포트 전체를 낮은 해상도 한 장으로 먼저 그리고 타일로 교체, ② 같은 페이지의 타일들을 한 번의 페이지 해석으로 이어서 렌더(PDFium 페이지 로드 재사용 방식 검토), ③ 목표를 "0.8 s"로 현실화(사용자 결정).
- **300쪽 3초 해석:** 프레임이 60 fps(16.7 ms)로 고정이라 스크롤 속도가 프레임 수로 정해진다. 위 3.02 s는 "길이의 1/180씩 프레임마다"로 맞춘 속도에서 끝까지 3.02 s 걸렸고 그동안 회색이 0 %였다는 뜻이다(렌더가 이 속도를 따라감). 더 빠른 속도에서의 한계는 재지 않았다. 참고: S4 스파이크 렌더 처리량은 쪽당 3.8 ms(300쪽 1.25 s).
- **작업 집합 424 MB (미달)의 원인 추정(검증 필요):** 타일 캐시 한도 160 MB + 왼쪽 썸네일 캐시 32 MB + 뷰 썸네일 캐시 32 MB + .NET·Avalonia·Skia 기본 사용량 + 타일 렌더 결과 바이트 배열과 비트맵의 일시 중복. 대안: 타일 캐시 한도를 약 100 MB로 낮춤(회색 비율 영향은 다시 재야 함), 비트맵 생성 뒤 원본 배열 즉시 반환.
- 한계: 창이 작다(782×581). 더 큰 창에서는 보이는 타일이 늘어 A1 첫 화면이 더 오래 걸릴 수 있다. 프레임 시각은 `RenderCount` 기준이라 모니터 주사율(여기서는 약 60 Hz)에 묶인다. 측정 중 PC의 다른 작업은 통제하지 않았다.
- 처음 만든 측정은 `RequestAnimationFrame`을 썼는데, 이 환경에서는 한 번 그리기 사이에 콜백이 수십 번 연달아 불려 프레임 시각이 무의미했다(첫 화면 1초로 잘못 나옴). `DocumentView.RenderCount` 방식으로 바꿔 재측정했고, 위 수치는 모두 바뀐 방식의 것이다.

## 전체 테스트 (Task 18)
`dotnet test PdfEditor.sln` 3회 연속(2026-10-16): 매번 실패 0 — Pdf.Native 56, Document 47, Pages 40, App 163, spikes 15(S1 3, S2 5, S3 5, S4 2). 크래시 0, 빌드 경고 0. `SettingsStoreTests.Settings_Recent_MaxTenNoDuplicates`는 가끔 실패하던 테스트이며(Task 13·14에서 관찰) Task 17에서 `LoadCore`에 IOException 재시도를 넣었다. 이번 3회와 이후 실행에서 재발하지 않았지만 **원인이 확인된 것은 아니다**(검증 필요; 재발 여부는 계속 지켜볼 것).

## 알려진 한계 (M1)
- 병합은 책갈피를 가져오지 않는다 (PDFium `FPDF_ImportPagesByIndex` 한계로 추정, 검증 필요).
- 삭제한 뒤 되살린(실행 취소) 페이지를 가리키던 책갈피는 끊길 수 있다.
- 텍스트 선택은 한 쪽 안에서만 된다. 빈 곳에서 시작한 드래그는 처음 닿은 글자부터 선택된다.
- 썸네일을 끌어서 순서를 바꾸는 기능은 없다 (위치 지정·위로·아래로 명령으로 대체).
- 레이어는 목록 표시만 되고 표시/숨김 전환은 2차로 미뤘다 (PDFium에 API 없음, 사용자 결정).
- 앱이 만드는 주석(M2~)은 반드시 Print 플래그(비트 4)를 켜야 인쇄된다 (`FPDF_PRINTING`은 Print 비트가 없는 주석을 그리지 않음).
- 복구 파일(M2~)은 PDFium 상태만 담는다. M2에서 주석 모델도 함께 기록해야 한다.
- 암호화된 PDF를 PDFsharp로 후처리하려면 소유자 암호가 필요하다 (V6 발견; 사용자 암호만 알면 후처리 불가).
- 100% 배율 = 1 px/pt이다 (실제 화면 크기는 약 133%에 해당). 이대로 둘지 사용자 결정 필요.
- 주석 탭 버튼이 약 30개라 설계서 5.1절("탭당 10~15개, 나머지 더보기")과 충돌한다 → M2 계획 때 결정.
- 빌드 함정: `dotnet build src/PdfEditor.App`는 `bin/Debug`, 솔루션 빌드는 `bin/x64/Debug`에 만든다. 오래된 쪽을 실행하기 쉽다(솔루션 빌드가 앱을 다시 컴파일하지 않은 적도 있음 — Task 14).
- 벤치 모드(`--bench`)로 시작하면 설정·복구 폴더를 임시 폴더로 바꾸고 명령줄의 PDF를 일반 열기로 열지 않는다.

## 사용자 직접 확인 필요 (자동 테스트·합성 입력으로 확인하지 못한 것)
- 실제 마우스·키보드 조작: Ctrl+휠 확대(커서 위치 고정), 손 도구 끌기, PageUp/PageDown, 한 쪽씩 보기, 슬라이더, 커서 mm 표시, Ctrl+C 복사와 "복사 제한" 안내, F3/Shift+F3, Ctrl+F, 썸네일 Delete 키·오른쪽 클릭 메뉴·Ctrl/Shift 다중 선택.
- 대화상자 실물: 열기/저장 파일 선택창, 암호 입력창, 저장 확인창, 쪽 삽입·이동·병합 창, 환경 설정 창, 정보 창(타사 고지 보기 단추), 복구 창의 모양, 첨부 파일 "저장" 단추, 드래그 앤 드롭 열기, 최근 문서 목록, 창 닫을 때 저장 확인.
- 인쇄: 실제 Win32 인쇄 창(`PrintDlgExW`) — 프린터 선택, 현재 쪽·쪽 범위, 매수·한 부씩, 파일로 인쇄; 취소; 실제 프린터 출력(용지·여백·방향, A1 플로터 포함). 다른 프린터 드라이버에서 HDC를 다른 스레드에서 쓰는 방식이 되는지.
- 다른 뷰어에서 보기: 페이지 편집 후 저장한 파일(+ 병합 문서)을 Acrobat·PDF-XChange·Chrome에서 열어 `docs/checklists/viewer-compat.md`의 M1 표를 채우기.
- 화면 모양: 창 제목 글자·⤡ 버튼 처리, 창 끌어 이동, Mica 적용 여부, 선택 강조색이 시스템 강조색(#005FB8이 아님), 첨부 목록 행 배치, 비활성 단추 모양.
- 제한 문서(소유자 제한·암호)로 패널·복사·인쇄·편집 제한 막대 동작, 책갈피 목적지가 없는 항목, 아주 큰 책갈피, 큰 문서에서 검색 중 스크롤.
- 복구 타이머 실제 간격 동작(첫 시도에서 1분 안에 기록이 안 보인 적 있음), 설정 창 열기.
- 상세한 확인 조건은 각 Task 보고서(`.superpowers/sdd/2026-10-15-m1-viewer-pages-print-panels/task-N-report.md`)의 "NOT verified" 항목.

## M1 마무리 검토 (Opus, 2026-10-16)
범위: 803f65b~36f4f1d (아이콘 e30ee24, 레이어 결정 6af1f18 포함). 계획서 Global Constraints와 Review Focus 1~5를 중심으로 브랜치 전체를 읽었다. 검토 커밋: b52806d, 853a5cd, 576cfe9, 5314eca (로컬만, push 안 함).

### 빌드·테스트
- 검토 시작 때: `dotnet build PdfEditor.sln` 경고 0·오류 0, `dotnet test PdfEditor.sln` 321개 모두 통과.
- 검토 끝: 경고 0, **326개 모두 통과** (Pdf.Native 59, Document 47, Pages 42, App 163, spikes 15). 추가한 테스트 5개: 우선 줄 2개(`InvokeHigh_RunsBeforeQueuedNormalWork`, `Dispose_WithPendingHighWork_CancelsPending`), 버전 2개(아래 1), 첨부 이름 1개(아래 화면 문제 조사).

### 규칙 점검 결과
| 항목 | 결과 |
|---|---|
| 전용 스레드 밖 PDFium 호출 | 없음. `PdfDocument`·`PdfPage`·`PdfTextPage`가 모든 호출 전에 `AssertOnThread()`. 앱의 `PrintInterop`·`BitmapHandle`은 Win32/Avalonia 호출만 한다. |
| UI 스레드에서 `.Wait()`/`.Result` | 직접 쓴 곳은 없음. 다만 `PdfDocument.Dispose`가 내부에서 PDFium 작업을 동기로 기다려서, UI 스레드에서 닫으면 그만큼 멈춘다. 그런 곳 2군데(파일에서 삽입의 원본 문서, 세션 닫을 때 명령이 가진 문서)를 전용 스레드에서 닫게 고침(576cfe9). |
| 하드코딩된 한글 문구 | 앱(`.cs`·`.axaml`)에는 주석 외에 없음. `Pdf.Native` 예외 메시지(개발자용)와 `SaveService` 검증 메시지는 화면에 그대로 나가지 않고 `ErrorMessages`가 리소스 문구로 바꾼다. |
| 저장 파이프라인 | 계획대로(임시 파일 → 다시 열어 쪽 수·주석 수 검증 → `File.Replace`, 실패 시 임시 파일 삭제). 문서를 메모리에서 열기 때문에 원본 파일을 잠그지 않는다. 문제 없음. |
| PageId·버전 | **결함 1건 수정**(아래 1). 렌더 직전 PDFium 스레드에서 PageId로 인덱스를 다시 찾는 구조는 맞다. |
| Review Focus 2~5 | 테스트가 계획대로 있고 통과. 첨부 이름은 화면 확인까지 함(아래 "화면 문제 조사"). |

### 고친 결함
1. **(Review Focus 1) 버전 번호 재사용** — 853a5cd. 쪽을 회전(버전 1) → 삭제 → 실행 취소로 되살리면 버전이 0으로 돌아갔다. 그러면 회전 전에 요청된 v0 타일·썸네일이 늦게 도착해도 "현재 것"으로 통과될 수 있다(크기·방향이 틀린 그림). 파일에서 삽입을 취소 → 다시 실행할 때도 같다. 이제 같은 PageId가 돌아오면 지난 번호 다음 번호를 받는다. 테스트: `RotateDeleteUndo_NeverReusesEarlierVersion`, `ImportUndoRedo_NeverReusesEarlierVersion`(고치기 전 실패 확인).
2. **인쇄 중 쪽 불러오기 실패 시 진행 창이 남음** — 576cfe9. `PrintService`가 `PrintException`만 잡아서, 손상된 쪽에서 `PdfiumException`이 나면 진행 창이 닫히지 않고 프린터 DC가 풀리지 않았다. 모든 실패를 같은 길(창 닫기 → DC 반환 → 오류 안내)로 처리. 자동 테스트 없음(대화상자 흐름) — 검증 필요: 실제 인쇄 창에서 수동 확인.
3. **문서가 없을 때 "페이지 수정이 제한되어 있습니다" 막대가 보임** — 5314eca. 앱 창 캡처로 발견. 문서가 없으면 바인딩이 실패해 `IsVisible`이 기본값(보임)으로 남았다. `FallbackValue=False`.
4. **제목 줄 중복 장식** — 5314eca. 아래 "화면 문제 조사" 참고.

### 성능 미달 2건: 원인과 조치
| 항목 | 목표 | 검토 전 (이 PC 재측정) | 검토 후 | 판정 |
|---|---|---|---|---|
| A1 첫 화면(현재 배율 타일로 다 덮임) | ≤ 0.5 s | 0.88 / 0.81 s (보고서 값 0.70 s) | **0.47 / 0.44 / 0.47 s** | 통과 (여유 작음) |
| 300쪽 최대 작업 집합 | < 400 MB | 428 / 428 MB (보고서 값 424 MB) | **362 / 374 / 363 MB** | 통과 |
| 300쪽 회색 비율(세 스크롤 방식) | ≤ 2 % | 0 % | 0 % (연속 최대 0 ms) | 통과 |
| 300쪽 100% 끝까지(1/180씩) | ≤ 3 s | 3.02~3.03 s | 3.02~3.03 s | 변화 없음(경계) |
| 512 타일 p95 | ≤ 100 ms | A1 67~73 ms | A1 50~55 ms | 통과 |
- 측정: `PdfEditor.App.exe --bench` (Release), A1 3회·300쪽 3회, 같은 PC·같은 창 크기(782×581). 숫자는 조정하지 않았다. 원본 JSON은 세션 임시 폴더(저장소 밖).
- **A1 원인:** 첫 타일이 그려진 뒤(약 0.36 s) 다음 타일까지 약 0.37 s가 비었다. 왼쪽 썸네일 패널이 A1 쪽 전체를 그리는 작업(약 0.3 s)이 PDFium 대기열(먼저 온 순서)에서 화면 타일 사이에 끼어 있었다. **조치(b52806d):** `PdfiumThread`에 우선 줄을 두고 화면에 보이는 타일만 그 줄로 보낸다(썸네일·검색·저장 등은 일반 줄). 실행 중인 작업을 끊지는 않는다.
- **메모리 원인:** 타일 캐시 한도 160 MB가 그대로 작업 집합에 더해진다. 한도를 96 MB로 바꾸자 작업 집합이 거의 같은 양(약 65 MB) 줄었다. 타일 렌더 버퍼를 `ArrayPool`로 재사용하는 방법도 시험했으나 효과가 없어(428→434 MB) 넣지 않았다. **조치(b52806d):** 타일 캐시 기본 한도 160 → 96 MB.
- 남는 위험(검증 필요): ① 큰 모니터(예: 2560×1440)에서 A1을 크게 확대하면 보이는 타일 + 한 타일 링이 약 50 MB라 96 MB 안에 들지만, 이전 배율 타일(흐릿한 대체 그림)이 더 빨리 밀려날 수 있다. ② 창이 크면 A1 첫 화면의 보이는 타일 수가 늘어 0.5 s를 넘을 수 있다(작은 창 기준 여유 약 30 ms).
- 참고(고치지 않음): 계획서는 썸네일 캐시를 썸네일 패널과 문서 화면이 **함께** 쓰도록 했는데, 구현은 각자 캐시·렌더러를 따로 둔다. 그래서 같은 쪽 썸네일을 두 번 그린다(A1은 한 번에 약 0.3 s). 메모리 목표는 이미 맞으므로 M2 이후 정리 후보로 남긴다.

### 화면 문제 조사 (앱 창만 PrintWindow로 캡처, 입력은 보내지 않음)
- **제목 줄 "PDF 에디터" 글자와 ⤡ 버튼:** Avalonia 12.1.4는 `ExtendClientAreaToDecorationsHint`일 때 창 장식을 직접 그린다. 시각 트리에서 확인한 부품 이름은 제목 글자 `PART_TitleTextPanel`(안의 TextBlock)과 전체 화면 버튼 `PART_FullScreenButton`. `App.axaml` 스타일로 둘 다 숨겼고, 제목 글자를 피하려 띄워 둔 빠른 실행 단추의 왼쪽 여백(100 px)을 12 px로 줄였다. 캡처에서 최소화·최대화·닫기 3개만 남은 것을 확인. 검증 필요: 빈 제목 영역을 끌어 창 이동(입력을 보내지 않아 확인 못 함), 패널 쪽 `IsVisible`은 장식 코드가 다시 켜므로 글자만 숨겼다(Avalonia 버전이 바뀌면 이름 재확인 필요).
- **첨부 이름 `..\vil.txt`:** 데이터 문제가 아니다. PDFium이 돌려준 이름과 화면 TextBlock의 글자 코드가 모두 `..\evil.txt`(역슬래시 0x5C 포함)임을 확인했다(테스트 `GetAttachments_NameWithBackslash_KeptAsIs` 추가). 화면에서는 **한글 글꼴(맑은 고딕)이 역슬래시를 원화 기호 `₩`로 그려서** `..₩evil.txt`로 보인다(한국어 Windows의 오래된 관례). 구현 세션이 본 `..\vil.txt`는 캡처를 잘못 읽었거나, C# 13의 `"\e"`(ESC 문자) 때문에 실제로 다른 이름이 들어간 시험 파일이었을 가능성이 높다(검증 필요, 재현 안 됨). 저장할 때 제안 이름은 `evil.txt`로 안전하다.
- 같은 캡처에서 본 것: 앱 전체 글꼴이 맑은 고딕으로 보인다(`Segoe UI Variable Text`가 잡히지 않는 것으로 추정, 검증 필요). 첨부 목록의 "저장" 단추가 패널 오른쪽 끝이 아니라 이름 바로 옆에 붙는다(모양 문제, 고치지 않음).

### 사용자 결정이 필요한 것 (고치지 않음)
1. **100% 배율 기준:** 지금 1 pt = 1 px. Acrobat은 96 dpi 기준(1 pt = 1.333 px)이라 같은 "100%"가 Acrobat보다 작게 보인다. 바꾸면 타일·메모리 수치도 다시 재야 한다.
2. **주석 탭 버튼 수:** 약 30개 vs 설계서 5.1절 "10~15개" — M2 계획 때 결정.
3. **글꼴(선택):** 역슬래시가 `₩`로 보이는 것을 그대로 둘지(한국어 Windows에서 흔함), 파일 이름처럼 경로가 보이는 곳만 다른 글꼴로 바꿀지.
- V5(PDFium 배포 라이선스)는 상위 세션이 완료했다(20a9899) — 결정 목록에서 뺐다.

## 사용자 검수 요청 (M1) — 쉬운 설명
**1. 앱 실행 방법**
- 탐색기에서 `D:\INBOX_WP\95. Claude\PDF EDITOR\src\PdfEditor.App\bin\Release\net10.0-windows\win-x64\PdfEditor.App.exe`를 더블클릭하세요. (먼저 터미널에서 `dotnet build src/PdfEditor.App -c Release`를 한 번 실행해야 이 파일이 최신이 됩니다. 터미널이 어려우면 저에게 "빌드해 줘"라고만 말씀하세요.)
- 또는 폴더에서 `dotnet run -c Release --project src/PdfEditor.App -- 열고싶은파일.pdf` 를 실행해도 됩니다.
- 시험용 큰 파일: `spikes/out/bench-a1-drawing.pdf`(A1 도면 1장), `spikes/out/bench-a4-300pages.pdf`(300쪽). 회사 도면 같은 실제 파일로도 꼭 열어 보세요(원본은 건드리지 말고 사본으로).

**2. 확인해 주세요 (되면 ○, 이상하면 한 줄 메모)**
1. 열기·보기: 파일 열기(끌어다 놓기 포함), 확대·축소(Ctrl+마우스 휠), 스크롤할 때 회색 빈칸이 눈에 띄는지, 다 열렸을 때 체감 속도. 특히 A1 도면 첫 화면이 "조금 기다린다"고 느껴지는지(마무리 검토 후 측정 0.44~0.47초, 목표 0.5초).
2. 페이지 편집: 삭제·회전·이동·빈 페이지 삽입·다른 PDF에서 삽입·병합, 실행 취소/다시 실행. 저장한 뒤 **Acrobat이나 크롬에서 열어** 쪽 순서와 회전이 맞는지.
3. 인쇄: Ctrl+P → 인쇄 창이 뜨는지, "현재 쪽"·"쪽 범위"가 맞게 나오는지, 실제 프린터(또는 PDF로 인쇄)로 용지에 맞게 나오는지.
4. 패널: 왼쪽 썸네일·책갈피·첨부·레이어(보기만)·검색, 오른쪽 문서 속성. 검색 결과 클릭 이동, 글자 드래그 선택 후 Ctrl+C.
5. 저장·복구: 편집 후 저장(원본이 다른 프로그램에서 열려 있으면 "사용 중" 안내), 편집 중 작업 관리자로 앱을 강제 종료한 뒤 다시 켜면 복구 창이 뜨는지. 파일 메뉴 → 정보 → "타사 고지 보기".

**3. 결정해 주셔야 하는 것**
1. ~~V5 라이선스~~ → **해결 (2026-10-16).** 사용자 승인으로 릴리스 파일을 받아 확인했습니다. 모두 기업에서도 무료로 쓸 수 있는 허용형 라이선스이고, 원문을 앱과 함께 배포하도록 넣었습니다 (V5 절).
2. ~~성능 목표 두 가지 미달~~ → **해결 (마무리 검토, 2026-10-16).** A1 첫 화면 0.44~0.47초, 300쪽 메모리 362~374 MB로 두 목표 모두 맞췄습니다(목표 숫자는 그대로). "M1 마무리 검토" 절 참고.
3. **100% 배율 기준:** 지금은 PDF 1pt = 화면 1px(실제 크기보다 작게 보임, 실제 크기는 약 133%). 이대로 둘지, "실제 크기"를 100%로 바꿀지.
4. **레이어 켜고 끄기**는 2차로 미룬 것(이미 결정)을 유지할지 재확인.
5. **주석 탭 버튼 수(약 30개 vs 설계서 10~15개)**는 M2 계획 때 다시 묻겠습니다.
6. (선택) 첨부 이름 등에서 역슬래시(`\`)가 `₩`로 보이는 것(한글 글꼴 관례)을 그대로 둘지.
