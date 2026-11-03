# 디자인 규격표 (Design Tokens)

화면의 글자 크기·컨트롤 높이·간격·반경·아이콘 크기는 이 표의 값만 쓴다. 값은 `src/PdfEditor.App/Styles/DesignTokens.axaml`에 있고,
코드로 화면을 만들 때는 `DesignTokens`(C# 상수, `Styles/DesignTokens.cs`)를 쓴다. 두 값이 같은지는 `DesignTokenTests.CodeConstants_MatchResources`가 확인한다.

## 규격표

| 구분 | 이름 | 값 | 쓰는 곳 |
|---|---|---|---|
| 글자 | `FontSizeCaption` | 11 | 보조 설명·쪽 번호·상태 줄·작은 단추 |
| 글자 | `FontSizeBody` | 12.5 | 본문(모든 단추·입력칸·체크 상자·메뉴의 기본, Fluent 기본 14를 덮어씀) |
| 글자 | `FontSizeSubtitle` | 14 | 소제목 |
| 글자 | `FontSizeTitle` | 16 | 도움말 등 대화 상자 제목 |
| 높이 | `ControlHeightS` | 24 | 작은 단추(`Classes="small"`), 체크 상자·라디오 한 줄 |
| 높이 | `ControlHeightM` | 28 | 기본 단추·입력칸·콤보 상자·토글 |
| 높이 | `ControlHeightL` | 32 | 큰 단추(`Classes="large"`) |
| 간격 | `Space4/8/12/16` (숫자), `Inset4/8/12/16` (네 변 같은 여백) | 4·8·12·16 | `Spacing`, `Margin`, `Padding` |
| 간격 | `InsetDialog` | 24,20,24,16 | 대화 상자 바깥 여백 |
| 크기 | `ButtonMinWidth` | 88 | 대화 상자 확인·취소 단추 최소 폭 |
| 반경 | `RadiusS` / `RadiusL` | 4 / 8 | 단추·입력칸 / 카드·팝업 |
| 아이콘 | `IconSizeS/M/L` | 12 / 16 / 20 | 작은 아이콘 / 체크 상자·라디오 / 중간 아이콘 |

### 근거와 조정
- 글자 4단계·높이 3단계·간격·반경·아이콘은 요청된 수치 그대로다. 앱에 이미 11/12/12.5/13이 섞여 쓰였는데 **12.5가 본문으로 가장 많이 쓰이는 값**(`MainWindow`·`ParagraphEditorPopup`)이라 본문을 12.5로 정했다.
- 기존 10·10.5·11.5는 11로, 12·13은 12.5로 모았다. 1~1.5 pt 차이라 화면 분위기는 거의 같고, 영어 문구 잘림 시험이 통과한다.
- 정보 창 제품 이름(26)은 표제 하나뿐이라 규격 밖 허용 목록에 둔다(`DesignTokenTests`의 `allow`).
- 리본(`Shell/Ribbon`)은 사용자가 만족해 온 모양이라 크기를 그대로 두고 글자 크기만 토큰(캡션 11)을 가리키게 했다. 그룹 이름(10 → 11)만 1pt 커졌다.

## 공통 컨트롤 (`Styles/ControlStyles.axaml`, `Styles/ControlThemes.axaml`)
- Button·ToggleButton: 최소 높이 28, 안쪽 여백 12,0, 글자는 가로·세로 가운데, 반경 4. `.small` 24, `.large` 32.
- TextBox·ComboBox·NumericUpDown: 최소 높이 28, 세로 가운데. 여러 줄 입력칸(`AcceptsReturn`)은 위쪽 정렬·안쪽 여백 8,6.
- CheckBox·RadioButton: **자체 ControlTheme**(Fluent 기본 템플릿은 상자 크기 20을 템플릿에 박아 둬 스타일로 줄일 수 없었다). 상자 16, 높이 24, 상자와 글자는 세로 가운데, 글자와 상자 사이 6.
- 개별 화면에서 `Padding`·`MinHeight`·`FontSize`를 따로 정하지 않는다. 예외는 위 `.small`처럼 클래스로 표현한다.

## 지키는 법
- XAML: `FontSize="{DynamicResource FontSizeBody}"`, `Margin="{DynamicResource Inset8}"`, `Spacing="{DynamicResource Space8}"`.
- 코드: `FontSize = DesignTokens.FontCaption`.
- `DesignTokenTests.NoHardCodedFontSizes_OutsideAllowList`가 숫자 FontSize 하드코딩을 막는다(Bench 제외).
- 새 컨트롤 종류를 쓰게 되면 `ControlStyles.axaml`에 먼저 규격을 추가한다.

## 아직 치환하지 않은 것
- 목록 안 행 간격 2, 말풍선·알약 모양의 모서리 반경 8 같은 한두 곳의 세부 값, 비대칭 여백(예: `0,4,0,0`)은 규격 간격(4·8)의 조합이라 그대로 뒀다.
- 125/150% 배율·실제 화면 확인은 하지 않았다(헤드리스 렌더만).

헤드리스 렌더 결과: `tokens-after-{ko,en}-{findreplace,comments,search,properties,print}.png`.
