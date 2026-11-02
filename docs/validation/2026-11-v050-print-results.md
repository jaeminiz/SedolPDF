# 0.5.0 인쇄 개편 — Windows API 확인과 결과

작성: 2026-11-03 (브랜치 `claude/v050-print-f2c966`). 이 PC: Windows 11 10.0.26300, 프린터 8개(SINDOH PCL 2·FAX, Microsoft Print to PDF, Adobe PDF, ZWCAD, OneNote 2). **물리 프린터로는 보내지 않았다** — 시험은 읽기 전용 조회와 Microsoft Print to PDF(파일 출력)만 썼다.

## 1. 0.4.0 인쇄 경로가 쓰던 API

| 단계 | API | 비고 |
|---|---|---|
| 옵션 | Avalonia `PrintOptionsDialog` | 주석 포함/제외·맞춤/실제 크기·자동 회전 3가지뿐, 미리보기 없음 |
| 프린터·쪽 범위·매수·한 부씩 | comdlg32 `PrintDlgExW` (`PD_RETURNDC`, `PD_USEDEVMODECOPIESANDCOLLATE`) | Windows 공용 대화상자가 프린터 선택·매수·한 부씩·드라이버 속성(양면 등)을 모두 맡고 **DC를 돌려줌**. 미리보기는 이 대화상자에 없다 |
| 그리기 | gdi32 `StartDocW/StartPage/RenderToDc(FPDF_RenderPage)/EndPage/EndDoc/AbortDoc` | PDFium 전용 스레드에서 호출(HDC를 만든 스레드와 다른 스레드 — PrintJobTests로 확인됨) |

즉 사용자가 말한 "인쇄 미리보기가 안 된다"는 고장이 아니라 **처음부터 없는 기능**이다(코드 흔적: 인쇄 폴더·문자열 어디에도 미리보기 없음. 찾기·바꾸기·주석 슬라이더의 "미리보기"는 다른 기능).

## 2. 새 대화상자가 직접 지정하려면 필요한 것

한 화면(우리 대화상자)에서 옵션을 모두 받으려면 `PrintDlgExW`를 쓰지 않고 **DEVMODE를 직접 만들어 `CreateDCW`에 넘긴다**.

| 필요한 것 | API | 이 PC 확인 |
|---|---|---|
| 프린터 목록·기본 프린터 | winspool `EnumPrintersW`(level 4, 로컬+연결), `GetDefaultPrinterW` | 8개 모두 나옴 |
| 기본 DEVMODE | `OpenPrinterW` + `DocumentPropertiesW(DM_OUT_BUFFER)` (크기는 출력 null로 먼저 조회) | 성공 |
| 방향·용지·양면·색·매수·한 부씩 | DEVMODE 필드 `dmOrientation/dmPaperSize/dmDuplex/dmColor/dmCopies/dmCollate` + `dmFields` 비트 → `DocumentPropertiesW(DM_IN_BUFFER\|DM_OUT_BUFFER)`로 드라이버가 합치게 함 → `CreateDCW("WINSPOOL", 이름, null, devmode)` | 방향·용지: Print to PDF에서 장치 크기가 실제로 바뀜(A4 세로 4961×7016 ↔ 가로 7016×4961 @600dpi, Letter 선택 시 세로 길이 달라짐) |
| 용지 목록 | `DeviceCapabilitiesW(DC_PAPERS, DC_PAPERNAMES, DC_PAPERSIZE)` | Print to PDF 84종, A4(9)·Letter(1) 포함 |
| 프린터 능력 | `DeviceCapabilitiesW(DC_COPIES, DC_DUPLEX, DC_COLORDEVICE, DC_COLLATE)` | Print to PDF: 매수 최대 **1**, 양면 없음, 컬러, 한 부씩 없음 |
| 드라이버 전용 값(트레이·화질) | `DocumentPropertiesW(DM_IN_PROMPT)` — 드라이버 속성 대화상자 | 코드 포함, 대화상자는 화면 조작이라 이 세션에서 열지 않음 |
| 미리보기용 용지·인쇄 가능 영역 | 만든 DC의 `GetDeviceCaps`(HORZRES/VERTRES/LOGPIXELS/PHYSICAL*) | 확인됨 |

### 결정
- **매수·한 부씩:** 드라이버가 처리하면(`DC_COPIES` ≥ 요청 매수) DEVMODE로 맡기고, 못 하면(Print to PDF처럼 최대 1) **프로그램이 직접 반복**한다(`PrintPlan`). 양면에서는 한 부 장 수가 홀수일 때 빈 장을 끼워 다음 부가 앞 부 뒷면에 찍히지 않게 하고, 한 부씩이 아니어도 한 부씩으로 본다.
- **흑백:** 컬러 프린터면 `dmColor=MONOCHROME` + PDFium `FPDF_GRAYSCALE`(드라이버가 흑백을 무시해도 그레이로 그려짐). 미리보기도 회색조.
- **방향 "자동":** 첫 인쇄 쪽의 방향을 따른다(2쪽 모아 찍기는 반대 — 세로 쪽 두 장을 가로 용지에 나란히).
- 한 작업(StartDoc~EndDoc) 안에서 용지·방향을 쪽마다 바꾸는 것(`ResetDC`)은 하지 않는다 → **0.5.1 이후**.

## 3. 막히는/미루는 옵션 (0.5.1 이후)
| 옵션 | 이유 |
|---|---|
| 쪽마다 다른 용지·방향(혼합 크기 문서를 자동으로 각 쪽 크기에 맞춰 인쇄) | `ResetDC`로 쪽마다 DEVMODE를 바꿔야 하고 드라이버별 동작 차이가 커서 실프린터 검증 필요 |
| 프린터별 사용자 설정 기억 | 범위 밖(0.6 이후로 이미 분류) |
| 실제 물리 프린터(SINDOH 등)의 양면·매수 동작 | 이 세션에서 물리 프린터 출력 금지 → **검증 필요**(아래 결과 절) |
| 드라이버 속성 대화상자 | 코드는 넣었으나 화면 조작 금지라 열어 보지 않음 → **검증 필요** |

(구현이 끝난 뒤 아래에 결과·수치를 이어서 적는다.)
