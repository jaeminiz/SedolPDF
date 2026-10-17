# M3a 검증 결과

Task 4(한글 글꼴 서브셋 임베드·글리프 윤곽선) 기준. 측정은 `tests/PdfEditor.Appearance.Tests/Fonts`의 테스트로 했다(2026-10-17).

## V10 — HarfBuzzSharp 8.3.1.3에서 글리프 윤곽선을 얻을 수 있는가 (결정 A5의 전제)

확인한 것: 화면이 PDF와 같은 글리프로 글자를 그리려면 글리프 윤곽선(획의 경로)이 필요하다.

- **관리(C#) API는 없다.** `HarfBuzzSharp.dll`(net8.0 빌드)에 `DrawGlyph`·`DrawFuncs` 같은 이름이 하나도 없다.
- **네이티브 내보내기는 있다 (통과).** 같은 패키지의 `libHarfBuzzSharp.dll`에 `hb_font_draw_glyph`, `hb_draw_funcs_create`, `hb_draw_funcs_set_move_to/line_to/quadratic_to/cubic_to/close_path_func`, `hb_draw_funcs_make_immutable`가 모두 내보내져 있다.
- **직접 호출로 동작한다 (통과).** S2의 `hb_subset_*`와 같은 방식(`LibraryImport`)으로 `GlyphOutlines`를 만들었다. 콜백은 `UnmanagedCallersOnly` 정적 함수, 모으는 객체는 `GCHandle`로 넘긴다. "가"는 윤곽선이 여러 개(`MoveTo` ≥ 2)이고 `MoveTo` 수와 `ClosePath` 수가 같으며, 좌표는 em 단위(1.0 = 글자 크기, 위쪽이 +)로 -0.3~1.3 안에 들어온다. 공백은 빈 경로다. 2차 곡선은 3차로 바꿔 `CubicTo`만 쓴다(Noto Sans KR OTF는 원래 3차).
- **결론:** A5 대안(Avalonia `GlyphRun`)은 쓰지 않는다. 윤곽선 캐시는 `AppFont` 안에서 gid별로 둔다.
- **검증 필요:** 화면에 실제로 그렸을 때 PDF 렌더(PDFium)와 글자 모양·위치가 같은지는 Task 5·8에서 기준 이미지로 확인한다.

## V11 — 서브셋을 만든 뒤에도 CID가 전체 글꼴과 같은가

확인한 것: 화면은 전체 글꼴로 글자를 배치(글리프 → CID)하고 PDF에는 그 CID를 그대로 쓰므로, HarfBuzz 서브셋이 CFF의 CID를 바꾸면 PDF에서 다른 글자가 나온다.

- **같다 (통과).** "가나다ABC"를 서브셋하면 서브셋 CFF charset에 전체 글꼴에서의 CID가 그대로 들어 있고(`Subset_KeepsCidsOfFullFont`), 서브셋 크기는 전체의 10분의 1 미만이다. Noto Sans KR OTF는 CID 키 방식 CFF라서 서브셋이 글리프 번호는 다시 매겨도 CID는 유지한다.
- **어긋나면 막는다.** `FontEmbedder.Embed`는 서브셋을 만들 때마다 (1) 요청한 모든 글자의 전체 글꼴 CID가 서브셋에 있는지, (2) 배치 결과(`GlyphUse.Cid`)가 서브셋에 있는지, (3) 전체와 서브셋이 같은 방식(CID 키/이름 키)인지를 확인하고 아니면 `InvalidOperationException`을 던진다(`Embed_UnknownCid_Throws`).
- **PDFium 렌더 (통과).** AP에 `<CID 16진> Tj`를 쓰고 `/Resources /Font`에 Embed 결과를 넣으면 PDFium이 비흰색 픽셀로 그린다(`Embed_RendersInPdfium`, 주석을 끄면 0).
- **검증 필요:** Acrobat·PDF-XChange에서 같은 파일이 보이는지는 S1에서 확인한 방식과 같은 객체 구조이지만, M3a 호환성 점검(Task 8 이후)에서 다시 확인한다.

## 동시 사용(S2 크래시 회귀)

- `AppFont`의 Blob은 `FontSubsetter.CreateBlob`(네이티브 메모리 복사)로만 만든다. 모든 HarfBuzz 호출(셰이핑·글리프·폭·윤곽선)은 `lock` 하나로 직렬화한다. 스레드 4개 × 200번 셰이핑·윤곽선·`GC.Collect` 반복에서 크래시 없음(`AppFont_ConcurrentShaping_NoCrash`). `ResourceGuardTests.NoBlobFromStream_InSources` 통과(주석 문구도 해당 이름을 쓰지 않도록 고침).

## 글꼴 라이선스 확인 (Noto Sans KR, OFL 1.1)

- 글꼴 파일(`assets/fonts/NotoSansKR-Regular.otf`, 4.6 MB) 안의 저작권 문구: "Copyright 2014-2021 Adobe (http://www.adobe.com/). Noto is a trademark of Google Inc." — Reserved Font Name 선언은 없다. 포함 허용 값 `OS/2 fsType` = 0(제한 없음).
- OFL은 글꼴을 문서에 포함하는 것과 함께 배포하는 것을 허용하고 글꼴 단독 판매만 막는다. 서브셋을 PDF에 넣는 것은 "문서 포함"이다.
- 배포 문구: `THIRD-PARTY-NOTICES.md`의 Noto Sans KR 항목에 저작권 문구·확인 결과를 적었다. `assets/fonts/OFL.txt`(라이선스 원문)는 앱 출력 폴더와 게시(publish) 폴더에 `assets/fonts/OFL.txt`로 복사된다. **OFL.txt 파일 자체에는 저작권 줄이 없으므로**(원문만 있음) 저작권 문구는 `THIRD-PARTY-NOTICES.md`로 함께 동봉한다 — 두 파일 모두 `build/package.ps1`의 필수 파일 검사에 들어 있다.
- 글꼴 파일 자체도 `PdfEditor.App.csproj`가 출력의 `assets/fonts/`로 복사하고(`NotoSansKR-Regular.otf`), `build/package.ps1` 필수 파일 검사에 추가했다. 설치 파일(Inno Setup)은 publish 폴더를 통째로 담으므로 따로 고칠 것이 없다.
- **검증 필요:** 실제 `build/package.ps1` 실행(Release 게시 + 압축)은 이번에 하지 않았다. 필수 파일 검사 줄만 바꿨고 Debug 출력에서 파일 복사를 확인했다.
