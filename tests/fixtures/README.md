# tests/fixtures

- `freetext-rc-only.pdf` — Acrobat 타자기 글상자 한 개(`/IT /FreeTextTypewriter`, `/DA`는 빨강 `rg`뿐, `/DS` 없음, `/RC` 본문은 Arial 12pt 검정, 첫 span이 맑은 고딕 10pt 굵게(700) #0000FF).
  실제 문서에서 해당 주석의 키 구조만 가져와 새 1쪽 PDF에 다시 만들었고, 글은 중립 문구("Sample note text"), 작성자·NM은 가짜 값이다(고객 자료 없음).
  쓰는 곳: `FormatPreservationTests`(0.12.0 서식 보존).
