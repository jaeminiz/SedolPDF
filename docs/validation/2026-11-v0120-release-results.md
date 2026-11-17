# 0.12.0 출시 결과 (2026-11-17)

## 범위
- 다른 편집기로 만든 FreeText 서식 보존(/RC만 있고 /DS 없는 경우, 옮기기만 하면 AP 유지, 원래 글꼴 이름 유지).
- 설치된 시스템 글꼴: 글 상자·편집 탭 쪽 글 고치기의 글꼴 목록, TrueType 서브셋 포함, 원래 글꼴을 이름·AP 글꼴·기본 14글꼴 별칭으로 찾기, Noto 대체. 설계: `docs/design/0.12.0-system-fonts.md`.
- 글 고치는 칸 크기, Esc로 글 상자 입력 유지, 주석 선택·개체 편집 끌어서 선택 수정.

## 검증
- main에서 `dotnet test -p:Platform=x64` 전체 통과(App.Tests 969개 포함). 처음 전체 실행에서 `ObjectEditMarquee_SelectsRewrittenTextLineOnce_MoveAndDelete("Arial 가나다 ABC")`가 한 번 실패했으나(모든 테스트 프로젝트를 동시에 돌려 부하가 큰 상태, 한 줄 지우기 대기 시간 문제로 보임) 단독 2회와 App.Tests 전체 재실행은 모두 통과. 불안정한 시험 후보.
- 확인하지 못한 것: 스토어(MSIX) 설치본에서 글꼴 목록(설계 7절 1~2항: 시스템 글꼴·사용자용 설치 글꼴이 보이는지). 서명 없는 MSIX라 이 PC에 설치할 수 없어 손 시험으로 남김.

## 패키지 (`artifacts/`)
| 파일 | 크기(byte) | SHA-256 |
|---|---|---|
| SedolPDF-0.12.0-setup-win-x64.exe | 47447088 | 613fd4665931951d12b295ea6fee9fabd50942810d37e82c7a1421ae26330c75 |
| SedolPDF-0.12.0-portable-win-x64.zip | 64295532 | 46234f7d06a3c9c65ef174efd0b2cc20d7454a0870315d5c4e9acaf15d693433 |
| SedolPDF-0.12.0-win-x64.msix | 64173608 | 831ae033fc0c85fad1b21e022d8ebc6121c83ea98b508ddb29fcefc16b4f61ce |

MSIX는 `-IdentityFile`로 메인 폴더 신원을 써서 만들었고 AppxManifest의 Identity Name은 `ParkJaemin.SedolPDF`, Version `0.12.0.0`.

## 배포
- 커밋 26e6165(버전) → 8a52098(결과 문서)가 main에 있고 태그 v0.12.0은 8a52098이다.
- GitHub 릴리스: https://github.com/jaeminiz/SedolPDF/releases/tag/v0.12.0 (설치 exe, 포터블 zip, SHA256SUMS-0.12.0.txt).
- 스토어: 제출 11(MSIX 0.12.0.0 하나만, 0.11.1 패키지는 같은 제출에서 제거)을 인증에 제출함(사전 처리 중). 한국어·영어 릴리스 노트(새로운 기능)를 0.12.0 내용으로 바꿨다. 결과는 몇 시간~영업일 3일.
- 업로드 방법: 브라우저 도구의 파일 업로드는 10MB 한도라, 임시 로컬 서버(127.0.0.1)에서 페이지 JS로 불러와 파일 입력에 넣었다. 끝난 뒤 서버를 껐다.
- 확인하지 못한 것: 스토어 설치본에서 글꼴 목록(설계 7절 1~2항). 인증 통과 후 스토어에서 설치해 확인 필요.
