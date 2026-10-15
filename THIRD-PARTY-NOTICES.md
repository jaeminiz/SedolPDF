# Third-Party Notices

이 프로젝트가 사용하거나 함께 배포하는 외부 구성 요소의 고지입니다.
바이너리 배포 때 필요한 정확한 조건과 라이선스 원문 동봉 여부는 **검증 V5(M1)** 에서 확정합니다.

## PDFium (pdfium-binaries 빌드)
- 용도: PDF 렌더링·편집 엔진 (NuGet `bblanchon.PDFium.Win32` 157.0.8100, 네이티브 `pdfium.dll`)
- 라이선스: NuGet 패키지 메타데이터는 `Apache-2.0` 로 표기. PDFium 원본 프로젝트는 BSD-3-Clause / Apache-2.0 계열.
- 패키지 안에는 별도 LICENSE 파일이 없음 → 원문과 NOTICE 요구사항은 V5에서 확인 (검증 필요)
- https://pdfium.googlesource.com/pdfium/ , https://github.com/bblanchon/pdfium-binaries

## Noto Sans KR
- 용도: 기본 한글 폰트 (`assets/fonts/NotoSansKR-Regular.otf`, 라이선스 원문 `assets/fonts/OFL.txt`). 앱에 포함해 배포 가능(기업 사용 무료), 폰트 파일 단독 판매만 금지
- 라이선스: SIL Open Font License 1.1 (OFL-1.1)
- 출처: https://github.com/notofonts/noto-cjk (Sans/SubsetOTF/KR)

## HarfBuzzSharp / PDFsharp (spikes 단계에서 사용)
- HarfBuzzSharp: MIT, PDFsharp: MIT

## Avalonia (12.1.4)
- 용도: UI 프레임워크 (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`)
- 라이선스: MIT

## CommunityToolkit.Mvvm (8.4.2)
- 용도: MVVM 도우미 (소스 생성기 포함)
- 라이선스: MIT (NuGet 메타데이터 확인)

## Fluent UI System Icons
- 용도: 리본·제목 표시줄·상태 표시줄 아이콘 (`src/PdfEditor.App/Resources/Icons.axaml`에 Regular 20 경로 데이터만 옮김)
- 라이선스: MIT — Copyright (c) 2020 Microsoft Corporation
- 출처: https://github.com/microsoft/fluentui-system-icons

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 검토했으나 쓰지 않은 패키지
- Avalonia.Headless.XUnit 12.1.4 (MIT): xUnit v3 전용(`xunit.v3.extensibility.core`)이라 현재 xUnit 2.9.3 테스트와 호환되지 않아 쓰지 않음. 화면 스모크 테스트는 수동 실행으로 대체.
