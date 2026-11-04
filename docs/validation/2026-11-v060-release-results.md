# 0.6.0 릴리스 준비 결과

작성: 2026-11-04 (릴리스 준비 세션, Sonnet 5.5 — 화면 조작 없이 명령줄만). 기준 형식: `2026-11-v050-review-results.md`.

범위: 0.5.0 이후 `src` 변경 — 텍스트 편집 개선(겹쳐 찍은 굵은 글에서 글 사라짐 해결, 편집 가능한 글 조각 상자 미리 표시, 반투명 제자리 편집 칸, 편집한 글의 굵기·색·크기를 원본에 맞춤), 탭 오른쪽 클릭 메뉴(포함하는 폴더 열기·전체 경로 복사·이 탭 닫기), 속성 패널 글자 형식(글꼴·크기·색·굵게·기울임·명조), 마우스 휠 버튼으로 화면 이동. 기준 커밋 86b7abd.

## 버전·문서
`Directory.Build.props` 0.5.0 → 0.6.0, `README.md`·`README.en.md`(버전 표기, 텍스트 편집·글자 형식·탭 메뉴·휠 버튼 이동), `docs/store/listing.md`(ko/en 기능 목록·릴리스 노트). 과거 기록 문서는 건드리지 않았다. `Strings.resx`·`Strings.en.resx` 키 640개씩, 짝 어긋남 0. 의존성 변경 0.

## 전체 시험 (`dotnet test PdfEditor.sln`, Debug, 1회)
App 802·TextEdit 266·Document 286·Appearance 269·Pdf.Native 97·Exchange 73·ContentStream 72·Pages 48·스파이크 S1·S2·S4 통과, **실패 0** — 단 스파이크 S3.TextReplace의 `ReplaceText_FallbackResult_RendersInk` 1건이 전체 실행 때 실패(앞 시험 `ReplaceText_Fallback_NewTextObjectWithAppFont`이 만드는 파일이 없다는 `FileNotFoundException`, 시험 실행 순서 의존). S3 프로젝트 단독 2회 모두 5/5 통과 — 제품 결함이 아니라 시험 순서 의존으로 판단(후속 정리 권고).
알려진 간헐 실패 후보(`LineStylePerformanceTests`·`SingleInstanceTests`·`OutputCopyTests`) 단독 재실행: 7/7 통과(이번 전체 실행에서도 실패 없음).

## 패키지 (0.6.0)
`pwsh build/package.ps1`(포터블 + 설치 파일) → `pwsh build/package-msix.ps1`(스토어 신원 `store-identity.json`, 서명 없음 — 제출용). 이 worktree에는 잠긴 실행 파일이 없어 `artifacts/`에 그대로 만들었다. 결과는 `artifacts/`(git 제외), 해시는 `artifacts/SHA256SUMS-0.6.0.txt`.

| 파일 | 크기 (바이트) | SHA-256 |
|---|---|---|
| `SedolPDF-0.6.0-setup-win-x64.exe` | 47,331,778 | `64065ec4382c91981f4f1f74a3dbffaec7cba7a495b0596c421c978eaae9a084` |
| `SedolPDF-0.6.0-portable-win-x64.zip` | 64,141,484 | `5b0e9bb297c7065ffe4571c3673b0f97137355235f1a0643649ff32a8fb6714d` |
| `SedolPDF-0.6.0-win-x64.msix` | 64,017,406 | `375002b0501f0c90faac5210a15bc725fcc4136cd4b13db10289544216067c27` |

위치: `D:\INBOX_WP\95. Claude\PDF EDITOR\.claude\worktrees\release-0-6-0-35a2b8\artifacts\`. 첫 MSIX는 신원 파일(gitignore)이 없어 자리표시자 신원으로 만들어졌으나 신원 파일을 복사해 다시 만들었고, 위 표는 다시 만든 것이다.

스토어 제출·푸시·태그는 하지 않았다(사용자 확인 대기).

## 미확인 (검증 필요)
- 화면 조작 시험(탭 메뉴, 휠 버튼 이동, 글자 형식 패널, 제자리 편집)은 이 세션에서 하지 않았다. 헤드리스 시험 통과만 확인.
- `--bench` 수치(첫 화면·300쪽 메모리)는 이번에 재지 않았다.
