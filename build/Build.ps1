<#
.SYNOPSIS
    Builds BatteryCheck.exe: one file with the native dashboard (App.cs) and the embedded analysis script.
    Uses the C# compiler that ships with Windows (.NET Framework 4.x), so no extra tools are needed.
    Run it again after changing BatteryCheck.ps1 or App.cs.
#>
param(
    [string]$Out,        # default: BatteryCheck.exe in the project folder
    [switch]$AsInvoker   # test build that does not ask for admin rights (advanced tests will then fail)
)
$ErrorActionPreference = 'Stop'
$here = $(if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path })
$root = Split-Path $here -Parent
$out  = $(if ($Out) { [IO.Path]::GetFullPath($Out) } else { Join-Path $root 'BatteryCheck.exe' })

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'C# compiler not found (.NET Framework 4.x is required).' }

# ---- icon: a battery glyph, drawn at several sizes and written as a PNG-based .ico ----
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $here 'app.ico'
$sizes = 16, 24, 32, 48, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $u = $s / 32.0
    $body = New-Object Drawing.Drawing2D.GraphicsPath
    $x = 2 * $u; $y = 8 * $u; $w = 25 * $u; $h = 16 * $u; $r = 3 * $u
    $body.AddArc($x, $y, 2*$r, 2*$r, 180, 90); $body.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $body.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90); $body.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90); $body.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 47, 95, 179))), $body)
    $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 47, 95, 179))), 27.5*$u, 13*$u, 2.5*$u, 6*$u)
    $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::White)), 4.5*$u, 10.5*$u, 20*$u, 11*$u)
    $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 31, 138, 76))), 6*$u, 12*$u, 12*$u, 8*$u)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    ,$ms.ToArray()
}
$fs = [IO.File]::Create($ico); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = $(if ($s -ge 256) { 0 } else { $s })
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Close()

# ---- compile ----
$manifest = Join-Path $here 'app.manifest'
if ($AsInvoker) {
    $manifest = Join-Path $env:TEMP 'batterycheck-asinvoker.manifest'
    (Get-Content (Join-Path $here 'app.manifest') -Raw).Replace('requireAdministrator', 'asInvoker') | Set-Content $manifest -Encoding UTF8
}
# Only the analysis script is embedded; the dashboard is native WinForms code in App.cs.
$fw = Split-Path $csc -Parent
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll' | ForEach-Object { "/reference:`"$(Join-Path $fw $_)`"" }
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:`"$out`"",
             "/win32icon:`"$ico`"", "/win32manifest:`"$manifest`"",
             "/resource:`"$(Join-Path $root 'BatteryCheck.ps1')`",BatteryCheck.ps1") + $refs + @("`"$(Join-Path $here 'App.cs')`"")
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
Write-Host ("Built {0} ({1:N0} KB)" -f $out, ((Get-Item $out).Length / 1KB)) -ForegroundColor Green
