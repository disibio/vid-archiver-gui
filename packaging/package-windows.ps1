<#
.SYNOPSIS
  Builds the standalone Windows downloads:
    publish/windows/Vid-Archiver-GUI-<version>-win-x64-portable.zip    (Intel/AMD PCs)
    publish/windows/Vid-Archiver-GUI-<version>-win-arm64-portable.zip  (ARM PCs, e.g. Snapdragon)

.DESCRIPTION
  Each zip holds one folder with the single-file VidArchiverGui.exe, the license files and portable.txt, so the
  app keeps its settings in a data folder beside the exe.
  The exe isn't code-signed, so SmartScreen may warn the first time it's run.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\package-windows.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish\windows'

$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

foreach ($rid in 'win-x64', 'win-arm64') {
    $name = "Vid-Archiver-GUI-$version-$rid-portable"
    $stage = Join-Path $out $name

    dotnet publish (Join-Path $root 'src\VidArchiverGui.App') -c Release -r $rid --self-contained -p:PublishSingleFile=true -o $stage
    if ($LASTEXITCODE) { throw "dotnet publish failed for $rid" }

    # Only the exe and the license files; nothing else (e.g. a stray .pdb) belongs in the download.
    Get-ChildItem $stage | Where-Object Name -notin 'VidArchiverGui.exe', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.txt' | Remove-Item -Recurse -Force
    New-Item (Join-Path $stage 'portable.txt') -ItemType File | Out-Null
    # Entries are added one by one with "/" paths: under Windows PowerShell 5.1 both Compress-Archive and
    # ZipFile.CreateFromDirectory write "\" into the zip's paths, which macOS/Linux unzippers misread.
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $out "$name.zip"), 'Create')
    try {
        foreach ($file in Get-ChildItem $stage -File) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "$name/$($file.Name)", 'Optimal') | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }
    Remove-Item $stage -Recurse -Force
}

Write-Host ''
Get-ChildItem $out -Filter *.zip | ForEach-Object { Write-Host "Built $($_.FullName)" -ForegroundColor Green }
