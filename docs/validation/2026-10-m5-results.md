# M5 기술 검증 결과 (Task 1: V17~V21) — 2026-10-22

범위: 문단 편집(M5) 계획서 Task 1. 탐침 구현은 테스트 안에만 있다(`tests/PdfEditor.TextEdit.Tests/ProbeCore·ProbeFonts·ProbeStudy·V17ToV21Probes·V20Probes·V21Probes.cs`, 샘플 `tests/TestFixtures/ParagraphPdfFactory.cs`). 정식 구현은 Task 2~4.
실제 PDF는 `private/`(커밋 금지)의 4개 파일에서 **앞 5쪽씩만** 썼고 파일 이름·내용은 적지 않는다(아래는 `real-doc-1/2`, `real-dwg-1/2` 같은 번호와 수치뿐). 수치 원본은 시험을 돌릴 때 임시 폴더의 `m5-v*.txt`로 나온다.

## 한눈에 보는 판정

| 항목 | 합격 조건 | 결과 | 판정 |
|---|---|---|---|
| V17 표 병합 오탐 | 0건 | 합성 22개 샘플 전 문턱에서 0건 | **통과** |
| V17 합성 문서형 정밀도 | ≥ 95 % | 문턱 0.62에서 100 % (44/44), 재현율 97.8 % | **통과** |
| V17 실제 문서형 무편집 재배치 재현율 | ≥ 70 % | 6/6 = 100 % — **단, 표본 6개 문단이고 규칙 하나를 같은 쪽을 보고 고친 뒤의 수치(아래 "주의")** | 통과(근거 약함) |
| V18 코드북 적용 범위 | 실패 비율 > 50 %면 C8 비중을 낮춤 | 엄격(C8 그대로) 실제 문서 99.4 %, **실제 도면 46.9 %(실패 53.1 %)**. "PDFium 글과 짝지어 배우기"로 확장하면 도면 100 % | 통과(확장 필요) |
| V19 한글 줄 이음 | C16 기본값 결정 | **실제 한글 문서가 글자 단위 줄바꿈** → C16 기본값을 바꿔야 함(아래) | 결정함 |
| V20 직접 인코딩 충실도 | 차이 ≤ 1 % | 글꼴 5종 10/10 차이 0.00 %, 자간·가로비율·TJ 폭 변화 공식과 일치 | **통과** |
| V20 지운 글 완전 제거 | 뒤 글 위치 불변·옛 글 안 남음 | 합성 8/8, 실제 글 객체 4,559개 중 100 % 연산자째 삭제 가능 | **통과** |
| V21 성능 | 도면 ≤ 100 ms, 문서 ≤ 30 ms | 도면 73 ms(코드북 제외), 문서 1~3 ms. **코드북 재료는 A1에서 402 ms** | 조건부 통과 |

**결론: 멈출 이유(표 병합 0·V18 전부 실패)에 해당하지 않아 계획을 유지한다. 계획에 반영할 변경 5가지(C4 문턱·특징, C8 확장, C16 기본값, 성능 구조, 알려진 미탐)는 아래와 계획서 "확정 기록"에 적었다.** 실제 PDF 표본이 작아(문서 6문단, 도면 1문단) 수치의 통계적 힘이 약하다 — Task 2에서 더 많은 쪽으로 다시 재야 한다.

## 샘플 (ParagraphPdfFactory)
22쪽, 정답표(줄 → 문단 번호·표 칸 여부·겹쳐 찍음 여부) 포함. 글 폭은 PDFsharp 측정값이라 줄바꿈이 실제 글꼴 폭과 같다.
- **문서형 14:** 영문 왼쪽 맞춤 / 영문 양쪽 맞춤(낱말마다 글 객체, 첫 줄 들여쓰기) / 한글 양쪽 맞춤(들여쓰기만 있고 문단 간격 0 포함) / 한글 왼쪽 어절 줄바꿈 / 한글 글자 단위 줄바꿈 / 제목+본문+소제목+글머리표 / 2단 / 한 줄 안에 굵은 낱말 섞임(묶으면 안 됨) / 겹쳐 찍은 굵게 / 가운데·오른쪽 맞춤 / 단순 TrueType WinAnsi / 표준 14(Helvetica) / 단순 Type1 Differences / 목차(제목+쪽번호 같은 기준선).
- **도면형 8:** 표제란(선 격자, 라벨 7 pt + 값 10 pt, 두 줄 값 칸, 같은 글꼴 개정란) / 선 없는 표 4열×6행 / 주기 번호 목록(이어지는 줄 들여쓰기) / **범례(같은 글꼴 짧은 항목이 줄 간격 1.3 em으로 쌓임 — 일부러 어려운 사례)** / 90° 문단 / 치수 글 40개 흩어짐 / 폼 XObject 안 글 / 보이지 않는 글(Tr 3)이 스캔 그림 위에 8줄.
- 폼 안 글은 쪽 글 객체로 안 보임(1개만 읽힘), 보이지 않는 글 8줄은 전부 걸러짐(읽힌 글 0) — V15 결론과 같다.

## V17 문단 추정 정확도·재현

### 탐침 알고리즘(정식 구현의 출발점)
- **글 읽기:** PDFium 글 객체의 행렬(E,F = 기준선 원점, A·B = 방향, C·D = 글자 크기 배율)과 경계. 각도는 0·90·180·270°만(±3°), 렌더 모드 3·투명 글은 제외. 글 방향 기준 로컬 좌표(u = 글 흐름, v = 위)로 바꿔 쓴다.
- **줄 조립(C3 ①):** 같은 기준선(±0.15 글자 크기)·같은 각도의 글 객체를 u순으로 잇는다. 틈 > 1.0 em이면 다른 칸. 보정 3가지 — 글머리표(≤ 3자 + 마커)는 틈 3 em까지 잇고, 두 글 사이에 **가는 세로선**이 있으면 끊고, 겹쳐 찍은 같은 글(0.12 em 이내)은 한 객체로 본다. 줄 글 안의 공백은 틈 ≥ **0.25 em**일 때만 넣는다(실제 PDF의 낱말 안 글자 틈 분포가 0.25 em 아래라 처음의 0.12 em은 낱말을 쪼갰다).
- **표의 행 판정(TableRow):** 같은 기준선에 다른 칸이 있고, 두 칸의 "같은 열 글 폭 중앙값"이 모두 12 em 이하이거나 사이에 세로선이 있으면 그 줄은 묶지 않는다. (처음에 글자 수 24자로 했더니 2단 문단의 짧은 끝 줄과 좁은 3단 문단이 표로 오판되어 폭(em) 기준으로 고쳤다.)
- **문단 묶기(C3 ②):** 씨앗 줄에서 아래·위로 한 줄씩. 같은 글꼴·크기(±3 %)·색, 줄 간격 1.0~2.2 em에 첫 간격 ±25 %, 두 줄 사이에 선·경로 없음, 가로 겹침, 정렬(왼쪽·가운데·오른쪽, 양쪽 맞춤은 왼쪽+오른쪽 끝 모임) 일관, 글머리표·번호 줄은 새 문단, 들여쓴 줄은 새 문단 첫 줄, 양쪽 맞춤 문단의 짧은 끝 줄 뒤엔 이어 붙이지 않음, **열 폭의 절반 이하인 짧은 줄은 문단의 끝(실제 PDF에서 문단 두 개가 합쳐진 것을 보고 추가)**.
- **신뢰도(C4):** 줄 수(2줄 0.55, 3줄 0.72, 4줄 0.80, 5줄 이상 0.85) + 간격 균일(±) + 양쪽 맞춤 +0.08 + 앞줄이 열 폭을 채움 +0.10 − 줄 간격이 1.1~1.9 em 밖 0.10 − 2줄인데 간격 > 1.6 em 0.15 − **짧은 줄**(평균 폭 < 10 em: 0.25, < 15 em: 0.10) − 낱말 3개 이하 0.10 − **목록 성격**(이음 줄이 대문자로 시작하는 라틴 글이 60 % 이상, 전부 대문자인 글은 제외: 0.25 / 가운데 맞춤 4줄 이상: 0.30). 평균 폭 < 4 em이면 0.3 상한.

### 합성 결과 (문턱별)
| 문턱 T | 문서형 묶음/정확 | 문서 재현율 | 도면형 묶음/정확/잘못 | 도면 재현율 | 표 병합 |
|---|---|---|---|---|---|
| 0.00 | 45/45 | 100 % | 9/7/**2**(범례) | 87.5 % | 0 |
| 0.55 | 45/45 | 100 % | 7/6/1 | 75 % | 0 |
| 0.60 | 44/44 | 97.8 % | 5/5/0 | 62.5 % | 0 |
| **0.62** | **44/44** | **97.8 %** | **5/5/0** | **62.5 %** | **0** |
| 0.65 | 44/44 | 97.8 % | 5/5/0 | 62.5 % | 0 |
| 0.70 | 42/42 | 93.3 % | 2/2/0 | 25 % | 0 |
| 0.80 | 39/39 | 86.7 % | 2/2/0 | 25 % | 0 |

- 정밀도 = 정확히 정답 문단과 같게 묶은 수 / 묶은 수. 놓친 문단은 한 줄 편집으로 되돌아가는 쪽이라 싼 실패다.
- 문턱 0.60~0.65 구간이 문서 정밀도 100 %·도면 잘못 묶음 0이다. 실제 도면의 "도면 목록"(같은 글꼴·가운데/왼쪽 맞춤으로 쌓인 항목 줄)이 처음에는 신뢰도 0.77~0.92로 묶였는데(표 병합류 오탐), 목록 성격·가운데 4줄 특징을 넣은 뒤 최대 0.59로 내려갔다 → **문턱 0.62 (0.59 위, 2줄 주기 항목 0.65 아래).** 7개 샘플이 아니라 22개 + 실제 4개로 맞춘 값이라 Task 2에서 재확인이 필요하다.
- 놓친 문단(T=0.62): 문서 1개(가운데 맞춤 2줄 제목, 신뢰도 0.55), 표제란 칸의 두 줄 값(선으로 막힘), 선 없는 표 칸의 두 줄 값(같은 기준선 이웃 있음 → 표 행), 주기 2줄 항목 하나(신뢰도 0.55). 모두 의도한 보수적 미탐.
- 범례는 문턱 없이는(T=0) 2개 묶음이 틀린다 → **문턱이 실제로 필요한 사례**.
- 같은 알고리즘의 재배치 점검(합성): 측정 가능 40개 문단 중 39개 재현(97.5 %). 9개는 코드북이 안 되는 글꼴(WinAnsi·표준 14·Differences)이라 측정 불가.

### 실제 PDF (앞 5쪽 × 4개)
| 파일 | 글 객체 | 줄(칸) | 표 행으로 막힌 줄 | 묶음(T=0.62, 2줄↑) | 묶인 줄 / 전체 |
|---|---|---|---|---|---|
| real-doc-1 | 735 | 114 | 15 | 5 | 19 / 114 (17 %) |
| real-doc-2 | 730 | 107 | 14 | 1 | 4 / 107 (4 %) |
| real-dwg-1 | 621 | 598 | 512 | 1 | 4 / 598 (0.7 %) |
| real-dwg-2 | 2,473 | 85 | 18 | 0 | 0 |

- 앞 5쪽에는 문단이 적다(표지·목차·표·도면 목록·한 줄 항목이 대부분). 그래서 표본이 문서 6문단·도면 1문단뿐이다. **합격선(문서 70 %)은 넘었지만 통계적으로 약하다.**
- **무편집 재배치 재현율(코드북으로 잰 폭 + 낱말/글자 단위 줄바꿈):** 문서 6/6 (측정 가능 100 %), 도면 1/1.
- **주의(자기 평가 오염):** 처음(규칙 추가 전)에는 문서 8개 묶음 중 6개(75 %) 재현이었다. 실패 2개는 *문단 두 개가 합쳐진 것*(짧은 끝 줄 뒤에 새 문단이 이어짐)이라 "열 폭의 절반 이하 짧은 줄은 문단 끝" 규칙을 넣었고, 합쳐졌던 묶음이 사라져 6/6이 됐다. 같은 5쪽으로 고친 것이므로 100 %는 낙관적이다. **Task 2에서 다른 쪽(예: 6~15쪽)으로 새로 재야 한다(검증 필요).**
- 실제 도면의 표 행 판정: 도면 1에서 598줄 중 512줄(86 %)이 표 행 → 문단 편집 대상은 거의 없고 대부분 한 줄 편집이다(도면은 표·라벨 위주라는 가정과 일치).

## V18 코드북(원래 글꼴 직접 인코딩) 적용 범위
글 객체(run) 단위. 쪽 내용 해석기(M4x)의 객체 순서가 PDFium과 같은 쪽만 대응(합성 22쪽·실제 20쪽 모두 대응 성공).

| 집합 | 글 객체 | 엄격(C8 그대로: ToUnicode·폭 필요) | "PDFium 글과 짝지어 배우기" 확장 후 |
|---|---|---|---|
| 합성 | 625 | 598 (95.7 %) | 625 (100 %) |
| 실제 문서 | 1,170 | 1,163 (99.4 %) | 1,170 (100 %), 충돌 2 |
| **실제 도면** | 1,346 | **631 (46.9 %)** | 1,346 (100 %), 충돌 0 |

- 실제 도면에서 엄격 실패 715개의 정체: `Type0 / CIDFontType0(CFF) / Identity-H`인데 **ToUnicode 없음** 625개(도면 글의 46 %), 단순 Type1 CFF 46개, 비임베드 TrueType 44개. 엄격 규칙은 이것을 못 쓴다.
- **확장(C8'):** ToUnicode가 없어도 PDFium이 읽어 준 글(유니코드)과 글 객체의 코드열을 순서대로 짝지어(공백 제외, 코드 수 = 글자 수일 때) (코드 → 글자)를 배운다. 이 문단에 쓰인 글자만 코드북에 들어가므로 "서브셋에 글리프가 있다는 보증이 구조적으로 생긴다"는 C8의 핵심은 그대로다. 성공 625/625·46/46·44/44·7/7, **같은 코드가 다른 글자로 풀린 충돌은 실제 비임베드 TrueType 7개 중 2개에서만**(그 글꼴은 대체 글꼴로).
- 글자 일치(코드북으로 푼 글 vs PDFium 글, 공백 무시): TrueType Identity-H 1,159/1,163, 619/621, CFF(ToUnicode 있는 단순 Type1) 10/10.
- 폭 정확도(코드북 공식 vs PDFium 객체 폭): PDFium 객체 경계가 글자 잉크(좌우 여백 제외)라 계획의 "1 % 또는 1 pt" 기준은 직접 못 잰다. 실제 문서 TrueType 1,163개 중 323개 ≤ 1 %/1 pt, 722개 ≤ +0.15 em(글자 여백), 118개(10 %) 초과, 실제 도면 CFF(배운 코드) 625개 중 50/551/24(3.8 %) 초과, **비임베드 TrueType 44개 중 22개 초과(폭표가 렌더 글꼴과 안 맞음)**. 초과분의 원인은 끝 공백·글자 여백이 대부분으로 보이나 전부 확인하지는 못했다 → 공식 자체의 정확성은 V20(같은 코드를 다시 쓰면 같은 렌더·폭 변화가 공식과 일치)과 V17 재현율로 간접 확인. **글자 폭이 렌더 폭과 안 맞는 글꼴(비임베드)은 코드북 대상에서 빼고 대체 글꼴로 보내는 것이 안전하다.**
- 표준 14(Helvetica, `/Widths` 없음): 엄격 불가. 이 글꼴은 AFM 폭표를 내장하는 방법이 있으나 범위 밖 — 대체 글꼴로 간다.
- 실제 문서 Type0 TrueType 글꼴은 대부분 `ToUnicode`가 있어 엄격 규칙만으로 99 % 가능. 문서형이 주 경로인 계획(C8)은 맞았고, 도면형은 확장이 있어야 주 경로가 된다.

## V19 한글 줄 이음
측정: 같은 열(글 시작 3 em 단위)에서 가장 먼 줄 끝과 이 줄 끝의 차이를 글자 크기로 나눈 **여유(slack)**. 낱말(어절) 단위로 줄바꿈하면 여유가 1.1 em을 넘는 줄이 많고(다음 낱말 길이만큼), 글자 단위로 줄바꿈하면 거의 없다(글자 하나 폭).

| 문서 | 한글 문단 | 비마지막 줄 | 여유 > 1.1 em 비율 | 중앙값 / 최대 | 판정 |
|---|---|---|---|---|---|
| 합성 양쪽 맞춤(어절) | 5 | 9 | 56 % | 1.98 / 3.2 em | 어절 단위 ✓ |
| 합성 왼쪽 어절 | 4 | 8 | 88 % | 1.87 / 2.8 em | 어절 단위 ✓ |
| 합성 글자 단위 | 4 | 8 | 13 % | 0.40 / 1.3 em | 글자 단위 ✓ |
| **real-doc-1** | 4 | 11 | **0 %** | 0.27 / 0.8 em | **글자 단위** |

- 판정 규칙(데이터에서): 열의 비마지막 줄이 6개 이상일 때 비율 ≥ 30 % → 어절 단위, ≤ 15 % → 글자 단위, 그 사이 → 불명(공백 이음, 검증 필요). 합성 3종을 모두 맞게 가르고(오판정 0/3) 실제 한 문서가 글자 단위로 나왔다.
- 실제 문서에서도 눈으로 확인한 결과 "참/조하여", "이/동합니다"처럼 낱말 가운데에서 줄이 끊기는 줄이 흔했다. 따라서 **C16의 "한글끼리 줄 끝-줄 시작은 항상 공백으로 잇는다" 기본값은 틀리다**(이 문서에서 "참 조하여"가 됨). 한 줄의 끝이 정확히 낱말 사이였는지는 알 수 없어(공백 글자가 사라짐) 글자 단위 문서에서 이음 없이 붙이면 낱말이 붙을 수도 있다 — 둘 다 불완전하므로 문서·열 단위 판정 + 편집 창에서 사용자가 고칠 수 있게 한다. 표본은 한글 문서 1개(문단 4개)뿐이라 **검증 필요**.
- 합성에서 "글자 단위"로 재현을 시험하면 낱말 단위 재현도 대부분 맞는다(이음 전 줄 끝 글자가 낱말 조각으로 취급되어 같은 곳에서 끊기므로). 재현율만으로는 이음 방식을 못 가르고 위의 여유 통계가 필요하다.

## V20 직접 인코딩 쓰기 충실도 + 지운 글 완전 제거
### (a) 같은 글을 코드북으로 다시 인코딩해 같은 자리에 덧붙이고(원래 객체는 연산자째 제거) PDFium으로 렌더 비교
| 글꼴 | 샘플 | plain 차이 |
|---|---|---|
| Type0 CIDFontType2 TrueType Identity-H (영문) | doc-en-left | 0.00 % ×2 |
| 같은 종류, 한글(맑은 고딕) | doc-ko-left-wordwrap | 0.00 % ×2 |
| 단순 TrueType WinAnsi(ToUnicode 없음, 폭 있음 — 완화 규칙) | doc-en-winansi | 0.00 % ×2 |
| 단순 Type1 + Differences + Widths(완화 규칙) | doc-differences | 0.00 % ×2 |
| **Type0 CIDFontType0 CFF(앱 글꼴 서브셋, 한 Tj에 여러 CID 포함)** | cff-sample | 0.00 % ×2 |
| 표준 14 (Helvetica, 폭표 없음) | doc-std14 | 건너뜀(코드북 불가 → 대체 글꼴) |

합격 10/10 (잉크 칸 대비 차이 칸 ≤ 1 %가 합격). 같은 PDFium으로 그린 비교라 **영문 쌍은 Adobe·PDF-XChange·SedolPDF에서 사용자가 열어 원본과 같게 보임을 확인(2026-10-22, 일부러 늘린 TJ 변형만 다르게 보임 — 예상대로). 한글·CFF 쌍과 Chrome·Foxit은 미확인 — 사용자 점검 필요.** 점검용 파일: `spikes/out/m5-v20-*.pdf` (원본/다시 쓴 것 쌍 8개: 영문·한글·CFF; Tc0.5·TJ 맞춤 변형 포함).

### (b) 자간(Tc)·가로 비율(Tz)·TJ 맞춤
같은 글을 기본(plain)으로 쓴 것 대비 PDFium 객체 폭 변화가 코드북 공식(Tc는 (글자 수−1) × Tc, Tz는 폭 × 비율 변화, TJ는 공백 수 × 추가 폭)과 일치하는지, 허용 0.3 pt + 0.5 %. TrueType Identity-H(87·44글자) 5/5, CFF(44글자) 5/5, 모두 오차 0.00 pt (Tc +0.5·−0.3, Tz 90·115 %, TJ 공백마다 +3 pt).
Tw(낱말 간격)는 2바이트 글꼴에서 안 먹으므로 C15대로 쓰지 않고 TJ 숫자로만 한다(확인 시험은 안 함 — 규격 사실).

### (c) 지운 글 완전 제거(C12)
규칙: 지울 글 보이기 연산자 **다음의 글 보이기**가 `Tm/Td/TD/T*/'/"`로 자리를 새로 잡거나 `ET/BT`가 먼저 오면 연산자째 삭제(Delete), 다음 글이 이 글의 이동량에 기대면(`Tj Tj` 연속) `3 Tr` 숨김(Hide). 지우는 연산자가 `'`/`"`이면 줄 이동(`T*`)과 `Tw/Tc` 설정은 남겨야 뒤 글이 안 움직인다(그렇게 처리).
- 합성 8경우(Tm 따로 BT / Td 상대 이동 / 이동량에 기댄 연속 Tj 가운데·첫 번째 / `'` 연산자 / 마지막 글 + ET / TJ 배열 / `T*` 줄바꿈) **8/8 통과**: 뒤 글 위치 변화 0.01 pt 미만, 화면에서 사라짐, Delete 방식은 저장된 내용 바이트에 옛 글이 없고 PDFium 글 추출에도 안 나옴, Hide 방식은 위치는 그대로지만 글이 파일에 남고 추출됨(예상대로 — 결과 창에 알림 필요).
- (d) 분류 통계(쓰기 없음): 글 객체 합성 642개 / 실제 문서 1,465개 / 실제 도면 3,094개가 **전부 Delete 가능**(Hide로 내려갈 일 0). 이 4개 파일에서는 글이 거의 항상 `Tm`/`Td`로 새로 자리를 잡거나 `ET`로 끝났다. Hide 경로는 합성으로만 검증했다.

## V21 성능 기준선 (Release, 클릭 3곳 × 3회)
| 단계 (ms, 중앙 / 최대) | A1 도면 5,000줄·205,000 객체 | 합성 문서 | 실제 문서 | 실제 도면 |
|---|---|---|---|---|
| (참고) 쪽 열기 / 객체 경계 목록(PageObjectIndex 상당) | 178 / 30 | 1 / 0 | 4~5 / 0 | 14 / 0 |
| ① 후보 고르기(근처 60개) | 0.27 / 7.3 | 0.02 | 0.04~0.06 | 0.03~0.08 |
| ① 글 쪽 열기(LoadText) | **39.2 / 46.5** | 0.30 | 0.6~1.7 | 0.5~0.7 |
| ① 후보 글 읽기(≤ 60개) | **23.8 / 26.3** | 0.31 | 0.3~1.2 | 0.1~0.7 |
| ① 장애물(선·경로) 읽기 | 4.8 / 8.1 | 0.01 | 0.01~0.02 | 0.02 |
| ② 줄 조립 + 문단 묶기 | 1.95 / 6.4 | 0.25 | 0.2 | 0.0~0.7 |
| **①+② 합계(PDFium 스레드 몫)** | **72.6 / 80.6** | **1.0 / 2.1** | **1.2~3.2** | **0.9~2.2** |
| ③ 쪽 내용 풀기·해석(쪽마다 한 번, PDFsharp 쪽) | **402 / 410** | 0.4 | 0.7~1.4 | 0.9~1.0 |
| ③ 문단 글꼴 코드북 + ④ 재배치 계산 | (A1에는 문단 없음) | 11.8 + 1.9 | 4.6 + 0.6 | — |

- 목표: 도면 ≤ 100 ms, 문서 ≤ 30 ms. **PDFium 스레드 몫은 A1에서도 73~81 ms로 통과.** 가장 큰 항목은 LoadText(39 ms) — 쪽 버전마다 한 번만 열어 두면 두 번째 클릭부터 사라진다(쪽 핸들 캐시와 함께 버림).
- **코드북 재료(쪽 내용 풀기·해석)가 A1에서 약 0.4 s**라 클릭 때 즉시 쓰기엔 늦다. 문서·일반 도면은 1 ms 안팎이라 문제 없다. 권장: 클릭→문단 상자 표시는 PDFium 데이터(경계·원점·글)만으로 하고(≤ 81 ms), 쪽 내용 해석+코드북은 텍스트 편집 도구 진입 때 백그라운드로 시작해 결과를 쪽 버전별 캐시(첫 문단 편집 창이 열린 뒤 입력이 시작될 때까지 약 0.4 s 안에 끝남 — 안 끝나면 입력 중 줄바꿈 계산을 그때까지 미루고 상태줄에 "글꼴 정보 읽는 중")로. 이는 M4x가 편집 쪽 저장 때 어차피 치르는 같은 해석이다.
- Debug 빌드는 몇 배 느리다(참고용으로만 Release 수치를 쓴다).
- 이 측정은 A1 1개(합성 글 "LINE n" 격자)와 실제 쪽 몇 개다. 큰 실제 도면의 문단 읽기는 추가 측정 권장.

## 계획에 미치는 영향 (변경 제안)
1. **C4 문턱 = 0.62** (0.7에서 낮춤). 점수 특징 추가: 짧은 줄 감점, 목록 성격 감점, 가운데 4줄↑ 감점, 앞줄 꽉 참 가점, 열 폭 절반 이하 줄은 문단 끝, 표 행 판정을 폭(em) 기준으로. 문턱 아래는 한 줄 편집 + 이유 안내. (Task 2에서 새 표본으로 재확인.)
2. **C8 확장:** 코드북에 "ToUnicode 없으면 PDFium 글과 짝지어 배우기"를 추가. 도면 적용 비율 46.9 % → 100 %. 같은 코드가 다른 글자로 풀리거나(충돌) 비임베드 TrueType처럼 폭표가 렌더와 안 맞는 글꼴은 대체 글꼴.
3. **C16 수정:** 한글 줄 이음 기본값은 "항상 공백"이 아니라 **문서·열 단위 여유 통계로 판정**(≥30 % 어절 단위 → 공백, ≤15 % 글자 단위 → 이음 없음, 사이/표본 부족 → 공백). 재배치(줄바꿈) 방식도 같은 판정을 따른다(글자 단위 문서는 글자 사이에서 끊음).
4. **성능 구조(C2 보강):** 클릭 경로는 PDFium 데이터만, 쪽 내용 해석+코드북은 백그라운드 + 쪽 버전 캐시(A1 0.4 s). `PdfTextPage`는 쪽 버전마다 한 번만 연다.
5. **알려진 미탐(의도한 한 줄 편집 되돌림):** 표 칸 안의 두 줄 값, 짧은 줄 목록(범례·개정란), 2줄짜리 주기 일부, 칸이 좁은 다단. 알림 문구에 이유를 쓴다.
6. **C12는 그대로 유효:** 실제 4개 파일에서 100 % 연산자째 삭제 가능. Hide 경로는 남기되 드물다.

## 알려진 한계·검증 필요
- 실제 PDF 표본이 4개 × 5쪽이고 문단이 적어(문서 6, 도면 1) 정밀도·재현율의 신뢰구간이 넓다. 규칙 하나(짧은 줄 = 문단 끝)는 같은 쪽을 본 뒤 추가했다.
- V19 한글: 한글 문서 1개 근거. 다른 한글 문서(어절 단위·양쪽 맞춤 글자 단위 등)·한글(HWP) 변환 PDF는 미확인.
- V20 렌더 비교는 PDFium만. **다른 뷰어 미확인(사용자 점검 필요).**
- 표준 14 글꼴(`/Widths` 없는 Helvetica 등)은 코드북 불가 → 대체 글꼴 경로(Task 3~4에서 확인).
- 세로쓰기·임의 각도·폼 안 글·OCR 층은 이번에도 대상 밖(V15·샘플로 읽히지 않음/걸러짐을 확인만).
- 폭 오차 "1 % 또는 1 pt" 기준은 PDFium 객체 경계가 잉크라 직접 못 쟀다(간접 확인).
- `ParagraphProbe`의 알고리즘 상수(0.25 em 공백, 12 em 열 폭, 0.5 열 폭 짧은 줄 등)는 이 표본에서 정한 값이다. Task 2에서 상수를 한곳(`ParagraphOptions`)에 모으고 시험으로 고정한다.

## Task 2 — 정식 구현 후 실제 PDF 새 쪽 재확인 (2026-10-22)
정식 구현(`src/PdfEditor.TextEdit/Paragraphs/`)으로 Task 1과 **다른 쪽**을 새로 쟀다. 실제 4개 파일의 6~10쪽(`real-doc-1`은 8쪽짜리라 6~8쪽만)과, 아래 보정 뒤에 처음 보는 11~15쪽(`real-doc-1`은 없음). 파일 이름·내용은 적지 않고 수치와 판정만 적는다(눈으로 본 확인은 임시 폴더의 `*-detail-LOCAL.txt`에서만, 저장소에 없음).

### 발견: Task 1 문턱(0.62)과 특징만으로는 새 쪽에서 "항목 나열"이 문단으로 묶였다
6~10쪽에서 문턱 0.62 이상으로 묶인 것 중 **눈으로 보아 문단이 아닌 것이 약 10개**였다(Task 1의 5쪽에서는 보이지 않던 종류):
- 매개변수 목록("the design pressure : 15.5 MPa …" 5줄, 신뢰도 1.00), 수량 목록("2 sets …" 5줄, 0.95), 장 목록("ANNEX II … V" 4줄, 0.92 — 전부 대문자라 기존 "목록 성격" 특징이 면제함), 연락처 블록(주소·Tel·Fax 2~4줄, 0.62~0.65 — 문턱 바로 위).
- 원인: 이 줄들은 글꼴·크기·간격이 모두 같고 줄 폭이 비슷해 문단 점수가 포화한다. 기존 특징(대문자로 시작하는 줄 비율 등)으로는 구분되지 않았다.
- **조치(같은 5쪽을 보고 고친 것이므로 자기 평가 오염이 있다 — 그래서 11~15쪽을 따로 쟀다):** 목록 특징 2개 추가.
  ① 줄 첫 낱말이 같은 줄이 대부분(숫자는 `#`로 보고, 숫자·대문자가 든 낱말만 셈; 소문자 the·and 겹침은 제외): 점수 −0.55.
  ② "라벨 : 값" 꼴(콜론이 줄 앞 40자 안, 뒤가 공백)인 줄이 절반 이상: −0.50.
  두 값은 점수가 1을 넘게 포화하는 경우(최대 약 1.1)를 눌러야 하므로 크게 잡았다. 문턱 0.62는 그대로.

### 수치 (문턱 0에서 2줄 이상 묶음의 신뢰도 분포, 보정 후)
| 쪽 | 종류 | 묶음 | ≥0.80 | 0.70~0.80 | 0.62~0.70 | 0.50~0.62 | <0.50 |
|---|---|---|---|---|---|---|---|
| 6~10 | 문서 | 36 | 11 | 1 | 0 | 7 | 17 |
| 6~10 | 도면 | 29 | 1 | 1 | 6 | 9 | 12 |
| 11~15 | 문서 | 10 | 7 | 1 | 0 | 2 | 0 |
| 11~15 | 도면 | 26 | 2 | 1 | 4 | 10 | 9 |

- 줄·표 행: 6~10쪽 문서 132·132줄 중 표 행 16·19, 도면 233·187줄 중 109·53(도면 1은 표 위주 — Task 1과 같은 경향). 11~15쪽 문서 83줄 중 15, 도면 419·269줄 중 363·88.
- **문턱 0.62 이상으로 묶인 것을 눈으로 판정:** 6~10쪽 20개 중 비문단 확실 0·애매 1("Pilot … / <Not included …>" 2줄), 11~15쪽 15개 중 **비문단 확실 1**(전부 대문자 도면 캡션 목록 3줄, 0.74)·애매 1("Internal piping … / Hydraulic connections …" 3줄 0.79). 합계 35개 중 확실한 오탐 1(2.9 %)·애매 2.
- **미탐(놓친 문단):** 0.50~0.62 구간의 2줄 문단이 문서 9·도면 19개이고 대부분 실제 이어진 문장이다(예: 문장 두 줄). 2줄 문단은 기본 점수가 0.55라 앞줄이 열 폭을 꽉 채워야(+0.10) 문턱을 넘는다. 의도한 보수적 미탐(한 줄 편집으로 되돌림)이며 문턱을 0.55로 낮추면 위 항목 나열 오탐이 다시 늘어 낮추지 않았다.
- 한계: 판정은 사람이 글을 보고 한 것(내 판정) 1명분이고 표본이 문서 2개·도면 2개 파일이다. **"전부 대문자 캡션 목록"형 오탐은 남아 있다** — 사용자가 문단 상자의 "줄 제외(−)"(C13)로 고칠 수 있는 수준으로 보고 Task 8의 탈출구에 맡긴다.

### 정식 구현 중 발견한 사항 (Task 3 이후에 영향)
1. **PDFium 글 쪽은 겹쳐 찍은 같은 글을 한 번만 돌려준다.** `doc-overlay-bold`(굵게 흉내, 줄마다 글 객체 2개)에서 `FPDFTextObj_GetText`가 두 번째 객체에는 빈 문자열을 준다. 그래서 PDFium으로 읽은 `TextRun`에는 한 벌만 들어오고 `TextLine.Duplicates`가 비어, **문단을 고치면 겹친 복사본 객체가 쪽에 남는다.** 순수 코드(`LineAssembler`)는 겹친 글이 입력으로 들어오면 한 줄로 합치고 원래 참조를 `Duplicates`에 보존함을 시험으로 확인했다(합성 입력). → **`ParagraphReader`(Task 5)가 빈 글 객체도 경계·위치로 대표 글 객체와 짝지어 `Duplicates`로 넘겨야 한다.** 짝지을 수 없으면 겹친 객체가 남을 수 있음을 알려야 함.
2. **오른쪽 맞춤 문단**은 앞줄 들여쓰기를 허용하는 왼쪽 맞춤 판정에 먼저 걸려 왼쪽으로 오인됐다 → 오른쪽 끝이 모두 같고 왼쪽 끝이 제각각이면 오른쪽 맞춤으로 먼저 분류(글머리표 줄·"앞줄만 다르고 나머지 왼쪽이 같은 3줄↑"은 제외 — 걸이 들여쓰기 문단). 오른쪽 맞춤 2줄 문단은 점수가 0.55라 한 줄 편집으로 남는다(알려진 미탐, 문턱을 풀면 오른쪽으로 분류됨).
3. 문단 편집 대상은 `TextLine.Runs`(편집할 객체) + `TextLine.Duplicates`(겹친 복사본)이고 `ParagraphModel.AllRefs`가 둘을 합친다(Task 5가 끄기 대상으로 사용).

### 정식 구현 합성 샘플 결과 (시험으로 고정, `SyntheticSamples_PrecisionAndTableMerge_AtThreshold`)
Task 1과 같은 합성 22쪽·정답표에서 문턱 0.62: 표 병합 0, 잘못 묶음 0, 문서형 정밀도 100 %, 문서형 재현율 ≥ 90 %(시험 기준), 도면형은 묶은 것이 모두 정답. 상수는 모두 `ParagraphOptions`에 모으고 `Options_Defaults_AreFixedByTask1`이 값을 고정한다.

## Task 3~4 — 글꼴 코드북·글 폭, 다시 맞추기 (2026-10-22, Sonnet)
구현: `src/PdfEditor.ContentStream/Fonts/{FontCodebook,TextLineWriter}.cs`(PDFsharp만, PDFium 참조 없음 — 시험 `ContentStream_DoesNotReferencePdfium`이 확인), `src/PdfEditor.TextEdit/Paragraphs/{TextMeasure,ParagraphLayout}.cs`. 시험 `tests/PdfEditor.ContentStream.Tests/Fonts/*`(코드북 10·쓰기 7), `tests/PdfEditor.TextEdit.Tests/Paragraphs/{CodebookWidthTests,ParagraphLayoutTests,ParagraphLayoutRealFontTests}.cs`(폭 1·다시 맞추기 28·실제 글꼴 2).

### Task 3 — 코드북·쓰기
- 코드북 대상(C8 그대로): ToUnicode + 폭표가 있는 임베드 글꼴. 제외 사유는 `TryBuild(..., out reason)`에 코드로 남는다: `type3`, `mixed-code-space`(Identity-H 아닌 Type0), `not-embedded`(V18에서 폭표가 렌더와 안 맞은 비임베드 글꼴 제외), `no-widths`(표준 14), `no-tounicode`, `learn-conflict`, `width-missing`(폭표 밖 코드가 보였는데 MissingWidth도 없음), `no-usable-chars`.
- C8 확장(ToUnicode 없으면 PDFium 글과 짝지어 배우기)은 `learnFrom` 인자로 구현. 코드 수 = 글자 수(공백 포함 1:1)이거나 공백을 뺀 글자 수 = 코드 수일 때만 배우고, 같은 코드가 다른 글자로 풀리면 그 글꼴 제외. **한계: 배운 코드북에는 공백 글자가 없다**(공백 코드를 알 수 없음) → 공백이 든 글은 그 글꼴로 못 쓰고 앱 글꼴로 간다. ToUnicode가 있는 글꼴은 공백이 문단에 한 번이라도 보였을 때만 공백을 쓸 수 있다(낱말마다 글 객체인 양쪽 맞춤 문서는 공백이 안 보여 앱 글꼴로 간다 — 아래 한계 1).
- 코드북에는 **이 문단에서 실제로 보인 코드의 글자만** 들어간다(`Codebook_OnlyCharsPresentInParagraph`).
- `TextLineWriter.Write`: `q [색] [w] Tc Tz Ts Tr BT /F 크기 Tf a b c d e f Tm <hex> Tj|[…] TJ ET Q`. 양쪽 맞춤은 공백 글자 뒤(이을 글이 있을 때만)에 `TJ` 숫자(= −이동량×1000 / (크기×Tz/100))로. Tw는 쓰지 않음(C15). 결과를 `ContentLexer`로 되읽어 연산자·피연산자·q/BT 균형 확인. 행렬은 `ContentStream.Mat`(PdfMatrix 대신 — PDFium 의존 방지).
- 계획 인터페이스와 달라진 점: `EncodedText`에 `Text`를 추가(TJ 분할용), `TextLineStyle`은 색을 연산자 문자열(`FillOps`/`StrokeOps`)로 받음(원본 색 공간 그대로 쓰기 위해), `ITextMeasure.CanRender`의 `missing`은 코드포인트.
- **폭 정확도(`WidthMatchesPdfium_Within1Percent`, 합성 샘플 코드북 598개 글 객체):** 코드북 폭 ↔ PDFium 객체 폭(잉크)이 "1 % 또는 1 pt + 글자 여백 0.15 em" 안인 것이 **582/598 (97.3 %)**, 초과 16개. 실제 PDF에서의 폭 정확도는 Task 1 V18(위 표)이 근거이며 이번에 새로 재지는 않았다.

### Task 4 — 다시 맞추기
- 알고리즘: 편집 창 글을 원래 문단 글(`OriginalText`: 줄 사이는 공백, 글자 단위 줄바꿈 문서의 한글끼리만 붙임)과 비교해 공통 앞·뒷부분을 찾고, **바뀐 곳이 든 줄부터** 탐욕으로 줄바꿈 → 새 줄 끝이 원래 줄 끝(글자 수 변화만큼 이동)과 같아지는 첫 곳에서 멈추고 나머지는 원본 줄. 줄 맨 앞이 바뀌었을 때만 앞 줄이 첫 낱말을 받을 수 있으면 앞 줄부터 다시 맞춘다.
- **동기화 조건에 "줄 번호가 같을 것"을 더했다(계획에 없던 보강):** 줄 수가 달라지면 뒤쪽 원본 줄이 한 줄씩 밀려야 하므로 원본 그대로 둘 수 없어, 동기화 없이 끝까지 다시 쓴다. 줄 수가 같게 돌아오는 경우(대부분의 낱말 고치기)에만 뒤 줄이 원본 그대로 남는다. 줄이 줄면 남는 원본 줄은 지워진다(`OriginalLineCount`와 `Lines.Length` 차이 — Task 5가 끄기 대상에 넣어야 함).
- **열 폭 = 상자(글자 잉크) 폭과 같은 측정으로 잰 원래 줄 최대 폭 중 큰 값.** 처음엔 잉크 폭 + 0.1 em 여유로 했더니 실제 한글 글꼴에서 줄 이동량이 잉크보다 1~2 pt 커서(글자 여백) 원래와 같은 줄도 안 들어가 한 줄을 고치면 뒤 줄까지 다시 쓰였다(실제 글꼴 시험 2/4 → 수정 뒤 4/4). 여유는 0.02 em(부동소수 오차용)으로 줄임.
- 2패스(C10): 다시 쓰는 줄 전체를 원래 글꼴로 배치해 보고 한 글자라도 못 그리면(코드북에 없음) 앱 글꼴 폭으로 **다시 줄바꿈**(원본 줄은 그대로). 둘 다 못 그리는 글자는 `MissingCodepoints`로. 시그니처가 계획(`Layout(p, text, measure, opt)`)과 달리 `Layout(p, text, original, app, opt)`(2패스에 두 측정이 필요).
- C6: 겹침은 `Overlap` 표시만(막지 않음), 쪽 경계는 `LayoutOptions.PageBounds`로 `MaxLines`를 계산해 `Overflow`만 표시(자르지 않음). 쪽 경계 없으면 `MaxLines = int.MaxValue`.
- C16: 어절 단위(공백 우선, 공백 없는 긴 낱말만 글자 사이) / 글자 단위(한글 사이 어디서나) 두 방식은 `LayoutOptions.Korean`으로 받는다 — **어느 쪽인지 문서·열 단위로 판정하는 코드는 이 Task 범위 밖**(V19 판정 규칙을 부르는 쪽(Task 5)이 적용). 금칙: 줄 머리 `.,;:!?)]}」』”’%·、。，．`, 줄 끝 `([{「『“‘`. 문단 안 `\n`은 강제 줄바꿈(그 줄은 마지막 줄 취급 — 양쪽 맞춤에서 늘리지 않음). 끝의 공백·빈 줄은 무시. 글이 비면 `DeleteAll`.
- C15: 양쪽 맞춤은 마지막 줄·강제 줄바꿈 줄 제외, 공백이 있는 줄마다 `JustifyExtra`(= 남는 폭 / 공백 수, 글 공간 단위) — `TextLineWriter`의 `justifyExtraAtSpaces`에 그대로 넣는다. 공백 없는 줄(글자 단위 한글 등)은 늘리지 않는다.

### 수치
- 합성 가짜 측정(글자당 0.5 em) 시험 28개: 무편집 0기록, 같은 길이 낱말 고치기는 그 줄만, 길어지면 바뀐 줄부터 다시 줄이 같아지면 원본, 줄어들면 줄 수 감소, 한글 어절/글자 단위, 금칙, 긴 낱말, 양쪽·가운데·오른쪽, 첫 줄 들여쓰기, 강제 줄바꿈, 전체 삭제, 겹침, 쪽 아래, 90° 회전, 2패스 대체 글꼴, 양쪽 글꼴 모두 불가 — 모두 통과.
- 실제 글꼴 폭(코드북, 합성 샘플 `doc-en-left`·`doc-ko-left-wordwrap`의 3줄 이상 문단): 같은 길이 낱말(글자 순서만 뒤집음)로 한 줄을 고쳤을 때 **그 줄만 다시 쓰임 8/8**(영문 4/4, 한글 4/4), 무편집은 모두 `NoChange`. 표본이 작다(검증 필요).

### 알려진 한계·검증 필요 (Task 5 이후에 영향)
1. **낱말마다 글 객체로 쪼개진 문서(양쪽 맞춤 등)는 공백 글 코드가 문단에 안 보여 원래 글꼴로 공백을 쓸 수 없다** → 그 문서의 다시 쓰는 줄은 앱 글꼴로 간다(U3 알림 대상). 공백 코드를 PDF 글꼴 사전에서 직접 찾는 방법(예: 1바이트 글꼴의 코드 32가 공백 글리프인지 확인)은 아직 없다 — 필요하면 Task 5에서 결정.
2. 폭표가 맞아도 글자 쌍 조정(커닝)은 PDF 글꼴 폭표에 없으므로 앱 글꼴(HarfBuzz) 폭과 원래 글꼴 폭의 정의가 다르다. 한 문단 안에서 원본 줄과 대체 글꼴 줄의 줄 끝이 조금 어긋날 수 있다.
3. 줄 수가 바뀌면 뒤쪽 원본 줄까지 모두 다시 쓴다(위 동기화 조건). 긴 문단 중간에 줄이 하나 늘면 아래 줄이 모두 새 줄(대체 글꼴이 섞일 수 있음)이 된다 — 원본 줄을 "위치만 옮겨" 보존하는 방안은 Task 5에서 `TransformEdit`와 함께 검토할 수 있다.
4. 한글 줄 이음 방식(어절/글자)의 문서별 판정, 겹쳐 찍은 글의 `Duplicates` 처리, 코드북 생성(원본 글 객체의 문자열 바이트 수집)은 `ParagraphReader`/`ParagraphEditPlanner`(Task 5) 몫이다.
5. 세로쓰기·임의 각도는 대상 아님(C18).

## Task 5 — 편집 모델·replay·ParagraphReader·ParagraphEditPlanner (2026-10-22, Sonnet)
구현: `ContentEdit.cs`(`ReplaceParagraphEdit`·`ParagraphTextLine`·`ParagraphMeta`), `ContentEditStore.cs`(`ParagraphEditCommand`, 기록 중간 빼기/넣기), `ContentReplayer.cs`(문단 편집 Screen/Output), `OutputCopyBuilder.cs`(저장 거절 가드), `PdfEditor.TextEdit/Paragraphs/{ParagraphReader,ParagraphEditPlanner}.cs`, `OriginalFontReplacer.cs`(허용 글자 집합 오버로드), `PdfEditor.ContentStream/Fonts/PageTextFonts.cs`(글 객체 번호 → 글꼴 사전·보인 문자열·Tc/Tz). 시험: `tests/PdfEditor.Document.Tests/Content/ParagraphReplayTests.cs` 11개, `tests/PdfEditor.TextEdit.Tests/Paragraphs/ParagraphPlannerTests.cs` 17개(Theory 포함). 전체 `dotnet test PdfEditor.sln` 통과(1390개, 실패 0).

### 계획과 달라진 점
1. **`ParagraphTextLine`에서 `FontChoice Font`를 뺐다**(Document는 TextEdit를 참조하지 못함). 원래 글꼴 줄은 `OriginalFontLines`, 앱 글꼴 줄은 `AppLines`로 이미 나뉘므로 정보는 같다. 대신 줄을 가리킬 `LineId`를 추가했고, 앱 글꼴 줄은 `AppLineIds`(AppLines와 같은 순서)를 둔다 — 줄마다 `ObjectRef.Inserted(id)`로 선택·옮기기·끄기를 할 수 있게 하려는 것(앱 글꼴 줄은 기존 `InsertAppTextEdit`처럼 화면 replay에서 맨 위로 미뤄 넣고, 그 줄을 가리키는 편집도 함께 미룬다).
2. `ParagraphMeta.Model`은 `object?`(TextEdit의 `ParagraphModel`을 담는다). `PlanReEdit`이 이것으로 원본 재추정 없이 다시 편집한다(C14).
3. **다시 편집 = `ParagraphEditCommand(page, previous, next)`**: 이전 편집을 기록의 같은 자리에서 새 편집으로 바꾼다(쌓이지 않음). `next=null`이면 이전 편집을 뺀다(원래 글로 되돌림), 실행 취소는 자리까지 복원. 기존 `ContentEditCommand`는 그대로.
4. `ParagraphEditPlanner.Plan`의 `AppFontCatalog`(Task 7)는 아직 없어 M4 `TextEditPlanner`처럼 `AppFont?`를 받는다. 한글 줄 이음 방식은 호출자가 `KoreanLineBreak`으로 넘기고, 판정 함수 `DecideKorean`(V19 규칙: 비마지막 줄 6개 미만이면 어절 단위, 여유 > 1.1 em 줄 비율 ≤ 15 %면 글자 단위)을 제공한다.
5. **글 코드북 재료는 `IParagraphFontSource`로 분리**: 기본 구현 `SessionFileFontSource`는 세션의 원본 파일을 PDFsharp로 열어(`PdfDocumentOpenMode.Import`) `PageTextFonts`로 객체 번호를 대응하고(V16: 객체 종류 열이 PDFium과 같을 때만), 문단의 모든 글 객체가 같은 글꼴 사전을 쓸 때만 `FontCodebook`을 만든다. 코드북으로 푼 글이 PDFium이 읽은 글과 다르면(쪽·글꼴 어긋남) 쓰지 않는다. 못 만들면 앱 글꼴 경로.
6. **V14 규칙을 줄마다 쓰되 ④(원래 줄에 있던 글자만)를 문단 단위로 넓혔다**: `OriginalFontReplacer.Verify(…, allowedChars)` — 허용 글자 = 코드북이 아는 글자(문단에서 실제 보인 글). 글 객체 하나가 아니라 문단의 모든 줄·낱말 객체의 글자를 허용한다. 같은 글꼴 사전의 서브셋은 그 글자들을 가지므로 안전하다고 보지만, 같은 이름의 다른 사전이 섞이면 어긋날 수 있다(②되읽기·③글자 칸 잉크가 마지막 보호) — **검증 필요**.

### 동작 요약
- `ParagraphReader.Read`(PDFium 스레드): 시작 줄과 같은 줄대의 가로 범위와 겹치는 글 객체를 세로 거리순으로 최대 `MaxObjects`(240)개(마지막 줄은 끝까지)·세로 `MaxLines`(60) × 1.5 글자 크기 창 안에서 `TextRun`으로 읽는다. 쪽 전체를 읽지 않는다(후보 선택은 이미 있는 `PageObjectIndex` 경계 목록에서). 장애물(선·칠한 가는 막대·그림)은 후보 창 안에서만. 폼 객체는 `InForm`(V15), 지워진·글 아닌 객체는 `NotText`, 직각이 아닌 각도는 `UnsupportedAngle`, 보이지 않는 글은 `Hidden`. **겹쳐 찍은 글(굵게 흉내)**: PDFium이 두 번째 객체의 글을 비우므로(Task 2 발견 1) 같은 자리·크기의 대표 글과 짝지어 같은 글로 넘기고, 줄 조립이 한 줄로 합쳐 `Duplicates`에 보존 → 끄기 대상에 복사본도 들어간다(시험으로 확인).
- `ParagraphEditPlanner.Plan`: 암호 문서 → `Refused(Encrypted)`(한 줄 편집은 그대로 됨, 시험으로 확인). 레이아웃(Task 4) → 바뀐 것 없음이면 편집 없음(이전 편집이 있으면 되돌림). 원래 글꼴로 정해졌으면 다시 쓰는 줄마다 V14를 통과해야 하고, 하나라도 못 하면 다시 쓰는 줄 전체를 앱 글꼴로 다시 배치(C10 2패스). 둘 다 못 그리는 글자는 `MissingGlyphs`, 쪽 아래를 넘으면 `Overflow`(겹침은 막지 않고 `Overlap` 표시). 끄기 대상 = 다시 쓰는 줄이 대체하는 원본 줄(동기화되면 first..last, 아니면 first..끝)의 글 객체 + 겹친 복사본. 새 줄의 위치 행렬 = 원본 글 객체 행렬에서 이동량만 줄의 기준선 원점으로 바꾼 것.
- **화면 replay**: Remove를 끄고 원래 글꼴 줄은 `CreateTextLike` + `Matrix = Placement`로 줄의 원본 글 객체 바로 뒤에 넣는다(그리기 순서 유지). 앱 글꼴 줄은 윤곽선 경로로 맨 위에. **Output replay는 끄기만** 한다.

### 안전장치 (Task 6 전까지)
- 문단 편집은 **아직 파일로 저장되지 않는다**: `OutputCopyBuilder.BuildAsync`가 `ReplaceParagraphEdit`가 있으면 `NotSupportedException`으로 저장·복구·인쇄를 거절한다(새 줄이 조용히 사라지는 것을 막음, 시험 `OutputBuilder_RefusesParagraphEditsUntilTask6`). 화면에는 아직 이 편집을 만드는 길이 없다(Task 8). Task 6이 이 가드를 없앤다.

### 알려진 한계·검증 필요 (Task 6 이후에 영향)
1. **화면의 원래 글꼴 줄은 PDFium 글 객체라 자간(Tc)·양쪽 맞춤 늘임(JustifyExtra)·가로 비율(Tz)을 그리지 못한다**(PDFium에 쓰기 API 없음). 파일은 Task 6이 `TextLineWriter`로 정확히 쓰므로 양쪽 맞춤 문단은 화면과 저장 모양이 조금 다를 수 있다 — 저장 뒤 비교 보호(C9)에서 확인할 것. 앱 글꼴 줄도 `AppFontLine`이 자간·늘임·가로 비율을 지원하지 않아 같은 한계(왼쪽 맞춤처럼 그려짐, 늘임은 무시) — Task 7이 `AppFontLine` 매개변수를 넓힐 때 함께 볼 것.
2. 클릭 경로(`Read`)는 Tc=0·Tz=100으로 읽는다(PDFium 글 객체에서 읽을 수 없음). `SessionFileFontSource`가 내용 스트림의 값을 얻으면 `Plan`이 모델의 폭 재료를 바로잡는다(Tz ≠ 100인 합성 시험은 아직 없음 — 검증 필요).
3. `SessionFileFontSource`는 세션 파일이 있는 문서에서만 코드북을 만든다(새 문서·암호 문서는 앱 글꼴). 쪽을 옮기거나 끼워 넣어 파일과 쪽 번호가 어긋나면 객체 종류 열 비교에서 거절되지만 같은 종류 열이면 잘못 대응할 수 있다 → 코드북 되읽기 확인(글 일치)이 마지막 보호이며 쪽 순서를 바꾼 문서에서의 시험은 없다(검증 필요). 문서를 열고 닫는 비용(PDFsharp Import)이 `Plan` 호출마다 든다 — Task 8이 쪽 버전마다 캐시해야 한다(A1에서 쪽 해석 약 0.4 s).
4. 후보 상한: 객체 240개(줄 수 약 24~60). 낱말마다 글 객체로 쪼개진 긴 문단(줄당 12낱말 × 20줄 ≈ 240)은 끝줄이 잘릴 수 있다 — 잘리면 문단이 짧게 추정된다(`줄 포함(+)` 탈출구, C13). 실제 문서에서의 영향은 측정하지 못했다.
5. 후보 창 안 장애물 읽기 상한 2,000개. 페이지 경계(`PageBounds`)는 `(0,0,쪽 크기)`로 가정한다(쪽 /CropBox가 원점이 아니거나 /Rotate가 있는 쪽에서 `Overflow` 판정이 어긋날 수 있음 — 회전 쪽은 가로·세로를 바꿔 계산, 검증 필요).
6. 쪽 아래 겹침(`Overlap`)·줄 이음(`DecideKorean`)·`Plan`의 V14 줄별 확인 비용(줄마다 쪽을 한 번 더 열어 타일 렌더)은 큰 문단에서 느릴 수 있다 — Task 10에서 측정.
7. 시험 표본: 합성 샘플 `doc-en-left`(영문 TrueType Identity-H)·`doc-ko-left-wordwrap`(한글)에서 원래 글꼴 경로를 확인했다. 실제 PDF와 도면(ToUnicode 없는 CFF를 배워서 쓰는 경로)에서의 계획은 아직 시험하지 않았다(검증 필요).

## Task 6 — 저장: 직접 인코딩 덧붙임·지운 글 제거·경로 선택 (2026-10-22, Sonnet)
구현: `ContentStream/ContentRewriter.cs`(글 완전 제거), `ContentStream/Fonts/TextLineWriter.cs`(코드북 없이 쓰는 오버로드), `Appearance/Patching/{ContentRewritePlanner,PdfSharpAnnotationPatcher}.cs`(문단 편집 접기·원래 글꼴 줄 직접 쓰기·시험 적용), `Document/Output/OutputCopyBuilder.cs`(C7 경로 선택·충실도 보호 마스크), `Document/Content/{ContentEdit,ContentReplayer}.cs`, `Document/Annotations/Patching/{IAnnotationPatcher,PatchPlanBuilder}.cs`, `TextEdit/Paragraphs/ParagraphEditPlanner.cs`. 시험: `tests/PdfEditor.ContentStream.Tests/ParagraphRemovalTests.cs` 7개, `tests/PdfEditor.TextEdit.Tests/Paragraphs/ParagraphSaveTests.cs` 13개(계획서의 `tests/PdfEditor.Document.Tests/Output/`가 아니라 TextEdit 시험 프로젝트에 둠 — 문단 계획기가 필요해서). 전체 `dotnet test PdfEditor.sln` 통과(실패 0; ContentStream 72·Document 262·TextEdit 150·App 527 …). M4/M4x 저장 시험 변화 없음.

### 동작
- **C12 지운 글 완전 제거:** `DisableObject(index, RemoveText: true)`. 지울 글 뒤의 첫 글 보이기가 `Tm/Td/TD/T*/'/"`로 자리를 새로 잡거나 `ET/BT`·끝이 먼저 오면 연산자째 삭제(`'`는 `T*`, `"`는 `aw Tw ac Tc T*`를 남김). `Tj/TJ`가 이어 쓰면 그 글도 함께 지워지고 안전할 때만 삭제(연쇄 판정), 아니면 지금의 `3 Tr` 숨김. `RewriteResult.DeletedTextCount/HiddenTextCount` → `PdfSharpAnnotationPatcher.HiddenTextCount` → `OutputCopy.HiddenTextCount`(화면 알림 문구는 Task 8). **문단 편집이 끄는 원본에만 켠다** — M4x의 일반 글 지우기(`RemoveText` 꺼짐)는 이전과 같이 숨김이라 M4/M4x 저장 바이트가 달라지지 않는다(원하면 같은 플래그를 켜는 한 줄 변경 — 사용자 결정 거리).
- **원래 글꼴 줄 덧붙임:** 계획 때 코드북으로 인코딩한 글 코드(`ParagraphTextLine.Encoded`, `TwoByte`)를 저장이 그대로 쓴다(PDFium·ToUnicode 학습 불필요). 저장 단계(`ParagraphLineBytes`)는 원본 글 객체에서 글꼴 자원 **이름**(이미 쪽 `/Resources /Font`에 있는 것)·`Tf` 크기·`Tm·CTM`의 기울기/크기·색/굵기/투명도 상태 연산자(`ReplayOps`)를 읽고, 위치만 계획한 기준선 원점으로 바꿔 `TextLineWriter`로 쓴다 → **새 글꼴 객체 없음**(시험 `OriginalFontLine_ReusesExistingFontDictionary`: 글꼴 객체 수·쪽 글꼴 이름 동일).
- **앱 글꼴 줄:** 쪽의 앱 글꼴 줄(문단 여러 줄 포함)이 서브셋 **하나**를 나눠 쓴다(글자 집합 합침, `MergeFontUse`). M4 PDFium 경로의 `AppendPageContents`도 같은 방식으로 바뀌어 줄마다 글꼴 객체를 만들던 것이 쪽당 하나가 됨(바이트는 줄어든다).
- **C7 경로 선택(`OutputCopyBuilder.RegenerateContent`):** 문단 편집이 있는 쪽 → ① 사본에서 PDFium 객체 목록을 읽고 패처의 `IContentRewriteProbe.CheckContentRewrites`(파일은 안 바꾸는 시험 적용: 번호 대응·글꼴 자원·상태 해석까지)로 확인 → 되면 내용 스트림 직접 쓰기 ② 거절된 쪽만 PDFium 경로(`ReplayMode.OutputPdfium`: 끄기 + 원래 글꼴 줄을 PDFium 글 객체로 + 기존 충실도 보호/색 정규화 재시도) ③ PDFium 경로도 모양이 달라지면 `ContentRegenerationException`(쪽 번호 + "paragraph edit: direct text writing was rejected (이유) and the PDFium path changed the look")로 저장 거절. 암호 문서는 문단 편집 자체가 계획 단계에서 거절(`Refused(Encrypted)`). 문단 편집이 없으면 새 코드는 실행되지 않는다(시험 `NoParagraphEdits_NoCostAndBytesIdentical`). 이전의 "문단 편집은 저장 거절" 가드와 그 시험(`OutputBuilder_RefusesParagraphEditsUntilTask6`)은 없앴다.
- **충실도 보호:** 직접 쓴 쪽도 기존대로 "화면 replay 모양(메모리) ≡ 저장 파일을 다시 연 모양"을 비교한다. 단 화면의 PDFium 글 객체는 자간(Tc)·가로 비율(Tz)·양쪽 맞춤 늘임을 못 그리므로(Task 5 한계 1) 그런 줄 주변 띠(줄 높이 3 em × 문단 상자 폭 양쪽)는 비교에서 뺀다(`MaskInfo`, 쪽 회전 반영). 그 띠 밖은 그대로 엄격하다.

### 수치·확인한 것
- 합성 `doc-en-left`: 한 낱말(같은 길이)을 고쳐 저장 → 그 줄만 바뀌고 문단 상자 밖 0 px 차이(`ParagraphPage_ContentRewritePath_UntouchedRegion_ZeroPixelDiff`), 원본 내용 바이트가 그대로 포함됨(PDFium이 쪽을 다시 쓰지 않았다는 증거), 저장 파일에서 옛 줄 글 추출 불가·새 줄 추출 가능, `3 Tr` 없음.
- 셰이딩(`sh`)·패턴(채우기+선) 쪽(M4x 알려진 한계, 400×400 Helvetica 5줄 문단): 문단 편집 저장 성공, 상자 밖 0 px(셰이딩 포함). 객체 번호 대응 거절을 흉내 내면(`RejectingProbe`) PDFium 경로를 시도하고 모양이 달라져 저장 거절 + 이유 노출(`BothPathsRejected_SaveRefused_WithReason`).
- 3회 저장 내용 동일, 편집→저장→실행 취소→저장 내용이 편집 없는 저장과 같음, 저장 뒤 재렌더 ≡ 화면 렌더(0 px).

### 알려진 한계·검증 필요
1. **글 순서:** 직접 쓴 새 줄은 쪽 맨 위(마지막)에 그려진다. 화면 replay는 원본 줄 바로 뒤에 넣으므로, 문단 위로 다른 도형이 덮고 있던 드문 쪽에서 겹침 순서가 다를 수 있다(보호 비교가 잡으면 저장 거절).
2. **Tw(낱말 간격)·Ts(글 올림):** 새 줄은 `Tw 0`·`Ts 0`으로 쓴다. 원본 줄이 Tw/Ts를 쓰던 문서에서 줄 폭이 조금 다르거나 올림이 빠질 수 있다 — 실제 PDF에서 확인 못 함.
3. **색:** 원본의 색 상태 연산자를 그대로 재생하므로 CMYK/그레이 글도 같은 색 공간으로 나가지만 `Tr` 1·2(선 있는 글)의 선 굵기는 상태에 있던 `w`에 의존한다. 글 클립(Tr ≥ 4)·보이지 않는 글(Tr 3)은 이 방식을 안 쓴다(PDFium 경로).
4. 저장 단계 시험 적용은 `PdfReader.Open(Modify)`를 한 번 더 한다(문단 편집이 있는 쪽이 있을 때만; 큰 파일에서 비용은 측정 못 함 — Task 10).
5. 양쪽 맞춤 문단의 직접 쓰기(`TJ` 숫자 조정)는 합성 `doc-en-justified`로 저장이 성공함만 확인했다. 다른 뷰어(Acrobat·Chrome·Foxit·PDF-XChange)에서의 `TJ` 모양은 사용자 확인 필요(`spikes/out/m5-v20-*.pdf`와 같은 검증).
6. PDFium 바닥 경로(`OutputPdfium`)는 자간·양쪽 맞춤 늘임·Tz를 쓰지 못한다(PDFium에 쓰기 API 없음) — 이 경로로 내려간 양쪽 맞춤 문단은 왼쪽 맞춤처럼 저장된다. 실제로 내려가는 비율은 실제 PDF로 못 재었다.
7. `HiddenTextCount` 알림 문구와 표시는 화면 Task(8)가 한다. 현재는 `OutputCopy.HiddenTextCount`로만 노출.
8. 문단 안 원래 글꼴 줄 + 앱 글꼴 줄이 한 쪽에 섞이면 앱 글꼴 줄은 맨 뒤에 그려진다(M4 규칙과 같음).

## Task 7 — 대체 글꼴 선택(고딕/명조, 가짜 굵게·기울임, fsType) (2026-10-27, Sonnet)

### 한 일
- `AppFontCatalog`(신규, `src/PdfEditor.Appearance/Fonts/`): `Classify(PdfFontInfo)`·`Choose(PdfFontInfo) → AppFontChoice(Family, Font, SyntheticBold, SyntheticItalic, FellBack)`, `FsTypeAllowsEmbedding(byte[])`, `ReadFsType`. `PdfFontInfo.FromFontDictionary`는 글꼴 사전(Type0이면 DescendantFonts)에서 `/Flags`·`/FontWeight`·`/ItalicAngle`·이름을 읽는다.
  - 명조 조건: 설명자 `Flags`의 Serif 비트(0x2) **또는** 이름에 serif/times/batang/myeongjo/mincho/garamond/georgia/palatino/bookman/century/gungsuh/바탕/명조/궁서 등(이름이 gothic/sans/dotum/굴림/arial 등이면 이름 규칙은 안 씀, 플래그가 있으면 항상 명조). `ming`·`song` 같은 짧은 조각은 오탐(Flamingo 등) 때문에 뺐다.
  - 굵게: 이름(Bold/Black/Heavy/굵…)·`FontWeight ≥ 600`·`ForceBold`. 기울임: 이름(Italic/Oblique)·Italic 비트·`ItalicAngle ≠ 0`.
  - 명조 파일이 없으면 고딕으로 물러나고 `FellBack = true`(알림용). 조용히 다른 글꼴로 저장하지는 않는다 — 저장 단계는 명조가 필요한데 공급자가 없으면 예외.
- `AppFontLine.Build(AppFontChoice, …)`(오버로드 추가, 기존 시그니처 유지): 가짜 굵게 = `r g b RG`, `w`(글자 크기 × 3 %), `2 Tr`; 가짜 기울임 = `1 0 0.21 1 0 0 cm`(기준선 기준, 약 12°), 화면 윤곽선도 같은 식(x += 0.21·y). 결과 `AppTextSpec`에 `BoldStrokePt`(화면 그리기용 선 굵기) 추가, `FontUse.Family`에 글꼴 가족을 실어 보낸다.
- 저장 경로: `FontUse`에 `Family`(""=고딕, "serif"=명조) 추가 → `PdfSharpAnnotationPatcher`가 쪽마다 **가족별로 서브셋 하나**(`SedolF1`, `SedolF2`)를 넣고(`EmbedFamilies`), `FontEmbedder`가 `XXXXXX+NotoSerifKR-Regular`·Flags 6(Serif)로 쓴다. 명조 공급자는 `PdfSharpAnnotationPatcher.SerifFont`(init 속성). 고딕만 쓰는 기존 경로는 결과가 같다(기존 시험 통과).
- 번들: `assets/fonts/NotoSerifKR-Regular.otf` + `AppFontFile.SerifPathIn/ReadSerifOrEmpty`, `PdfEditor.App.csproj`가 출력 폴더로 복사, `build/package.ps1` 필수 파일 목록에 추가, `THIRD-PARTY-NOTICES.md`에 Noto Serif KR 절.

### 다운로드 기록 (2026-10-27)
| 항목 | 값 |
|---|---|
| 출처 | https://github.com/notofonts/noto-cjk — `Serif/SubsetOTF/KR/NotoSerifKR-Regular.otf` (공식 noto-cjk 저장소 main) |
| 크기 | 7,669,020 바이트 (7.3 MiB) |
| SHA-256 | 5ea012e15cb7eacc1f680aee1703f3b164791b1443ea3e52b65080cca5d179cf |
| 라이선스 | SIL OFL 1.1. 같은 저장소 `Serif/LICENSE`(4,301 B)가 `assets/fonts/OFL.txt`와 바이트 단위로 같아 새 라이선스 파일은 만들지 않고 OFL.txt를 함께 쓴다 |
| fsType | 0 (제한 없는 포함) — 시험이 두 번들 글꼴 모두 확인 |
| 같이 받은 것(버림) | `Serif/OTF/Korean/NotoSerifCJKkr-Regular.otf` 24.5 MB — 너무 커서 쓰지 않고 지움 |

### 크기 실측
- **포터블 zip 영향:** 명조 파일 단독 압축 5.91 MB(고딕 3.75 MB). 따라서 **포터블 zip은 +약 5.9 MB**. 지금 `artifacts/`의 0.2.0 zip이 이미 57.9 MB(50 MB 넘음 — M3a부터 54.7 MB)이므로 새 zip은 약 **63.8 MB**로 예상(패키지를 새로 만들어 재지는 않음). 사용자에게 알림 대상.
- **서브셋 후 글꼴 추가 크기** (한글 문단 약 4문장, 서로 다른 글자 117개): 고딕 18.5 KB, 명조 40.4 KB (한도 300 KB 대비 매우 작음).

### 시험
- `tests/PdfEditor.Appearance.Tests/Fonts/FontCatalogTests.cs` 41개 중 신규: Serif 플래그/이름 규칙, 고딕 기본(오탐 방지 포함), 굵게·기울임 판정, 명조 없을 때 물러남, fsType 값표(0·0x8 허용 / 0x2·0x4·0x100·0x200·모순 조합·OS/2 없음 거부), 번들 두 글꼴 fsType = 0, `BundledFontOnlyOfl`·`PackageContainsLicenses`(csproj·package.ps1·NOTICES 검사), `SubsetSizeReasonable_KoreanParagraph`, 명조 서브셋 이름.
- `tests/PdfEditor.TextEdit.Tests/FontChoiceTests.cs` 7개: 굵게(`2 Tr`, 3 % 선, 선 색 = 글자 색), 보통 글은 아무 것도 안 붙음, 기울임 윤곽선 변위 = 0.21·y, 명조 줄의 가족·윤곽선, 명조+고딕 한 쪽에 저장(두 글꼴 객체 각각 1개, 글 추출 가능), 명조 없이 명조 저장 시 예외, 굵은 기울임 명조 저장.
- 계획서 시험 이름 중 `Italic_UsesSkew`·`BoldName_UsesSyntheticBold_RenderMode2`·`FsTypeRestricted_NotEmbedded`·`BundledFontOnlyOfl`·`SubsetSizeReasonable_KoreanParagraph`·`PackageContainsLicenses`·`SerifFlag_ChoosesMyeongjo`·`NameContainsBatang_ChoosesMyeongjo`·`DefaultSans`는 그대로 쓴다(`PackageContainsLicenses`는 package.ps1 **실행**이 아니라 스크립트·csproj 내용 검사 — 실제 패키지 빌드는 하지 않음).

### 알려진 한계·검증 필요·다음 Task에 넘기는 것
1. **`ParagraphEditPlanner`는 아직 고딕(`AppFont` 하나)만 쓴다.** Task 7 범위(계획서 Files)에는 플래너가 없다. 카탈로그를 쓰려면 원본 글꼴 사전을 플래너가 `PdfFontInfo.FromFontDictionary`로 읽어 `Choose` → `AppFontLine.Build(choice, …)`를 부르게 하고, 앱 시작 때 `AppFontCatalog.ForBaseDirectory(sans, baseDir)`를 만들어 패치어의 `SerifFont`에 `() => catalog.Serif`를 연결해야 한다(App 조립부). **Task 8(화면)에서 같이 연결할지 Opus 판단 필요.** 연결 전에는 명조가 쓰이는 일이 없어 동작 변화 없음.
2. **가짜 굵게의 화면 모양:** 파일은 렌더 모드 2 + 3 % 선이지만, 화면은 지금 윤곽선 채우기만 한다. `AppTextSpec.BoldStrokePt`를 화면 그리기가 선으로도 긋도록 해야 화면 ≡ 파일(설계서 4.2-3). 연결 전에는 굵게가 화면에서 조금 가늘게 보인다(**검증 필요**, 화면 Task).
3. 가짜 굵게·기울임은 어디까지나 흉내다(실제 Bold/Italic 글리프와 모양이 다르다). 다른 뷰어(Acrobat·Chrome·Foxit·PDF-XChange)에서 렌더 모드 2 굵기는 **미확인**.
4. 기울임은 글자 하나하나가 아니라 줄 전체를 기준선에서 기울인다(글리프 위치는 기울기와 무관하게 같음).
5. 고딕/명조 판정은 이름·플래그만 본다. 원본이 세리프인데 이름에 단서가 없고 Flags도 0이면(드문 PDF) 고딕이 된다. 시스템 글꼴 직접 선택은 범위 밖(U2).
6. 새 NuGet 없음. 명조 파일은 HarfBuzz가 CID-키 CFF로 읽는다(서브셋·CID 유지 시험 통과) — 고딕과 같은 규칙(V11).

## Task 8 — 화면: 문단 상자·여러 줄 편집 창·알림 (2026-10-27, Sonnet)

### 한 일
- **텍스트 편집 도구 확장(C1)**: 줄을 누르면 백그라운드(PDFium 전용 스레드)에서 `ParagraphReader.Detect` → 문단(2줄 이상, 신뢰도 문턱 0.62 이상 — Task 1 확정값이 계획의 0.7을 대체)이면 점선 문단 상자 + 여러 줄 편집 창, 아니면 M4 한 줄 창. 이유(표 안의 글·글꼴 섞임·확신 낮음·암호 문서·읽기 실패)는 토스트로 알리고, 이웃이 없는 외톨이 줄은 평범한 한 줄 편집이라 알리지 않는다(M4 시험 불변). `App/Viewer/Objects/ParagraphController.cs`(계산, Avalonia 없음), `ParagraphEditorPopup.axaml(.cs)`(편집 창), `DocumentView.Paragraph.cs`(입력·그리기·창 수명).
- **편집 창(C17)**: 여러 줄 `TextBox`. Ctrl+Enter·확정 단추·창 밖 클릭 = 확정, Esc·취소 단추 = 취소, Enter = 줄바꿈(강제 줄바꿈). IME 조합 중(`PreeditText`)이거나 `Key.ImeProcessed`이면 모든 키를 조합에 맡긴다(M4 한 줄 창과 같은 판정). 열었다 그냥 닫으면 기록 0(`text == 처음 글 && 폭 안 바꿈`이면 계획도 안 함).
- **입력 중 미리 계획**: 50 ms 디바운스(`Task.Delay` + Dispatcher — `DispatcherTimer`는 헤드리스에서 안 돌아 쓰지 않음) 뒤 PDFium 스레드에서 `ParagraphEditPlanner.Plan`을 돌려(문서는 안 바뀜) "줄 N/최대 M", 쪽 아래 넘침(빨강, 확정 단추 꺼짐), 다른 글·그림과 겹침(주황, 확정 가능), 대체 글꼴 예고("기본 글꼴로 바뀝니다")·고친 줄/안 고친 줄 글꼴 다름 알림을 창 아래 줄에 보인다. 코드북 만들기(원본 파일 PDFsharp 열기)는 `CachedFontSource`가 편집 창이 열려 있는 동안 한 번만 한다(Task 5 한계 3 해소).
- **확정**: 확정 직전에 다시 계획해(미리 계획이 낡았을 수 있음) 거절(넘침·쓸 수 없는 글자)이면 창을 열어 둔 채 이유만 보이고, 통과하면 `ParagraphEditCommand` 하나(실행 취소 한 번). 대체 글꼴로 바뀌면 `Msg_TextFallbackApp` 토스트(+글꼴 다름 알림).
- **상자 폭(C13)**: 편집 창 좌우 가장자리 손잡이(`Thumb`)를 끌었다 놓거나 속성 패널 "상자 폭 (mm)" 칸을 바꾸면 줄바꿈을 다시 맞춘다. 이를 위해 `ParagraphModel.ColumnWidthOverride`(TextEdit, 기본 null)를 추가했다: 있으면 `ParagraphLayout`이 원래 줄 폭을 무시하고 모든 줄을 그 폭으로 다시 맞춘다(최소 변경 동기화 끔, 글이 그대로여도 변경으로 본다). 왼쪽 손잡이는 글이 똑바로 선 쪽(쪽 회전 + 글 각도 0)에서만 시작 위치도 같이 옮기고, 그 밖은 오른쪽 손잡이와 같이 폭만 바꾼다. 폭은 `Meta.Model`에 실려 다시 편집에서도 유지된다.
- **틀린 묶음 탈출구(C13)**: 창 위 "위 줄 포함/제외, 아래 줄 포함/제외" 단추와 Shift+↑/↓(포함)·Alt+Shift+↑/↓(제외). `ParagraphDetector.Extend` 사용. 글을 고치기 전에만 동작(고친 글이 사라지지 않게)하고, 이전에 고친 문단을 다시 열 때는 줄 목록이 없어 숨긴다.
- **다시 편집**: 이미 고친 문단의 새 줄(원래 글꼴 줄·앱 글꼴 줄)이나 남은 원본 줄을 누르면 `ReplaceParagraphEdit.Meta`로 열고(원본 재추정 없음) 확정하면 이전 편집을 대체한다(쌓이지 않음). 앱 글꼴 문단 줄도 텍스트 편집 도구로 고를 수 있게 선택 거르개를 넓혔다.
- **편집 탭 "한 줄만" 토글**: `MainWindowViewModel.OneLineOnly` → 활성 문서 `DocumentTabViewModel.OneLineOnly`. 켜면 문단을 묶지 않고 M4 한 줄 편집(아이콘은 기존 "textedit" 재사용, 툴팁 있음). 암호 문서는 문단 편집을 끄고 한 줄 편집으로 되돌린다(이유 토스트).
- **알림**: 대체 글꼴·고친/안 고친 줄 글꼴 다름·옛 글이 보이지 않는 글자로 남음. 마지막은 저장 때 알 수 있으므로 `SaveService.LastHiddenTextCount`(= `OutputCopy.HiddenTextCount`)를 만들고 `FileWorkflow`가 저장 성공 뒤 정보 대화상자 `Msg_ParagraphHiddenText`를 한 번 보인다.
- **명조/굵게/기울임 연결(Task 7 산출물)**: `ParagraphEditPlanner.Plan`/`PlanReEdit`에 `AppFontCatalog? catalog` 매개변수(끝, 기본 null)를 추가해 원본 글꼴 이름(PDFium `BaseName`)으로 `Choose`한 글꼴(고딕/명조, 가짜 굵게·기울임)로 앱 글꼴 줄을 만든다. 앱 쪽: `AppearanceServices.Catalog`(명조 파일은 필요할 때만 읽음 — 시작 비용 0)와 `PdfSharpAnnotationPatcher.Shared(font, serif)`로 저장 경로에 명조 공급자를 연결했다. **작은 변경이라 이 Task에서 했다.**
- **그 밖**: `TestFixtures`의 글꼴 해석기에 Georgia 4종 추가(명조 원본 시험용).

### 시험 (헤드리스, 합성 입력)
- 신규 `tests/PdfEditor.App.Tests/Viewer/Objects/ParagraphEditInputTests.cs` 21개: 계획서 목록 이름 그대로 — `ClickLine_HighConfidence_ShowsBoxAndEditor`, `ClickTableCell_ShowsSingleLineEditor_WithReason`, `Ime_Composing_EnterEscIgnored`, `CtrlEnter_Commits_EnterInsertsNewline`, `Esc_Cancels_NoEdit`, `Unchanged_Close_NoEdit`, `PageBottomOverflow_CommitBlocked_ShowsCount`, `OverlapOtherObjects_CommitAllowed_BoxTurnsOrange`, `DragBoxEdge_RewrapsOnRelease`, `EmptyText_DeletesParagraph_ConfirmFlow`, `ExtendBox_PlusMinus_ChangesLines`, `OneLineOnlyToggle_UsesM4Editor`, `RotatedPage_EditorOrientation`, `ZoomChange_KeepsBoxAligned`, `UndoRedo_OneStepPerParagraph`, `EncryptedDocument_ParagraphToolFallsBackToSingleLine`, `ClickOnFormObject_Message` + 추가 `OutsideClick_Commits`, `PanelWidth_ChangesBox`, `ReEdit_OpensWithEditedText_NoStacking`, `MainWindow_OneLineOnlyToggle_AppliesToDocuments`.
- `ParagraphLayoutTests` +3(폭 좁힘·넓힘·무변경), `ParagraphPlannerTests` +2(명조 원본 → 명조 서브셋 가족 `serif`, 굵은 고딕 원본 → `2 Tr`).
- M4 한 줄 편집 시험(`TextEditInputTests` 포함 `Viewer.Objects` 41개)은 전부 그대로 통과.
- 전체 `dotnet test PdfEditor.sln` 통과(실패 0): ContentStream 72 · Pages 48 · Pdf.Native 97 · Appearance 210 · Document 262 · Exchange 69 · TextEdit 162 · App 548.

### 알려진 한계·검증 필요
1. **실제 마우스·한글 IME·125/150 % 배율·다른 뷰어는 자동 시험으로 못 잡는다.** 시험은 합성 입력(키 이벤트·`PreeditText` 직접 지정·`Thumb` 끌기 흉내)이다.
2. **편집 칸의 줄바꿈은 Avalonia 기본 글꼴 기준이다.** 실제 PDF 글꼴 폭과 달라 창에서 보이는 줄바꿈이 결과와 다를 수 있다. 결과 줄 수는 창 아래 "줄 N/최대 M"(PDF 폭 기준 계획)가 맞다. 확정 전 화면의 문단 자리에는 원래 글이 그대로 비치고 편집 창이 위에 덮인다(M4 한 줄 창과 같은 방식).
3. 창 밖 클릭은 확정하고 그 클릭은 삼킨다(M4 한 줄 창과 같음, 리본 단추를 눌러도 첫 클릭은 확정에만 쓰임). 확정이 막혀(넘침) 있으면 창이 안 닫힌다. 탭 전환·도구 변경·문서 닫기는 **확정하지 않고 취소**로 닫는다 — 입력하던 글이 사라질 수 있다(검증 필요).
4. 이전에 고친 문단을 다시 열면 위·아래 줄 포함/제외를 쓸 수 없다. 표 안의 글처럼 확신이 낮아 한 줄 편집으로 되돌아간 줄을 억지로 문단으로 묶는 길은 없다(계획서 C4/C13 범위 그대로 — 필요하면 사용자 결정).
5. 폭을 줄이면 원래 줄 수보다 늘 수 있고 바로 아래 문단과 겹쳐도 확정된다(C6, 주황). 겹침 판정은 장애물과 같은 열의 다른 줄 기준이다.
6. **가짜 굵게의 화면 모양**(Task 7 한계 2)은 이 Task에서 고치지 않았다 — 앱 글꼴 굵은 줄은 파일에는 렌더 모드 2 선이 있고 화면에는 선 없이 그려진다.
7. 명조 선택은 PDFium이 주는 글꼴 이름만 본다(`Flags`/`FontWeight`는 쓰지 않음, 이름에 단서가 없는 세리프는 고딕). 코드북 경로(원래 글꼴)는 이전과 같다.
8. 문단 편집 후 저장 때 옛 글이 숨김으로 남은 경우의 알림은 저장 성공 뒤 정보 대화상자로 보인다(인쇄·복구·요약에는 알리지 않음).
9. 5,000줄 도면에서의 클릭→상자 시간, 편집 창을 열어 둔 채의 메모리 등 **성능은 Task 10에서 잰다**(이번에 `--bench` 하지 않음).

### 사용자 검수 요청 — 문단 편집 화면에서 직접 해 볼 순서
최신 빌드: `D:\INBOX_WP\95. Claude\PDF EDITOR\artifacts\m5-task8-build\SedolPDF.exe` (Release, 실행 중인 앱은 건드리지 않고 다른 폴더에 빌드)
1. 글이 여러 줄인 PDF(Word·한글에서 만든 보고서 등)를 열고 **편집 탭 → 텍스트 편집**을 고른 뒤 본문 문단의 한 줄을 누른다. → 파란 점선 상자와 여러 줄 편집 창이 문단 위에 떠야 한다. 창 아래에 "줄 N/최대 M"가 보인다.
2. 낱말 하나를 고쳐 **Ctrl+Enter**. → 그 줄 근처만 바뀌고 나머지 줄은 그대로여야 한다. **Ctrl+Z** 한 번에 문단 전체가 돌아와야 한다. 저장해 Acrobat·Chrome에서도 확인.
3. 글을 아주 길게 붙여 넣는다. → 쪽 아래를 넘으면 빨간 글씨로 안내되고 확정 단추가 꺼져야 한다. 다음 문단과 겹칠 정도면 점선이 **주황색**으로 바뀌지만 확정은 되어야 한다.
4. 편집 창 **좌우 가장자리(연한 파란 띠)를 끌었다 놓는다**. → 놓는 순간 줄바꿈이 다시 맞춰지고 줄 수가 바뀐다. 오른쪽 속성 패널의 "상자 폭 (mm)"에 숫자를 넣어도 같아야 한다.
5. 한글을 입력해 본다(IME). 조합 중에 **Enter·Esc**를 눌러도 창이 닫히거나 확정되지 않아야 한다. 조합이 끝난 뒤 **Esc**는 취소, **Enter**는 줄바꿈.
6. 표나 표제란의 칸을 눌러 본다. → 문단 창이 아니라 한 줄 창이 뜨고 화면 아래에 "표 안의 글처럼 보여 한 줄만 편집합니다"가 잠깐 보여야 한다. 편집 탭의 **"한 줄만"** 단추를 켜면 문단 글에서도 한 줄 창만 떠야 한다.
7. 문단이 틀리게 묶였을 때 창 위의 **위/아래 줄 포함·제외** 단추(Shift+↑/↓, Alt+Shift+↑/↓)로 줄을 더하고 빼 본다(글을 고치기 전에만 눌림).
8. 원래 글꼴에 없는 글자(예: 한글이 없는 영문 서브셋 글꼴 문단에 한글)를 넣고 확정 → "기본 글꼴로 바꿨습니다" 알림. 명조 계열 문서면 명조 모양이어야 한다.
9. 같은 문단을 한 번 더 눌러 다시 고친다. → 고친 글이 열리고, 확정하면 겹쳐 쌓이지 않고 대체되어야 한다.
10. 회전된 쪽, 125·150 % 배율, 암호 PDF(한 줄 편집으로 되돌아가야 함)도 가능하면 확인.

## 사용자 시나리오 자동 시험 (2026-10-27, Sonnet)

사용자가 바빠서 직접 검수하지 못하므로, 안내서(`docs/checklists/m5-user-test-guide.md`) 1~6번을 **실제 샘플 PDF(`spikes/out/m5-user-test/01~06c`)를 열어 헤드리스 합성 입력으로** 재현했다. 시험 파일: `tests/PdfEditor.App.Tests/Viewer/Objects/UserScenarioTests.cs`(18개). 샘플 파일이 없는 PC(`spikes/out`은 저장소에 없음)에서는 조용히 건너뛴다. 합성 입력의 한계(실제 IME·마우스·배율·다른 뷰어)는 아래 "사용자 최소 점검 목록".

### 안내서 항목별 결과
| 안내서 | 자동 시험 | 결과 |
|---|---|---|
| 1 한글 입력 중 Enter·Esc | `S1_Ime_…`: `PreeditText`("다", "라")를 켠 채 Ctrl+Enter·Esc·`ImeProcessed`를 보냄 → 창 열림·편집 0개. 조합이 끝난 뒤 Enter는 줄바꿈(확정 아님), Esc는 취소(기록 0, 더티 아님) | 통과 |
| 2 표 칸 | `S2_Table_*`: 선 있는 표 "배관 배치도"·선 없는 표 "해수 펌프" → 한 줄 창 + 안내(`Para_ReasonTable`), 한 줄 창에서 확정하면 편집이 생김. 보통 문단은 여러 줄 창, "한 줄만"을 켜면 안내 없이 한 줄 창 | 통과 |
| 3 틀린 묶음 | `S3_Merged_*`: 위쪽 두 문단이 6줄로 묶임(안내서와 같음) → "위 줄 제외"를 첫 문단 줄 수만큼 눌러 둘째 문단만 남김("줄 N/최대"도 같이 바뀜), 아래 줄 제외·포함 왕복, 낱말 하나를 고쳐 저장 → 옆 문단 글은 그대로. **아래쪽 "예비품은…" 문단은 서명 줄을 처음부터 상자에 넣지 않음**(3줄) | 통과 |
| 4 원래 글꼴에 없는 글자 | `S4_Subset_*`: Arial 영문 문단 끝에 한글 → 확정 전 "기본 글꼴로 바뀝니다" 예고, 확정 뒤 토스트 `Msg_TextFallbackApp`, 저장 파일에서 한글 추출됨·`NotoSansKR` 글꼴·화면 모양 변화. Georgia 문단 → **`NotoSerifKR`(명조)** 로 저장 | 통과 |
| 5 두 번 고치기 | `S5_Twice_*`: 낱말 바꿈 → 다시 눌러 고친 글로 열림 → 끝에 추가 → 또 바꿈. 편집은 항상 1개(쌓이지 않음), 저장 파일에 문단 글이 **한 번만**(숨은 옛 글·`3 Tr` 없음), Ctrl+Z 한 번 = 직전 편집 상태, 세 번 = 원본 | 통과 |
| 6-가 회전 90° | `S6a_Rotated90_*`: 회전된 쪽에서 문단이 열림(글 각도 0), 편집·저장 → 쪽 회전 유지, **상자 밖 0 px** | 통과(아래 D1 고침 뒤) |
| 6-나 125/150 % | `S6b_Zoom_*`: 화면 배율(`Tab.Scale`)을 ×1.25·×1.5로 흉내 → 클릭이 열리고 편집 글자 크기·폭이 배율을 따라가며, 더 키워도 따라감 | 통과(진짜 Windows DPI 배율은 **흉내 못 냄** — 사용자 점검) |
| 6-다 암호 | `S6c_Encrypted_*`: 한 줄 창 + `Para_Encrypted` 안내, 글꼴에 있는 글자(끝 글자 지우기)는 한 줄 편집으로 편집됨. 서브셋에 없는 글자를 넣으면 "암호가 걸린 문서라 원래 글꼴에 없는 글자는 바꿀 수 없습니다" 알림이 뜨고 편집은 안 됨(조용한 실패 아님) | 통과 |

### 사용자가 스크린샷으로 알려 온 3건 — 재현 시험으로 고정(고치지 않음)
| 번호 | 시험 | 현재 상태(숫자) |
|---|---|---|
| ① 글꼴이 바뀌면 줄바꿈이 크게 달라짐 | `KnownIssue1_…` (`01-ime` 첫 문단) | 글자 하나만 바꿔도: **같은 글꼴 글자**면 끊는 자리 그대로(줄 길이 [50,49,47]→[50,49,47], 다시 쓴 줄 1) / **서브셋에 없는 글자**면 앱 글꼴로 넘어가 **문단 3줄이 모두 다시 쓰이고 줄 길이 [54,55,37]**(끊는 자리 3줄 모두 다름) |
| ② 편집 창 줄바꿈이 실제와 다름 | `KnownIssue2_…` | 편집 창은 PDF 글꼴이 아니라 테마 기본 글꼴(Inter)을 쓰고, 한글은 음절 사이에서도 끊김: 폭 180 DIP에서 10줄 중 **낱말 중간 끊김 5곳**. 계획은 항상 어절(공백) 단위(`KoreanLineBreak.Word`). 헤드리스 글꼴 폭이 실제와 달라 실제 화면의 줄 수 차이는 못 잼 |
| ③ 안내 글씨가 작고 접힘 | `KnownIssue3_…` | 안내 글씨 11 pt(편집 글 14.7), `MaxWidth` 360 DIP, 안내가 두 문장이면 **2줄로 접힘**(높이 30 DIP) |

이 시험들은 "고쳐지면 실패"하도록 현재 동작을 고정했다. 고칠 때 기대를 뒤집는다.

### 발견된 결함
| # | 어디서 | 증상 | 원인 | 처리 |
|---|---|---|---|---|
| D1 | 문단 편집 창 (회전 쪽) | `/Rotate 90` 쪽에서 편집 창 폭이 **62 DIP**(글자 네 개쯤)로 좁고 길쭉해 글이 읽히지 않음. 위치는 맞지만 폭이 문제 | `PlaceParagraphPopup`이 화면에서 본 상자 폭(+6, 최소 60)을 그대로 씀 — 회전 쪽의 가로 줄 문단은 화면에서 세로로 서므로 상자 폭 = 줄 높이 합 | **고침**(3줄): 폭이 `ParagraphEditorPopup.ReadableMinWidth`(240 DIP)보다 좁으면 240으로 넓힘. `S6a`가 확인 |
| D2 | **실제 PDF**의 양쪽 맞춤 문단 저장 (`PdfSharpAnnotationPatcher.ParagraphLineBytes`, Task 6 코드) | 글 크기를 `Tf`가 아니라 글 행렬 배율(`/F 1 Tf 9 0 0 9 x y Tm`)로 주는 PDF에서 고친 줄의 **낱말 사이가 배율만큼(약 9배) 벌어져 줄이 오른쪽 단을 덮음**. 실제 PDF 12쪽 표본 중 3쪽에서 상자 밖 다른 픽셀 57~246, 1쪽은 같은 이유로 충실도 보호가 저장을 거절(`ContentRegenerationException`) | `TextLineWriter`의 `TJ` 숫자는 글 공간 단위인데 `JustifyExtra`(쪽의 pt)를 그대로 넘김 — `Tf`=1·`Tm`=9이면 이동량이 9배. 합성 샘플은 `Tf` 11·`Tm` 1이라 안 드러남 | **고침**(4줄, Task 8 범위 밖의 Task 6 코드지만 원인이 분명하고 영향 범위가 한정): 늘임을 글 행렬 배율(`hypot(A,B)`)로 나눠 넘김. 합성 재현 시험 `ParagraphEdit_ScaledTextMatrix_JustifiedLine_StaysInsideRegion`(수정 전 175 px 실패 → 수정 후 0) + 실제 PDF 표본 모두 영역 밖 0 px·저장 거절 0 |
| D3 | 안내서 6-다 설명 | "한두 글자 고치고 확정"이라고 했지만 암호 PDF에서 서브셋에 없는 글자는 알림만 뜨고 편집이 안 됨 | 설계(A2: 암호 문서는 앱 글꼴로 바꿀 수 없음) — 동작은 정상 | 안내서 문구 보강 권장(결함 아님) |
| D4 | ① 글꼴 바뀌면 줄바꿈 달라짐 | 위 표 | 글자 하나가 서브셋에 없다는 이유만으로 **다시 쓰는 줄 전체**가 앱 글꼴이 되고, 앱 글꼴은 원래 글꼴과 폭이 달라 끊는 자리가 틀어짐 | **고치지 않음 — 설계 변경 필요**. 아래 추천안 |
| D5 | ② 편집 창 줄바꿈 | 위 표 | 편집 창은 일반 `TextBox`(테마 글꼴·UAX#14) | **고치지 않음** — 추천안 |
| D6 | ③ 안내 글씨 | 위 표 | `Status` `FontSize=11`, `MaxWidth=360` | **고치지 않음**(요청대로) — 추천안 |

**추천안 (결정이 필요한 것은 사실상 D4 하나):**
- D4: ⓐ 글자 단위 혼합 — 줄 안에서 서브셋에 있는 글자는 원래 글꼴로, 없는 글자만 앱 글꼴로 쓴다(같은 줄을 `Tj` 둘로). 끊는 자리가 거의 안 변해 문제 ①이 사실상 사라지지만 구현이 크다(Task 4·5·6 변경). ⓑ 다시 쓰는 줄이 원래 줄 폭을 넘으면 그 줄만 `Tz`(가로 비율, ±8 % 이내)로 맞춰 원래 끊는 자리를 유지 — 작은 변경. **추천: ⓑ를 먼저 해 보고, 부족하면 ⓐ.**
- D5: 편집 창 아래에 "실제 줄바꿈 미리보기"(계획의 줄을 그대로 보이는 읽기 전용 글)를 둔다. 입력칸 자체의 줄바꿈을 PDF에 맞추는 것은 불가능에 가깝다.
- D6: `Status` 글자 11 → 12.5, `MaxWidth` 360 → 편집 창 폭(최소 360), 문장마다 줄을 나눠 `·` 구분 대신 줄바꿈. (한 곳 4줄 정도 — 승인하면 바로 고침.)

### 자동으로 확인할 수 없는 것 — 사용자 최소 점검 목록 (5분)
1. **한글 입력(IME)**: `01-ime.pdf`에서 문단을 눌러 한글을 치다가(밑줄 글자 상태에서) Enter·Esc를 눌러 창이 안 닫히는지. (자동 시험은 `PreeditText` 흉내뿐)
2. **다른 프로그램에서 열어 보기**: `spikes/out/m5-paragraphs-document.pdf`를 Acrobat(없으면 크롬)으로 열어 ① 한글 줄이 네모 없이 보이는지 ② 첫 문단을 전부 선택·복사해 메모장에 붙였을 때 고친 글이 **한 번씩만** 나오는지.
3. **화면 배율 125 %/150 %**: 윈도우 배율을 바꾸고 `06b-zoom.pdf` 문단을 눌러 점선 상자와 편집 창이 글 위에 겹치는지(안내서 6-나). 진짜 배율은 헤드리스에서 흉내 낼 수 없다.
4. **마우스 감각**: 편집 창 좌우 끝의 연한 파란 띠를 끌어 폭을 바꿨을 때 부드럽게 되는지.
5. **돌아간 쪽**: `06a-rotated90.pdf`에서 문단 편집 창이 읽을 수 있는 폭(이제 최소 240)으로 뜨는지.

## Task 9 — 충실도·왕복·호환성 시험 (2026-10-27, Sonnet)

시험: `tests/PdfEditor.TextEdit.Tests/Paragraphs/ParagraphFidelityTests.cs`(계획서의 `tests/PdfEditor.Document.Tests/Output/`가 아니라 TextEdit 시험 프로젝트에 둠 — 문단 계획기가 필요해서, Task 6과 같은 이유. 이를 위해 시험 프로젝트에 `PdfEditor.Pages` 프로젝트 참조를 추가했다, NuGet 없음). 모두 실제 저장 길(편집 → `OutputCopyBuilder` → PDFsharp 패치)로 저장한 뒤 PDFium으로 다시 열어 비교한다.

| 시험 | 내용 | 결과 |
|---|---|---|
| `ParagraphEdit_OutsideRegion_ZeroPixelDiff` ×7 | 문서형(영문 왼쪽 맞춤·양쪽 맞춤·한글·제목+글머리표·가운데/오른쪽) + 도면형(주기·90° 도면) 쪽마다 문단 2개를 고쳐 저장 → 편집 영역(문단 상자 + 다시 배치한 줄) 밖 **0 px** | 모두 0 |
| `ParagraphEdit_ShadingAndPatternPages_ZeroPixelDiff` ×2 | M4x 알려진 한계 쪽(셰이딩 `sh`, 패턴 채우기+선) | 0 px(여유 30 pt) |
| `ParagraphEdit_ScaledTextMatrix_JustifiedLine_StaysInsideRegion` | D2 재현: 글 크기를 `Tm` 배율로 준 양쪽 맞춤 쪽 | 수정 전 175 px → 0 |
| `EditSaveReopenEdit_RoundTrip` | 저장 → 다시 열어 같은 문단을 같은 줄 수로 추정 → 고친 글이 문단 글로 읽힘 → 다시 편집·저장: 새 글 있음·옛 줄 한 번만·`3 Tr` 없음·영역 밖 0 px | 통과 |
| `UndoParagraph_RestoresOriginalLines` | 편집 → 실행 취소 저장 = 원본(글·전체 0 px), 다시 실행 저장 = 편집본(전체 0 px) | 통과 |
| `ParagraphThenPageOps_EditFollowsPage` | 3쪽 문서 가운데 쪽 편집 → 회전(`/Rotate 90`)·맨 앞으로 이동·다른 쪽 삭제 → 편집이 쪽을 따라감, 삭제·이동·회전 실행 취소 후 0 px, 문단 편집 취소 → 원본 글, 편집한 쪽 삭제→실행 취소 → 편집 복원 | 통과 |
| `Print_ShowsEditedParagraph` | 인쇄 경로(`BuildIfNeededAsync`)의 사본에 편집이 들어 있고 저장본과 전체 0 px | 통과 |
| `Recovery_ContainsParagraphEdit` | `RecoveryStore.WriteAsync` 복구본에 편집이 들어 있고 세션은 더티 그대로 | 통과 |
| `Summary_PageImageShowsEdits` | 요약의 쪽 그림은 인쇄와 같은 출력 사본에서 그린다(`AnnotationSummaryRunner`가 `BuildIfNeededAsync` 사용) — 이 사본의 렌더가 상자 안만 달라지고 밖은 0 px | 통과(요약 PDF 생성 자체는 기존 App 시험이 따로) |
| `ExtractPages_IncludesParagraphEdit` | `PageExtractor`로 편집한 쪽만 추출 → 1쪽, 편집 포함 | 통과 |
| `Rotated90Drawing_NoteParagraph_Edit` | 90° 돌아간 도면 주기 문단 편집·저장: 글 추출·영역 밖 0 px·가로 문단 보존 | 통과 |
| `KoreanParagraph_FontsEmbeddedOnce_NoTofu` | 서브셋에 없는 한글을 문단 둘에 넣어 저장: 글자 추출 일치, **앱 글꼴 서브셋 한 번만**(Type0+CID 한 쌍), 화면(글꼴 윤곽선)과 저장(글꼴 글자)이 **강한 차이 0 px**(흐린 안티앨리어싱 차이 11,045 px는 글꼴 글자 vs 경로 그리기의 차이) | 통과 |
| `Sample_Document_*`·`Sample_Drawing_*` | 호환성 샘플 생성 + 저장·다시 열기 확인(`spikes/out/m5-paragraphs-document.pdf`·`-drawing.pdf` + PNG, 편집 영역 밖 0 px) | 통과 |
| `RealPdfs_PrivateOptional` | `private/`의 실제 PDF(회사 문서, 저장소에 안 올림). 파일마다 쪽 최대 4개를 무작위로 골라 첫 문단(3줄 이상)을 낱말 하나 뒤집어 저장·비교. **수치만** 기록(`spikes/out/m5-real-pdf-stats.txt`, gitignore, 파일 이름 없음) | 아래 |

### 실제 PDF 수치 (`private/` 4개 파일, 16쪽 표본, 2026-10-27, D2 수정 뒤)
- 글 객체가 있는 쪽 중 **3줄 이상 문단으로 묶인 쪽 5/16(31 %)**, 문단으로 안 읽혀 **한 줄 편집으로 폴백할 쪽 11/16(69 %)**(표·표제란·여러 단 낱말 객체·글자 단위로 쪼개진 글 등). 문단 편집은 "문단일 때만" 켜지는 설계라 폴백 자체는 정상이지만 비율이 높다 — 실제 사용에서 문단 편집이 얼마나 자주 켜지는지 사용자가 체감해 볼 것.
- 문단이 있던 5쪽: 계획 성공 5(원래 글꼴 5·**앱 글꼴 0**), 저장 성공 5, 저장 거절 0, 편집 영역 밖 달라진 쪽 0(픽셀 합 0).
- **수정 전**(같은 시험, D2 미수정, 12쪽): 저장 성공 4, **저장 거절 1**(충실도 보호가 모양 불일치를 잡음), 영역 밖 달라진 쪽 3(픽셀 57·106·246 — 오른쪽 단 침범). 합성 샘플만으로는 안 잡히던 결함을 실제 PDF가 잡았다.
- 표본이 작다(파일 4개·16쪽). 도면(CAD) 쪽은 거의 없다 → 도면에서의 비율은 모름(검증 필요).

### 한계·검증 필요
1. 다른 뷰어(Acrobat·PDF-XChange·Foxit·Chrome)에서의 모양·글자 복사는 자동으로 못 한다 → `docs/checklists/viewer-compat.md` M5 절(사용자 점검).
2. 편집 영역 밖 0 px 판정에서 "편집 영역"은 문단 상자 + 다시 배치한 줄의 외접 사각형(+8 px 여유)이다. 영역 안의 변화는 이 시험이 보지 않는다(화면 ≡ 저장 보호는 저장 안에서 이미 한다).
3. `Summary` 시험은 요약 PDF를 만드는 실제 러너가 아니라 그 러너가 쓰는 출력 사본 경로를 확인한다.
4. 실제 PDF 시험은 낱말 하나를 같은 길이로 바꾸는 편집만 한다(줄 수가 늘거나 글자를 넣는 편집은 합성 샘플에서만). 앱 글꼴 비율 0 %는 이 표본에서 서브셋 밖 글자를 안 넣어서다.
5. 위 D4(글꼴 바뀌면 줄바꿈)·D5·D6은 사용자 결정 대기.

## 한글 IME 조합 중 Esc·Enter 수정 (2026-10-27, Sonnet)

**증상(사용자가 실제 한글 입력기로 확인):** 조합 중 Esc → 문단 편집 창이 닫힘, "가나" 뒤 Enter → 줄바꿈이 들어감.

**원인(추정 — 실제 입력기로 확인 필요):** 실제 한글 입력기는 Enter/Esc에서 조합을 먼저 끝내(PreeditText가 비워짐) 그 키를 앱에 그대로 넘긴다. 기존 판정은 "키가 올 때 PreeditText가 비어 있지 않을 때만 무시"라서 놓쳤고, 조합 중이어도 Enter를 막지(Handled) 않아 입력칸이 줄바꿈을 넣었다. 자동 시험은 PreeditText를 직접 켜 둔 채 키를 보내 이 순서를 못 잡았다.

**조치:** `ImeCompositionGuard`(src/PdfEditor.App/Viewer) 한 곳에서 판정 — ① ImeProcessed 키 ② 지금 조합 중 ③ 조합이 150 ms 안에 끝남, 하나라도 맞으면 입력기 몫. 이때 Enter/Esc는 Handled로 삼켜 줄바꿈·확정·취소가 일어나지 않는다. 문단 편집 창·한 줄/여러 줄 글 편집 칸(FreeTextEditor, 한 줄 편집 포함)·답글 칸이 모두 같은 도우미를 쓴다(여러 줄 글 편집 칸의 Esc도 같은 결함이 있어 같이 고침).

**시험:** `ImeCompositionGuardTests` 7개(조합 중, 조합이 직전에 끝나 PreeditText가 이미 빔, 150 ms 뒤, ImeProcessed 뒤 Enter, 문단 창, 한 줄 편집 창). 기존 시험 3개는 "시간이 흐른 뒤"를 `ForgetRecentEnd()`로 표현. 전체 시험 통과.

**사용자 재확인 필요 (자동 시험은 실제 입력기를 대신할 수 없음):**
1. 문단 편집 창을 열고 한글 입력기로 바꾼다.
2. "가나"를 치고 **Enter 한 번** → 줄바꿈이 안 들어가고 글자만 확정돼야 한다. 이어서 Enter를 한 번 더 누르면 줄바꿈.
3. "다"를 치는 도중(밑줄) **Esc** → 편집 창이 그대로 있어야 한다. 조합이 끝난 상태에서 Esc를 다시 누르면 창이 닫힌다(취소).
4. 같은 순서를 한 줄 편집 창(텍스트 편집 도구, 표 칸)과 코멘트 패널 답글 칸에서도 확인한다. 한 줄 편집 창에서는 "가나" Enter가 확정이 되면 안 되고, 두 번째 Enter가 확정이다.
5. 영문 상태에서 Enter/Esc는 예전처럼 바로 동작해야 한다(지연 느낌 없음). 한 번 눌러 안 먹으면 150 ms 판정이 과한 것이니 알려 주세요.

## 대체 글꼴 줄바꿈 유지 개선 — C5b (2026-10-27, Sonnet)

문제(D4 ①): 서브셋에 없는 글자 하나만 넣어도 앱 글꼴로 내려가고, 글자 폭이 달라 "원래 줄 끝과 다시 맞는 곳"(C5 동기화)을 못 찾아 문단 끝까지 줄바꿈이 새로 계산됨.

**동작 (`ParagraphLayout.Run`, 앱 글꼴 + 열 폭을 끌어 정하지 않은 경우만):**
- 원래 줄 끝을 새 글 위치로 옮겨(바뀐 글자 수만큼 이동) 줄바꿈 후보로 삼는다. 바뀐 구간 안의 경계는 버린다.
- 고친 줄부터 후보 경계까지를 한 줄로 쓰되, 앱 글꼴이 더 넓어 상자 폭을 넘치면 **그 줄만** 나눈다. 남은 조각은 다음 줄과 합쳐도 한 줄에 들어가면 합치고(줄 수가 늘지 않음), 아니면 따로 한 줄이 된다.
- 고친 곳 뒤 원래 줄 경계에서 줄 번호가 같으면 그 뒤는 원본 줄 그대로(C5 동기화와 같음). 줄이 늘거나 줄어 번호가 어긋나면 뒷줄은 앱 글꼴로 다시 쓰되 줄 끝 위치는 원본을 따른다.
- 좁은 글꼴이라 줄이 짧아져도 허용(양쪽 맞춤은 기존 늘임 규칙으로 폭을 채움). 정렬·회전은 기존 계산 그대로.
- **원래 글꼴 경로(C5)와 열 폭 직접 지정은 코드·동작 모두 그대로** (기존 시험 전부 통과).

**시험:** `ParagraphLayoutTests`에 `AppFont_*` 9개 추가(안 고친 줄 끝 유지·넘친 줄만 분할·조각 합치기·줄 수 불증가·양쪽/가운데/오른쪽·90° 회전·열 폭 지정은 전체 다시 맞춤). 먼저 6개가 실패하는 것을 확인한 뒤 구현.

**실제 샘플 수치 (헤드리스, 서브셋에 없는 글자 1개를 첫 줄에 넣음):**

| 샘플 | 수정 전 | 수정 후 |
|---|---|---|
| 01-ime 첫 문단 (줄 길이 [50,49,47]) | 3줄 모두 다시 쓰임, [54,55,37], 줄 끝 일치 0/3 | 1줄만 다시 쓰임, [50,49,47], 줄 끝 일치 **3/3** |
| 04-subset 영문 문단 (한글 입력, [87,91,79]) | 측정 안 함 | 1줄만 다시 쓰임, [87,91,79], 줄 끝 일치 **3/3** |

`UserScenarioTests`의 `KnownIssue1_…`은 새 동작에 맞게 기대를 뒤집고 이름을 `FontSubstitution_*`로 바꿈. **한계:** 실제 화면·다른 뷰어 확인은 안 함(헤드리스만). D4 ⓑ(Tz 조절)·ⓐ(글자 단위 혼합)는 하지 않음 — 이 개선으로 우선 충분한지 사용자가 앱에서 확인 필요.

## Task 10 — 성능·규칙 점검·문서 (2026-10-27, Sonnet)

측정 방법: Release, 실행 중인 앱을 건드리지 않게 `dotnet publish src/PdfEditor.App -c Release -o artifacts/m5-task10-build`, 아무것도 안 돌리며 순차, 각 3회. 합성 샘플 `spikes/out/bench-a1-drawing.pdf`(A1, 선 20만 + 글 5천, 205,000 객체)·`bench-a4-300pages.pdf`(300쪽), 문단 측정용 `bench-a1-paragraph.pdf`(A1 + 같은 구성에 6줄 Helvetica 문단 하나, 문단 자리의 선·글은 비움; `dotnet run -c Release --project spikes/S4.RenderBench -- spikes/out --paragraph-sample`로 생성), 문서형 `m5-paragraphs-document.pdf`(TrueType 한글/영문 3줄 문단)·`m5-user-test/01-ime.pdf`(한글)·`04-subset.pdf`(Arial 영문 서브셋). 측정 진입점: `SedolPDF.exe --bench <pdf> --out <json> --paragraph [--para-at x,y]`(앱 코드는 `Bench/` 폴더만 추가; `SEDOL_BENCH_PARA_CHAR=가`를 주면 서브셋에 없는 글자로 고쳐 앱 글꼴 경로를 잼). 편집은 "첫 번째 e를 a로"(같은 길이 낱말 고치기).

### 판정 한눈에

| 항목 | 목표 | 3회 결과 | 판정 |
|---|---|---|---|
| A1 첫 화면 | ≤ 0.5 s | 0.361 · 0.413 · 0.374 s | 통과 |
| 300쪽 작업 집합 | < 400 MB | 379 · 381 · 382 MB (M4x 383~389 MB보다 작거나 같음 — 문단 편집이 상시 메모리를 늘리지 않음) | 통과 |
| 300쪽 회색 비율 | ≤ 2 % | 0 % (세 스크롤 방식 모두) | 통과 |
| 클릭 → 문단 상자, 문서(PDFium 읽기+계산 포함, 처음 클릭) | ≤ 30 ms | 합성 문서 19~27 ms · 한글/영문 서브셋 문서 59~66 ms (두 번째부터 0.3~1 ms, 합성 문서 3.4~4.0 ms) | **절반 통과** — 처음 클릭만 30 ms를 넘음(프로세스 첫 호출의 JIT·글 쪽 열기 추정, 분해는 안 함) |
| 클릭 → 문단 상자, A1 도면 | ≤ 100 ms | **두 번째 클릭부터 82 · 94 · 87 ms**, 처음 클릭 172~189 ms | 첫 클릭은 **미달**(아래), 이후 통과 |
| 확정 → 화면 갱신(M4 편집 직후 A1 0.87 s를 넘지 않을 것) | ≤ 0.87 s | A1: 확정 38 + 갱신 0.58~0.67 s = **0.62~0.71 s**. 문서: 15 + 17~23 ms | 통과 |
| 문단 편집 있는 쪽 출력 사본 추가 비용 | ≤ +0.3 s | 문서형: +57~77 ms(편집 전 20~25 → 78~98 ms) 통과. **A1(205,000 객체): +3.6~3.9 s**(편집 전 0.19~0.21 → 3.7~4.1 s; M4x 편집 100개 1.3 s 기준으로도 크게 초과) | 문서 통과 / **A1 미달** |

### 성능 문제를 찾아 고친 것 (`perf:` 커밋)
1. **클릭이 A1에서 1.2 s 걸림 → 82~94 ms (두 번째 클릭부터).** 글 5,000개가 쪽에 흩어진 도면에서 후보 글 객체 상한(240개)이 사실상 작동하지 않았다: 상한에 닿은 뒤 "마지막 줄을 끝까지 채운다"는 규칙이 세로 거리 틈 > 0.35 글자 크기일 때만 멈추는데, 흩어진 글 사이에는 틈이 없어 후보가 2,337개까지 늘었다(글 읽기가 객체마다 약 0.33 ms). 고침: ① 마지막 줄 채우기 추가 상한 48개 ② 같은 열(시작 줄과 이어진 글의 가로 범위)과 같은 기준선대의 가까운 이웃 40개를 먼저 읽고 나머지는 뒤로(같은 기준선의 먼 칸이 가로 범위를 쪽 전체로 넓혀 세로 거리순만으로는 문단 줄이 밀려났음 — 이 때문에 상한만 걸었을 때 문단이 "표 안의 글"로 오판됨) ③ 글 객체가 1,000개 이상인 쪽은 상한을 240 → 120(+24)으로. 시험 `Reader_PicksOnlyNearbyCandidates`의 상한을 이에 맞춤. **V21(73~81 ms)은 글 격자 샘플이었고, 글이 흩어진 샘플에서는 이 문제가 있었다.**
2. **A1 출력 사본의 중복 일 줄임.** 문단 편집 쪽의 시험 적용(`CheckContentRewrites`)이 해석·분할·번호 대응·고쳐 쓰기를 하고 실제 쓰기가 같은 일을 또 하던 것을, 쪽 내용 바이트와 편집이 같을 때만 시험 적용 결과를 재사용하게 했고(`PdfSharpAnnotationPatcher._dry`), 객체 경계 목록을 시험 적용과 화면 모양 기억 사이에서 한 번만 읽게 했다(`OutputCopyBuilder` refCache). 효과는 약 0.4~0.9 s(4.4~4.8 → 3.7~4.1 s)로 **목표에는 못 미친다.**

### 남은 미달 (원인은 나눠 봤고 고치지 못함)
- **A1 출력 사본 3.7~4.1 s.** 단계별(한 번 잰 값): 객체 경계 읽기 0.2 + 시험 적용(파일 열기·해석) 약 1.1 → 재사용 후 감소 + 화면 모양 기억(화면 replay + 640 px 렌더) 약 0.6 + 실제 쓰기(PDFsharp 저장 포함) 1.2 + 저장 뒤 모양 비교(다시 열기 + 렌더) 0.7. 20만 객체 쪽의 해석+재작성 자체가 M4x 기준 1.4 s라서 이 쪽에서는 문단 편집이 있을 때 약 3~4 s가 든다. 일반 문서 쪽은 +60~80 ms. 백그라운드(`Task.Run`/PDFium 스레드 대기)에서 일어나 UI는 막히지 않지만, 큰 도면에서 저장·인쇄 시작이 그만큼 늦다. **추가로 줄이려면** 시험 적용과 실제 쓰기를 한 번의 PDFsharp 열기로 합치거나(구조 변경) 저장 뒤 비교를 문단 상자 주변으로 좁히는 방법이 있으나 이번 Task 범위 밖이라 하지 않았다 — 사용자 판단 거리.
- **첫 클릭 지연:** A1 처음 클릭 172~189 ms는 두 번째 클릭(82~94 ms)과 90 ms 차이로, 처음 호출의 글 쪽 열기(LoadText 25~40 ms)·JIT로 보이나 분해하지 않았다. 서브셋 문서 처음 클릭 59~66 ms도 같은 원인으로 추정(**검증 필요**).
- **첫 글자 입력 지연(미리 계획 처음 한 번):** 문서 90~117 ms, A1 462~504 ms(원본 파일을 PDFsharp로 열어 쪽 해석 + 코드북 만들기, 편집 창이 열려 있는 동안 한 번만 하고 `CachedFontSource`에 캐시). 이후 입력은 0.4~1.5 ms. 입력창 아래 상태줄만 늦게 갱신되고 입력 자체는 막히지 않는다.
- 객체 목록 만들기(A1 160~185 ms)는 M4에서 이미 있던 비용이다.

### 규칙 점검 (grep, 2026-10-27)
| 규칙 | 결과 |
|---|---|
| 문단 추정 순수 코드가 PDFium을 부르지 않음 (`ParagraphReader` 제외) | 지킴. `Paragraphs/` 안에서 `PdfDocument`·`PdfPage`·`PdfTextPage`·`FPDF*`를 쓰는 파일은 `ParagraphReader.cs` 하나뿐. 다른 파일(`LineAssembler`·`ParagraphDetector`·`ParagraphLayout`·`ParagraphModel`·`TextRun`·`ParagraphEditPlanner`)의 `using Pdf.Native`는 `PdfRectF`·`PdfMatrix` 같은 값 형식 때문이다 (`ParagraphEditPlanner`는 PDFsharp `PdfReader`로 원본 파일을 읽고 `Thread.AssertOnThread()`로 PDFium 스레드에서만 호출됨을 확인). 계획서의 "`Pdf.Native` 미참조"는 문자 그대로가 아니라 "PDFium 호출 없음"으로 해석함 |
| `PdfEditor.ContentStream`이 PDFium을 참조하지 않음 | 지킴. 소스에서 `Pdf.Native`·`FPDF` 참조 0건(주석의 낱말만), 시험 `ContentStream_DoesNotReferencePdfium` 통과 |
| 파일을 만드는 길이 `OutputCopyBuilder` 하나뿐 | 지킴. `SaveCopy`는 `Pdf.Native`(정의)·`OutputCopyBuilder`·`PageExtractor`(별도 사본 문서를 쪽 추출용으로 쓰는 기존 경로, 문단 편집은 `ExtractPages_IncludesParagraphEdit`가 확인)만 호출. M5가 새로 만든 파일 쓰기 길 없음 |
| `FPDFText_LoadFont` 미사용 | 지킴 (소스 0건) |
| 새 화면 문구는 `Strings.resx` | 지킴. 문단 편집 파일(`ParagraphController`·`ParagraphEditorPopup`·`DocumentView.Paragraph`·`ImeCompositionGuard`)의 한글은 주석뿐이고 화면 문구는 모두 `Strings.*` |
| 새 글꼴(Noto Serif KR) 라이선스 | 지킴. `assets/fonts/OFL.txt`(저장소 `Serif/LICENSE`와 바이트 동일), `THIRD-PARTY-NOTICES.md` Noto Serif KR 절, `build/package.ps1` 필수 파일 목록, csproj 복사. 시험 `PackageContainsLicenses`(스크립트·csproj 내용 검사). 실제 패키지는 만들지 않음 — 포터블 zip은 약 +5.9 MB(약 64 MB) 예상 |
| `Blob.FromStream` 없음 | 지킴 (소스 0건) |
| 새 NuGet 없음 | 지킴. M5 기간 csproj 변경은 프로젝트 참조·번들 글꼴 복사뿐 (`PackageReference` 변화 0) |

## M5 요약 (Task 1~10)

구현 범위: V17~V21 검증, 문단 추정(줄 조립·묶기·신뢰도 0.62), 원래 글꼴 코드북 직접 인코딩, 최소 변경 다시 맞추기(+C5b 대체 글꼴 줄 끝 유지), 저장(내용 스트림 직접 쓰기·옛 글 완전 제거), 고딕/명조 대체 글꼴, 화면(문단 상자·여러 줄 편집 창·폭 손잡이·줄 포함/제외·한 줄만 토글·알림), IME 조합 판정 공용화. 결정 C1~C18 요약과 알려진 한계는 설계서 2.3절 "구현 결과 (M5)".

### 알려진 한계 (모음)
1. **추정은 보수적:** 표 칸·범례·짧은 줄 목록·일부 2줄 문단·좁은 다단은 한 줄 편집으로 남는다. 실제 PDF 16쪽 표본에서 3줄 이상 문단으로 열리는 쪽은 5쪽(31 %), 69 %는 한 줄 편집 폴백. 도면(CAD) 쪽 표본은 거의 없어 도면 비율은 모른다. "전부 대문자 캡션 목록"형 오탐은 남아 있다(줄 제외 단추로 고침).
2. **스크린샷으로 확인된 이전 문제 3건의 현재 상태:** ① 글꼴이 바뀌면 줄바꿈이 달라짐 → **해결**(C5b, 서브셋 밖 글자 하나만 넣어도 끊는 자리 유지 3/3). ② 편집 창 입력칸의 줄바꿈이 실제와 다름 → ****고침(D5)**: 입력칸은 그대로지만 아래에 실제 줄바꿈 미리보기를 추가. ③ 안내 글씨가 작고 접힘 → ****고침(D6)**: 12.5 pt, 폭 제한 없음, 문장마다 한 줄.
3. 새 줄은 쪽 맨 위에 그려진다(겹침 순서가 다른 드문 쪽은 충실도 보호가 잡아 저장 거절). 태그(접근성) 구조에는 속하지 않는다.
4. 화면의 원래 글꼴 줄은 PDFium 글 객체라 자간·양쪽 맞춤 늘임·가로 비율을 못 그린다(저장은 정확, 화면과 조금 다를 수 있음). 양쪽 맞춤 문단이 PDFium 바닥 경로로 내려가면 왼쪽 맞춤처럼 저장된다(실제로 내려가는 비율은 못 잼).
5. 서브셋에 없는 글자를 넣으면 다시 쓰는 줄이 대체 글꼴이 된다(원본 줄과 섞임, 가짜 굵게·기울임은 흉내). 가짜 굵게의 화면 모양은 선 없이 가늘게 보인다(파일은 렌더 모드 2).
6. 암호 문서(한 줄 편집으로 폴백)·폼(묶음) 안 글·세로쓰기·임의 각도·OCR 글 층은 대상 밖. 후보 상한(240개, 글 많은 쪽 120개)에 걸리는 낱말-객체 문단은 끝줄이 잘려 짧게 추정될 수 있다(줄 포함 단추로 고침).
7. 큰 도면 쪽(205,000 객체)의 출력 사본은 문단 편집 때 약 3.7~4.1 s, 첫 클릭 0.17~0.19 s(위 성능 절).
8. 탭 전환·도구 변경·문서 닫기는 입력 중인 문단 편집을 확정하지 않고 취소한다. 포터블 zip은 명조 글꼴로 약 +5.9 MB.

### 사용자 최소 점검 목록 (자동으로 확인할 수 없는 것 — 쉬운 말로, 5~10분)
1. **한글 입력(IME)**: 문단 편집 창에서 한글 입력기로 "가나"를 치고 Enter 한 번 → 줄바꿈이 안 들어가야 한다(조합 확정만). 조합 중 Esc → 창이 안 닫혀야 한다. **코멘트 패널 답글 칸**과 한 줄 편집 창에서도 같은지(`ImeCompositionGuard` 공용 적용, 실제 입력기로는 미확인).
2. **다른 프로그램에서 열어 보기**: `spikes/out/m5-paragraphs-document.pdf`·`m5-paragraphs-drawing.pdf`를 Acrobat(없으면 Chrome)으로 열어 ① 한글이 네모 없이 보이는지 ② 고친 문단을 전부 선택·복사해 메모장에 붙였을 때 고친 글이 한 번씩만 나오는지. 가능하면 PDF-XChange·Foxit에서도(양쪽 맞춤 `TJ` 모양, 가짜 굵게 렌더 모드 2 포함).
3. **화면 배율 125 %/150 %**: 윈도우 배율을 바꾸고 `m5-user-test/06b-zoom.pdf`의 문단을 눌러 점선 상자와 편집 창이 글 위에 겹치는지(헤드리스로는 배율만 흉내 냄).
4. **마우스 감각**: 편집 창 좌우 끝 띠를 끌어 폭을 바꿀 때 부드러운지, 큰 도면에서 문단을 처음 누를 때 눈에 띄게 느린지(측정은 0.17~0.19 s).
5. **실제 회사 도면·문서**로 문단을 몇 개 고쳐 저장한 뒤 다른 뷰어에서 모양 확인(특히 해치·그러데이션이 있는 쪽, M4x 사용자 검수와 겹침).
6. 위 ②③ 입력칸 줄바꿈 차이·안내 글씨 크기를 고칠지(추천: 둘 다 고침) 결정.

## D5·D6 수정 (2026-10-27, 사용자 승인: "추천대로")
- **D6 안내 글씨:** 11 → 12.5 pt, 최대 폭 360 제한을 없애 편집 창 폭을 따라가게 하고, 안내를 `·`로 이어 붙이지 않고 문장마다 한 줄로 나눴다(`ParagraphPreview.StatusText`).
- **D5 편집 창 줄바꿈:** 입력칸(`TextBox`)의 줄바꿈은 그대로(테마 글꼴 폭이라 PDF와 다를 수 있음 — 입력칸을 PDF 글꼴에 맞추는 것은 불가능에 가깝다). 대신 입력칸 아래에 **"실제 줄바꿈 미리보기 (PDF 기준)"**: 미리 계획이 낸 줄(`ParagraphLayoutResult.Lines`)을 한 줄씩 보여 주는 읽기 전용 글(최대 높이 130, 넘으면 스크롤). 문구 `Para_LinePreview`는 `Strings.resx`. 글꼴은 테마 글꼴이라 줄 **끊는 자리**만 정확하고 글자 폭은 실제와 다르다.
- 시험: `UserScenarioTests`의 고정 시험 2개를 새 동작으로 뒤집음(`EditorWrap_InputBoxDiffers_PlannedLinesPreviewShown`: 미리보기 줄 수 = 계획 줄 수, `StatusText_Larger_OneSentencePerLine`). App 시험 573개 통과. 실제 화면 모양(글자 크기·창 높이가 커져 문단을 가리는 정도)은 헤드리스로 못 봄 — **사용자 점검 항목에 추가**: 문단 편집 창 아래 미리보기와 안내 글씨가 읽기 편한지, 창이 너무 커져 아래 글을 가리지 않는지.

## 실제 화면 조작 시험 (2026-10-27, 사용자 허락 "지금 가능", 보조 모니터, 저장 안 함)
최신 빌드(`artifacts/m5-ui-test-build`, D5·D6 반영)로 `m5-user-test/01-ime.pdf`를 열어 텍스트 편집 도구로 첫 문단을 눌렀다. 한글 입력은 `keybd_event(VK_HANGUL)`로 한글 모드로 바꾼 뒤 영문 키(r, k → 가)를 눌러 실제 입력기 조합을 만들었다.

| 항목 | 결과 |
|---|---|
| 문단 상자·여러 줄 편집 창 열림 | 정상. 점선 상자 + 위/아래 줄 포함·제외 단추 + 입력칸 + 확정/취소 |
| 새 "실제 줄바꿈 미리보기 (PDF 기준)" | 정상 표시: 입력칸 아래 3줄, 줄 끊김이 계획과 같음. 안내("줄 3/최대 47", "고친 줄은 원래 글꼴 대신 기본 글꼴로…", "고친 줄과 안 고친 줄의 글꼴이…")가 12.5pt로 문장마다 한 줄, 접히지 않음 |
| 한글 조합 중 **Esc** (조합 중 "가") | **편집 창이 그대로 유지됨**(닫히지 않음) — IME 수정 확인 |
| 조합 중 **Enter** ("가" 조합 → Enter) | **줄바꿈이 들어가지 않고** 글자만 확정("…없다.가가") — IME 수정 확인 |
| 조합이 끝난 뒤 Esc | 편집 창이 닫히고 문단 글은 원본 그대로(취소) |
| 영문/한글 상태 복원 | 시험 뒤 한/영 상태를 되돌림. 시험용 앱은 닫음(제가 연 창만) |

**확인 못 한 것(검증 필요):** ① 편집 창 좌우 폭 손잡이 끌기 — 합성 마우스 드래그가 손잡이가 아니라 입력칸 글 선택으로 처리됨(손잡이 위치를 맞추지 못한 것일 수 있어 결함으로 단정하지 않음, 사람 손으로 한 번 확인 필요). ② 코멘트 패널 답글 칸·한 줄 편집 창의 한글 입력(같은 `ImeCompositionGuard`라 같을 것으로 보나 미확인). ③ 125/150 % 배율(시스템 설정이라 하지 않음). ④ 다른 뷰어. ⑤ 편집 창이 아래 문단(이웃 문단)을 일부 가리는 것은 확인됨 — 편집 중에는 창이 문단 위에 덮이는 설계라 정상으로 보이나, 미리보기 때문에 이전보다 창이 커졌다.
