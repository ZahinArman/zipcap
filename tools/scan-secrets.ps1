# Fails (exit 1) if anything that looks like a key, token or personal path is in the repo:
# the files git tracks or is about to commit, AND every past commit. Runs before every push/release.
#   powershell -ExecutionPolicy Bypass -File tools\scan-secrets.ps1
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$bad = $false

# 1) gitleaks, if installed (winget install Gitleaks.Gitleaks): the thorough scan of all history.
$gl = Get-Command gitleaks -ErrorAction SilentlyContinue
if ($gl) {
    $ErrorActionPreference = "Continue" # gitleaks logs to stderr; only its exit code matters
    & $gl.Source git --no-banner --redact --log-level warn . 2>&1 | ForEach-Object { "$_" }   # every commit
    if ($LASTEXITCODE -ne 0) { $bad = $true }
    foreach ($f in (git ls-files --cached --others --exclude-standard)) {                      # files about to be committed
        if (-not (Test-Path $f -PathType Leaf)) { continue }
        & $gl.Source dir --no-banner --redact --log-level warn $f 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { $bad = $true }
    }
    $ErrorActionPreference = "Stop"
} else {
    Write-Output "gitleaks not found; running the built-in patterns only."
}

# 2) Built-in patterns, on tracked + staged files and on full history.
$patterns = @(
    'ntn_[A-Za-z0-9]{20,}',            # Notion integration secret
    'secret_[A-Za-z0-9]{30,}',         # older Notion secret
    '(?<![0-9a-fA-F])[0-9a-f]{40}(?![0-9a-fA-F])', # Todoist API token (40 hex)
    'gh[pousr]_[A-Za-z0-9]{30,}',      # GitHub tokens
    '(?<![0-9a-fA-F-])[0-9a-f]{32}(?![0-9a-fA-F-])', # bare Notion page/database IDs
    'C:\\Users\\[A-Za-z]+'             # personal Windows paths
)
# IndexNow keys are public by design (the site must serve docs/<key>.txt containing <key>),
# so exactly those values are allowed. Any other 32-hex value still fails.
$publicKeys = @(Get-ChildItem docs -Filter *.txt -ErrorAction SilentlyContinue |
    Where-Object { $_.BaseName -match '^[0-9a-f]{32}$' -and (Get-Content -Raw $_.FullName).Trim() -eq $_.BaseName } |
    ForEach-Object { $_.BaseName })
# Email addresses in files (commit trailers like noreply@ are fine).
$emailPattern = '(?<![\w.+-])(?!noreply@)[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z.]{2,}'
$files = git ls-files --cached --others --exclude-standard
foreach ($f in $files) {
    if (-not (Test-Path $f -PathType Leaf)) { continue }
    if ($f -match '\.(png|jpg|jpeg|gif|ico|exe|webp|mp4)$') { continue }
    $text = Get-Content -Raw -LiteralPath $f
    if ($null -eq $text) { continue }
    foreach ($p in $patterns) {
        foreach ($m in [regex]::Matches($text, $p)) {
            if ($publicKeys -contains $m.Value) { continue }
            $v = $m.Value; $show = $v.Substring(0, [Math]::Min(6, $v.Length)) + "…"
            Write-Output "FOUND in ${f}: $show  (pattern: $p)"
            $bad = $true
        }
    }
    foreach ($m in [regex]::Matches($text, $emailPattern)) {
        Write-Output "FOUND email in ${f}: $($m.Value.Substring(0, 3))…"
        $bad = $true
    }
}
# --format= drops the "commit <hash>" lines, which would look like 40-hex tokens.
$history = (git log -p --all --no-color --format= 2>$null) -join "`n"
foreach ($p in $patterns) {
    if ([regex]::Matches($history, $p) | Where-Object { $publicKeys -notcontains $_.Value }) {
        Write-Output "FOUND in git history (pattern: $p). Run: git log -p --all -G '<pattern>'"
        $bad = $true
    }
}

if ($bad) { Write-Output "Secret scan FAILED. Nothing was pushed."; exit 1 }
Write-Output "Secret scan passed."
