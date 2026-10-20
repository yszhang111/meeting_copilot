<#
.SYNOPSIS
  Builds the distributables: an unpackaged, self-contained win-x64 folder, a zip of it, and an installer
  (setup.exe, Inno Setup 6) that installs the same folder with Start menu shortcut and uninstaller.
  No MSIX, certificate, Developer Mode or machine trust changes are required.
.PARAMETER NoInstaller
  Skip setup.exe (otherwise it is built when Inno Setup 6 is installed: winget install --id JRSoftware.InnoSetup -e).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Publish.ps1
#>
param([string]$Configuration = 'Release', [switch]$NoInstaller)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Directory.Build.props' }
$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts "publish\MeetingCopilot-$version-win-x64"
$zip = Join-Path $artifacts "MeetingCopilot-$version-win-x64.zip"
$setup = Join-Path $artifacts "MeetingCopilot-$version-win-x64-setup.exe"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
if (Test-Path $setup) { Remove-Item $setup -Force }

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

if ($NoInstaller) { return }
$iscc = @($env:ISCC,
          (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
          (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
          (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
  Write-Warning 'Inno Setup 6 not found: setup.exe not built (winget install --id JRSoftware.InnoSetup -e, or use -NoInstaller).'
  return
}
& $iscc /Q "/DSourceDir=$out" "/DAppVersion=$version" "/O$artifacts" (Join-Path $root 'tools\installer\ProactiveAI.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Installer: $setup ($size MB)"
