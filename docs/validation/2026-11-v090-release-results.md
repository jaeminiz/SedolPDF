# 0.9.0 릴리스 준비 결과

작성: 2026-11-11 (릴리스 세션, Sonnet 5.5). 기준 형식: `2026-11-v080-release-results.md`.

범위: 0.8.0 이후 변경 — 개체 복사·잘라내기·붙여넣기·복제(Ctrl+C/X/V/D, 오른쪽 클릭 메뉴, 같은 문서만), 주석(영역 강조 포함) 복사·붙여넣기, 개체 드래그 회전(Shift=15°, 각도 표시), 회전한 쪽 방향키·붙여넣기, 개체 이동 때 화면이 비던 문제 수정, 확대·축소 1%~6400%, 큰 확대 때 타일 이음매 격자선 제거, 영역 강조 아이콘 노랑. 손 시험 모두 통과(사용자). 기준 커밋 7fe5c78.

알려진 한계: 사본이 있는 원천 쪽을 지울 때 알림 없음, 개체를 끌 때 점선 상자만 움직임.

## 전체 시험 (`dotnet test PdfEditor.sln -c Release`, 1회)
904개 중 903 통과, 1 실패: `CommentsPanelPerformanceTests`(시간 의존 시험). 단독 실행하면 통과. 나머지 프로젝트는 전부 통과(TextEdit 266, Document 342, Appearance 279, Pdf.Native 103, Exchange 73, Optimize 58 등).

## 패키지 (0.9.0)
`build/package.ps1` → `build/package-msix.ps1`(스토어 신원, 서명 없음). 해시는 `artifacts/SHA256SUMS-0.9.0.txt`.

| 파일 | 크기 (바이트) | SHA-256 |
|---|---|---|
| `SedolPDF-0.9.0-setup-win-x64.exe` | 47394192 | `9a5c8ac2ab95b4845d37333058971564d8af075b79fa941ea367fc0b49f312e1` |
| `SedolPDF-0.9.0-portable-win-x64.zip` | 64235094 | `644ef837b73c9ca1671abb11d7b3b0507600108a8ec7086ee07ea1fe538e3e02` |
| `SedolPDF-0.9.0-win-x64.msix` | 64110679 | `778700d0afd1a85c48e82f2d322be1e26b196f7b8784299427e6a086dbd54164` |

## 미확인
- 설치본·MSIX를 실제로 설치해 보는 시험은 하지 않았다(손 시험은 개발 빌드로 통과).
