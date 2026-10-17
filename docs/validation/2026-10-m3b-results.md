# M3b 결과 (작성 중)

M3b(주석 관리) 작업 중에 확인한 것을 Task별로 쌓는다. 마일스톤을 마칠 때 정리한다.

## Task 3 — 코멘트 패널 (답글·상태·삭제)

### 만든 것
- 왼쪽 "코멘트" 패널: 검색칸, 정렬(쪽·작성 시각·작성자·종류)과 방향 단추, "거르기" 메뉴(종류·작성자·상태·쪽 범위), 줄 수 표시, 가상화 목록.
- 줄: 종류 아이콘·이름, 쪽 번호, 작성자·시각, 내용 두 줄, 상태 표시. 답글은 한 단계 들여쓴다. 읽기 전용 원본(다른 프로그램이 만든 것)은 흐리게 보이고 눌러서 이동만 된다.
- 줄 메뉴(⋯ 단추 또는 오른쪽 단추): 답글 / 상태(없음·승인·거부·취소·완료) / 삭제 / 위치로 이동. 답글 입력칸은 줄 아래에 펼쳐진다(Enter 확정, Shift+Enter 줄바꿈, Esc 취소).
- 주석 탭 "더보기" 메뉴 끝의 "코멘트 목록"이 이 패널을 연다. 코멘트 패널을 열면 왼쪽 패널 폭이 200 → 320 DIP로 넓어진다.
- 답글·상태·삭제는 각각 명령 하나(실행 취소 한 번). 삭제는 항상 `DeleteSetOf` → `DeleteThread`로 답글·상태·바꾸기 묶음 구성원을 함께 지운다. 새 답글·상태의 시각은 그 스레드의 가장 늦은 시각보다 반드시 뒤라서 "스레드 끝"에 붙고 "가장 최근 상태"가 항상 새 것이다.
- 상태 문구 틀은 `{0} · 상태: {1}`(조사를 붙이지 않는 구조)이라 "승인로" 같은 오류가 없다. 문구는 `Strings.resx`에만 있다.
- 암호 문서(패치 불가): 답글·상태 비활성 + 패널 위에 `Msg_RepliesUnavailableEncrypted` 안내. 목록 보기는 된다.
- `CommentsViewModel`은 코멘트 패널을 처음 열 때 만들고 탭을 닫을 때(`Detached`) 해제한다(저장소·쪽 이벤트 구독 해제).

### 자동 테스트
`CommentsViewModelTests`: `Reply_OneCommand_AppearsIndented`, `Reply_ToAnnotationWithReplies_GoesToThreadEnd`, `SetState_ShowsInRow_UndoReverts`, `SetState_OnReplyRow_AppliesToThreadRoot`, `Delete_RemovesThread`(답글·상태 포함 삭제, 고아 없음, 실행 취소로 전부 복원), `Delete_ReadOnlyRow_Ignored`, `Encrypted_ReplyAndStateDisabled`, `NormalDoc_CanReply_NoNotice`.
`CommentsPanelHeadlessTests`: 헤드리스 화면에서 목록이 모델대로 그려지고, 답글 명령으로 입력칸이 열리며, Enter로 답글이 달리고 칸이 닫힌다.
`CommentsPanelPerformanceTests`: 2,000개 문서에서 가상화(줄 8개 미만만 만듦) 확인.

### 성능 (주석 2,000개: 20쪽 × 쪽당 최상위 80개 + 답글 400개 + 상태 200개 = 화면 줄 2,000개)
| 항목 | 결과 | 방법 |
|---|---|---|
| 모델 만들기(2,000줄 캐시) | 13.7 ms | 헤드리스 테스트(Debug) |
| 패널 열기(첫 줄까지) | 399 ms | 헤드리스 소프트웨어 렌더, Debug, 첫 실행(JIT 포함) |
| 만들어 둔 줄(컨테이너) 수 | 7개 (스크롤 뒤 8개) | 같은 테스트 — 가상화 확인 |
| 스크롤 한 번(4,000 DIP씩 30회) | p50 21.8 ms, 최대 39.0 ms | 같은 테스트(레이아웃까지, 그리기 제외) |
| 글자 검색 거르기 | 160.9 ms (첫 실행, JIT 포함) → 11줄 | 같은 테스트. 다시 하면 Task 2 기준(1,000개 50 ms 미만) |
- 실제 앱(Release)에서 2,000개 PDF(`spikes/out/m3b-2000-comments.pdf`, 저장소에 넣지 않음)를 열고 UI 자동화로 코멘트 패널 단추만 눌러 앱 창을 캡처했다(M2와 같은 PrintWindow 방법). 패널이 `2000 / 2000개`로 나오고 줄 6개가 만들어졌으며 앱 작업 집합은 약 241 MB였다. 위쪽 줄(1쪽, 답글 들여쓰기, "승인" 표시)과 답글 입력칸이 열린 모양을 캡처로 확인했다.
- **검증 필요:** 실제 마우스 휠·스크롤 막대로 스크롤하는 부드러움은 확인하지 못했다. UI 자동화의 `SetScrollPercent`는 이 목록에서 화면을 움직이지 못했고(캡처가 그대로), 호출 시간(약 2 s)은 자동화 자체의 지연이라 앱 성능으로 보지 않는다. "패널 단추 → 줄이 보임" 2.4 s도 자동화 폴링 시간이 섞여 있어 쓸 수 없는 수치다. 위 헤드리스 수치만 근거이며 Release·실제 화면·실제 입력에서는 확인하지 않았다.

### 직접 확인하지 못한 것 (검증 필요)
- 실제 마우스·키보드로 줄 클릭 이동, 메뉴 선택, 답글 입력, Enter·Shift+Enter 동작 (자동 테스트와 UI 자동화 `Invoke`로 메뉴 → "답글"까지만 확인).
- 다른 뷰어(Acrobat·PDF-XChange)에서 답글·상태가 같은 모양으로 보이는지(V3 점검 항목).

### Task 3 수정 1 — 한글 IME와 답글 입력칸
- 입력칸은 IME가 키를 먹었거나(`Key.ImeProcessed`) 조합 중인 글자(`TextPresenter.PreeditText`)가 있으면 Enter·Esc를 무시한다. 조합을 끝내는 Enter가 답글을 확정하지 않고, 그다음 Enter가 확정한다. 확정할 때는 입력칸의 글을 직접 읽어 `Draft`에 넣어 마지막 글자가 빠지지 않게 한다.
- 쓰던 답글은 주석 Id 기준으로 모델이 들고 있어, 같은 쪽 줄이 다시 만들어져도(다른 주석 편집·실행 취소) 입력칸과 글이 이어진다. 답글을 달거나 취소하면 지워진다.
- 자동 테스트: `CommitReply_UsesCurrentText_WhenDraftLagsBehind`, `ReplyDraft_SurvivesRebuildOfItsPage_AndIsClearedAfterCommit`, `ImeProcessedEnter_DoesNotCommit_PlainEnterDoes`(헤드리스에서 `ImeProcessed` 키를 보냄), Document 쪽 `EncryptedDoc_DeleteOriginalParent_RemovesItsUnmodeledReplies`(암호 문서에서 원본 부모를 지우면 모델에 없는 원본 답글·상태·Popup도 저장 때 파일에서 지워짐).
- **검증 필요:** 실제 한글 IME(Windows 한글 입력기)로 조합 중 Enter·Esc를 눌렀을 때의 동작. 헤드리스는 IME 조합(`PreeditText`)을 흉내 내지 못해 `ImeProcessed` 경로만 검사했고, `PreeditText` 검사는 코드로만 있고 실제 입력으로 확인하지 않았다.
- 같은 위험이 있는 다른 편집기(FreeText·스티커 메모)는 Ctrl+Enter를 쓰므로 범위 밖이지만, 그 편집기들이 Enter/Esc를 조합 중에 먹는지는 확인하지 않았다(검증 필요).

## Task 4 — 주석 스타일과 "형식" 탭

### 구현
- `AnnotationStyle` 레코드(이름·적용 종류·색·채우기·불투명도·두께·글자 크기·선 끝·클라우드)는 설정 파일의 `Styles`에 저장된다. `Styles`가 없으면(옛 settings.json 포함) 기본 3개("검토 빨강"·"형광 노랑"(강조에만)·"승인 초록")를 보인다. 이 이름은 `Strings.resx`의 화면 문구이고, 한 번 저장·삭제·이름 바꾸기를 하면 기본 스타일을 포함한 전체 목록이 설정에 저장돼 그때부터 이름은 데이터다(빈 목록 = 모두 지움, 기본값으로 되돌아가지 않음). 저장은 `SettingsStore.Update`(잠금 하나 안에서 읽기-고치기-쓰기).
- `AnnotationStylesViewModel`: `Available`(선택·도구의 종류에 맞는 것; 적용 종류가 없으면 모두에게 맞음; 섞인 선택은 하나라도 맞으면), `ApplyAsync`(선택 → 명령 하나, 그 종류에 맞는 속성만; 도구만 켜짐 → 도구 기본값 설정), `SaveCurrentAs`(빈 이름·대소문자 무시 중복 거절 + 안내), `Delete`, `Rename`(중복 거절). 속성 패널과 같은 규칙: 지금의 현재 주석을 다시 찾아 바꾼다.
- 종류별 적용: 색·불투명도는 전부, 두께는 두께가 있는 종류만, 채우기는 채우기 있는 종류만(스타일에 채우기가 있을 때만; 없으면 기존 채우기를 건드리지 않음), 글자 크기는 텍스트 상자 계열만, 선 끝은 선·폴리라인만, 클라우드는 사각형·다각형만. 선 끝·클라우드는 패치가 필요해 패치를 못 하는(암호) 문서에서는 적용하지 않고 나머지만 적용한다.
- "형식" 탭: `MainWindowViewModel.IsFormatTabVisible`(만들기 도구 켜짐 또는 선택 있음), 메뉴 탭 오른쪽 끝에 "도구/선택 이름 | 형식"(시안의 ctxlabel 모양, 작은 파랑 글씨 + 강조 탭). 보이지 않는 탭은 고를 수 없고, 탭이 선택된 채로 사라지면 주석 탭으로 돌아간다. 내용(`FormatTab`)은 스타일(목록·× 삭제·이름 입력 + "지금 모양 저장")·색(견본)·선(두께, 선 끝 시작/끝)·글자(크기, 글자 색) 그룹이며 속성 패널과 같은 `AnnotationPropertiesViewModel`·`AnnotationStylesViewModel` 인스턴스를 바인딩해 같은 메서드를 부른다(로직 중복 없음). 권한이 없으면 전체, 패치가 필요한 선 끝은 패치 불가 문서에서 비활성이다.

### 자동 테스트 (전체 `dotnet test PdfEditor.sln` 통과: App 388 / Document 220 / Appearance 161 …)
`AnnotationStylesViewModelTests`(14): `DefaultStylesWhenNone`, `Available_FilteredBySelectionAndTool`, `Apply_ToSelection_OneCommand_KindAware_UndoRestores`(사각형·강조·선·텍스트 상자 한 번에 적용 → 종류별로 맞는 속성만, 실행 취소 한 번에 복원), `Apply_StyleWithAppliesTo_SkipsOtherKinds`, `Apply_WithoutFillStyle_KeepsExistingFill`, `Apply_ToTool_UpdatesToolDefaults`, `Apply_BrokenStyle_NoticeAndNoChange`, `SaveCurrentAs_DuplicateNameRejected`, `SaveCurrentAs_FromTool_UsesToolStyle`, `SaveCurrentAs_FromLine_KeepsEndings_AndAppliesToLines`, `DeleteAndRename_Persist_DuplicateRenameRejected`, `TargetText_ToolSelectionAndMixed`, `EncryptedDoc_PatchPropertiesNotApplied_OthersAre` 등. `SettingsStoreTests.Styles_RoundTripInSettings_OldFileLoads`. `MainWindowViewModelTests.FormatTab_VisibleForToolOrSelection`(+`FormatTab_HiddenWithoutDocument`).

### 화면 확인 (앱 창만 PrintWindow로 캡처)
Release 앱을 설정 파일 경로를 따로 지정해 실행하고, UI 자동화로 열기 대화상자에 `m2-annotations.pdf` 사본 경로를 넣어 문서를 연 뒤 주석 탭 → 연필 도구 → "형식" 탭을 누른 모양을 캡처했다. 확인한 것: 메뉴 탭 오른쪽 끝에 "연필 도구 | 형식"이 나타남, "형식"을 누르면 리본이 스타일(연필에 맞는 "검토 빨강"·"승인 초록"만, "형광 노랑"은 강조 전용이라 숨음, 이름 입력칸, "지금 모양 저장")·색 견본·선 두께 그룹으로 바뀜. 도구를 바꾸면 "형식" 탭이 사라지는 동작은 자동 테스트로만 확인했다.
- **검증 필요(실제 입력으로 확인하지 못함):** 스타일 단추·× 삭제·견본·두께 스피너·선 끝 목록을 실제 마우스로 누르는 동작, 이름 입력칸에 한글 입력(IME)·Enter 저장, 실제 주석을 선택했을 때의 "사각형 2개 선택" 표시, 화면 배율·좁은 창에서 리본이 잘리지 않는지. (이 환경에서는 SendInput이 막혀 키·마우스 입력을 보낼 수 없었고, 클릭은 UI 자동화 Invoke로 대신했다.)
- 알려진 모양: "형식" 탭을 열면 리본 카드가 약 8 px 커져 아래 화면이 조금 내려간다(스타일 그룹의 이름 입력칸+저장 단추 때문). 기능 문제는 아니다.

### 한계·결정
- 도구 기본값(`ToolStyleSetting`)에는 색·채우기·불투명도·두께만 있어, 도구에 스타일을 적용할 때 글자 크기·선 끝·클라우드는 반영하지 않는다(선택한 주석에는 반영). 화살표 도구의 기본 끝 모양 등은 도구 기본값이 아니라 도구 코드에 있다.
- "형식" 탭에는 클라우드 켜기·세기 컨트롤을 두지 않았다(브리프의 그룹 목록에 없음). 클라우드는 속성 패널에서 켜고, 스타일은 적용 때 클라우드 값을 쓴다. 이름 바꾸기는 모델(`Rename`)만 있고 화면 단추는 없다.
- 선택·도구가 없는 상태에서 `Available`은 전체 목록을 돌려준다(탭이 보이지 않으므로 화면에는 나오지 않음).

## XFDF 가져오기 (Task 6) 사용자에게 보이는 한계
- 이름(`name`/NM)이 없는 XFDF 주석은 가져올 때 새 이름이 붙으므로, 같은 파일을 다시 가져오면 중복된다. 이름이 있는 주석은 같은 이름의 기존 주석을 바꾸므로 중복되지 않는다. 우리 내보내기는 이름이 없는 주석에 `sedol-N` 이름을 붙여 내보낸다.
- 읽기 전용 원본(다른 앱 스탬프·링크 등)은 NM을 보관하지 않아 같은 이름이라도 바꿀 수 없고 새 주석이 추가된다.
- 이름이 같지만 종류가 다른 주석은 바꾸되 상세 목록에 "replaced annotation of a different kind"로 알린다.
- 안전 한계: DTD·외부 개체 금지, 파일 100 MB, 요소 중첩 32단계(넘으면 그 주석만 건너뜀), 첨부 합계 200 MB(풀린 크기)·파일 25 MB, 좌표 목록 16 MB·가져오기 전체 800만 개, 답글 사슬 깊이 256(넘으면 새 뿌리).

## Task 7 — 주석 요약 PDF

### 구현
- `PdfEditor.Exchange.Summary.SummaryPdfBuilder.Build(SummaryInput, AppFont, outputPath, ct, progress?)`: A4 세로, 첫 쪽 머리말(문서 이름·만든 시각·주석 수), 주석이 있는 원본 쪽마다 왼쪽에 쪽 그림(폭 40%, BGRA → RGB Flate 이미지 XObject, 쪽 비율 유지)과 그 위 번호 배지·붉은 테두리(`MarkRect`, 그림 픽셀 좌표), 오른쪽에 번호·종류·작성자·시각·내용(답글은 14 pt씩 들여쓰기). 글자는 Noto Sans KR(`TextLayout` 줄바꿈 + `FontEmbedder` 서브셋 한 번)이고 내용 스트림을 직접 쓴다(한 줄 = `Tm` 한 번 + `TJ` 배열; 글자마다 `Tm`을 쓰면 PDFium 글자 추출이 글자마다 줄을 바꿔서 이렇게 했다).
- 긴 내용은 줄 단위로 흘러 쪽 아래에 닿으면 다음 요약 쪽에서 같은 쪽 머리(`SummaryText.PageHeadingContinuedFormat`, 앱은 `Strings.resx`의 "{0}쪽 (이어서)")로 계속한다. 주석이 없는 쪽은 건너뛴다. 모듈 안에는 한국어 문구가 없다(틀은 `SummaryInput.Text`로 받음, 기본값은 영어).
- 글꼴에 없는 글자(예: 이모지)는 조용히 빼지 않고 `SummaryBuildResult.MissingCodepoints`로 돌려주고 요약 맨 끝에 붉은 알림 줄(`U+1F600` 형식)을 적는다. 앱은 완료 알림에도 개수를 적는다.
- 임시 파일에 쓴 뒤 옮긴다. 취소·실패하면 임시 파일을 지우고, 이미 있던 같은 이름 파일은 그대로 둔다.
- 앱: "더보기" 메뉴의 "주석 요약" (`annot.summary`). 주석이 없으면 알림만, 있으면 저장 위치(기본 `<PDF 이름>-summary.pdf`)를 묻고 진행 창(취소 단추, 기존 `PrintProgressDialog`를 제목·문구 틀을 받게 일반화)에서 만든다. 주석 구성(번호·종류 이름·답글)은 코멘트 목록과 같은 `CommentsViewModel`의 줄 묶음을 그대로 써서 목록과 같다(링크·양식 칸은 뺌). 번호는 문서 전체에서 이어지고 답글은 번호가 없다.
- 쪽 그림은 인쇄(PrintJob)와 같은 방식 — Flush → SaveCopy 임시 → 패치 → 별도 `PdfDocument`로 열어 PDFium 전용 스레드에서 쪽마다 보통 우선순위로 그리고(긴 변 900 px) 끝나면 닫은 뒤 임시 파일 삭제. 편집 중인 PDFium 문서에는 선 같은 패치 단계 주석이 없기 때문(M3a V10/V11). 패치할 것이 없거나 암호 문서면 세션 문서에서 그린다. 번호 표시 위치는 `PageTransform`(회전 반영)으로 계산. PDF 쓰기는 스레드 풀.

### 자동 테스트
- Exchange(`SummaryPdfBuilderTests` 7개): `Summary_FirstPageHeaderAndCount`, `Summary_PagesWithoutCommentsSkipped`, `Summary_LongCommentFlowsToNextPage`(400문장 → 3쪽 이상, 이어서 머리 반복, 모든 글 빠짐없이 추출), `Summary_KoreanTextExtractable`(PDFium 글자 추출), `Summary_RepliesIndented`(PDFium 글자 위치), `Summary_Cancel_NoFileLeft`(처음부터·쓰는 도중 취소, 기존 파일 유지), `Summary_MissingGlyph_ReportedNotSilent`.
- App(`AnnotationSummaryRunnerTests` 5개): 주석 있는 쪽만(1·2쪽), 선이 있는 쪽 그림에 붉은 선 픽셀이 있음(= 패치된 사본에서 그림), 취소하면 출력·임시 파일이 남지 않음, 명령이 저장 위치를 기본 이름으로 묻고 파일을 만듦, 주석이 없으면 알림만.

### 눈으로 확인 (결과 PDF를 PDFium으로 렌더한 PNG)
- 1쪽: 맨 위 큰 제목(문서 이름), 회색 "만든 시각"·"주석 5개", 구분선. "1쪽" 아래 왼쪽에 쪽 그림(회색 줄무늬 가짜 쪽)과 그 위 붉은 테두리 3개와 왼쪽 위 붉은 번호 배지 1·2·3. 오른쪽에 `1 형광펜 / 김재민 · 시각 / 검토 필요`, 2번 아래 들여쓴 답글, 3번의 긴 한글 내용이 줄바꿈되어 쪽 아래까지 이어짐. 한글이 깨지지 않음.
- 2쪽: "3쪽" 머리, 그림에 번호 4 표시, 목록 4번, 맨 아래 붉은 알림 "글꼴에 없는 글자 1개: U+1F600".
- 이 렌더는 임시 시험(저장소에 안 넣음)으로 만든 것이고, 실제 앱에서 문서를 열어 명령을 눌러 본 것은 아니다.

### 검증 필요·한계
- 실제 앱 화면(저장 대화상자, 진행 창, 취소 단추)은 눈으로 확인하지 못했다. 진행 창은 `PrintProgressDialog` 재사용이고 가짜 프롬프트로 흐름만 시험했다.
- 다른 뷰어(Acrobat 등)에서 요약 PDF가 같게 보이는지는 확인하지 않았다(글꼴은 S1 방식 그대로).
- 큰 문서 메모리: 쪽 그림을 BGRA로 모아 한 번에 쓰므로 주석이 있는 쪽 N개면 약 N × 2 MB(900×636×4)를 잠시 쓴다(300쪽이면 약 0.7 GB). 쪽 그림 렌더·PDF 쓰기는 UI 스레드 밖이라 화면은 멈추지 않지만 이 최고치는 측정하지 않았다 — 필요하면 그림을 쪽마다 압축해 두는 방식으로 줄일 수 있다.
- 이어지는 쪽에는 쪽 그림을 다시 그리지 않고(왼쪽 비움) 목록만 오른쪽 열에 계속한다.
