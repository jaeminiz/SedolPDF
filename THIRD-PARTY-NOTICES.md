# Third-Party Notices

이 프로젝트가 사용하거나 함께 배포하는 외부 구성 요소의 고지입니다.
바이너리 배포 때 필요한 정확한 조건과 라이선스 원문 동봉 여부는 **검증 V5(M1)** 에서 확정합니다.

## PDFium (pdfium-binaries 빌드)
- 용도: PDF 렌더링·편집 엔진 (NuGet `bblanchon.PDFium.Win32` 157.0.8100, 네이티브 `pdfium.dll`)
- 라이선스: NuGet 패키지 메타데이터는 `Apache-2.0` 로 표기. PDFium 원본 프로젝트는 BSD-3-Clause / Apache-2.0 계열.
- 패키지 안에는 별도 LICENSE 파일이 없음 → 원문과 NOTICE 요구사항은 V5에서 확인 (검증 필요)
- https://pdfium.googlesource.com/pdfium/ , https://github.com/bblanchon/pdfium-binaries

## Noto Sans KR (Task 4에서 추가 예정)
- 용도: 기본 한글 폰트 (`assets/fonts/`)
- 라이선스: SIL Open Font License 1.1 (OFL-1.1)
- https://fonts.google.com/noto/specimen/Noto+Sans+KR

## HarfBuzzSharp / PDFsharp (spikes 단계에서 사용)
- HarfBuzzSharp: MIT, PDFsharp: MIT
