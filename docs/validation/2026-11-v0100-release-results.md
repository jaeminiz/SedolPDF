# 0.10.0 릴리스 준비 결과

작성: 2026-11-12 (릴리스 세션, Sonnet 5.5). 기준 형식: `2026-11-v090-release-results.md`.

범위: 0.9.0 이후 변경 — 0.8.0 이하 저장으로 망가진 쪽을 저장 때 복구(알림 + 다시 열기 단추), 글상자 Acrobat 호환(글자 크기·테두리 색 양방향 유지, Acrobat에서 다시 저장한 글상자를 옮겨도 검은 글자 유지), 주석 회전(초록 손잡이, Shift=15°, 상자는 90°), 개체를 끌 때 개체 그림 표시, 개체를 돌리거나 옮긴 뒤 글자가 잘리던 0.9.0 문제 수정(회전한 Showcase 패널 저장이 Acrobat에서 올바로 그려짐). 손 시험과 Acrobat 재확인 통과(사용자). 병합 커밋 7e03bd6.

## 전체 시험 (`dotnet test`, Debug, 1회)
전 프로젝트 통과, 실패 0 (App 926, Document 386, Appearance 283, Pdf.Native 106, ContentStream 98, Exchange 73, Optimize 58, Pages 51 등).

## 패키지 (0.10.0)
`build/package.ps1` → `build/package-msix.ps1`(스토어 신원, 서명 없음). 해시는 `artifacts/SHA256SUMS-0.10.0.txt`.

| 파일 | 크기 (바이트) | SHA-256 |
|---|---|---|
| `SedolPDF-0.10.0-setup-win-x64.exe` | 47408288 | `49c67b759bcec17948bca21af645e8b42e0f8d94b58145b713e5438c7613e3fa` |
| `SedolPDF-0.10.0-portable-win-x64.zip` | 64251094 | `4adc95c10610a94e2dd7c42f2ba57b2dfe171dce97347187ff3a2a77c275020b` |
| `SedolPDF-0.10.0-win-x64.msix` | 64129066 | `a681738bfa457df005ce4050efbd073f7b2b60d11dae8dc05990361895e0c325` |

## 미확인
- 설치본·MSIX를 실제로 설치해 보는 시험은 하지 않았다(손 시험은 개발 빌드로 통과).
