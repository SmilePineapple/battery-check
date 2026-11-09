<#
.SYNOPSIS
    Publishes a new version: sets the version number, builds the exe, and creates a GitHub Release with the exe
    and its SHA-256 checksum. Everyone's "Check for updates" button then offers it.
.EXAMPLE
    .\build\Publish.ps1 1.3.1 "Fixed the runtime estimate on Tellus devices"
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$Notes
)
$ErrorActionPreference = 'Stop'
$here = $(if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path })
$root = Split-Path $here -Parent
$app  = Join-Path $here 'App.cs'

# 1. version number in the exe
$t = [IO.File]::ReadAllText($app)
$t = [regex]::Replace($t, 'Assembly(File)?Version\("[\d\.]+"\)', { param($m) "Assembly$($m.Groups[1].Value)Version(""$Version.0"")" })
[IO.File]::WriteAllText($app, $t, (New-Object Text.UTF8Encoding $true))

# 2. build
& (Join-Path $here 'Build.ps1')
$exe = Join-Path $root 'BatteryCheck.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()

# 3. commit, tag and release (the app only accepts a release whose notes carry the matching SHA256 line)
Push-Location $root
$ErrorActionPreference = 'Continue'   # git writes progress to stderr; check exit codes instead
try {
    git add -A
    git commit -m "Release v$Version" -m $Notes -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    git push 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }
    $body = "$Notes`n`nSHA256: $hash"
    gh release create "v$Version" $exe --title "Battery Check $Version" --notes $body
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
} finally { Pop-Location }
Write-Host "Published v$Version (SHA256 $hash)" -ForegroundColor Green
