# SedolPDF (세돌PDF)

[English](README.en.md) · **한국어**

Windows용 무료 오픈소스 PDF 편집기입니다. 보기, 주석, 쪽 편집, 텍스트·이미지·도형 직접 편집을 한 프로그램에서 하고, 개인과 기업 모두 무료로 쓸 수 있습니다(MIT 라이선스).

> **현재 공개 버전(v0.11.1)입니다.** 기본 기능은 쓸 수 있지만 아직 다듬는 중입니다. 중요한 파일은 원본을 따로 보관하고 쓰세요. 문제나 제안은 [Issues](https://github.com/jaeminiz/SedolPDF/issues)에 남겨 주세요.

<p align="center">
  <a href="https://github.com/jaeminiz/SedolPDF/releases/latest"><img alt="release" src="https://img.shields.io/github/v/release/jaeminiz/SedolPDF?label=release"></a>
  <img alt="Windows" src="https://img.shields.io/badge/Windows-x64-0078D4">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-green">
  <img alt="PRs" src="https://img.shields.io/badge/PRs-welcome-brightgreen">
</p>

<p align="center">
  <a href="https://github.com/sponsors/jaeminiz"><img alt="Sponsor" src="https://img.shields.io/badge/%E2%99%A5%20SUPPORT%20SEDOLPDF-GITHUB%20SPONSORS-3DD68C?style=for-the-badge&labelColor=black"></a>
</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/9NV1D7TDKVJK"><img alt="Microsoft Store에서 받기" src="https://get.microsoft.com/images/ko%20dark.svg"></a>
</p>

![주석 탭](docs/screenshots/03-annotate.png)

## 주요 기능

**보기**
- 큰 CAD 도면 PDF도 빠르게 열리도록 타일 방식으로 그립니다(A1 크기 도면 첫 화면 0.5초 이내 목표, 300쪽 문서도 메모리 400 MB 미만 목표).
- 연속·한 쪽 보기, 확대·축소, 실제 크기(100% = 96dpi), 폭·쪽 맞춤, 스페이스바 또는 마우스 휠 버튼을 누른 채 끌어서 이동(손 도구).
- 텍스트 선택·복사, 찾기, 썸네일(Acrobat처럼 여러 열, 크기 슬라이더 64~1000px)·책갈피·첨부·레이어 패널, 스냅숏(영역 또는 쪽 전체를 그림으로 복사).

**주석**
- 스티커 메모, 형광펜·밑줄·취소선, 텍스트 상자·타자기·설명선, 선·화살표·폴리라인·다각형·구름·사각형·타원, 연필·지우개, 스탬프(기본 + 그림), 파일 첨부, 텍스트 삽입·바꾸기.
- 코멘트 목록, 답글, 검토 상태, 스타일 저장·적용, 주석 요약 PDF, XFDF 가져오기·내보내기(다른 PDF 프로그램과 주석 주고받기).
- 한글 주석을 다른 뷰어에서도 깨지지 않게 저장합니다.
- 다른 PDF 프로그램이 만든 사각형·타원(`/RD` 여백이 있는 것)과 타자기 글자 주석도 선택하고 고칠 수 있습니다.

**쪽 편집**
- 빈 쪽·파일에서 쪽 넣기, 삭제, 회전, 순서 바꾸기, 합치기, 쪽 추출, PDF를 끌어다 놓아 넣기. 모두 실행 취소를 지원합니다.

**텍스트·객체 편집**
- 기존 텍스트 한 줄 고치기: 원래 글꼴로 안전하게 쓸 수 있으면 그대로 쓰고, 아니면 앱에 포함된 Noto Sans KR로 바꿉니다.
- 같은 글을 겹쳐 찍어 굵게 만든 글에서 글이 사라지던 문제를 고쳤습니다. 편집할 수 있는 글 조각은 상자로 미리 보이고, 고치는 칸은 반투명이라 원본 글 위에서 바로 고칩니다. 고친 글의 굵기·색·크기를 원본에 맞춥니다.
- 글자 형식: 속성 패널에서 글꼴·크기·색·굵게·기울임·명조 여부를 보고 바꿀 수 있습니다.
- 이미지·도형 옮기기, 크기 바꾸기, 지우기, 새 텍스트·이미지·도형 넣기, 여러 개 선택.
- 객체·주석 복사·붙여넣기(Ctrl+C/X/V/D): 같은 문서 안에서 개체와 주석을 복사·잘라내기·붙여넣기·복제합니다. 오른쪽 클릭 메뉴도 쓸 수 있고, 여러 개를 한꺼번에 처리해도 실행 취소는 한 번입니다.

**용량 줄이기**
- 파일 → 다른 형식으로 저장 → 용량 줄여 저장: 목표 용량(MB)을 정하면 이미지를 다시 압축해 그 이하의 새 파일로 저장합니다(메일 첨부용, 기본 16 MB). 글자·벡터·주석은 그대로입니다(흑백 스캔은 JBIG2로 다시 압축합니다). 목표의 5%까지는 허용합니다. 목표를 못 맞추면 한 화면에서 그 상태로 저장, 컬러 이미지로 변환, 도면용 변환(선·글자는 고해상도 흑백, 색은 저해상도로 입혀 색 유지, 높음·중간·최저) 중에서 고릅니다. 각 방식의 예상 용량이 미리 보입니다. 변환하면 글자·링크·주석이 사라집니다. 원본은 바뀌지 않습니다.

**탭**
- 탭을 오른쪽 클릭하면 포함하는 폴더 열기, 전체 파일 경로 복사, 이 탭 닫기를 쓸 수 있습니다.
- 여러 창: 파일 → 새 창(Ctrl+Shift+N). 탭을 창 밖으로 끌어 놓으면 새 창이 되고, 다른 창의 탭 줄에 놓으면 그 창으로 옮겨집니다(Esc로 취소). 탭 오른쪽 클릭의 "새 창으로 이동"·"다른 창으로 이동"도 같습니다. 옮겨도 저장 안 한 편집과 실행 취소 기록은 그대로입니다.
- 저장 때 0.8.0 이하에서 망가진 쪽을 고치고(알림과 "다시 열기" 단추), 글상자는 Acrobat과 호환(글자 크기·테두리 색 유지), 주석을 초록 손잡이로 회전(Shift=15°, 상자는 90° 단위), 개체를 끌 때 개체 그림이 따라옴
- 개체 복사·잘라내기·붙여넣기·복제(Ctrl+C/X/V/D, 오른쪽 클릭 메뉴, 같은 문서 안), 주석(영역 강조 포함) 복사·붙여넣기, 개체를 끌어 회전(Shift=15°, 각도 표시), 확대·축소 1%~6400%

**안전**
- 저장은 임시 파일에 쓰고 검증한 뒤 원본과 교체합니다. 알 수 없는 주석 키는 보존합니다.
- 자동 복구(비정상 종료 뒤 편집 내용 복구), 암호가 걸린 PDF 열기.
- 앱 코드에는 네트워크 통신이 없습니다.

![홈 탭](docs/screenshots/02-home.png)

![편집 탭](docs/screenshots/04-edit.png)

![페이지 탭](docs/screenshots/05-page.png)

![인쇄 미리보기](docs/screenshots/06-print-preview.png)

![정보 창](docs/screenshots/01-info.png)

## 설치

**[Microsoft Store에서 받기](https://apps.microsoft.com/detail/9NV1D7TDKVJK)** — 자동 업데이트되고 SmartScreen 경고가 없습니다. 스토어에서는 "SedolPDF" 또는 "세돌PDF"로 검색하세요. 새 버전은 심사를 거쳐 올라오므로 GitHub보다 며칠 늦을 수 있습니다.

직접 받으려면 [Releases](https://github.com/jaeminiz/SedolPDF/releases)를 이용하세요.

| 파일 | 설명 |
|---|---|
| `SedolPDF-<버전>-setup-win-x64.exe` | 설치 파일(현재 사용자용) |
| `SedolPDF-<버전>-portable-win-x64.zip` | 포터블 버전. 압축을 풀고 `SedolPDF.exe` 실행. 설정은 같은 폴더의 `data`에 저장 |

- Windows 10/11 64비트에서 동작합니다.
- 코드 서명을 하지 않아 처음 실행할 때 Windows SmartScreen이 "알 수 없는 게시자" 경고를 띄울 수 있습니다. 소스는 이 저장소에 모두 공개되어 있고, 릴리스에 올린 `SHA256SUMS.txt`로 파일을 확인할 수 있습니다.

## 직접 빌드

필요한 것: [.NET SDK 10](https://dotnet.microsoft.com/download), PowerShell 7.

```bash
dotnet build PdfEditor.sln -c Release
dotnet test PdfEditor.sln          # 1100개 이상의 시험
pwsh build/package.ps1             # 포터블 zip과 설치 파일 만들기 (build/README.md 참고)
```

## 한계와 계획

- 화면 언어는 한국어와 영어입니다(0.4.0부터, 환경 설정에서 바꾸면 다시 시작한 뒤 적용). 긴 설명서·개발 문서는 아직 한국어뿐입니다.
- 아직 없는 기능: 문단 단위 편집(여러 줄 다시 맞추기), 전자서명, 양식 편집, OCR, 측정 도구, 다크 모드.
- 묶음(폼) 안에 든 그림·글자는 묶음 전체로만 옮기거나 지울 수 있습니다.
- 다른 PDF 프로그램에서의 모양은 계속 확인 중입니다. 이상한 파일은 Issues로 알려 주세요(개인·회사 문서는 올리지 마세요).

## 기술

C# / .NET 10, [Avalonia](https://avaloniaui.net) 12(Fluent), [PDFium](https://pdfium.googlesource.com/pdfium/)(P/Invoke 직접 래핑), [PDFsharp](https://github.com/empira/PDFsharp) 6.2, HarfBuzzSharp, Noto Sans KR. 설계서는 `docs/superpowers/specs/`, 검증 결과는 `docs/validation/`에 있습니다.

## 라이선스

[MIT](LICENSE). 사용한 오픈소스(PDFium BSD-3-Clause, PDFsharp·Avalonia·HarfBuzzSharp MIT, Noto Sans KR OFL 1.1)의 고지는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 있습니다.

## 후원

SedolPDF는 광고도 유료 기능도 없습니다. 도움이 되셨다면 [GitHub Sponsors](https://github.com/sponsors/jaeminiz)로 응원해 주실 수 있습니다(선택).

## 만든 사람

Park Jaemin ([jaemini@gmail.com](mailto:jaemini@gmail.com)) · Park Sehyeon ([sedolisehyun@gmail.com](mailto:sedolisehyun@gmail.com))
