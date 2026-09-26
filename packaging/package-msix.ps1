<#
.SYNOPSIS
  Builds the Microsoft Store package: publish/msix/Vid-Archiver-GUI-<version>.msixbundle (x64 + arm64).

.DESCRIPTION
  Needs the Windows 10/11 SDK (makeappx, makepri, signtool). Identity values come from packaging/msix/store-identity.json.
  Upload the .msixbundle to Partner Center unsigned; the Store signs it.

  -Sign signs it with a self-signed test certificate (created on first use, subject = the Publisher value) so the
  bundle can be installed locally by double-clicking it. Trusting the certificate needs an elevated prompt once:
    Import-Certificate publish\msix\test-cert.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\package-msix.ps1 -Sign
#>
param(
    [string[]] $Arch = @('x64', 'arm64'),
    [switch] $Sign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish\msix'
$work = Join-Path $root 'obj\msix'

# Newest SDK that has makeappx for this machine's architecture.
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\$hostArch\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 | ForEach-Object DirectoryName
if (-not $sdkBin) { throw 'Windows SDK not found (needs makeappx.exe). Install it with: winget install Microsoft.WindowsSDK.10.0.26100' }
$makeappx = Join-Path $sdkBin 'makeappx.exe'
$makepri = Join-Path $sdkBin 'makepri.exe'
$signtool = Join-Path $sdkBin 'signtool.exe'

# Store versions are four numbers and the last one must be 0: 1.2.3-beta → 1.2.3.0.
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$semver = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$numbers = ($semver -split '[-+]')[0].Split('.')
$version = (@($numbers) + @('0', '0', '0'))[0..2] -join '.'
$version += '.0'

$identity = Get-Content (Join-Path $PSScriptRoot 'msix\store-identity.json') -Raw | ConvertFrom-Json
$template = Get-Content (Join-Path $PSScriptRoot 'msix\AppxManifest.xml') -Raw

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, "$work\packages" | Out-Null

foreach ($a in $Arch) {
    Write-Host "== $a" -ForegroundColor Cyan
    $layout = Join-Path $work "layout-$a"
    # Not single-file: MSIX compresses the package itself, and loose files start faster.
    dotnet publish (Join-Path $root 'src\VidArchiverGui.App') -c Release -r "win-$a" --self-contained -o $layout
    if ($LASTEXITCODE) { throw "dotnet publish failed for $a" }

    Copy-Item (Join-Path $PSScriptRoot 'msix\Assets') (Join-Path $layout 'Assets') -Recurse
    $template.Replace('$IDENTITY_NAME$', $identity.IdentityName).
        Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($identity.Publisher)).
        Replace('$PUBLISHER_DISPLAY_NAME$', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
        Replace('$VERSION$', $version).Replace('$ARCH$', $a) | Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

    # resources.pri lets Windows pick the right logo size / unplated taskbar icon.
    $priConfig = Join-Path $work "priconfig-$a.xml"
    & $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
    if ($LASTEXITCODE) { throw 'makepri createconfig failed' }
    & $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
    if ($LASTEXITCODE) { throw 'makepri new failed' }

    & $makeappx pack /d $layout /p (Join-Path $work "packages\VidArchiverGui-$version-$a.msix") /o | Out-Null
    if ($LASTEXITCODE) { throw "makeappx pack failed for $a" }
}

$bundle = Join-Path $out "Vid-Archiver-GUI-$($semver)-store.msixbundle"
& $makeappx bundle /d (Join-Path $work 'packages') /p $bundle /bv $version /o | Out-Null
if ($LASTEXITCODE) { throw 'makeappx bundle failed' }

if ($Sign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $identity.Publisher -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $identity.Publisher -KeyUsage DigitalSignature `
            -FriendlyName 'Vid Archiver GUI MSIX test' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    Export-Certificate -Cert $cert -FilePath (Join-Path $out 'test-cert.cer') | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $bundle
    if ($LASTEXITCODE) { throw 'signtool failed' }
}

Write-Host ''
Write-Host "Built $bundle (version $version)" -ForegroundColor Green
