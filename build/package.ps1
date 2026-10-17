#requires -Version 7
# SedolPDF 배포 패키지 만들기 (PowerShell 7)
#   pwsh build/package.ps1 [-Version 0.1.0] [-SkipInstaller]
# 결과: artifacts\SedolPDF-<버전>-portable-win-x64.zip (+ Inno Setup이 있으면 설치 파일)
[CmdletBinding()]
param(
    [string]$Version = '',
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Fail([string]$msg) {
    Write-Host "오류: $msg" -ForegroundColor Red
    exit 1
}

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$project = Join-Path $root 'src/PdfEditor.App'

# 버전: 지정하지 않으면 Directory.Build.props 의 <Version>
if (-not $Version) {
    $props = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'Directory.Build.props')
    if ($props -match '<Version>([^<]+)</Version>') { $Version = $Matches[1] }
    else { Fail 'Directory.Build.props 에서 <Version>을 찾지 못했습니다. -Version 으로 지정하세요.' }
}
Write-Host "== SedolPDF $Version 패키지 만들기 =="

# 1. 게시(publish)
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $artifacts | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 가 실패했습니다 (종료 코드 $LASTEXITCODE)." }

# .pdb(빌드 경로가 들어 있고 용량만 큼)는 패키지에서 뺀다
Get-ChildItem -LiteralPath $publish -Recurse -File -Filter *.pdb | Remove-Item -Force

# 2. 필수 파일 검사 (하나라도 없으면 중단)
$required = @('SedolPDF.exe', 'pdfium.dll', 'THIRD-PARTY-NOTICES.md', 'assets/fonts/OFL.txt')
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publish $_) -PathType Leaf) })
if ($missing.Count -gt 0) { Fail ("패키지에 꼭 필요한 파일이 없습니다: " + ($missing -join ', ')) }
$srcLic = @(Get-ChildItem -LiteralPath (Join-Path $root 'assets/licenses/pdfium') -File)
$pubLicDir = Join-Path $publish 'licenses/pdfium'
$pubLic = if (Test-Path $pubLicDir) { @(Get-ChildItem -LiteralPath $pubLicDir -File) } else { @() }
if ($srcLic.Count -le 0) { Fail 'assets/licenses/pdfium 에 라이선스 파일이 하나도 없습니다.' }
if ($pubLic.Count -ne $srcLic.Count) { Fail "PDFium 라이선스 파일 개수가 맞지 않습니다: 저장소 $($srcLic.Count)개, 패키지 $($pubLic.Count)개." }
$diff = Compare-Object ($srcLic.Name | Sort-Object) ($pubLic.Name | Sort-Object)
if ($diff) { Fail ('PDFium 라이선스 파일 이름이 저장소와 패키지에서 다릅니다: ' + (($diff.InputObject) -join ', ')) }
Write-Host "필수 파일 검사 통과 (라이선스 $($pubLic.Count)개)"

# 3. 스모크 테스트: 한글·공백 경로에서 --bench 실행
function New-MinimalPdf([string]$path) {
    $objs = @(
        '<< /Type /Catalog /Pages 2 0 R >>',
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
        $null,
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
    )
    $stream = 'BT /F1 24 Tf 72 720 Td (SedolPDF smoke test) Tj ET'
    $objs[3] = "<< /Length $($stream.Length) >>`nstream`n$stream`nendstream"
    $sb = [System.Text.StringBuilder]::new("%PDF-1.4`n")
    $offsets = @()
    for ($i = 0; $i -lt $objs.Count; $i++) {
        $offsets += $sb.Length
        [void]$sb.Append("$($i + 1) 0 obj`n$($objs[$i])`nendobj`n")
    }
    $xref = $sb.Length
    [void]$sb.Append("xref`n0 $($objs.Count + 1)`n0000000000 65535 f `n")
    foreach ($o in $offsets) { [void]$sb.Append(('{0:D10} 00000 n ' -f $o) + "`n") }
    [void]$sb.Append("trailer`n<< /Size $($objs.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [System.IO.File]::WriteAllBytes($path, [System.Text.Encoding]::ASCII.GetBytes($sb.ToString()))
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sedolpdf-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$smokeApp = Join-Path $smokeRoot '한글 폴더/PDF 에디터'
try {
    New-Item -ItemType Directory -Force $smokeApp | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $smokeApp -Recurse -Force
    $pdf = Join-Path $smokeRoot '테스트 문서.pdf'
    $json = Join-Path $smokeRoot '결과.json'
    New-MinimalPdf $pdf
    $exe = Join-Path $smokeApp 'SedolPDF.exe'
    $p = Start-Process -FilePath $exe -ArgumentList @('--bench', "`"$pdf`"", '--out', "`"$json`"") -PassThru
    if (-not $p.WaitForExit(120000)) { $p.Kill(); Fail '스모크 테스트가 120초 안에 끝나지 않았습니다.' }
    if ($p.ExitCode -ne 0) { Fail "스모크 테스트 실패: SedolPDF.exe --bench 종료 코드 $($p.ExitCode)." }
    if (-not (Test-Path -LiteralPath $json)) { Fail '스모크 테스트 실패: 결과 JSON 파일이 만들어지지 않았습니다.' }
    $null = Get-Content -Raw -Encoding UTF8 -LiteralPath $json | ConvertFrom-Json
    Write-Host '스모크 테스트 통과 (한글·공백 경로에서 실행, 종료 코드 0, JSON 생성)'
}
finally {
    if (Test-Path $smokeRoot) { Remove-Item -Recurse -Force $smokeRoot -ErrorAction SilentlyContinue }
}

# 4. 포터블 zip
$stage = Join-Path $artifacts 'stage-portable'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse -Force
Set-Content -LiteralPath (Join-Path $stage 'portable.txt') -Encoding UTF8 -Value '이 파일이 SedolPDF.exe 옆에 있으면 포터블 모드입니다: 설정과 자동 복구 파일을 이 폴더 아래 data 폴더에 저장합니다. 일반(설치) 모드로 쓰려면 이 파일을 지우세요.'
$zip = Join-Path $artifacts "SedolPDF-$Version-portable-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$zi = Get-Item $zip
Write-Host ("포터블 zip: {0} ({1:N1} MB)" -f $zi.FullName, ($zi.Length / 1MB))

# 5. 설치 파일 (Inno Setup)
if ($SkipInstaller) { Write-Host '-SkipInstaller: 설치 파일은 건너뜀'; exit 0 }
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $iscc) { Write-Host 'Inno Setup이 없어 설치 파일은 건너뜀'; exit 0 }
$iss = Join-Path $PSScriptRoot 'installer/PdfEditor.iss'
if (-not (Test-Path $iss)) { Write-Host "Inno Setup 스크립트($iss)가 아직 없어 설치 파일은 건너뜀"; exit 0 }
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$artifacts" $iss
if ($LASTEXITCODE -ne 0) { Fail "Inno Setup 컴파일이 실패했습니다 (종료 코드 $LASTEXITCODE)." }
Write-Host '설치 파일 만들기 완료'
