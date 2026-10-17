# Third-Party Notices

SedolPDF — 박재민, 박세현 (Copyright (c) 2026 박재민, 박세현)

이 프로젝트가 사용하거나 함께 배포하는 외부 구성 요소의 고지입니다.

## PDFium (pdfium-binaries 빌드) — 검증 V5 완료 (2026-10-16)
- 용도: PDF 렌더링·편집 엔진 (NuGet `bblanchon.PDFium.Win32` 157.0.8100, 네이티브 `pdfium.dll`)
- 앱이 쓰는 NuGet의 `pdfium.dll`과 pdfium-binaries 릴리스 `chromium/8100`의 `pdfium-win-x64.tgz` 안 `bin/pdfium.dll`은 **같은 파일**이다 (SHA-256 `ae913f40…7a227a45` 일치). 그래서 아래 라이선스가 그대로 적용된다.
- 라이선스 원문은 모두 `assets/licenses/pdfium/`에 있다. 빌드하면 앱 출력 폴더의 `licenses/pdfium/`으로 복사되어 함께 배포된다.

| 구성 요소 | 라이선스 | 원문 파일 | 배포 때 지킬 것 |
|---|---|---|---|
| PDFium | BSD-3-Clause + Apache-2.0 (Copyright 2014 The PDFium Authors) | `pdfium.txt` | 원문·저작권 표시 동봉 |
| pdfium-binaries (빌드 스크립트) | MIT (Copyright 2014-2025 Benoit Blanchon) | `pdfium-binaries-LICENSE.txt` | 원문 동봉 |
| abseil | Apache-2.0 | `abseil.txt` | 원문 동봉 |
| Anti-Grain Geometry 2.3 | 허용형 (저작권 표시 유지 조건) | `agg23.txt` | 저작권 표시 동봉 |
| dragonbox | Apache-2.0 with LLVM Exception 또는 Boost-1.0 | `dragonbox-*.txt` | 원문 동봉 |
| fast_float | MIT | `fast_float.txt` | 원문 동봉 |
| FreeType | FreeType License (FTL, BSD 계열) | `freetype.txt` | 원문 동봉 + 문서에 FreeType 사용 표기 (아래 "크레딧") |
| HarfBuzz | Old MIT | `harfbuzz.txt` | 원문 동봉 |
| ICU | Unicode License V3 | `icu.txt` | 원문 동봉 |
| Little CMS | MIT | `lcms.txt` | 원문 동봉 |
| libjpeg-turbo | IJG + BSD-3-Clause + zlib | `libjpeg_turbo.md`, `libjpeg_turbo.ijg` | 원문 동봉 + 문서에 IJG 표기 (아래 "크레딧") |
| OpenJPEG | BSD-2-Clause | `libopenjpeg.txt` | 원문 동봉 |
| libpng | PNG Reference Library License v2 | `libpng.txt` | 원문 동봉 |
| LLVM libc | Apache-2.0 with LLVM Exception | `llvm-libc.txt` | 원문 동봉 |
| simdutf | MIT | `simdutf.txt` | 원문 동봉 |
| zlib | zlib License | `zlib.txt` | 원문 동봉 |

모두 기업에서도 무료로 쓰고 재배포할 수 있는 허용형 라이선스다(GPL·AGPL 없음). FreeType은 FTL과 GPLv2 중 고를 수 있는데, 이 빌드는 FTL을 따른다.

### 크레딧 (FreeType·IJG 라이선스가 요구하는 문서 표기)
- Portions of this software are copyright © 2026 The FreeType Project (www.freetype.org). All rights reserved.
- This software is based in part on the work of the Independent JPEG Group.

- 출처: https://pdfium.googlesource.com/pdfium/ , https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F8100

## Noto Sans KR
- 용도: 기본 한글 폰트 (`assets/fonts/NotoSansKR-Regular.otf`, 라이선스 원문 `assets/fonts/OFL.txt`). 앱에 포함해 배포 가능(기업 사용 무료), 폰트 파일 단독 판매만 금지
- 라이선스: SIL Open Font License 1.1 (OFL-1.1)
- 출처: https://github.com/notofonts/noto-cjk (Sans/SubsetOTF/KR)

## HarfBuzzSharp / PDFsharp
- HarfBuzzSharp: MIT (spikes 단계에서만 사용), PDFsharp 6.2.4: MIT
- PDFsharp은 앱에서 레이어 목록 읽기(`PdfEditor.Document/Layers/LayerReader.cs`)에 쓴다. 6.5.0 이후는 AGPL이라 6.2.x에 고정.
- System.Security.Cryptography.Pkcs 10.0.13: MIT (PDFsharp이 끌어오는 8.0.1에 알려진 취약점이 있어 직접 지정)

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
- Avalonia.Headless 12.1.4 (MIT): 테스트 프로젝트(PdfEditor.App.Tests)에만 사용 — 화면 출력과 PDFium 렌더 비교(Task 11). 배포물에는 들어가지 않음. xUnit 연동 없이 `HeadlessUi`가 고정 스레드에서 직접 설정.
- Avalonia.Headless.XUnit 12.1.4 (MIT): xUnit v3 전용(`xunit.v3.extensibility.core`)이라 현재 xUnit 2.9.3 테스트와 호환되지 않아 쓰지 않음. 화면 스모크 테스트는 수동 실행으로 대체.

## 그 밖의 참고
- 앱 안에서 "파일 → 정보 → 타사 고지 보기"로 이 파일(출력 폴더에 복사된 `THIRD-PARTY-NOTICES.md`)을 연다.
