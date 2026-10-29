# 용어집 (한국어 → 영어) — SedolPDF 0.4.0

화면 문구(`src/PdfEditor.App/Resources/Strings.resx` ↔ `Strings.en.resx`)를 번역할 때 같은 개념은 **항상 같은 영어 낱말**을 쓴다. 새 문구를 추가할 때 이 표를 먼저 본다. 문구는 Adobe·PDF 관례를 참고하되 자체 문장이다(다른 프로그램 화면과 똑같이 만들지 않는다).

## 문서·쪽
| 한국어 | 영어 | 비고 |
|---|---|---|
| 문서 | document | 열린 PDF |
| 쪽 / 페이지 | page | 탭 이름은 "Pages"(복수), 문장 안에서는 "page" |
| 썸네일 | Thumbnails | |
| 책갈피 | Bookmarks | |
| 첨부(파일) | Attachments / attached file | |
| 레이어 | Layers | |
| 도면 | drawing | 문장 안 일반 용어 |
| 호선 | hull no. | 조선 도면 용어: 표제란의 호선 번호는 "hull no."(번호 값은 그대로 `H-2501`) |
| 표제란 | title block | |
| 도면 번호 | drawing number | |
| 암호(가 걸린 문서) | password-protected | "encrypted"는 속성 항목(Prop_Encrypted)에서만 |
| 복구(자동 복구) | recovery / recover | |
| 인쇄 | print / printing | |

## 주석(Annotation)
| 한국어 | 영어 | 비고 |
|---|---|---|
| 주석 | annotation | 개체 하나. 탭 이름만 "Comment"(탭 폭 때문에 짧게) |
| 주석 탭 | Comment tab | 문장에서는 "Comment tab" |
| 코멘트(패널·목록) | Comment(s) | 코멘트 패널은 "Comments" |
| 답글 | Reply | |
| 상태 | Status | 승인=Accepted, 거부=Rejected, 취소=Cancelled, 완료=Completed |
| 메모 | Note | "Memo"는 쓰지 않는다 |
| 스티커 메모 | Sticky Note | |
| 텍스트 상자 | Text Box | |
| 타자기 | Typewriter | |
| 설명선 | Callout | |
| 지시선 | leader line | |
| 텍스트 강조 / 영역 강조 | Highlight Text / Highlight Area | |
| 밑줄 / 취소선 | Underline / Strikeout | |
| 텍스트 삽입 / 바꾸기 | Insert Text / Replace Text | |
| 클라우드 | Cloud | 리비전 구름 표시 |
| 스탬프 | Stamp | |
| 선·화살표·사각형·타원·다각형·폴리라인 | Line · Arrow · Rectangle · Ellipse · Polygon · Polyline | |
| 연필 / 지우개 | Pencil / Eraser | |
| 실선·점선·파선·일점쇄선 | Solid · Dotted · Dashed · Dash-dot | |
| 스타일(주석 스타일) | Style | |
| 요약(주석 요약) | Summarize / summary | |

## 편집·찾기
| 한국어 | 영어 | 비고 |
|---|---|---|
| 개체 / 객체 | object | 이미지·도형·글 객체 |
| 텍스트 편집 | Edit Text | |
| 문단 | paragraph | |
| 묶음(폼 XObject) | group | 사용자 문장에서는 "group" |
| 찾기 / 찾아 바꾸기 | Find / Find and Replace | |
| 완전일치(낱말 단위) | Whole word | |
| 대체 글꼴 | substitute font | 앱 글꼴 = "app font"/"default font"는 같은 개념: 안내 문장에서 "default font" 또는 "app font" 중 문장에 맞게 |
| 스냅숏 | Snapshot | |
| 실행 취소 / 다시 실행 | Undo / Redo | |

## 화면
| 한국어 | 영어 |
|---|---|
| 리본 탭: 홈·주석·편집·페이지·보기·도움말 | Home · Comment · Edit · Pages · View · Help |
| 패널 | panel |
| 속성(패널) | Properties |
| 환경 설정 | Preferences |
| 정보 | About |
| 타사 고지 | third-party notices |
| 후원하기 | Sponsor |
| 별 누르기 | Star on GitHub |

## 규칙
- 한글 UI 단추의 동사형(저장·열기 등)은 영어 명령형 한 낱말: Save, Open.
- 안내 문장은 "~합니다"체 → 간결한 현재형/명령형. 번역투(…될 수 있습니다 → "can be …ed")는 피하고 능동으로: "The file was damaged…".
- 숫자 단위: mm, pt, %. 날짜·숫자 서식은 `AppLanguage.FormatCulture`(화면 언어 기준).
- 복수형은 단순화한다: 문구에 개수를 넣을 때 "page(s)"처럼 쓰거나 개수가 앞에 오는 형태("{0} annotations")를 쓴다(1개일 때의 복수형은 알려진 한계).
- 고유명사(SedolPDF, PDFium, Noto Sans KR 등)와 사람 이름은 번역하지 않는다. 언어 이름은 자기 언어로(한국어, English).
