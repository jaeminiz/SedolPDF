# 후원 페이지(GitHub Sponsors) 준비 안내서

작성: 2026-10-28. 0.4.0 계획서 D16·U9에 따라 앱에는 "후원하기" 링크 하나만 둡니다(앱 안 결제·광고 없음). 이 문서는 **나중에** 사용자가 후원 페이지를 만들 때 쓰는 준비물입니다. 화면 이름과 순서는 GitHub가 바꿀 수 있으니 실제 화면이 다르면 화면 안내를 따르세요. 아래 지급·세금·지역 지원 내용은 **검증 필요**입니다(제가 확인한 것이 아니라 일반 정보).

## 1. 먼저 알아 둘 것 (쉬운 말)
- 후원은 **선택**입니다. 후원해도 기능이 달라지지 않습니다(무료·MIT·광고 없음은 그대로).
- 가입은 사용자 본인이 해야 합니다(본인 확인, 지급 계정, 세금 정보 입력). 소요: 입력 10~20분 + 승인 대기(며칠 걸릴 수 있음).
- 한국에서 받은 후원금은 소득 신고 대상일 수 있습니다. 반복해서 받기 시작하면 세무서·세무사에게 한 번 확인하세요(검증 필요).
- 개인정보: 후원자의 이름·메일은 GitHub가 관리하며, 앱은 아무 정보도 받지 않습니다.

## 2. 가입 순서 (대략)
1. GitHub에 로그인하고 계정에 **2단계 인증**이 켜져 있는지 확인합니다.
2. 브라우저에서 `https://github.com/sponsors` 로 가서 **"Join the waitlist" 또는 "Get sponsored"** 같은 버튼을 누릅니다(지역·시기에 따라 다름).
3. 후원받을 대상: **개인 계정**(조직 아님). 대리인 이름은 실제 이름 그대로 씁니다.
4. 지급 방법: 안내하는 **지급 계정(Stripe Connect 등)** 을 연결하고 본인 확인 서류, 은행 계좌, 세금 양식(비미국인은 W-8BEN 등)을 입력합니다. ← 금융 정보라 반드시 본인이 입력하세요.
5. 프로필 작성: 아래 3절의 문구와 금액 단계를 붙여 넣습니다.
6. 제출 후 GitHub 승인을 기다립니다. 승인되면 `https://github.com/sponsors/<GitHub 사용자 이름>` 주소가 생깁니다.
7. **그 주소를 저에게 알려 주세요.** 앱의 `AppLinks.Sponsor` 한 곳에 넣고, 저장소에 `.github/FUNDING.yml`(아래 5절)을 추가하겠습니다.

## 3. 소개 문구 초안

### 한국어
> **세돌PDF를 만든 사람입니다.**
> 세돌PDF는 광고도 유료 기능도 없는 무료 오픈소스(MIT) PDF 편집기입니다. 도면·문서를 검토하면서 "원래 글꼴 그대로 글자를 고치는 문단 편집", "도면 번호 일괄 바꾸기" 같은 업무용 기능이 필요해서 직접 만들고 있습니다.
> 후원은 선택입니다. 후원하셔도 기능이 달라지지 않습니다. 후원금은 개발 시간, 시험용 장비·문서, 다른 뷰어 호환 점검, 영어 번역과 매뉴얼 작성에 쓰겠습니다.
> 문제나 제안은 GitHub Issues에 남겨 주세요(개인·회사 문서는 올리지 마세요).

### English
> **I build SedolPDF.**
> SedolPDF is a free, open-source (MIT) PDF editor with no ads and no paid features. I'm building it to review drawings and documents — things like paragraph editing that keeps the original fonts and batch replacement of drawing numbers.
> Sponsoring is optional and does not change what the app can do. Funds go to development time, test equipment and documents, compatibility checks with other viewers, translation and a user manual.
> Please report problems or suggestions in GitHub Issues (do not attach personal or company documents).

## 4. 금액 단계 제안 (매달 / 한 번)
| 단계 | 매달 | 설명 문구 |
|---|---|---|
| 응원 | $3 | 커피 한 잔 값으로 개발을 응원합니다 / A coffee to keep development going |
| 후원 | $10 | 다음 기능 투표에 참여(Issues에서 의견을 우선 검토) / Your feedback is prioritized in Issues |
| 든든한 후원 | $25 | 이름을 README 후원자 목록에 올림(원할 때만) / Name in the README supporters list (optional) |
| 한 번만 | $5 · $20 | 한 번만 후원 / One-time |

- 금액·혜택은 마음대로 바꿔도 됩니다. 단, **유료 기능이나 약속한 납기**는 만들지 마세요(약속이 생기면 책임이 생깁니다). "우선 검토" 정도로만 쓰는 것을 추천합니다.

## 5. 저장소에 넣을 파일 (승인 후 제가 추가)
`.github/FUNDING.yml`
```yaml
github: <GitHub 사용자 이름>
```
저장소 오른쪽에 "Sponsor" 단추가 생깁니다. 다른 후원 서비스를 쓰면 같은 파일에 `buy_me_a_coffee: 이름` 처럼 한 줄을 더합니다.

## 6. 앱에서의 모습 (0.4.0 계획)
- 정보 창과 환경설정에 "후원하기" 링크. 누르면 **외부 브라우저**가 열립니다.
- 후원 주소가 비어 있으면 단추가 보이지 않습니다.
- 앱은 후원 여부를 알지 못하고, 아무 정보도 외부로 보내지 않습니다.
- 스토어 심사: 앱 안 외부 결제 유도로 보일 수 있는지 제출 전에 인증 정책을 확인합니다(스토어 계획서 8절, 검증 필요).

## 7. 하지 말 것
- 후원을 이유로 기능을 잠그거나 광고·팝업으로 조르지 않기.
- 후원자 개인정보를 저장소·앱·README에 허락 없이 쓰지 않기.
- 약관에 없는 환불·납기 약속 하지 않기.

## 8. 별(Star) 누르기 링크 (2026-10-28 추가)
- 정보 창에 `GitHub에서 보기 · ★ 별 누르기 · 후원하기` 링크 한 줄을 둡니다(외부 브라우저로 열기만, 팝업·사용 횟수 요청 없음).
- 스토어에는 별·후원 링크만 두고 "스토어 평점을 눌러 주세요" 같은 문구는 쓰지 않습니다(심사 규칙 확인 전까지).
