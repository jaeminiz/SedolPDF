# Microsoft Store 목록 자료 초안 (SedolPDF)

> 초안입니다. 아래 기능 문구는 README·결과 문서 기준이며, 제출 직전에 실제 앱과 다시 대조하세요(검증 필요). 다른 PDF 프로그램 이름은 설명·키워드에 쓰지 않습니다(상표·심사 문제).

## 기본 정보
| 항목 | 값 |
|---|---|
| 예약할 앱 이름 | `SedolPDF` (한국어 목록 이름: `세돌PDF`). 먼저 `SedolPDF`를 예약하고, 한국어 목록의 표시 이름만 `세돌PDF`로 둔다. 둘 다 이미 쓰이고 있으면 `SedolPDF - PDF 편집기` 등으로 대체(검증 필요) |
| 게시자 표시 이름 | Park Jaemin (개인 계정 이름) — 제작자 Park Jaemin·Park Sehyeon은 설명 아래 줄과 저작권 줄에 표기 |
| 범주 | 생산성 (Productivity) |
| 가격 | 무료, 앱 내 구매·광고 없음 |
| 지원 언어 | 한국어(기본), 영어 |
| 저작권·상표 | © 2026 Park Jaemin, Park Sehyeon |
| 웹사이트 | https://github.com/jaeminiz/SedolPDF |
| 지원 연락처 | GitHub Issues 또는 jaemini@gmail.com |
| 개인정보처리방침 URL | 게시 후 `https://jaeminiz.github.io/SedolPDF/privacy/` (게시 전 사용자 승인 필요) |
| 라이선스 약관 | MIT (스토어 기본 약관 대신 사용자 지정 약관으로 `LICENSE` 링크 가능 — 검증 필요) |

## 한국어 (기본)
**짧은 설명 (100자 이내 권장):**
무료 오픈소스 PDF 편집기. 보기, 주석, 쪽 편집, 글·이미지 직접 편집을 한 프로그램에서.

**설명:**
세돌PDF는 Windows용 무료 PDF 편집기입니다. 개인과 기업 모두 무료로 쓸 수 있고, 광고와 로그인이 없습니다. 열어 본 파일은 내 컴퓨터 밖으로 나가지 않습니다.

주요 기능
• 보기: 빠른 열기, 큰 도면도 부드럽게, 확대·축소·쪽 이동, 찾기
• 주석: 형광펜, 밑줄, 선·화살표·도형, 텍스트 상자, 스탬프, 메모, 지우개, 코멘트 목록과 답글·상태
• 쪽 편집: 쪽 추가·삭제·회전·순서 바꾸기, 썸네일(여러 열·크기 슬라이더), 파일 끌어 놓아 넣기
• 직접 편집: 글자 고치기(굵은 글·색·크기를 원본에 맞춤), 글자 형식 보기와 바꾸기, 문단 편집, 이미지·도형 넣기와 옮기기
• 탭 오른쪽 클릭 메뉴(폴더 열기, 경로 복사, 닫기), 마우스 휠 버튼으로 화면 이동
• 여러 창: 파일 → 새 창(Ctrl+Shift+N), 탭을 창 밖으로 끌면 새 창, 다른 창 탭 줄에 놓으면 그 창으로 이동(Esc로 취소), 탭 오른쪽 클릭 메뉴로도 이동
• 용량 줄여 저장: 목표 용량(MB)을 정하면 이미지를 다시 압축해 새 파일로 저장(메일 첨부용), 못 맞추면 변환 방식을 골라 예상 용량 확인
• 저장 때 이전 버전이 망가뜨린 쪽 복구, Acrobat과 호환되는 글상자, 주석 회전(초록 손잡이, Shift=15°), 개체 드래그 때 개체 그림 표시
• 개체 복사·잘라내기·붙여넣기·복제(Ctrl+C/X/V/D, 오른쪽 클릭 메뉴), 주석 복사·붙여넣기, 개체를 끌어 회전(Shift=15°, 각도 표시), 확대·축소 1%~6400%
• 주석 요약 PDF 만들기, 주석 가져오기/내보내기(XFDF)
• 인쇄, 자동 복구
• Windows 11 디자인(Fluent), 한국어·영어 화면

제작: Park Jaemin, Park Sehyeon. MIT 라이선스 오픈소스 — https://github.com/jaeminiz/SedolPDF

**이 버전의 새 기능 (릴리스 노트):**
0.11.1: 오른쪽 위 최소화·최대화·닫기 버튼이 환경에 따라 사라지던 문제를 고쳤고, 버튼 모양을 Windows 11에 맞췄습니다. 0.11.0: 썸네일 패널이 Acrobat처럼 여러 열로 보이고, 크기 슬라이더(64~1000px, 설정 기억)로 썸네일 크기를 바꿀 수 있습니다. 사이드바 폭을 더 넓게 늘릴 수 있고, 썸네일 패널에서 PageUp/PageDown 쪽 이동이 다시 동작합니다. 주석 수정: Acrobat이 만든 글상자·설명선(/RD 여백 포함)을 편집·이동할 수 있고 테두리 색과 글자 위치가 유지됩니다.
0.10.0: 0.8.0 이하 버전의 저장으로 망가진 쪽을 저장할 때 고치고 알림과 "다시 열기" 단추를 보여 줍니다. 글상자가 Acrobat과 호환됩니다(글자 크기·테두리 색을 양쪽에서 유지). 주석을 초록 손잡이로 회전합니다(Shift=15° 단위, 상자는 90° 단위로 붙음). 개체를 끌 때 개체 그림이 따라옵니다. 고친 문제: 개체를 돌리거나 옮긴 뒤 글자가 잘리던 문제(0.9.0).
0.9.0: 개체 복사·잘라내기·붙여넣기·복제(Ctrl+C/X/V/D와 오른쪽 클릭 메뉴, 같은 문서 안), 주석(영역 강조 포함) 복사·붙여넣기, 개체를 끌어 회전(Shift=15° 단위, 각도 표시), 회전한 쪽에서도 방향키 이동·붙여넣기가 올바르게 동작, 확대·축소 1%~6400%. 고친 문제: 개체를 옮길 때 화면이 잠깐 비던 문제, 크게 확대할 때 보이던 격자선, 영역 강조 아이콘 색.
0.8.0: 여러 창(파일 → 새 창, Ctrl+Shift+N). 탭을 창 밖으로 끌어 놓으면 새 창이 되고, 다른 창의 탭 줄에 놓으면 그 창으로 옮겨집니다(Esc로 취소). 탭 오른쪽 클릭의 "새 창으로 이동"·"다른 창으로 이동"도 같습니다. 옮겨도 저장 안 한 편집과 실행 취소 기록은 그대로이고, 새 창은 화면 안으로 자리가 보정됩니다.
0.7.0: 용량 줄여 저장(파일 → 다른 형식으로 저장 → 용량 줄여 저장: 목표 용량을 정하면 이미지를 다시 압축, 흑백 스캔은 JBIG2, 못 맞추면 컬러 이미지·도면용 변환 중에서 예상 용량을 보고 선택, 원본은 바뀌지 않음), 다른 PDF 프로그램이 만든 사각형·타원(/RD 여백)과 타자기 글자 주석도 선택·편집.
0.6.0: 텍스트 편집 개선(겹쳐 찍은 굵은 글에서 글이 사라지던 문제 해결, 편집 가능한 글 조각 미리 표시, 반투명 제자리 편집 칸, 편집한 글의 굵기·색·크기를 원본에 맞춤), 탭 오른쪽 클릭 메뉴(포함하는 폴더 열기·전체 경로 복사·이 탭 닫기), 속성 패널의 글자 형식(글꼴·크기·색·굵게·기울임·명조) 보기와 바꾸기, 마우스 휠 버튼으로 화면 이동.
기본 기능은 사용할 수 있지만 아직 다듬는 중입니다. 중요한 파일은 원본을 따로 보관하고 쓰세요.

**검색 키워드 (7개 이하, 각 30자 이내):**
PDF, PDF 편집, PDF 뷰어, 주석, 형광펜, 무료 PDF, 오픈소스

## English
**Short description:**
Free, open-source PDF editor: view, annotate, edit pages, text and images.

**Description:**
SedolPDF is a free PDF editor for Windows, for personal and business use. No ads, no sign-in, and your files never leave your computer.

Features
• Viewing: fast open, smooth with large drawings, zoom, page navigation, search
• Annotations: highlight, underline, lines/arrows/shapes, text boxes, stamps, notes, eraser, comment list with replies and status
• Page tools: add, delete, rotate, reorder pages; thumbnails (multiple columns, size slider); drag-and-drop files in
• Direct editing: fix text (edited text keeps the original weight, color and size), view and change text format, edit paragraphs, add and move images and shapes
• Tab right-click menu (open folder, copy path, close); pan with the mouse wheel button
• Multiple windows: File → New Window (Ctrl+Shift+N); drag a tab out to open a new window, drop it on another window's tab row to move it (Esc cancels); also on the tab right-click menu
• Save with reduced size: set a target (MB) and images are recompressed into a new file (for email); if it can't be met, pick a conversion with its expected size shown
• Repairs pages damaged by older saves, Acrobat-compatible text boxes, annotation rotation (green handle, Shift = 15°), object image follows while dragging
• Copy, cut, paste and duplicate objects (Ctrl+C/X/V/D, right-click menu), copy/paste annotations, drag-to-rotate objects (Shift = 15°, angle shown), zoom 1% to 6400%
• Annotation summary PDF, annotation import/export (XFDF)
• Printing and auto-recovery
• Windows 11 Fluent design, Korean and English interface

By Park Jaemin and Park Sehyeon. MIT-licensed open source — https://github.com/jaeminiz/SedolPDF

**Release notes:** 0.11.1 — fixed the minimize/maximize/close buttons at the top right disappearing on some setups, and restyled them to match Windows 11. 0.11.0 — the thumbnail panel now shows multiple columns like Acrobat, with a size slider (64–1000 px, remembered). The sidebar can be widened further, and PageUp/PageDown page navigation works again in the thumbnail panel. Annotation fixes: text boxes and callouts made by Acrobat (with /RD margins) can be edited and moved, keeping border colour and text position. 0.10.0 — pages damaged by saves in 0.8.0 and earlier are repaired on save, with a notice and a Reopen button. Text boxes are compatible with Acrobat (font size and border colour kept both ways). Annotations can be rotated with the green handle (Shift snaps to 15°, boxes snap to 90°). The object image follows while dragging. Fixed: text being clipped after rotating or moving objects (a 0.9.0 bug). 0.9.0 — copy, cut, paste and duplicate objects (Ctrl+C/X/V/D and the right-click menu, within the same document), copy/paste annotations (including area highlight), drag objects to rotate them (Shift snaps to 15°, angle shown), correct arrow-key moves and pasting on rotated pages, zoom 1% to 6400%. Fixed: the view briefly going blank while moving an object, faint grid lines at high zoom, and the area-highlight icon color. 0.8.0 — multiple windows (File → New Window, Ctrl+Shift+N). Drag a tab out of the window to open it in a new window, or drop it on another window's tab row to move it there (Esc cancels); "Move to new window" and "Move to another window" on the tab right-click menu do the same. Unsaved edits and undo history move with the tab, and new windows are repositioned to stay on screen. 0.7.0 — save with reduced size (File → Save in another format → Save with reduced size: set a target size and images are recompressed, black-and-white scans use JBIG2; if the target can't be met, choose between color-image and drawing-mode conversion with expected sizes shown; the original is never changed), and rectangles/ellipses with /RD margins and typewriter text annotations made by other PDF tools can now be selected and edited. 0.6.0 — better text editing (fixes text vanishing from overprinted bold text, editable text pieces previewed, translucent in-place editor, edited text keeps the original weight/color/size), tab right-click menu (open containing folder, copy full path, close tab), text format (font, size, color, bold, italic, serif) in the properties panel, and panning with the mouse wheel button. Core features are usable but still being polished; keep a backup of important files.

**Keywords:** PDF, PDF editor, PDF viewer, annotate, highlight, free PDF, open source

> 참고: 이 파일의 화면 언어 문구(한국어·영어)는 0.4.0 제출용입니다. 현재 심사 중인 0.3.0 제출본은 한국어 화면뿐이라 "Korean interface"로 이미 입력했습니다(화면 언어는 환경 설정 > 화면 언어, 다시 시작 후 적용).

## 스크린샷 규격
- 형식: PNG, 장당 50 MB 이하, 최소 1~최대 10장. 데스크톱: 가로 1366×768 이상 권장(검증 필요: 현재 Partner Center 규격).
- 현재 `docs/screenshots/01-info.png`~`06-print-preview.png` 6장(정보·홈·주석·편집·페이지·인쇄 미리보기, 1600×1000 / 인쇄 창 980×880, 영어판은 `docs/screenshots/en/`). 스토어에는 이 6장을 올린다. 다시 만들기: 환경 변수 `SEDOL_STORE_SHOTS_DIR`, `SEDOL_STORE_SAMPLE`을 정하고 `dotnet test tests/PdfEditor.App.Tests --filter Generate_StoreScreenshots`.
- 샘플 PDF는 직접 만든 가상의 제품 소개서(SEDOL Analytics)이며 개인정보·회사 도면이 없다. 문서에 실제 회사명(BP, HSBC 등)이 시세 예시로 나오므로 스토어 심사 전에 한 번 더 확인한다.
- 스크린샷 촬영은 헤드리스 렌더 또는 사용자가 허락한 시간에만(화면 조작 시험 지침).

## 앱 로고(스토어 목록용)
- 1:1 아이콘 300×300 PNG 권장(필수 아님/검증 필요). `assets/icon/sedolpdf-1024.png`에서 줄여서 만든다.
