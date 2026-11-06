# 0.7.0 릴리스 준비 결과

작성: 2026-11-06 (릴리스 준비 세션, Sonnet 5.5 — 화면 조작 없이 명령줄만). 기준 형식: `2026-11-v060-release-results.md`.

범위: 0.6.0 이후 변경 — 용량 줄여 저장(SizeReducer: 이미지 재압축, 흑백 스캔 JBIG2, 흑백·컬러 변환 선택창, 예상 용량 표시, 오류 로그), 외부 프로그램이 만든 `/RD` 사각형·타원과 타자기 글자 주석 선택·편집, README 스토어 배지. 기준 커밋 ac379e0.

## 버전·문서
`Directory.Build.props` 0.6.0 → 0.7.0, `README.md`·`README.en.md` 버전 표기, `docs/store/listing.md`(ko/en 기능 목록·릴리스 노트). `Strings.resx`·`Strings.en.resx` 키 667개씩 일치. 새 의존성 없음(SkiaSharp·PDFsharp은 기존 사용분).

## 전체 시험 (`dotnet test PdfEditor.sln`, Debug, 1회)
실패 0. App 836·TextEdit 266·Document 295·Appearance 269·Pdf.Native 97·Exchange 73·ContentStream 72·Optimize 58·Pages 48·스파이크 S1 3·S2 5·S3 5·S4 2 통과. 로그 `artifacts/test-full-070.log`(git 제외).

## 패키지 (0.7.0)
`build/package.ps1` → `build/package-msix.ps1`(스토어 신원, 서명 없음). 해시는 `artifacts/SHA256SUMS-0.7.0.txt`.

| 파일 | 크기 (바이트) | SHA-256 |
|---|---|---|
| `SedolPDF-0.7.0-setup-win-x64.exe` | 47,357,321 | `440733297ce51275abe962b990ea638d7c0c17c00fadd1e0da79ac36acf6fdcc` |
| `SedolPDF-0.7.0-portable-win-x64.zip` | 64,186,039 | `adf7f399232ae55c05a1cde930bb73ffc1e4a01d73a251a5d09ac6e15685cad2` |
| `SedolPDF-0.7.0-win-x64.msix` | 64,062,556 | `2a1fcc85b6d07e3584dd5b4d24f9408f690f01797f12a466caa35ae60dadb886` |

## 미확인 (검증 필요)
- 화면 조작 시험(용량 줄여 저장 대화상자, 외부 주석 선택)은 하지 않았다. 헤드리스 시험 통과만 확인.
- `--bench` 수치는 재지 않았다.
