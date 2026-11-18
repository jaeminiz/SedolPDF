# 0.13.0 출시 결과 (2026-11-18)

## 범위
- CAD 도면 표제란(DATE, REV 등)의 원래 글꼴 한 줄 저장과 칸 간격 유지.
- 글자별 글꼴 대체: 고른 글꼴에 없는 글자만 Noto로 그림(주석·글 줄 고치기).
- 저장 창 단축키 Y/N/C/E/L, 권한 제한 경고 띠 끄기, 저장 중 닫기 재질문 수정, 긴 탭 이름 '•' 잘림 수정.
- 저장 수정: 경로 안의 상태 연산자 허용, 겉모양 검사에 걸리면 확인 후 저장.
- 설계 문서: 폼 XObject 글 편집(보류), 표제란 저장.

## 검증
- 병합 후 `dotnet test PdfEditor.sln` 전체: 실패는 알려진 `V16ObjectMapSpike` 1건뿐(0.12.0 main에서도 실패). App.Tests 999, Document 451, Appearance 283, TextEdit 279(+1 실패) 등 나머지 통과.
- 사용자 손 시험 통과(2026-11-18): (a) 영문 Arial·한글 Noto 글자별 대체, (b) 표제란 날짜 수정 후 칸 간격 유지, (c) 저장 직후 닫을 때 저장 창이 다시 뜨지 않음.

## 알려진 한계
- 저장이 느림(33쪽 5.9MB 도면에서 오래 걸림, 다음 버전에서 개선).
- 폼 XObject 안 글자는 편집되지 않음.
- 칸 중간에서 글자 수가 바뀌면 같은 칸 뒤 글자가 밀림.

## 패키지 (`artifacts/`)
| 파일 | 크기(byte) | SHA-256 |
|---|---|---|
| SedolPDF-0.13.0-setup-win-x64.exe | 47463537 | 4bf3764177914e55ba4095535bc4b6c63417694cad96d43c4ecbfb3f76181e14 |
| SedolPDF-0.13.0-portable-win-x64.zip | 64314871 | 4235ea05aa8cae2d5daea144ee862e95a8f5acda9c0b077555dbc541cbd04ab3 |
| SedolPDF-0.13.0-win-x64.msix | 64190605 | 31e99a8655934de6193cd3a7013bd01d11d0af223d0b35fe343ff9cc09ca29d1 |

MSIX는 메인 폴더에서 만들어 `store-identity.json`을 썼고, Identity Name `ParkJaemin.SedolPDF`, Version `0.13.0.0`.
