<#
.SYNOPSIS
  Builds the standalone Windows download: publish/windows/Vid-Archiver-GUI-<version>-win-x64.zip.

.DESCRIPTION
  The zip holds one folder with the single-file VidArchiverGui.exe and the license files next to it.
  The exe isn't code-signed, so SmartScreen may warn the first time it's run.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File packaging\package-windows.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish\windows'

$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$name = "Vid-Archiver-GUI-$version-win-x64"
$stage = Join-Path $out $name

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\VidArchiverGui.App') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $stage
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# Only the exe and the license files; nothing else (e.g. a stray .pdb) belongs in the download.
Get-ChildItem $stage | Where-Object Name -notin 'VidArchiverGui.exe', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.txt' | Remove-Item -Recurse -Force
# Entries are added one by one with "/" paths: under Windows PowerShell 5.1 both Compress-Archive and
# ZipFile.CreateFromDirectory write "\" into the zip's paths, which macOS/Linux unzippers misread.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
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

Write-Host ''
Write-Host "Built $(Join-Path $out "$name.zip")" -ForegroundColor Green
