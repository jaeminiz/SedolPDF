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
