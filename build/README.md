# 배포 파일 만들기 (SedolPDF)

프로그램을 다른 사람에게 줄 수 있는 파일로 묶는 방법입니다. 개발자가 아니어도 따라 할 수 있습니다.

## 필요한 것
- 이 컴퓨터에 .NET SDK 10과 PowerShell 7(`pwsh`)이 깔려 있어야 합니다. 기본 "Windows PowerShell"(버전 5)로는 실행되지 않으니 꼭 `pwsh`로 실행하세요. (이미 이 프로젝트를 빌드할 수 있으면 준비된 것입니다.)
- 설치 파일(setup.exe)까지 만들려면 Inno Setup 6이 필요합니다. 없으면 설치 파일만 건너뛰고 나머지는 정상으로 만들어집니다.

## 만드는 법
프로젝트 폴더에서 PowerShell 7을 열고 입력합니다.

```
pwsh build/package.ps1 -SkipInstaller
```

- `-SkipInstaller`를 빼면 Inno Setup이 있을 때 설치 파일도 만듭니다.
- 버전을 바꾸려면 `-Version 0.2.0`처럼 씁니다. 안 쓰면 `Directory.Build.props`의 `<Version>` 값을 씁니다.
- 몇 분 걸립니다.

## 결과물
모두 `artifacts` 폴더에 생깁니다. (이 폴더는 git에 올라가지 않습니다.)

| 파일 | 설명 |
|---|---|
| `SedolPDF-<버전>-portable-win-x64.zip` | 포터블 버전. 압축을 풀고 `SedolPDF.exe`를 실행하면 됩니다. 설정과 자동 복구 파일은 같은 폴더의 `data` 안에 저장됩니다. |
| `SedolPDF-<버전>-setup-win-x64.exe` | 설치 파일 버전 (Inno Setup이 있을 때만) |

포터블 폴더를 USB 등에 옮겨도 설정이 함께 따라갑니다. 단, 쓰기가 막힌 위치(예: `C:\Program Files`)에 풀면 일반 위치(`%APPDATA%`)를 대신 씁니다.

## 스크립트가 알아서 확인하는 것
1. 필수 파일이 모두 들어 있는지: `SedolPDF.exe`, `pdfium.dll`, `THIRD-PARTY-NOTICES.md`, 글꼴 라이선스, PDFium 라이선스 파일 18개. 하나라도 없으면 빨간 글씨로 알리고 멈춥니다.
2. 한글과 공백이 든 폴더(`한글 폴더\PDF 에디터`)에서 프로그램이 실제로 뜨고 끝나는지 (작은 PDF를 자동으로 만들어 시험합니다).

## 문제가 생기면
- "오류: ..."로 시작하는 빨간 줄이 원인입니다. 그대로 복사해 전달하세요.
- 파일이 잠겼다는 오류는 프로그램이나 탐색기가 `artifacts` 폴더를 쓰고 있을 때 납니다. 닫고 다시 실행하세요.
- 처음 실행하면 Windows SmartScreen이 "알 수 없는 게시자" 경고를 띄울 수 있습니다. 코드 서명을 하지 않았기 때문입니다.

## 설치 파일 (Inno Setup)
- Inno Setup 6이 필요합니다. 설치: `winget install JRSoftware.InnoSetup` (공식 배포본, 약 33 MB). 설치 파일을 만드는 컴퓨터에만 필요하고, 사용자에게 주는 파일에는 들어가지 않습니다. 라이선스는 상업 사용 포함 무료입니다.
- `pwsh build/package.ps1`(`-SkipInstaller` 없이)로 실행하면 `artifacts\SedolPDF-<버전>-setup-win-x64.exe`가 만들어집니다. 스크립트는 `build/installer/PdfEditor.iss`입니다. (`AppId`는 업그레이드 판단용이므로 바꾸지 마세요.)
- 설치하는 사람: 파일을 더블클릭합니다. 기본은 관리자 권한이 필요 없는 "현재 사용자용"이고 `%LOCALAPPDATA%\Programs\SedolPDF`에 깔립니다. 처음 화면에서 "모든 사용자용"을 고를 수도 있습니다(관리자 권한).
- 추가 작업 두 가지(바탕 화면 바로가기, PDF "연결 프로그램"에 추가)는 처음에는 꺼져 있습니다. PDF 기본 프로그램은 바꾸지 않습니다.
- 새 버전을 같은 곳에 덮어 설치하면 업그레이드됩니다. 제거하면 프로그램 폴더만 지워지고 설정(`%APPDATA%\SedolPDF`)은 남습니다.
- 서명하지 않아서 처음 실행할 때 SmartScreen 경고가 뜰 수 있습니다("추가 정보 → 실행").
- 무엇을 확인했고 무엇을 아직 확인하지 못했는지는 `docs/validation/2026-10-packaging-results.md`를 보세요.

## 포터블 사용법
zip을 원하는 폴더에 풀고 `SedolPDF.exe`를 실행합니다. 설치도 관리자 권한도 필요 없습니다. 지우려면 폴더를 통째로 지우면 됩니다. `portable.txt`를 지우면 일반(설치판) 방식으로 `%APPDATA%`를 씁니다.

## Microsoft Store용 MSIX (준비 중)
- 계획: docs/superpowers/plans/2026-10-27-microsoft-store-msix.md. 스크립트: pwsh build/package-msix.ps1 (아직 실행 검증 전).
- Windows SDK(makeappx)가 필요합니다: winget install Microsoft.WindowsSDK.10.0.26100 (정확한 패키지 이름은 검증 필요).
- Partner Center에서 받은 값을 uild/msix/store-identity.json에 적어야 제출용이 됩니다(견본: store-identity.sample.json, 실제 파일은 git에 올리지 않음).
