; SedolPDF 설치 파일 (Inno Setup 6)
; 직접 컴파일하지 말고 `pwsh build/package.ps1` 로 만드세요 (인자를 넘겨 줍니다).
;   /DAppVersion=0.1.0  /DSourceDir=<publish 폴더>  /DOutputDir=<artifacts 폴더>
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

[Setup]
; AppId는 업그레이드 판단용입니다. 한 번 정했으면 절대 바꾸지 마세요.
AppId={{837C545E-F832-428B-9C15-765B1772CC34}
AppName=SedolPDF
AppVersion={#AppVersion}
AppVerName=SedolPDF {#AppVersion}
AppPublisher=Park Jaemin, Park Sehyeon
AppCopyright=Copyright (c) 2026 Park Jaemin, Park Sehyeon
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\SedolPDF
DefaultGroupName=SedolPDF
DisableProgramGroupPage=yes
; 기본은 현재 사용자용(관리자 권한 불필요). 설치 처음 화면에서 "모든 사용자용"을 고를 수 있습니다.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; MIT는 고지만 보여 주면 되므로 동의 클릭이 없는 안내 화면(InfoBeforeFile)을 씁니다.
; LICENSE.txt는 루트 LICENSE의 사본이며 UTF-8 BOM이 있어야 한글이 깨지지 않습니다 (package.ps1이 두 파일이 같은지 검사).
InfoBeforeFile=LICENSE.txt
OutputDir={#OutputDir}
OutputBaseFilename=SedolPDF-{#AppVersion}-setup-win-x64
UninstallDisplayName=SedolPDF
UninstallDisplayIcon={app}\SedolPDF.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
; 포터블 표식 파일이 설치 폴더에 있으면 설치판이 포터블로 동작하므로 설치 파일에는 절대 넣지 않습니다.

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked
Name: "pdfassoc"; Description: "PDF 파일의 ""연결 프로그램""에 SedolPDF 추가 (기본 프로그램은 바꾸지 않음)"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable.txt"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SedolPDF"; Filename: "{app}\SedolPDF.exe"
Name: "{autodesktop}\SedolPDF"; Filename: "{app}\SedolPDF.exe"; Tasks: desktopicon

[Registry]
; PDF 연결(선택): "연결 프로그램" 목록에만 추가합니다. 기본 프로그램은 사용자가 Windows 설정에서 직접 고릅니다.
Root: HKA; Subkey: "Software\Classes\Applications\SedolPDF.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "SedolPDF"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\Applications\SedolPDF.exe\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\Applications\SedolPDF.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SedolPDF.exe"" ""%1"""; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\SedolPDF.Document"; ValueType: string; ValueName: ""; ValueData: "PDF 문서 (SedolPDF)"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\SedolPDF.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\SedolPDF.exe,0"; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\SedolPDF.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SedolPDF.exe"" ""%1"""; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "SedolPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassoc

[Run]
Filename: "{win}\notepad.exe"; Parameters: """{app}\THIRD-PARTY-NOTICES.md"""; Description: "타사 고지(사용한 오픈소스 목록) 보기"; Flags: postinstall skipifsilent unchecked
Filename: "{app}\SedolPDF.exe"; Description: "SedolPDF 실행"; Flags: postinstall nowait skipifsilent

; [UninstallDelete] 는 일부러 두지 않습니다: 제거해도 사용자 설정(%APPDATA%\SedolPDF)과 복구 폴더(%LOCALAPPDATA%\SedolPDF\Recovery)는 남깁니다.
