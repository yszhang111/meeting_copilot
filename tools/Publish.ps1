<#
.SYNOPSIS
  Builds the distributable: an unpackaged, self-contained win-x64 folder and zip.
  No MSIX, certificate, Developer Mode or machine trust changes are required.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Publish.ps1
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = '0.1.0'
$out = Join-Path $root "artifacts\publish\ProactiveAI-$version-win-x64"
$zip = Join-Path $root "artifacts\ProactiveAI-$version-win-x64.zip"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish (Join-Path $root 'src\ProactiveAI.Desktop\ProactiveAI.Desktop.csproj') `
  -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 `
  -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $root 'README.md') $out -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'tools\Set-ApiKey.ps1') $out
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Published: $out"
Write-Host "Zip: $zip ($size MB)"
