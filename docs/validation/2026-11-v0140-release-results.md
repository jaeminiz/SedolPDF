# 0.14.0 출시 결과 (2026-11-19)

## 범위
- 저장 속도 개선: 쪽 검사 결과 캐시, 파일을 열 때 미리 검사, 병렬 검사, 저장 뒤 확인은 바뀐 쪽만 다시 읽음. 대형 도면 저장 9.6초 → 약 0.3초.
- 수정한 글 줄을 다시 열어도 한 줄로 잡힘(쪼개지지 않음).
- 이미 선택된 썸네일을 눌러도 그 쪽으로 이동.
- 설계 문서: `docs/design/0.14.0-save-speed.md`.

## 검증
- 병합 후 `dotnet test PdfEditor.sln` 전체: 알려진 `V16ObjectMapSpike` 1건 외에, 전체 실행 때 `ContentStream.Tests` 8건(PdfSharp 목록 동시 접근 오류, 글꼴 시험)과 `S3.TextReplace` 1건(시험 실행 순서 의존)이 한 번 실패했다. 두 프로젝트를 따로 다시 돌리니 각각 6회·1회 모두 통과(108/108, 5/5)해 일회성 흔들림으로 판단. 나머지 통과: App.Tests 1000, Document 454, Appearance 301 등.
- 사용자 손 시험 통과(관제 세션 보고).

## 패키지 (`artifacts/`)
| 파일 | 크기(byte) | SHA-256 |
|---|---|---|
| SedolPDF-0.14.0-setup-win-x64.exe | 47472505 | 6d2e68515738109e7c8252caedae6bca774729dea6a0ab5595ba1f046db1dd10 |
| SedolPDF-0.14.0-portable-win-x64.zip | 64322070 | 7cff5f9d06a89f9c490f0349899a363ca72c3e2731c9819d2ce1a5b16e32cb51 |
| SedolPDF-0.14.0-win-x64.msix | 64197052 | bac10064a1480d02f34b7ba764e391f74c1a1f1e8fc06b4bd94c46951e713d74 |

MSIX는 `-IdentityFile`로 메인 폴더 신원을 써서 만들었고 AppxManifest의 Identity Name은 `ParkJaemin.SedolPDF`, Version `0.14.0.0`.

## 배포
- 커밋: 596c7a2(병합, 끝 커밋 422c4e9) → 57a0be8(버전·README·스토어 문구). 태그 v0.14.0은 57a0be8.
- GitHub 릴리스: https://github.com/jaeminiz/SedolPDF/releases/tag/v0.14.0 (설치 exe, 포터블 zip, SHA256SUMS-0.14.0.txt).
- 스토어: 제출 13(MSIX 0.14.0.0 하나만, 0.13.0 패키지는 같은 제출에서 제거)을 인증에 제출함(사전 처리 중). 한국어·영어 "새로운 기능"을 0.14.0 내용으로 바꿨다. 결과는 몇 시간~영업일 3일.
- 업로드 방법: 0.13.0과 같음(임시 로컬 서버 → 페이지 JS로 파일 입력에 넣기). 끝난 뒤 서버를 껐다.
- 참고: 파트너 센터 패키지 화면에서 0.13.0 제거 뒤 저장을 눌러야 반영된다. runFullTrust 제한 기능 경고는 평소와 같다.
