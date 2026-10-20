# 배포 패키지 결과 (설치 파일 + 포터블)

2026-10-17. 만드는 법은 `build/README.md`, 계획서는 `docs/superpowers/plans/2026-10-16-packaging-installer-portable.md`.

## 만들어진 파일 (버전 0.1.0)
| 파일 | 크기 |
|---|---|
| `artifacts/SedolPDF-0.1.0-setup-win-x64.exe` (설치 파일) | 약 37.4 MB |
| `artifacts/SedolPDF-0.1.0-portable-win-x64.zip` (포터블) | 약 51.0 MB |

두 파일 모두 git에는 올라가지 않습니다(`artifacts/`는 제외됨). 설치 후 크기는 설치 파일 기준 파일 261개입니다. 두 패키지 모두 `LICENSE`(MIT), `THIRD-PARTY-NOTICES.md`, `licenses/pdfium/*`(18개), `assets/fonts/OFL.txt`가 들어 있습니다. `.pdb`는 뺐습니다.

## 설치 파일 설정 요약
- 도구: Inno Setup 6.7.3 (`winget install JRSoftware.InnoSetup`, 공식 jrsoftware.org 배포본, GitHub 릴리스 `is-6_7_3`에서 받음). 현재 사용자 위치 `%LOCALAPPDATA%\Programs\Inno Setup 6`에 설치됨(약 33 MB). 한국어 번역 `Korean.isl`은 기본 포함이라 따로 받지 않았습니다.
- 기본값은 현재 사용자용(관리자 권한 불필요), 설치 시작 화면에서 "모든 사용자용"을 고를 수 있습니다. 기본 위치 `%LOCALAPPDATA%\Programs\SedolPDF`.
- 선택 작업(둘 다 기본 끔): 바탕 화면 바로가기, PDF "연결 프로그램"에 추가(기본 프로그램은 바꾸지 않음). 시작 메뉴 바로가기는 항상 만듭니다.
- 설치 시작 부분에 MIT 라이선스 고지를 안내 화면(동의 클릭 없음)으로 보여 줍니다. 한글이 깨지지 않도록 BOM이 있는 사본 `build/installer/LICENSE.txt`를 쓰고, `package.ps1`이 루트 `LICENSE`와 같은지 검사합니다. 마지막 화면: "SedolPDF 실행"(기본 켬), "타사 고지 보기"(메모장으로 열기, 기본 끔).
- 제거: 프로그램 폴더, 시작 메뉴·바탕 화면 바로가기, (선택했다면) PDF 연결 레지스트리만 지웁니다. 사용자 설정(`%APPDATA%\SedolPDF`)과 복구 폴더(`%LOCALAPPDATA%\SedolPDF\Recovery`)는 남깁니다.
- 업그레이드용 AppId(고정 GUID)는 `build/installer/PdfEditor.iss`에 있으며 바꾸면 안 됩니다. 바꾸면 이전 설치와 별개 프로그램으로 취급됩니다.
- PDF 연결은 `.pdf`의 `OpenWithProgids`에 `SedolPDF.Document`를 추가하고, `Applications\SedolPDF.exe`에 열기 명령을 등록하는 방식입니다. (계획서의 `PdfEditor.App.exe`는 실행 파일 이름이 `SedolPDF.exe`로 바뀐 것을 반영해 고쳤습니다.)

## Inno Setup 라이선스 요약 (`license.txt` 읽음, 원문 복사 안 함)
- 상업적 사용을 포함해 어떤 목적으로든 무료로 쓰고, 수정·재배포할 수 있습니다. 보증은 없고 문제 시 책임을 지지 않습니다.
- 재배포(소스·바이너리)할 때 저작권 고지와 웹 주소를 그대로 둬야 하고, 원 저작자인 척하면 안 되며, 수정본은 수정했다고 분명히 표시해야 합니다.
- 설치 파일에 들어가는 Inno 설치 프로그램 런타임은 이 조건으로 배포할 수 있습니다. 제품 문서에 감사 표시를 넣으면 좋지만 필수는 아닙니다. Inno Setup 자체는 앱과 함께 나가지 않는 개발 도구입니다.
- 결론: 기업에서 써도 무료이고 프로젝트 규칙(상업 사용 무료)에 맞음. (법률 자문은 아님)

## 실제로 확인한 것 (이 PC, 2026-10-17)
모두 임시 폴더 `...\Temp\sedol-inst-test\한글 폴더\SedolPDF 설치`에서 했고, 끝난 뒤 지웠습니다.
1. 설치 파일 만들기 성공(경고 없이 컴파일). 스크립트의 필수 파일 검사(LICENSE 포함)와 한글·공백 경로 스모크 테스트 통과.
2. 무인 현재 사용자 설치(`/VERYSILENT /CURRENTUSER /DIR=...`): 종료 코드 0, 파일 261개, `portable.txt` 없음, `LICENSE`·`unins000.exe` 있음, 시작 메뉴 바로가기 생성, "앱 및 기능" 항목(버전 0.1.0) 생성.
3. 한글·공백 경로의 `SedolPDF.exe` 실행: 6초 뒤에도 실행 중이고 창 제목 "SedolPDF"(이후 종료).
4. 업그레이드: 0.1.1 설치 파일을 같은 위치에 덮어 설치 → 파일 버전 0.1.0.0 → 0.1.1.0, "앱 및 기능" 항목은 1개(버전 0.1.1)로 유지.
5. 무인 제거(`unins000.exe /VERYSILENT`): 종료 코드 0, 바로가기와 "앱 및 기능" 항목 삭제, 프로그램 파일 삭제. (내가 넣은 임시 파일 하나는 설치 프로그램이 만든 파일이 아니라서 남음 — Inno 의 정상 동작.)
6. 제거 후 `%APPDATA%\SedolPDF\settings.json`이 남아 있고 해시가 설치 전과 같음.

## 확인하지 못한 것 (검증 필요 — 사용자 확인 부탁)
- PDF 연결 작업: 이 PC의 파일 연결을 바꾸지 않으려고 시험하지 않았습니다. 설치 때 체크 후 "PDF 파일 > 연결 프로그램"에 SedolPDF가 나타나는지, 제거 후 사라지는지 확인해 주세요.
- "모든 사용자용"(관리자 권한, `C:\Program Files\SedolPDF`) 설치·제거.
- 설치 마법사 화면(한국어 문구, MIT 안내 화면에서 "Park Jaemin, Park Sehyeon"이 깨지지 않는지, 마지막 화면 선택 항목)을 눈으로 확인하지 않았습니다(무인 설치만 함). BOM이 있는 사본을 쓰도록 고쳤지만 화면 표시는 직접 보지 못했습니다.
- 설치 후 시작 메뉴에서 실행해 PDF 열기·저장, 업그레이드 후 설정 유지(설정 파일을 실제로 바꿔 둔 상태에서)는 하지 않았습니다. 앱 시작만 확인.
- 바탕 화면 바로가기 작업.
- 실행 중인 앱 위에 업그레이드할 때 앱 닫기 요청(CloseApplications)과 다시 시작(restart) 동작.

## SmartScreen 경고
코드 서명을 하지 않았으므로(결정 P2) 인터넷에서 받은 설치 파일·exe를 처음 실행하면 Windows SmartScreen이 "Windows에서 PC를 보호했습니다 / 알 수 없는 게시자"를 띄울 수 있습니다. "추가 정보 → 실행"으로 진행합니다. 회사 PC는 보안 정책에 따라 막힐 수도 있습니다(검증 필요). 공개 배포 직전에 서명 여부를 다시 결정합니다.

## 사용자가 직접 해 볼 순서
1. `artifacts\SedolPDF-0.1.0-setup-win-x64.exe`를 더블클릭 → 언어(한국어)·라이선스 화면 → 설치 위치(기본값) → 추가 작업은 둘 다 체크 → 설치 → "SedolPDF 실행".
2. PDF 하나를 열고 저장해 봅니다. 설정(창 크기 등)을 바꿔 둡니다.
3. 탐색기에서 PDF 우클릭 > 연결 프로그램 > 다른 앱 선택에 SedolPDF가 보이는지 확인합니다.
4. `0.1.1` 설치 파일(필요하면 `pwsh build/package.ps1 -Version 0.1.1`)로 덮어 설치 → 설정이 유지되는지 확인합니다.
5. 설정 > 앱 > 설치된 앱에서 SedolPDF 제거 → 프로그램 폴더가 사라지고 `%APPDATA%\SedolPDF`는 남는지 확인합니다. 연결 프로그램 목록에서도 사라져야 합니다.
6. 포터블: zip을 풀고 `SedolPDF.exe` 실행 → 설정이 같은 폴더의 `data`에 생기는지 확인합니다.
