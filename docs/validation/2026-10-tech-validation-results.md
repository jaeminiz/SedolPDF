# 기술 검증 S0~S4 결과 (2026-10-15)

- 계획서: `docs/superpowers/plans/2026-10-15-tech-validation.md`
- 실행: Sonnet 세션. 측정 환경은 Windows 11, 12 논리 코어, .NET 10.0.13 (Release).
- 표기: **검증 필요**는 이번 검증으로 확정하지 못한 것입니다.

## 1. 요약

| ID | 판정 | 한 줄 결론 |
|---|---|---|
| S0 | 성공 | .NET 10 + Avalonia 12.1.4 + PDFium 157.0.8100 환경에서 전용 스레드로 열기·저장·타일 렌더링 동작. HarfBuzzSharp은 8.3.1.3으로 확정 |
| S1 | 성공 (일부 뷰어·항목 미확인) | 한글 FreeText AP는 "PDFium 저장 → PDFsharp 후처리" 2단계로 가능. Acrobat Pro·PDF-XChange·Chrome에서 정상. PDFium 단독(경로 A)은 불가 |
| S2 | 부분 성공 (미해결 결함) | 네이티브 `hb_subset_*`를 직접 호출하면 서브셋은 되지만, **간헐적으로 접근 위반(0xC0000005)으로 프로세스가 죽음.** 원인 조사 필요 |
| S3 | 부분 성공 | 줄 교체는 동작. 폰트에 글자가 있는지 판정하는 방법과 PDFium 방식 폰트 대체는 실제 도면·다른 뷰어에서 통하지 않음 → M4 정책 변경 필요 |
| S4 | 성공 | 전용 스레드 하나로 목표 달성 가능. Hidden 플래그 방식(4.2-4) 채택 |

## 2. 고정 버전 (기업에서도 무료로 쓸 수 있는 라이선스만)

| 패키지 | 버전 | 라이선스 | 비고 |
|---|---|---|---|
| .NET SDK | 10.0.402 | MIT | `global.json`으로 고정 |
| bblanchon.PDFium.Win32 | 157.0.8100 | Apache-2.0 (패키지 표기) | 라이선스 원문·NOTICE 요구사항은 V5에서 확인 (검증 필요) |
| Avalonia (+ Desktop, Themes.Fluent) | 12.1.4 | MIT | 12 계열 안정판 |
| HarfBuzzSharp (+ NativeAssets.Win32) | 8.3.1.3 | MIT | Avalonia.Skia 12.1.4가 요구하는 버전 |
| PDFsharp | **6.2.4** | MIT | **6.5.0은 AGPL-3.0이라 사용 금지.** 6.2.x에서만 올릴 것 |
| xUnit / runner / Test.Sdk | 2.9.3 / 3.1.5 / 18.11.0 | Apache-2.0 / Apache-2.0 / MIT | |
| System.Security.Cryptography.Pkcs | 10.0.13 (테스트 프로젝트만) | MIT | PDFsharp이 끌어오는 8.0.1에 알려진 취약점이 있어 직접 지정 |
| Noto Sans KR (SubsetOTF/KR Regular, CFF) | noto-cjk main | OFL-1.1 | `assets/fonts/`. 배포 시 OFL 원문 동봉 |

- HarfBuzzSharp 최신 14.2.1.301은 Avalonia.Skia(SkiaSharp 3.119.4)가 요구하는 8.3.1.3과 달라 네이티브 충돌 위험이 있어 쓰지 않았습니다. 8.3.1.3에서 S1·S2 테스트가 모두 통과했습니다.
- 위 패키지는 NuGet 라이선스 표기로 확인했습니다. 새 패키지를 추가할 때마다 같은 확인이 필요합니다.

## 3. S0 — 개발 환경과 PDFium 래퍼

**판정: 성공.**
- 근거: `Pdf.Native.Tests` 16개 통과 (`PdfiumThreadTests` 8개 + `PdfDocumentTests` 8개). 빌드 경고 0.
  - 한글·공백 경로 열기 (`Open_KoreanPath_Succeeds`), 암호 PDF 예외 (`Open_Encrypted_WithoutPassword_ThrowsPasswordRequired`), 깨진 파일의 형식 오류 코드 3, 전용 스레드 밖 호출 즉시 예외, 디스패처 종료 중 대기 작업 취소, A1을 4배로 그릴 때 타일 크기만 할당 (`RenderTile_LargePageHighZoom_AllocatesOnlyTile`).
- Avalonia 12.1.4 창이 정상으로 떠서 12초간 실행했고, 사용자가 직접 스크롤·확대를 확인했습니다.
- 계획서와 달라진 점:
  1. `PdfDocument.AddPage(width, height)`를 추가했습니다 (새 문서 왕복 테스트에 필요).
  2. PDFium은 한 번에 한 스레드만 쓸 수 있어서 테스트 병렬 실행을 껐습니다.
  3. `PdfiumThread` 테스트를 2개 더 넣었습니다 (재진입, 전용 스레드 안 `AssertOnThread`).
  4. 닫는 중에 대기하던 작업이 취소되지 않고 실행되는 결함을 테스트로 발견해 고쳤습니다.
- 설계 영향: 3절에 버전 반영 (이 문서 2절).

## 4. S1 — 한글 FreeText AP

**판정: 성공 (뷰어 일부·항목 미확인).**
- 경로 A (PDFium만): **불가.** `FPDFAnnot_SetAP`는 내용 스트림 문자열만 받고, `/Resources`(폰트 자원)를 붙이는 API가 `pdfium.dll` 내보내기에 없습니다. (`FPDFAnnot_AppendObject`로 폰트가 든 텍스트 객체를 AP에 넣는 변형은 S3에서 폰트 내장 방식이 규격에 어긋나 불채택.)
- 경로 B (PDFium 저장 → PDFsharp 후처리): 테스트 3개 통과.
  - `FreeTextKorean_SavedAp_RendersInPdfium`: 주석 포함 렌더에서 비흰색 비율 2% 초과, 주석 제외 렌더에서 0.
  - `FreeTextKorean_Font_IsEmbeddedSubsetWithToUnicode`: Type0 / Identity-H, 서브셋 접두어, `/ToUnicode`, `FontFile3` 확인.
  - `FreeTextKorean_UnknownKeysOfOtherAnnots_Preserved`: 다른 주석의 사용자 키 보존 (4.2-5).
- **핵심 교훈:** Noto Sans KR은 CID 키 CFF입니다. OpenType 그대로(`FontFile3 /OpenType`) 넣으면 PDFium에서 글자가 안 보였습니다. **순수 CFF 테이블만 꺼내 `FontFile3 /CIDFontType0C`로 넣고, 글자 코드는 CFF charset의 CID**로 써야 합니다. `CffCharset.GetCidByGid`가 그 대응표를 읽습니다.
- 뷰어 호환성 (사용자 스크린샷, `docs/checklists/viewer-compat.md`):

| 뷰어 | 글자 표시 | 모양 동일 | 이동 후 유지 | 텍스트 복사 |
|---|---|---|---|---|
| Acrobat Pro | ○ | ○ | 미확인 | 미확인 |
| PDF-XChange Editor | ○ | ○ | 미확인 | 미확인 |
| Chrome 기반 | ○ | ○ | 해당 없음 | 미확인 |
| Foxit | 미확인 | 미확인 | 미확인 | 미확인 |

  Reader가 아닌 Pro로 확인했습니다 (검증 필요).
- 현재 한계: 줄바꿈은 `\n`만 지원(자동 줄바꿈 없음), 커닝은 `/W` 폭만 사용.
- 설계 영향: 8.2절 저장 파이프라인을 **2단계로 확정**. 1.4-2·3(다른 뷰어에서 같은 모양·한글)은 위 범위까지만 검증됨.

## 5. S2 — 폰트 서브셋

**판정: 부분 성공 — 기능은 동작하나 간헐 크래시 미해결.**
- HarfBuzzSharp 관리 API에 `hb_subset_*`가 없습니다. 네이티브 `libHarfBuzzSharp.dll`은 `hb_subset_or_fail` 등을 내보내서 `FontSubsetter`(`spikes/S2.FontSubset`)가 직접 P/Invoke로 호출합니다.
- 근거: 3개 통과 (요청한 글자만 남음, 원본의 5% 미만, 빈 집합은 `.notdef`만).
- **미해결 결함:** `hb_subset_or_fail` 안에서 접근 위반(0xC0000005)이 간헐적으로 발생합니다. 전체 솔루션 테스트를 5~10번 돌리면 1번꼴로 S2·S3 테스트 호스트가 죽었고, 같은 서브셋을 400번 반복하면 8번 중 5번 죽었습니다. HarfBuzzSharp 8.3.1.3과 14.2.1.301 모두 같아서 라이브러리 버전 문제는 아닌 것으로 보이며, `FontSubsetter`의 P/Invoke 사용 방식(수명·해제 순서 등)을 의심하지만 **원인은 미확정(검증 필요)**입니다. 재현 테스트는 `spikes/S2.FontSubset/SubsetCrashRepro.cs`(평소에는 Skip)입니다. 제품 코드에 들어가기 전에 반드시 원인을 찾아 해결해야 합니다.
- 설계 영향: 3절의 서브셋 경로(네이티브 `hb_subset_*` 직접 호출)는 유지하되, 크래시 해결을 M1 착수 전 조건으로 둡니다.

## 6. S3 — 텍스트 줄 교체와 폰트 대체

**판정: 부분 성공.**
- 합성 샘플 (PDFium 렌더·추출 기준) 테스트 4개 통과:
  1. 서브셋 폰트에 있는 글자로 바꾸기 (`FPDFText_SetText`) → 정상.
  2. 폰트에 없는 글자 감지: `FPDFTextObj_GetFont` + `FPDFFont_GetFontData` + HarfBuzz cmap 조회 → 정상.
  3. 대체: 원래 객체를 지우고 같은 행렬로 `FPDFText_LoadFont`(Noto) 텍스트 객체 생성 → PDFium에서 정상.
- **문제 1 — PDFium 방식 폰트 대체는 다른 뷰어에서 깨집니다.** `FPDFText_LoadFont`는 Noto(CFF)를 서브셋 없이 통째로(약 3.8MB) `FontFile2 + CIDFontType2`로 넣습니다. Acrobat Pro·PDF-XChange에서 `가`가 네모(□)로 보였고 Chrome만 정상이었습니다. → **불채택.** M4의 폰트 대체는 S1 방식(서브셋 + `CIDFontType0C`, PDFsharp 후처리)으로 합니다.
- **문제 2 — 폰트에 글자가 있는지를 cmap으로 판정하는 방식은 실제 도면에서 통하지 않습니다.** (사용자 도면 4개, 파일명·내용 비공개, 앞 3~5쪽, 텍스트 객체 최대 300개 표본)
  - 텍스트가 없는 도면 2개: 글자가 외곽선으로 바뀐 도면(경로만 있음)과 스캔 이미지 도면(이미지만 있음)으로 추정됨. 이 경우 M4·M5는 쓸 수 없습니다.
  - 텍스트가 있는 도면 2개 (영문·숫자): 한쪽은 영문·숫자 판정이 약 65%에서 되고 다른 쪽은 0%.
  - **한글이 든 도면 2개**: 한글 포함 텍스트 객체가 143개와 33개 있었는데 폰트 조회로 글자가 확인된 객체는 0개였습니다.
  - 원인은 서브셋 폰트에 유니코드 cmap이 없거나(CID 키, Identity-H) 돌려받은 폰트 데이터가 우리 도구가 읽는 형태가 아닐 수 있습니다 (원인 미확정, 검증 필요). 합성 샘플의 통과는 cmap이 있는 폰트에서만 유효합니다.
- 일반화 한계: 도면 4개, 앞 일부 쪽, 표본 기준입니다.
- 설계 영향 (사용자 확인 필요, 8절 참고).

## 7. S4 — 타일 렌더링과 지원하지 않는 주석 표시

**판정: 성공.**
- 4.2-4 (Hidden 플래그): `HiddenFlag_HidesSupportedType_KeepsUnsupported`, `HiddenFlag_RestoredFlags_NotWrittenOnSave` 2개 통과. 지원 유형(Square)은 사라지고 미지원 유형(Squiggly)은 그려지며, 저장 파일에 플래그가 남지 않았습니다. → **채택.**
- 성능 (Release, 12 논리 코어; 샘플은 `BenchPdfFactory`가 생성):

| 측정 | 결과 |
|---|---|
| A1 도면(선 20만 + 텍스트 5천) 파일 열기 / 페이지 로드 | 13 ms / 203 ms |
| 512×512 타일 p50 / p95: 0.25배 | 314 ms / 314 ms (표본 2개, 페이지 전체가 거의 한 번에 들어가는 경우) |
| 〃 1배 | 40 ms / 61 ms |
| 〃 4배 | 12 ms / 19 ms |
| A4 300쪽 100% 배율로 처음부터 끝까지 (쪽당 6타일) | 1.75 초 (172쪽/초, 1,030타일/초), 쪽당 p95 8.9 ms |
| 작업 집합 메모리 최대 | 221 MB |

  300쪽 샘플은 이미지 데이터를 모든 쪽이 공유합니다.
- 체감 (사용자): 창 정상, 스크롤 끊김 없음, 확대·축소는 빠름. 처음과 확대 직후에 회색 빈칸이 보임 → M1에서 확대 중 이전 배율 타일을 흐릿하게 임시 표시하고 화면 밖 한 타일을 미리 렌더.
- 전용 스레드 하나로 충분해서 "렌더링 전용 문서 인스턴스"는 지금 필요 없습니다.
- **성능 목표 제안 (설계서 1.4-4):**
  1. 512×512 타일 p95 100 ms 이하 (밀집한 A1 도면 1배 실측 61 ms)
  2. A1 도면 첫 화면 표시 0.5초 이하
  3. 300쪽 문서를 100%로 끝까지 훑는 데 3초 이내, 작업 집합 400 MB 미만
  4. 확대·스크롤 때 회색 빈칸이 눈에 띄지 않게 (정량 기준은 M1에서 정함)

## 8. 결정이 필요한 것

8.1. **M4·M5 대상.** 텍스트가 없는 도면(외곽선·스캔)이 실제로 있으므로, 이런 PDF를 열면 "편집 가능한 텍스트가 없습니다"라고 알려 주는 동작이 필요합니다. M4·M5가 주로 다룰 PDF 종류를 정해 주세요.
8.2. **판정 불가 폰트의 처리.** 폰트에 글자가 있는지 판정하지 못하면 기본값을 "고친 줄 전체를 앱 폰트로 대체"로 하는 안을 제안합니다. 대안인 "PDFium에 글자를 설정한 뒤 다시 추출해 맞는지 확인", ToUnicode 역매핑은 M4 설계 때 검증합니다.
8.3. **한글 UI·매뉴얼.** 화면 언어와 사용자 매뉴얼은 설계서에 없습니다. M1 계획 때 범위에 넣을지 정해야 합니다.

## 9. 남은 검증

- **S2 간헐 크래시 원인 조사·해결 (M1 착수 전 필수).**
- S1: Foxit, 주석 이동 후 유지, 텍스트 복사, Acrobat Reader(Pro가 아닌).
- V5 (PDFium 라이선스 원문·NOTICE), V6 (암호 PDF 저장 시 암호화 유지; 이번 2단계 저장에서 PDFsharp이 암호 PDF를 어떻게 다루는지 포함)은 M1.
- S3 실제 도면 일반화 (더 많은 도면·쪽·폰트).
