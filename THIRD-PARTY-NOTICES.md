# Third-Party Notices

이 프로젝트가 사용하거나 함께 배포하는 외부 구성 요소의 고지입니다.
PDFium 배포 조건(검증 V5)은 조사만 끝났고, 원문 동봉 여부는 사용자 결정 대기 중입니다.

## PDFium (pdfium-binaries 빌드)
- 용도: PDF 렌더링·편집 엔진 (NuGet `bblanchon.PDFium.Win32` 157.0.8100, 네이티브 `pdfium.dll`)
- 저작권 표시: Copyright 2014 The PDFium Authors (PDFium 원본). 패키지 제작: Copyright © Benoît Blanchon 2017-2025
- 라이선스: NuGet 메타데이터는 `Apache-2.0`. PDFium 소스의 `LICENSE`(chromium/8100)는 BSD 형식 조항과 Apache License 2.0 전문이 함께 들어 있다고 웹 조회로 확인함 (**검증 필요**: 원문 파일을 직접 열어 확인하지 못함)
- NuGet 패키지 안에는 LICENSE·NOTICE 파일이 **없음**. 라이선스 폴더(`licenses/`)는 pdfium-binaries 릴리스 압축 파일에만 있음 → 내려받기 전이라 **원문을 아직 동봉하지 못함** (V5 미완료, 사용자 결정 대기: `docs/validation/2026-10-m1-results.md` V5 절)
- https://pdfium.googlesource.com/pdfium/ , https://github.com/bblanchon/pdfium-binaries

### PDFium에 함께 빌드되었을 수 있는 서드파티 (후보 — 실제 포함 여부·원문은 릴리스 압축 파일의 `licenses/`로 확인 필요)
pdfium-binaries 빌드 스크립트(`steps/08-licenses.sh`)가 알고 있는 목록이다. 이 빌드에 실제로 들어간 것은 확인하지 못했다.
abseil, agg23, bigint, catapult, cpu_features, dragonbox (Apache2-LLVM / Boost), fast_float, fp16, FreeType (FTL), HarfBuzz, highway, ICU, lcms2, libpng, libjpeg-turbo (BSD / IJG / zlib), OpenJPEG, libtiff, libunwind, llvm-libc, simdutf, zlib.
- MIT·BSD·Apache·OFL 밖의 허용형으로 보이는 것(이름만 보고 분류, **사용자 확인 필요**): FreeType(FTL), libjpeg-turbo(IJG), zlib, libpng, ICU(Unicode), libtiff, AGG, bigint, Boost(dragonbox).
- 배포 때는 위 구성 요소 각각의 라이선스 원문과 저작권 표시를 함께 제공해야 하는 것으로 보인다 (최종 판단은 사용자).

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
- Avalonia.Headless.XUnit 12.1.4 (MIT): xUnit v3 전용(`xunit.v3.extensibility.core`)이라 현재 xUnit 2.9.3 테스트와 호환되지 않아 쓰지 않음. 화면 스모크 테스트는 수동 실행으로 대체.

## 그 밖의 참고
- 앱 안에서 "파일 → 정보 → 타사 고지 보기"로 이 파일(출력 폴더에 복사된 `THIRD-PARTY-NOTICES.md`)을 연다.
