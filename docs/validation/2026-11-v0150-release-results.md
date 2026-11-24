# 0.15.0 출시 결과 (2026-11-24)

## 범위
- 그러데이션·무늬 채우기·이미지 마스크가 있는 쪽에서도 객체 편집(복제·옮기기·크기 바꾸기, 선 색·굵기, 글자 색)이 저장됨. 위치를 모르는 글 복제는 BT 머리 재생으로 처리.
- "그래도 저장"은 반드시 저장하고, 저장할 수 없는 경우 처음에 원인을 한 번만 알림(LookChanged/CannotRewrite).
- 설계 문서: `docs/design/0.15.0-object-edit-save.md`.

## 검증
- 병합 후 `dotnet test` 전체: 알려진 `V16ObjectMapSpike` 1건 외에 `S3.TextReplace` 1건이 전체 실행 때만 실패(0.14.0 때와 같은 순서 의존 흔들림). 따로 돌리면 통과(5/5)하고 관련 코드 변경 없음. 나머지 통과: App.Tests 1012, Document 465, Appearance 303, ContentStream 109 등.
- 사용자 손 시험 통과(관제 세션 보고).

## 패키지 (`artifacts/`)
| 파일 | 크기(byte) | SHA-256 |
|---|---|---|
| SedolPDF-0.15.0-setup-win-x64.exe | 47472130 | 78b42d64510e4a54fbbc8c198611ea8403c98a2fa780840c387705c0d93e8215 |
| SedolPDF-0.15.0-portable-win-x64.zip | 64329411 | 55734a4e14d32e885c8f6ccbec978544c00a96eb808537a1551d7ea0d41ca8aa |
| SedolPDF-0.15.0-win-x64.msix | 64203626 | 71d3897800da5ab04821c8cb1d8a4afe78dfd38a6a8941157ba39e07bd6f5b90 |

MSIX는 `-IdentityFile`로 메인 폴더 신원을 써서 만들었고 Identity Name은 `ParkJaemin.SedolPDF`, Version `0.15.0.0`. 첫 빌드는 makeappx가 20분 넘게 멈춰 강제 종료 후 재실행해 성공.

## 배포
- 커밋: 02e33c0(병합, 끝 커밋 1f5caa2) → 5704cc7(버전·README·스토어 문구). 태그 v0.15.0은 5704cc7.
- GitHub 릴리스: https://github.com/jaeminiz/SedolPDF/releases/tag/v0.15.0 (설치 exe, 포터블 zip, SHA256SUMS-0.15.0.txt).
- 스토어: **미제출**. Partner Center에 로그인된 브라우저가 없어(내장 브라우저·Chrome 모두 로그인 화면) 업로드를 못 했다. 로그인 후 MSIX 업로드, 0.14.0 패키지 제거, 한국어·영어 "새로운 기능" 입력, 제출이 남았다.
