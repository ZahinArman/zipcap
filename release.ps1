# Builds and publishes a Zipcap release.
#   .\release.ps1 0.2.0 "What changed, one line"
# Steps: set version -> build exe -> build installer -> secret scan -> commit + tag -> push -> GitHub release.
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Notes = "Zipcap $Version"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$repo = "ZahinArman/zipcap"

if (git status --porcelain) { throw "Commit or stash your changes first; releases are built from a clean tree." }
if (git tag -l "v$Version") { throw "v$Version already exists." }

# 1) Version everywhere it appears.
$cs = Get-Content -Raw Zipcap.cs
$cs = $cs -replace 'public const string Version = "[0-9.]+"', "public const string Version = `"$Version`""
$cs = $cs -replace 'Assembly(File)?Version\("[0-9.]+"\)', "Assembly`$1Version(`"$Version.0`")"
[IO.File]::WriteAllText("$PWD\Zipcap.cs", $cs)
$iss = Get-Content -Raw installer\zipcap.iss
$iss = $iss -replace '#define AppVersion "[0-9.]+"', "#define AppVersion `"$Version`""
[IO.File]::WriteAllText("$PWD\installer\zipcap.iss", $iss)

# 2) Exe, 3) installer.
cmd /c build.cmd
if ($LASTEXITCODE -ne 0 -or -not (Test-Path Zipcap.exe)) { throw "Build failed." }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found. winget install JRSoftware.InnoSetup" }
& $iscc /Q installer\zipcap.iss
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

# 4) Secret scan, then commit + tag.
git add -A
powershell -NoProfile -ExecutionPolicy Bypass -File tools\scan-secrets.ps1
if ($LASTEXITCODE -ne 0) { git reset -q; throw "Secret scan failed. Nothing was committed or pushed." }
git commit -q -m "Release v$Version"
git tag -a "v$Version" -m "Zipcap $Version"

# 5) Push and publish. The asset is always named ZipcapSetup.exe so the
#    .../releases/latest/download/ZipcapSetup.exe link on the website never changes.
git push -q origin HEAD --follow-tags
gh release create "v$Version" "dist\ZipcapSetup.exe" --repo $repo --title "Zipcap $Version" --notes $Notes
Write-Output "Released v$Version: https://github.com/$repo/releases/tag/v$Version"
