# 0.5.0 마무리 검토 결과

작성: 2026-11-03 ([0.5.0-review], Opus 5.5 — 화면 조작 없이 헤드리스·명령줄·`--bench`만). 기준 형식: `2026-10-v040-results.md` "0.4.0 마무리 검토" 절.

범위: `e43d55c..641f7ab`(0.4.0 이후 0.5.0 네 묶음 병합까지)의 `src` 변경 88개 파일(+2,257/-308줄) — 작은 개선(단일 인스턴스 새 탭·흰 배경·패널 폭·손 커서·배율 슬라이더·영역 강조 크기 조절·타일 캐시 64 MB), 인쇄 전면 개편, 글자 서식(글꼴 2종·굵게·기울임·밑줄·취소선), 디자인 규격표. 선·화살표 확장은 사용자가 철회해 되돌려진 상태(d2edb01)로 검토했다. 각 묶음의 결과는 `2026-11-v050-print-results.md`·`2026-11-v050-style-results.md`.

## 규칙 점검

| 규칙 | 결과 |
|---|---|
| PDFium 호출 스레드 | 인쇄 미리보기(`PrintPreviewRenderer.RenderSheet`)·인쇄(`PrintJob.PrintSheetPage`)·출력 사본 열기/닫기 모두 `session.Thread.InvokeAsync` 안에서만 `LoadPage`/`RenderTile`/`Open`/`Dispose`. 지킴 |
| UI 스레드 블로킹 | 미리보기: 장치 크기 측정은 `Task.Run`, 그림은 PDFium 스레드, 결과만 UI로(150 ms 디바운스 + 세대 번호로 늦은 결과 버림). 출력 사본은 대화상자가 열린 동안 백그라운드에서 만든다. **남은 위험(결함 아님, 검증 필요):** 프린터 목록·능력(`ListPrinters`·`QueryCaps`·`DefaultDevMode`)과 확정 때 `Apply`는 UI 스레드에서 동기로 부른다. 이 PC의 로컬 프린터 8개에서는 문제 없었으나, 꺼진 네트워크 프린터는 드라이버가 수 초 멈출 수 있다 |
| 문구는 리소스에만 | 추가 코드·axaml의 한글 리터럴은 주석뿐. 서식 단추 글자 B·I·U·S와 ◀▶는 기호(툴팁은 리소스). `Strings.resx`·`Strings.en.resx` 키 632개씩, 짝 어긋남 0 |
| 라이선스·NuGet | `.csproj`·`.props`·`.targets` 변경 0(이번 검토의 버전 줄 제외), 신규 패키지 0 |
| `Blob.FromStream` | 소스 전체에 없음 |
| 외부 링크 | `AppLinks.IsAllowed`가 https만 허용(변경 없음). 스토어 설치본(`\WindowsApps\`)은 별·후원 링크 숨김(`AppLinks.Compute`, 변경 없음). 0.5.0 변경에 새 링크·네트워크 요청 없음 |
| 단일 인스턴스 보안 | 뮤텍스 `Local\SedolPDF-<사용자>-mutex`(세션 단위), 파이프 `SedolPDF-<사용자>-pipe`는 서버·클라이언트 모두 `PipeOptions.CurrentUserOnly` → 다른 사용자는 연결할 수 없고, 다른 사용자가 같은 이름의 파이프를 먼저 만들어도 클라이언트가 서버 소유자를 확인해 거부한다. 받는 줄은 64개·3초 제한. **보강 1건(9b15659):** 받은 경로 중 전체 경로(`Path.IsPathFullyQualified`)가 아닌 것은 버린다(상대 경로는 먼저 뜬 프로세스의 현재 폴더 기준으로 다른 파일을 열 수 있음). 보내는 쪽은 원래 전체 경로로 바꿔 보내므로 동작 변화 없음 |
| 단일 인스턴스 스토어·포터블 | 포터블·설치판은 같은 사용자 안에서 하나로 합쳐진다(설계 의도). `--bench`·`PDFEDITOR_NO_SINGLE_INSTANCE`는 건너뜀. **검증 필요:** 스토어(MSIX) 설치본과 포터블을 동시에 실행했을 때 합쳐지는지(전체 신뢰 MSIX는 이름 있는 개체를 가상화하지 않는 것으로 알고 있으나 실제 확인 안 함) |
| 인쇄 = 패치된 출력 사본(설계서 4.2-6) | `PrintSource.PrepareAsync` → `OutputCopyBuilder.BuildIfNeededAsync`가 만든 사본을 미리보기와 인쇄가 함께 쓴다. 살아 있는 PDFium 문서는 패치가 필요 없을 때만 직접 쓴다. 지킴 |
| 글자 서식 비표준 키 vs 모르는 키 보존 | 패치는 `/SedolFont`·`/SedolFmt` 두 키만 `SetName`/`SetInteger`/`RemoveKeys`로 건드리고 사전의 다른 키는 그대로 둔다. 기본 서식이면 두 키를 지워 0.4.0과 같은 출력. 읽기는 모르는 값·비트를 기본으로(`TextFormat.Sanitize`·`FamilyFromName`). 다른 도구가 만든 주석에는 이 키가 없으므로 손대지 않은 원본은 그대로 보존. XFDF 읽기·쓰기도 같은 두 키. 충돌 없음 |
| AP는 Painter | 글자 서식(굵게·기울임·밑줄·취소선·명조)은 `FreeTextPainter`가 화면·저장 공통으로 그림 |

## 결함·수정
1. **9b15659 `fix(app)`** — 단일 인스턴스 파이프로 받은 경로를 전체 경로만 연다(위 표).
2. 인쇄 출력 확인 시험 추가(b437ad3) — 아래 "인쇄 실제 출력".
3. 시험 기준 조정(b437ad3·22e2851) — 아래 "시험 안정성".

그 밖의 결함은 찾지 못했다.

## 시험 안정성
- 전체 `dotnet test PdfEditor.sln`(Debug) **3회 연속 실패 0**(검토 시작 시점, 수정 전): App 776·TextEdit 259·Document 276·Appearance 250·Pdf.Native 97·Exchange 73·ContentStream 72·Pages 48 + 스파이크 S1~S4.
- 이어서 4회 더(새 시험 포함, App 781): 1회에서 `FindEngineTests.Find_Synthetic5000Lines_IsFast`만 실패, 나머지 3회 통과. ContentStream·Document는 **7회 모두 통과해 재현되지 않음** — 원인 미확정(두 프로젝트 시험에 공유 파일 경로·고정 임시 이름 충돌은 없음을 확인). 이전에 한 번씩 흔들린 `LineStylePerformanceTests`·`FontCodebook` 시험도 이번 7회는 모두 통과.
- **`Find_Synthetic5000Lines_IsFast` 원인:** 전체 실행은 시험 프로세스 여러 개가 동시에 돌아 벽시계 시간이 늘어난다(이전 실패 922 ms, 기준 150 ms). 1차로 스레드 CPU 시간(`GetThreadTimes`)으로 바꿨으나 부하 중에는 CPU 시간도 188 ms까지 늘었다(캐시·클럭 경쟁). 같은 시험의 단독 값은 **Debug 109~125 ms, Release 78~94 ms** — 150 ms 목표는 제품(Release) 기준으로는 넉넉히 통과하고, 최적화가 꺼진 Debug에서는 원래 여유가 없었다. 조정: CPU 시간으로 판정, Release 150 ms(목표 그대로), Debug 300 ms(알고리즘 퇴행 — 예: 줄 수의 제곱 — 은 여전히 잡힘).
- **최종 확인(조정 뒤, 버전 0.5.0):** 전체 `dotnet test` 3회 연속 실패 0(App 781·TextEdit 259 외 모두 통과).

## `--bench` (Release 게시 `artifacts/v050-review-build`, HEAD 22e2851, 합성 샘플, 순차 6회)
| 항목 | 결과 | 목표 |
|---|---|---|
| A1 첫 화면(`firstScreenCurrentScaleMs`, `spikes/out/bench-a1-drawing.pdf`) | 0.391 / 0.397 / 0.395 / 0.391 / 0.417 / 0.412 s | ≤ 0.5 s 통과 |
| 300쪽 작업 집합(`peakWorkingSetMb`, `bench-a4-300pages.pdf`) | 361.7 / 368.0 / 370.2 / 364.6 / 362.0 / 362.1 MB | < 400 MB 통과 — **여유 30~38 MB 확보**(0.4.0 검토 때 0~10 MB). 타일 캐시 96→64 MB(1d85b70) 효과 |
| 300쪽 회색 비율(세 스크롤 방식) | 0 % (6회 모두, 연속 최대 0 ms) | ≤ 2 % 통과 |
- A1 스크롤 회색 비율(44~52 %)은 참고값(목표는 300쪽 기준, M1 이후 같은 성격).

## 인쇄 실제 출력 (Microsoft Print to PDF, 물리 프린터로는 보내지 않음)
기존 시험(`PrintFeatureTests`: 2쪽 모아 찍기 5쪽 → 가로 3장, 매수 2부 한 부씩/아님 → 6장, 강제 가로, 양면 홀수 장 빈 장 포함 7장)은 장 수·방향만 본다. 이번에 **어느 쪽이 어떤 순서로 나오는지**를 출력 PDF에서 확인하는 `PrintRangeOutputTests`를 추가했다(쪽 k에 폭 60·k pt 막대를 그리고 출력 쪽의 막대 폭으로 쪽을 알아냄, 실제 크기 인쇄).

| 경우 | 출력 쪽 순서 |
|---|---|
| 직접 입력 `5,2-3` | 5, 2, 3 (입력 순서 그대로) |
| `1-6` + 홀수만 | 1, 3, 5 |
| `1-6` + 짝수만 | 2, 4, 6 |
| `5,2` 2부 한 부씩 | 5, 2, 5, 2 |
| `5,2` 2부 한 부씩 아님 | 5, 5, 2, 2 |

5개 모두 통과. 이 PC의 Print to PDF는 드라이버 매수 최대 1·양면 없음이라 매수는 프로그램 반복 경로만 확인했다.

## 패키지 (0.5.0)
`Directory.Build.props` 버전 0.4.0 → 0.5.0, README 두 곳의 "v0.4.0" 표기도 0.5.0으로. `pwsh build/package.ps1`(포터블 + Inno Setup 설치 파일, 스모크 테스트 "한글·공백 경로에서 실행, 종료 코드 0, JSON 생성" 통과) → `pwsh build/package-msix.ps1`(스토어 신원 `ParkJaemin.SedolPDF`, 버전 0.5.0.0, 서명 없음 — 스토어 제출용). 결과는 `artifacts/`(git 제외), 해시는 `artifacts/SHA256SUMS-0.5.0.txt`.

| 파일 | 크기 (바이트) | 0.4.0 대비 | SHA-256 |
|---|---|---|---|
| `SedolPDF-0.5.0-setup-win-x64.exe` | 47,313,739 (45.1 MB) | +28 KB | `dc86276b9dd64df29157173f32082e0d30710e2605ef947e955ab31bec48d13e` |
| `SedolPDF-0.5.0-portable-win-x64.zip` | 64,122,723 (61.2 MB) | +45 KB | `6b4b73cf75fb93df468f280de875ba0e9155950d4b08ae23ec40a6bc6853aeb7` |
| `SedolPDF-0.5.0-win-x64.msix` | 64,001,575 (61.0 MB) | +44 KB | `15c3804afac02c7a9e6ecfc0be96eae79218e5b64fd29b1786756ebe81131ba2` |

스토어 제출·푸시·태그는 하지 않았다(준비까지만).

## 미확인 (검증 필요)
- 125/150 % 배율에서 인쇄 대화상자·서식 단추·패널 폭 손잡이 모양
- 실제 한글 IME(텍스트 상자 서식 바꾼 뒤 입력, 코멘트 답글·찾아 바꾸기 입력칸)
- 다른 뷰어(Acrobat·PDF-XChange·Foxit·Chrome)에서 굵게·기울임·밑줄·취소선·명조 텍스트 상자 모양(AP로는 보이지만, 그 뷰어에서 편집하면 `/SedolFont`·`/SedolFmt`를 모르므로 서식이 기본으로 돌아갈 수 있음 — 선 확장을 철회한 것과 같은 성격이라 사용자 판단 거리)
- 실제 물리 프린터(SINDOH 등)의 양면·드라이버 매수·"프린터 속성…" 대화상자, 꺼진 네트워크 프린터에서 대화상자 응답성
- 스토어(MSIX) 설치본과 포터블 동시 실행 시 단일 인스턴스 합쳐짐, 설치 파일 SmartScreen 경고·PDF 연결·관리자 설치
