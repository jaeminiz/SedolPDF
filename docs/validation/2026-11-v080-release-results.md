# 0.8.0 릴리스 준비 결과

작성: 2026-11-06 (릴리스 세션, Sonnet 5.5 — 명령줄만). 기준 형식: `2026-11-v070-release-results.md`.

범위: 0.7.0 이후 변경 — 여러 창(새 창 Ctrl+Shift+N, 탭을 창 밖으로 끌어 새 창, 다른 창 탭 줄에 놓아 이동·Esc 취소, 탭 오른쪽 클릭 "새 창으로 이동"/"다른 창으로 이동", 이동해도 저장 안 한 편집·실행 취소 기록 유지, 새 창 화면 안 자리 보정). 스펙 `docs/superpowers/specs/2026-11-06-multi-window-tabs-design.md`, 손 시험 9항목 통과 `docs/superpowers/notes/2026-11-06-multi-window-manual-tests.md`. 기준 커밋 72e23f4. 0.7.0은 이미 GitHub 릴리스로 나가 있었음.

## 버전·문서
`Directory.Build.props` 0.7.0 → 0.8.0, `README.md`·`README.en.md` 버전 표기, `docs/store/listing.md`(ko/en 기능 목록·릴리스 노트). 새 의존성 없음.

## 전체 시험 (`dotnet test PdfEditor.sln -c Release`, 1회)
실패 0. App 886·TextEdit 266·Document 295·Appearance 269·Pdf.Native 97·Exchange 73·Optimize 58 등 전부 통과. 로그 `.superpowers/out-test080.log`(git 제외).

## 패키지 (0.8.0)
`build/package.ps1` → `build/package-msix.ps1`(스토어 신원, 서명 없음). 해시는 `artifacts/SHA256SUMS-0.8.0.txt`.

| 파일 | 크기 (바이트) | SHA-256 |
|---|---|---|
| `SedolPDF-0.8.0-setup-win-x64.exe` | 47,364,181 | `155d725c7fec15d04a719bad07315d400643b57fc2cd99128104b510835d6d32` |
| `SedolPDF-0.8.0-portable-win-x64.zip` | 64,197,505 | `95e3050d4ce56432f5106d37980be54c226394a053c6b145fabbc55d88723be9` |
| `SedolPDF-0.8.0-win-x64.msix` | 64,077,015 | `b17ec3949e909774785a993b37c5b883abc9236a5f5fb75be01cb285c498959e` |

## 미확인
- 패키지 설치본·MSIX를 실제로 설치해 여러 창을 띄워 보는 시험은 하지 않았다(손 시험은 개발 빌드로 통과).
