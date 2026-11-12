# 0.11.0 릴리스 준비 결과

범위: 썸네일 패널 여러 열(Acrobat처럼), 크기 슬라이더(64~1000px, 설정 기억), 사이드바 폭 확대, PageUp/PageDown 수정, 0.10.1 계열 주석 수정(FreeText /RD). 병합: claude/v0101-fixes → claude/v0110-thumbs-cc21b7, 충돌 없음.

## 전체 시험 (`dotnet test`, Debug)
전 프로젝트 통과, 실패 0 (App 964, Document 392, Appearance 283, TextEdit 266, Pdf.Native 106, Exchange 73, Optimize 58, Pages 51).

## 패키지 (0.11.0)
| 파일 | 크기 (바이트) | SHA-256 |
|---|---|---|
| `SedolPDF-0.11.0-setup-win-x64.exe` | 47420102 | `7d8416339038267f348d8548d7f0ed3825feb5a65fcbcb8f3d560d5fa50391e1` |
| `SedolPDF-0.11.0-portable-win-x64.zip` | 64259689 | `62fc87fae80be05ca18046ea2969bb9f219e1418502d8dcc67be6be2462d922a` |
| `SedolPDF-0.11.0-win-x64.msix` | 64137651 | `4f6a60879d8b531d33906d1b59cbfe614bc3e88cf76626ab1c053a4a47babf4e` |

## 배포
- main 푸시(1231a9a)와 태그 v0.11.0 푸시 완료.
- GitHub: https://github.com/jaeminiz/SedolPDF/releases/tag/v0.11.0 (설치 exe, 휴대용 zip, SHA256SUMS-0.11.0.txt).
- 스토어: Partner Center 로그인 필요로 미제출(사용자 로그인 후 진행).
