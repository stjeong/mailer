<#
.SYNOPSIS
  MailClient 를 게시(self-contained)하고 배포용 zip 과 서명된 msix 를 만듭니다.

.EXAMPLE
  ./build/Package.ps1 -Version 1.2.3 -CertificatePath my.pfx -CertificatePassword secret
  # dist/ 에 MailClient-1.2.3-win-x64.zip, MailClient-1.2.3-x64.msix 생성

.NOTES
  인증서를 주지 않으면 이번 실행에만 쓰는 자체 서명 인증서(CN=MailClient)를 만들어 서명하고 .cer 도 dist/ 에 둡니다.
  매니페스트의 Publisher 는 서명 인증서의 Subject 와 같아야 하므로 인증서에서 읽어 채웁니다.
#>
param(
    [Parameter(Mandatory)][string]$Version,          # 1.2.3 (msix 에는 1.2.3.0 으로 들어감)
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$OutDir = 'dist'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$OutDir = [IO.Path]::GetFullPath((Join-Path $root $OutDir))
$work = Join-Path $root 'artifacts'
$publish = Join-Path $work "publish-$Arch"
$layout = Join-Path $work "msix-$Arch"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version 은 major.minor.patch 형식이어야 합니다: $Version" }
$msixVersion = "$Version.0"
$rid = "win-$Arch"

Remove-Item $publish, $layout -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $OutDir | Out-Null

# 1. 게시: .NET 런타임을 함께 넣어 설치 없이 실행되도록
dotnet publish (Join-Path $root 'src/MailClient.csproj') -c Release -r $rid --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "dotnet publish 실패 ($LASTEXITCODE)" }

# 2. zip (포터블)
$zip = Join-Path $OutDir "MailClient-$Version-$rid.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "zip:  $zip"

# 3. Windows SDK 도구 (makeappx, signtool)
$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdkBin) { throw 'Windows SDK 의 makeappx.exe 를 찾을 수 없습니다.' }
$makeappx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
$signtool = Join-Path $sdkBin.FullName 'x64\signtool.exe'

# 4. 서명 인증서
if (-not $CertificatePath) {
    Write-Warning '인증서가 없어 임시 자체 서명 인증서로 서명합니다. 릴리스마다 인증서가 바뀌므로 배포용으로는 고정 인증서를 쓰세요.'
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=MailClient' -KeyUsage DigitalSignature `
        -FriendlyName 'MailClient MSIX (temporary)' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $CertificatePassword = [guid]::NewGuid().ToString('N')
    $CertificatePath = Join-Path $work 'temp-signing.pfx'
    Export-PfxCertificate -Cert $cert -FilePath $CertificatePath `
        -Password (ConvertTo-SecureString $CertificatePassword -AsPlainText -Force) | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}
$pfx = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    [IO.Path]::GetFullPath($CertificatePath), $CertificatePassword)
$publisher = $pfx.Subject
Write-Host "publisher: $publisher"
# 사용자가 '신뢰할 수 있는 사람' 저장소에 넣을 공개 인증서
[IO.File]::WriteAllBytes((Join-Path $OutDir 'MailClient.cer'), $pfx.Export('Cert'))

# 5. msix 레이아웃: 게시 결과 + 매니페스트 + 로고
Copy-Item $publish $layout -Recurse
Copy-Item (Join-Path $PSScriptRoot 'msix/Images') (Join-Path $layout 'Images') -Recurse
$manifest = Get-Content (Join-Path $PSScriptRoot 'msix/AppxManifest.xml') -Raw -Encoding UTF8
$manifest = $manifest.Replace('$VERSION$', $msixVersion).Replace('$ARCH$', $Arch).
    Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($publisher))
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

$msix = Join-Path $OutDir "MailClient-$Version-$Arch.msix"
& $makeappx pack /o /d $layout /p $msix
if ($LASTEXITCODE) { throw "makeappx 실패 ($LASTEXITCODE)" }

# 6. 서명 (타임스탬프를 넣어 인증서가 만료된 뒤에도 서명이 유효하도록)
& $signtool sign /fd SHA256 /f ([IO.Path]::GetFullPath($CertificatePath)) /p $CertificatePassword `
    /tr http://timestamp.digicert.com /td SHA256 $msix
if ($LASTEXITCODE) { throw "signtool 실패 ($LASTEXITCODE)" }
Write-Host "msix: $msix"
