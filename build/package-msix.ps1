#requires -Version 7
# SedolPDF MSIX 패키지 만들기 (Microsoft Store 제출용). 기존 package.ps1(zip·설치 파일)과 별개로 병행한다.
#   pwsh build/package-msix.ps1 [-Version 0.3.0] [-IdentityFile build/msix/store-identity.json] [-TestSign]
# 결과: artifacts\SedolPDF-<버전>-win-x64.msix
#   -TestSign : 내 PC 시험 설치용 자체 서명(스토어 제출용 아님, 인증서 신뢰 등록에 관리자 권한 필요)
# 필요: .NET SDK 10, Windows SDK 의 makeappx.exe (없으면 안내만 하고 멈춤 — build/README.md 참고)
# 주의: 이 스크립트는 아직 실행해 보지 않았다 (이 PC에 Windows SDK 없음) — 검증 필요.
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$IdentityFile = '',
    [switch]$TestSign
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Drawing

function Fail([string]$msg) { Write-Host "오류: $msg" -ForegroundColor Red; exit 1 }

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish-msix'
$stage = Join-Path $artifacts 'stage-msix'
$project = Join-Path $root 'src/PdfEditor.App'

# 버전: 3자리 -> 4자리 (마지막 자리는 스토어 예약이라 항상 0)
if (-not $Version) {
    $props = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'Directory.Build.props')
    if ($props -match '<Version>([^<]+)</Version>') { $Version = $Matches[1] } else { Fail 'Directory.Build.props 에서 <Version>을 찾지 못했습니다.' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "버전은 0.3.0 처럼 숫자 3개여야 합니다: $Version" }
$version4 = "$Version.0"

# 신원 값: 파일이 있으면 그 값(Partner Center 가 준 값), 없으면 자리표시자(제출 불가, 시험용)
if (-not $IdentityFile) { $IdentityFile = Join-Path $PSScriptRoot 'msix/store-identity.json' }
$placeholder = $false
if (Test-Path -LiteralPath $IdentityFile) { $id = Get-Content -Raw -Encoding UTF8 $IdentityFile | ConvertFrom-Json }
else {
    $id = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'msix/store-identity.sample.json') | ConvertFrom-Json
    $placeholder = $true
    Write-Host '주의: store-identity.json 이 없어 자리표시자 신원으로 만듭니다 (스토어에 올릴 수 없고 시험용).' -ForegroundColor Yellow
}
if ($TestSign -and -not $placeholder) { Fail '-TestSign 은 자리표시자 신원에서만 씁니다 (실제 신원으로 자체 서명하면 Publisher 가 맞지 않음).' }

function Find-SdkTool([string]$name) {
    $c = (Get-Command $name -ErrorAction SilentlyContinue)?.Source
    if ($c) { return $c }
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        return Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
$makeappx = Find-SdkTool 'makeappx.exe'
if (-not $makeappx) { Fail 'makeappx.exe 를 찾지 못했습니다. Windows SDK 설치가 필요합니다: winget install Microsoft.WindowsSDK.10.0.26100 (build/README.md 참고)' }
Write-Host "makeappx: $makeappx"

# 1. 게시 (zip·설치 파일과 같은 방식: self-contained win-x64)
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 실패 (종료 코드 $LASTEXITCODE)." }
Get-ChildItem -LiteralPath $publish -Recurse -File -Filter *.pdb | Remove-Item -Force

# 2. 스테이지: 게시물 복사. 포터블 표식은 MSIX 에 넣지 않는다 (MSIX 설치 폴더는 읽기 전용, 설정은 %APPDATA% 가상화)
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse -Force
Remove-Item -LiteralPath (Join-Path $stage 'portable.txt') -ErrorAction SilentlyContinue
$required = @('SedolPDF.exe', 'pdfium.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'assets/fonts/OFL.txt', 'assets/fonts/NotoSansKR-Regular.otf', 'assets/fonts/NotoSerifKR-Regular.otf', 'licenses/pdfium/pdfium.txt')
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $stage $_) -PathType Leaf) })
if ($missing.Count -gt 0) { Fail ('필수 파일이 없습니다: ' + ($missing -join ', ')) }

# 3. 아이콘: assets/icon/sedolpdf-1024.png 에서 규격별로 만든다 (가운데 배치, 투명 배경)
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$src = [System.Drawing.Image]::FromFile((Join-Path $root 'assets/icon/sedolpdf-1024.png'))
function New-Logo([string]$name, [int]$w, [int]$h, [double]$fill = 0.8) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [int]([Math]::Min($w, $h) * $fill)
    $g.DrawImage($src, [int](($w - $side) / 2), [int](($h - $side) / 2), $side, $side)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
# 타일은 여백을 두고(0.8), 작은 아이콘·스토어 로고는 꽉 채운다(1.0)
New-Logo 'Square44x44Logo.png' 44 44 1.0
# 작업 표시줄·시작 메뉴: 크기별 + "판 없음"(unplated) 변형. 없으면 Windows가 투명 모서리 뒤에 회색·강조색 판을 깔아 테두리처럼 보인다
foreach ($s in 16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256) {
    New-Logo "Square44x44Logo.targetsize-$s.png" $s $s 1.0
    New-Logo "Square44x44Logo.targetsize-${s}_altform-unplated.png" $s $s 1.0
    New-Logo "Square44x44Logo.targetsize-${s}_altform-lightunplated.png" $s $s 1.0
}
New-Logo 'Square150x150Logo.png' 150 150
New-Logo 'SmallTile.png' 71 71
New-Logo 'Wide310x150Logo.png' 310 150
New-Logo 'LargeTile.png' 310 310
New-Logo 'StoreLogo.png' 50 50 1.0
$src.Dispose()

# 4. 매니페스트
$xml = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'msix/AppxManifest.template.xml')
$xml = $xml.Replace('{{IdentityName}}', $id.IdentityName).Replace('{{Publisher}}', $id.Publisher).
    Replace('{{PublisherDisplayName}}', $id.PublisherDisplayName).Replace('{{Version4}}', $version4)
if ($xml -match '\{\{') { Fail '매니페스트에 채우지 못한 {{자리}}가 남았습니다.' }
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $xml, [System.Text.UTF8Encoding]::new($false))

# 5. 묶기
$msix = Join-Path $artifacts "SedolPDF-$Version-win-x64.msix"
if (Test-Path $msix) { Remove-Item -Force $msix }
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { Fail "makeappx 실패 (종료 코드 $LASTEXITCODE)." }

# 6. (선택) 시험용 자체 서명
if ($TestSign) {
    $signtool = Find-SdkTool 'signtool.exe'
    if (-not $signtool) { Fail 'signtool.exe 를 찾지 못했습니다 (Windows SDK).' }
    $cert = New-SelfSignedCertificate -Type Custom -Subject $id.Publisher -KeyUsage DigitalSignature -FriendlyName 'SedolPDF test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $cer = Join-Path $artifacts 'SedolPDF-test.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { Fail "signtool 실패 (종료 코드 $LASTEXITCODE)." }
    Write-Host "시험 설치: 관리자 PowerShell 에서 Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople 실행 후 msix 더블클릭"
}
$mi = Get-Item $msix
Write-Host ("MSIX: {0} ({1:N1} MB){2}" -f $mi.FullName, ($mi.Length / 1MB), $(if ($placeholder) { ' [자리표시자 신원 - 제출 불가]' } else { '' }))
