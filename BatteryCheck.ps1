<#
.SYNOPSIS
    Battery Health Check - decides objectively whether a laptop battery is worn out,
    or whether short battery life is caused by power consumption (software / configuration).

.DESCRIPTION
    Basic mode (no admin needed) uses `powercfg /batteryreport`:
      - design vs full charge capacity, wear %, cycle count, capacity trend
      - real measured power draw while in use on battery, and standby drain
      - runtime projection: current battery vs. a new battery
      - split of the runtime shortfall into "battery wear" vs "power consumption"

    Advanced mode (-Advanced, needs admin) additionally uses `powercfg /sleepstudy`:
      - standby sessions on battery: drain rate and hardware low-power (DRIPS) residency
      - devices / components that keep the system awake during standby (top blockers)
      - apps and services that used the most energy while on battery

    Output: an HTML report (printable / attachable to a repair ticket), a JSON file
    with the raw verdict for logging, and a short summary in the console.

.EXAMPLE
    .\BatteryCheck.ps1
    .\BatteryCheck.ps1 -Advanced
    .\BatteryCheck.ps1 -BatteryReportXml .\customer\battery.xml -SleepStudyXml .\customer\sleep.xml
#>
[CmdletBinding()]
param(
    # Also run and analyse powercfg /sleepstudy (requires administrator rights; the script elevates itself).
    [switch]$Advanced,
    # Analyse an existing battery report XML (powercfg /batteryreport /xml) instead of generating one.
    [string]$BatteryReportXml,
    # Analyse an existing sleep study XML (powercfg /sleepstudy /xml). Implies -Advanced.
    [string]$SleepStudyXml,
    # Where reports are written (default: a 'Reports' folder next to this script).
    [string]$OutputDir,
    # How many days of usage history to analyse (sleepstudy max is 28).
    [ValidateRange(1, 28)][int]$Days = 14,
    # Days covered by the sleep study. Kept short on purpose: powercfg /sleepstudy can take minutes on laptops
    # with a lot of logged history, and the most recent standby sessions are the ones that matter.
    [ValidateRange(1, 28)][int]$SleepStudyDays = 7,
    # Do not open the HTML report when done.
    [switch]$NoOpen,
    # JSON file with ticket details stored with the result: { Ticket, Customer, Technician, Notes }
    [string]$MetaFile,
    # Run without opening the report or waiting for a key (used by the dashboard).
    [switch]$Unattended,
    # Internal: keep the elevated window open at the end (set when the script re-launches itself as admin).
    [switch]$PauseAtEnd
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty in parameter defaults under Windows PowerShell 5.1 -File, so resolve it here
$scriptDir = $(if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path })
if (-not $OutputDir) { $OutputDir = Join-Path $scriptDir 'Reports' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if ($Unattended) { $NoOpen = $true; $PauseAtEnd = $false }
$scriptPath = $(if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path })

trap {
    Write-Host ''
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkGray
    if ($PauseAtEnd) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

# ---------------------------------------------------------------------------------------------
# Thresholds - tune these to your own repair policy
# ---------------------------------------------------------------------------------------------
$Config = @{
    # Battery health = Full Charge Capacity / Design Capacity
    HealthOkPct          = 80     # >= : battery in good condition
    HealthFairPct        = 75     # >= : normal ageing, no replacement needed
    HealthReplacePct     = 65     # <  : replacement recommended regardless of other factors
                                  # between Replace and Fair: "worn" - replacement justified when it is the main cause

    CycleHigh            = 500    # typical rated life of a laptop pack is 300-1000 cycles
    CycleVeryHigh        = 800
    ExpectedWearPerCycle = 0.04   # % capacity lost per cycle for a healthy Li-ion pack (~20% at 500 cycles)
    AbnormalWearMarginPct= 15     # health this far below the expectation for its cycle count = abnormal

    # Power draw while in use on battery (W). Light office use on a typical 13-16" laptop is 6-12 W.
    # Raise these for gaming / workstation laptops.
    ActiveReferenceW     = 10     # "normal" consumption used for the runtime comparison
    ActiveElevatedW      = 13     # above: elevated
    ActiveHighW          = 20     # above: high

    # Standby drain on battery (% of full charge per hour)
    StandbyElevatedPctH  = 1.0
    StandbyHighPctH      = 2.5
    DripsLowPct          = 80     # modern standby: hardware low-power residency below this = system is not really sleeping

    # Minimum data needed before a consumption verdict is given
    MinActiveMinutes     = 30
    MinActiveW           = 1.0     # 'in use' sessions below this are not real use (screen off, powered off or hibernated)
    MinStandbyMinutes    = 60
    MinSessionSeconds    = 300
    ShortBurstMinutes    = 30     # 'kept awake' standby sessions shorter than this are reported but do not decide the verdict
}
$ShortBurstHours = $Config.ShortBurstMinutes / 60

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------
function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Enc([object]$s) { if ($null -eq $s) { return '' } return [System.Net.WebUtility]::HtmlEncode([string]$s) }

function Get-Nodes($doc, [string]$name) { return @($doc.SelectNodes("//*[local-name()='$name']")) }

function Get-Attr($node, [string]$name) {
    $a = $node.Attributes[$name]
    if ($a) { return $a.Value }
    $c = $node.SelectSingleNode("*[local-name()='$name']")
    if ($c) { return $c.InnerText }
    return $null
}

function Num($v) {
    if ($null -eq $v -or "$v" -eq '') { return 0.0 }
    $d = 0.0
    if ([double]::TryParse("$v", [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return 0.0
}

# Sum / max / min of a property over a list. Measure-Object returns no Sum on an empty list, which
# Set-StrictMode turns into "The property 'Sum' cannot be found", so empty lists give 0 here instead.
function Sum-Of($items, [string]$prop) {
    $t = 0.0
    foreach ($i in @($items)) { if ($null -ne $i) { $t += Num $(if ($prop) { $i.$prop } else { $i }) } }
    return $t
}
function Max-Of($items, [string]$prop) {
    $v = @(@($items) | Where-Object { $null -ne $_ } | ForEach-Object { Num $_.$prop })
    if ($v.Count -eq 0) { return 0.0 }
    return ($v | Measure-Object -Maximum).Maximum
}
function Min-Of($items, [string]$prop) {
    $v = @(@($items) | Where-Object { $null -ne $_ } | ForEach-Object { Num $_.$prop })
    if ($v.Count -eq 0) { return 0.0 }
    return ($v | Measure-Object -Minimum).Minimum
}

function Format-Hours([double]$h) {
    if ($h -le 0 -or [double]::IsInfinity($h) -or [double]::IsNaN($h)) { return 'n/a' }
    $ts = [TimeSpan]::FromHours($h)
    if ($ts.TotalHours -ge 48) { return ('{0:N1} days' -f $ts.TotalDays) }
    return ('{0}h {1:00}m' -f [int][math]::Floor($ts.TotalHours), $ts.Minutes)
}

function Format-Pct([double]$v, [int]$dec = 0) { return ("{0:N$dec}%" -f $v) }

function Get-ShortAppName([string]$id) {
    if (-not $id -or $id -eq 'Unknown') { return '(Unattributed)' }
    if ($id -match '\\([^\\]+)$') { return $Matches[1] }                    # \Device\...\foo.exe -> foo.exe
    if ($id -match '^([^_]+)_\d[\d\.]*_[^_]*_[^_]*_[a-z0-9]{10,}$') { return $Matches[1] } # Package_1.2.3_x64__hash or _neutral_neutral_hash -> Package
    return $id
}

$LevelRank = @{ 'UNKNOWN' = -1; 'OK' = 0; 'NORMAL' = 0; 'FAIR' = 1; 'ELEVATED' = 2; 'WORN' = 2; 'HIGH' = 3; 'REPLACE' = 3 }
function Max-Level([string[]]$levels) {
    $best = 'UNKNOWN'
    foreach ($l in $levels) { if ($LevelRank[$l] -gt $LevelRank[$best]) { $best = $l } }
    return $best
}

# ---------------------------------------------------------------------------------------------
# Self-elevate for advanced mode
# ---------------------------------------------------------------------------------------------
if ($SleepStudyXml) { $Advanced = $true }
$needSleepStudyRun = $Advanced -and -not $SleepStudyXml
if ($needSleepStudyRun -and -not (Test-IsAdmin)) {
    Write-Host 'Advanced mode needs administrator rights for powercfg /sleepstudy - requesting elevation...' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scriptPath`"", '-Advanced', '-Days', $Days, '-SleepStudyDays', $SleepStudyDays, '-OutputDir', "`"$OutputDir`"")
    if ($BatteryReportXml) { $argList += @('-BatteryReportXml', "`"$((Resolve-Path $BatteryReportXml).Path)`"") }
    if ($Unattended) { $argList += '-Unattended' } else { $argList += '-PauseAtEnd' }
    if ($NoOpen) { $argList += '-NoOpen' }
    if ($MetaFile) { $argList += @('-MetaFile', "`"$((Resolve-Path $MetaFile).Path)`"") }
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
        return
    } catch {
        Write-Warning 'Elevation was declined - continuing in basic mode (battery report only).'
        $Advanced = $false; $needSleepStudyRun = $false
    }
}

# ---------------------------------------------------------------------------------------------
# Collect data
# ---------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$workDir = Join-Path $OutputDir "raw-$stamp"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

# Always use the 64-bit powercfg: the 32-bit one cannot load energy.dll and fails on 64-bit Windows
$powercfg = Join-Path $env:WINDIR 'System32\powercfg.exe'
if (-not [Environment]::Is64BitProcess -and [Environment]::Is64BitOperatingSystem) { $powercfg = Join-Path $env:WINDIR 'Sysnative\powercfg.exe' }
if (-not (Test-Path $powercfg)) { $powercfg = 'powercfg.exe' }

# Runs powercfg with a time limit, so a stuck call ends the test with a clear message instead of hanging.
# Returns the console output; throws when the limit is reached.
function Invoke-Powercfg([string]$arguments, [int]$timeoutSec, [string]$what) {
    $tmp = Join-Path $env:TEMP ("batterycheck-pc-" + [Guid]::NewGuid().ToString('N'))
    $p = Start-Process -FilePath $powercfg -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput "$tmp.out" -RedirectStandardError "$tmp.err"
    $null = $p.Handle   # keeps the exit code available after the process ends
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        try { $p.Kill() } catch {}
        throw "$what did not finish within $timeoutSec seconds and was stopped. Windows' own powercfg tool is not responding on this device."
    }
    $text = ''
    foreach ($f in "$tmp.out", "$tmp.err") {
        if (Test-Path $f) { $text += (Get-Content $f -Raw -ErrorAction SilentlyContinue); Remove-Item $f -ErrorAction SilentlyContinue }
    }
    return "$text".Trim()
}
# Finds short wake-ups on battery in the battery report: the laptop was in standby for at least 30 min, was
# 'in use' for a few minutes and went straight back to standby. Many of those in a row, often at night, means
# it is waking itself up (nobody opens the lid every 1-2 hours for 3 minutes). Works without admin and covers
# the whole battery report period, also when the sleep study does not go back far enough.
function Find-SelfWakes([string]$path) {
    $doc = New-Object Xml.XmlDocument
    $doc.Load($path)
    $sleepTypes = @('ConnectedStandby', 'Standby', 'Suspend')
    $rows = @(foreach ($u in $doc.SelectNodes("//*[local-name()='UsageEntry']")) {
        $type = $u.GetAttribute('EntryType')
        $sec = (Num $u.GetAttribute('Duration')) / 1e7
        if ($type -eq 'ReportGenerated' -or $sec -lt 30) { continue }
        [pscustomobject]@{
            Start = [datetime]::Parse($u.GetAttribute('LocalTimestamp'), [Globalization.CultureInfo]::InvariantCulture)
            Type = $type; Dc = ($u.GetAttribute('Ac') -eq '0'); Min = $sec / 60
            mWh = Num $u.GetAttribute('Discharge'); Charge = Num $u.GetAttribute('ChargeCapacity'); FCC = Num $u.GetAttribute('FullChargeCapacity')
        }
    })
    $rows = @($rows | Sort-Object Start)
    $wakes = @()
    for ($i = 1; $i -lt $rows.Count - 1; $i++) {
        $a = $rows[$i]; $p = $rows[$i - 1]; $n = $rows[$i + 1]
        if ($a.Type -ne 'Active' -or -not $a.Dc -or $a.Min -gt 6) { continue }
        if ($p.Type -notin $sleepTypes -or -not $p.Dc -or $p.Min -lt 30) { continue }
        if ($n.Type -notin $sleepTypes -or -not $n.Dc -or $n.Min -lt 20) { continue }
        $wakes += [pscustomobject]@{ Time = $a.Start; Minutes = $a.Min; FCC = $a.FCC
                                     ChargeBefore = $p.Charge; ChargeAfter = $n.Charge - [math]::Max(0, $n.mWh); End = $n.Start.AddMinutes($n.Min) }
    }
    return ,$wakes
}
# Self-wakes only count when there are many: at least 4 on one day and 5 in total (a user may check the laptop briefly now and then)
function Test-SelfWakePattern($wakes) {
    if ($wakes.Count -lt 5) { return $false }
    $perDay = @($wakes | Group-Object { $_.Time.Date } | Sort-Object Count -Descending)
    return ($perDay[0].Count -ge 4)
}

Write-Host 'Step: started'

if (-not $BatteryReportXml) {
    $BatteryReportXml = Join-Path $workDir 'batteryreport.xml'
    Write-Host 'Running powercfg /batteryreport ...'
    $pcOut = Invoke-Powercfg "/batteryreport /xml /output `"$BatteryReportXml`" /duration $Days" 120 'powercfg /batteryreport'
    if (-not (Test-Path $BatteryReportXml)) { throw "powercfg /batteryreport failed: $(if ($pcOut) { $pcOut } else { 'no report was created' })" }
    Write-Host 'Step: battery report html'
    try { $null = Invoke-Powercfg "/batteryreport /output `"$(Join-Path $workDir 'batteryreport.html')`" /duration $Days" 120 'powercfg /batteryreport (HTML copy)' }
    catch { Write-Warning "$_" }   # the HTML copy is only a convenience
}
if ($needSleepStudyRun) {
    $SleepStudyXml = Join-Path $workDir 'sleepstudy.xml'
    # powercfg /sleepstudy gets much slower with more history (measured: 3 days 8 s, 7 days 41 s), so start
    # short and only look further back when the short window has too few standby sessions on battery.
    # Only the XML is generated; a second (HTML) run would double the slowest step.
    $ssDays = [math]::Min($SleepStudyDays, 3)
    Write-Host "Running powercfg /sleepstudy ($ssDays days) ..."
    try { $pcOut = Invoke-Powercfg "/sleepstudy /xml /output `"$SleepStudyXml`" /duration $ssDays" 300 'powercfg /sleepstudy' }
    catch { $pcOut = "$_"; Remove-Item $SleepStudyXml -ErrorAction SilentlyContinue }
    if ((Test-Path $SleepStudyXml) -and $SleepStudyDays -gt $ssDays) {
        $probe = New-Object Xml.XmlDocument
        $probe.Load($SleepStudyXml)
        # minutes of standby on battery (sessions of 10 min or more, as used by the analysis below)
        $dcSleepMin = 0
        foreach ($n in $probe.SelectNodes("//*[local-name()='ScenarioInstance'][@Type='Sleep'][@Ac='0']")) {
            $m = (Num $n.GetAttribute('Duration')) / 6e8
            if ($m -ge 10) { $dcSleepMin += $m }
        }
        $probe = $null
        # The battery report covers more days: if it shows the laptop waking itself up before the short window,
        # look further back so the sleep study can say why
        $olderWakes = $false
        try {
            $pre = Find-SelfWakes $BatteryReportXml
            $olderWakes = (Test-SelfWakePattern $pre) -and @($pre | Where-Object { $_.Time -lt (Get-Date).AddDays(-$ssDays) }).Count -gt 0
        } catch {}
        if ($dcSleepMin -lt 30 -or $olderWakes) {
            $why = $(if ($olderWakes) { 'the battery report shows wake-ups from standby before that' } else { "only {0:N0} min of standby on battery in the last $ssDays days" -f $dcSleepMin })
            Write-Host "Extending powercfg /sleepstudy to $SleepStudyDays days ($why) ..."
            $short = "$SleepStudyXml.short"
            Copy-Item $SleepStudyXml $short -Force
            try { $pcOut = Invoke-Powercfg "/sleepstudy /xml /output `"$SleepStudyXml`" /duration $SleepStudyDays" 300 'powercfg /sleepstudy' }
            catch { Write-Warning "$_ Using the last $ssDays days instead."; Copy-Item $short $SleepStudyXml -Force }
            Remove-Item $short -ErrorAction SilentlyContinue
        }
    }
    if (-not (Test-Path $SleepStudyXml)) { Write-Warning "powercfg /sleepstudy failed - advanced analysis skipped. $pcOut"; $SleepStudyXml = $null }
}

$meta = [ordered]@{ Ticket = ''; Customer = ''; Technician = ''; Notes = ''; Status = 'Open' }
if ($MetaFile -and (Test-Path $MetaFile)) {
    $m = Get-Content $MetaFile -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($k in @($meta.Keys)) { if ($m.PSObject.Properties[$k] -and $m.$k) { $meta[$k] = [string]$m.$k } }
}

Write-Host 'Step: analysing battery report'
$br = New-Object Xml.XmlDocument
$br.Load((Resolve-Path $BatteryReportXml).Path)

$findings = New-Object System.Collections.Generic.List[object]   # @{ Level; Area; Text }
function Add-Finding([string]$level, [string]$area, [string]$text) { $findings.Add([pscustomobject]@{ Level = $level; Area = $area; Text = $text }) }

# ---------------------------------------------------------------------------------------------
# System information
# ---------------------------------------------------------------------------------------------
$sysNode = (Get-Nodes $br 'SystemInformation') | Select-Object -First 1
$system = [ordered]@{
    Computer     = Get-Attr $sysNode 'ComputerName'
    Manufacturer = Get-Attr $sysNode 'SystemManufacturer'
    Model        = Get-Attr $sysNode 'SystemProductName'
    BIOS         = "$(Get-Attr $sysNode 'BIOSVersion') ($(Get-Attr $sysNode 'BIOSDate'))"
    OSBuild      = Get-Attr $sysNode 'OSBuild'
    Standby      = $(if ((Get-Attr $sysNode 'ConnectedStandby') -eq '1') { 'Modern Standby (S0)' } else { 'Classic sleep (S3)' })
    ReportTime   = Get-Attr ((Get-Nodes $br 'ReportInformation') | Select-Object -First 1) 'LocalScanTime'
}
$wakeConfig = [ordered]@{ ArmedDevices = @(); Timers = @() }
# Only for tests on this PC (not for imported reports). WMI can hang on a damaged repository, so it gets a time limit.
if ($BatteryReportXml -like "$workDir*") {
    Write-Host 'Step: reading system information'
    try {
        $serialTag = (Get-CimInstance Win32_BIOS -OperationTimeoutSec 10 -ErrorAction Stop).SerialNumber
        if ($serialTag) { $system['ServiceTag'] = $serialTag }
    } catch { Write-Warning "Could not read the serial number: $_" }
    try {
        $scheme = Invoke-Powercfg '/getactivescheme' 15 'powercfg /getactivescheme'
        if ($scheme -match '\((.+)\)\s*$') { $system['PowerPlan'] = $Matches[1] }
    } catch { Write-Warning "$_" }
    # Devices allowed to wake the laptop (a network adapter here = Wake-on-LAN) and scheduled wake timers
    try {
        $armed = @((Invoke-Powercfg '/devicequery wake_armed' 15 'powercfg /devicequery') -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^(NONE|GEEN|AUCUN|KEINE)$' })
        $wakeConfig.ArmedDevices = $armed
    } catch { Write-Warning "$_" }
    try {
        $timers = @((Invoke-Powercfg '/waketimers' 15 'powercfg /waketimers') -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $wakeConfig.Timers = @($timers | Where-Object { $_ -match '\\|\.exe|Task|taak' })
    } catch { Write-Warning "$_" }
}

if ($wakeConfig.ArmedDevices.Count -gt 0) {
    $net = @($wakeConfig.ArmedDevices | Where-Object { $_ -match 'Ethernet|Network|Netwerk|Wi-?Fi|Wireless|WLAN|LAN|GbE|Realtek|Killer' })
    $txt = "Devices allowed to wake the laptop: {0}." -f ($wakeConfig.ArmedDevices -join '; ')
    if ($net.Count) { $txt += " This includes a network adapter, so the laptop can be woken over the network (Wake-on-LAN). If the customer does not need that, turn off 'Allow this device to wake the computer' for it in Device Manager (Power Management tab)." }
    Add-Finding $(if ($net.Count) { 'ELEVATED' } else { 'INFO' }) 'Wake-ups' $txt
}
if ($wakeConfig.Timers.Count -gt 0) {
    Add-Finding 'INFO' 'Wake-ups' ("Windows has wake timers set, which can wake the laptop from sleep: {0}. They can be turned off in the power plan (Sleep > Allow wake timers)." -f (($wakeConfig.Timers | Select-Object -First 3) -join ' | '))
}

# ---------------------------------------------------------------------------------------------
# 1. Battery wear
# ---------------------------------------------------------------------------------------------
$batteries = @()
$abnormalWear = $false
foreach ($b in (Get-Nodes $br 'Battery')) {
    $design = Num (Get-Attr $b 'DesignCapacity')
    $full   = Num (Get-Attr $b 'FullChargeCapacity')
    $batteries += [pscustomobject]@{
        Id           = Get-Attr $b 'Id'
        Manufacturer = Get-Attr $b 'Manufacturer'
        Serial       = Get-Attr $b 'SerialNumber'
        Chemistry    = Get-Attr $b 'Chemistry'
        Design       = $design
        Full         = $full
        Cycles       = [int](Num (Get-Attr $b 'CycleCount'))
        HealthPct    = $(if ($design -gt 0) { 100.0 * $full / $design } else { 0 })
    }
}
if ($batteries.Count -eq 0) { throw 'No battery found in the battery report.' }

$designTotal = (Sum-Of $batteries 'Design')
$fullTotal   = (Sum-Of $batteries 'Full')
$maxCycles   = [int](Max-Of $batteries 'Cycles')
$healthPct   = $(if ($designTotal -gt 0) { 100.0 * $fullTotal / $designTotal } else { 0 })
$wearPct     = 100 - $healthPct

if ($designTotal -le 0 -or $fullTotal -le 0) {
    $batteryLevel = 'UNKNOWN'
    Add-Finding 'HIGH' 'Battery' 'The battery does not report its design or full charge capacity. The pack, its controller or the connection may be faulty. Test with a known-good battery.'
} elseif ($healthPct -ge $Config.HealthOkPct)      { $batteryLevel = 'OK' }
elseif ($healthPct -ge $Config.HealthFairPct)    { $batteryLevel = 'FAIR' }
elseif ($healthPct -ge $Config.HealthReplacePct) { $batteryLevel = 'WORN' }
else                                             { $batteryLevel = 'REPLACE' }

switch ($batteryLevel) {
    'OK'      { Add-Finding 'OK'       'Battery' ("The battery holds {0} of its original capacity. This is good." -f (Format-Pct $healthPct)) }
    'FAIR'    { Add-Finding 'FAIR'     'Battery' ("The battery holds {0} of its original capacity. This is normal ageing and no reason to replace it." -f (Format-Pct $healthPct)) }
    'WORN'    { Add-Finding 'ELEVATED' 'Battery' ("The battery holds only {0} of its original capacity and is noticeably worn. Replacing it is justified if wear is the main cause of the complaint." -f (Format-Pct $healthPct)) }
    'REPLACE' { Add-Finding 'HIGH'     'Battery' ("The battery holds only {0} of its original capacity. It is worn out and should be replaced." -f (Format-Pct $healthPct)) }
}

if ($batteries.Count -gt 1) {
    $n = 0
    $parts = foreach ($b in $batteries) { $n++; "battery {0} ({1}): {2}{3}" -f $n, $b.Id, (Format-Pct $b.HealthPct), $(if ($b.Cycles -gt 0) { ", $($b.Cycles) cycles" } else { '' }) }
    Add-Finding 'INFO' 'Battery' ("This laptop has {0} batteries: {1}. The figures above combine them." -f $batteries.Count, ($parts -join '; '))
    $hs = @($batteries | ForEach-Object { $_.HealthPct })
    $spread = ($hs | Measure-Object -Maximum).Maximum - ($hs | Measure-Object -Minimum).Minimum
    if ($spread -ge 10) {
        $worst = @($batteries | Sort-Object HealthPct)[0]
        Add-Finding 'ELEVATED' 'Battery' ("One battery is clearly more worn than the other: '{0}' holds only {1} (a difference of {2:N0} percentage points). Check that battery on its own before replacing both." -f $worst.Id, (Format-Pct $worst.HealthPct), $spread)
    }
}
if ($fullTotal -gt $designTotal * 1.05 -and $designTotal -gt 0) {
    Add-Finding 'ELEVATED' 'Battery' 'The full charge capacity is higher than the design capacity, so the battery gauge is probably miscalibrated. Do one full charge-discharge cycle and run the test again.'
}

if ($maxCycles -le 0) {
    Add-Finding 'INFO' 'Battery' 'The battery does not report a cycle count, so the wear check against cycles is not possible.'
} else {
    if ($maxCycles -ge $Config.CycleVeryHigh) { Add-Finding 'ELEVATED' 'Battery' "The battery has done $maxCycles charge cycles. This is very high: most laptop packs are rated for 300-1000." }
    elseif ($maxCycles -ge $Config.CycleHigh) { Add-Finding 'INFO' 'Battery' "The battery has done $maxCycles charge cycles. This is high, so wear is expected." }
    $expectedHealth = [math]::Max(50.0, 100 - $maxCycles * $Config.ExpectedWearPerCycle)
    if ($designTotal -gt 0 -and $healthPct -lt $expectedHealth - $Config.AbnormalWearMarginPct) {
        $abnormalWear = $true
        Add-Finding 'ELEVATED' 'Battery' ("This is more wear than expected for {0} cycles (a healthy pack would be around {1}). Possible causes are a gauge calibration error, heat damage, long storage at 100% or a defective cell. Recalibrate first, and check the warranty." -f $maxCycles, (Format-Pct $expectedHealth))
    }
}

# Capacity history (weekly / daily entries)
$history = @()
foreach ($h in (Get-Nodes $br 'HistoryEntry')) {
    $d = Num $h.GetAttribute('DesignCapacity'); $f = Num $h.GetAttribute('FullChargeCapacity')
    if ($designTotal -gt 0 -and $d -gt 0 -and [math]::Abs($d - $designTotal) -gt 0.1 * $designTotal) { $d = $designTotal }  # glitched design value
    if ($d -gt 0 -and $f -gt 0 -and $f -le 1.15 * $d) {   # more than 115% is a gauge glitch, not a real value
        $history += [pscustomobject]@{
            Date      = [datetime]::Parse($h.GetAttribute('LocalStartDate'), [Globalization.CultureInfo]::InvariantCulture)
            HealthPct = 100.0 * $f / $d
            Cycles    = [int](Num $h.GetAttribute('CycleCount'))
        }
    }
}
$trendPerMonth = $null
if ($history.Count -ge 3) {
    $first = $history[0]; $last = $history[-1]
    $months = ($last.Date - $first.Date).TotalDays / 30.44
    if ($months -ge 1) {
        $trendPerMonth = ($first.HealthPct - $last.HealthPct) / $months
        if ($trendPerMonth -ge 0.05) {
            Add-Finding 'INFO' 'Battery' ("Capacity has fallen from {0} to {1} over {2:N0} months, about {3:N1} percentage points per month." -f (Format-Pct $first.HealthPct), (Format-Pct $last.HealthPct), $months, $trendPerMonth)
        } else {
            Add-Finding 'INFO' 'Battery' ("Capacity has stayed about the same over {0:N0} months ({1} then, {2} now)." -f $months, (Format-Pct $first.HealthPct), (Format-Pct $last.HealthPct))
        }
    }
    for ($i = 1; $i -lt $history.Count; $i++) {
        $recovers = ($i + 1 -lt $history.Count) -and ($history[$i - 1].HealthPct - $history[$i + 1].HealthPct -lt 4)   # one odd reading, then back to normal
        $recent = $history[$i].Date -ge $history[-1].Date.AddDays(-183)   # older drops are history, not a current fault
        if ($history[$i - 1].HealthPct - $history[$i].HealthPct -ge 8 -and -not $recovers -and $recent) {
            Add-Finding 'ELEVATED' 'Battery' ("Capacity dropped suddenly in the week of {0:yyyy-MM-dd}, from {1} to {2}. A drop this sudden points to a cell fault or a gauge reset, not normal ageing." -f $history[$i].Date, (Format-Pct $history[$i - 1].HealthPct), (Format-Pct $history[$i].HealthPct))
            break
        }
    }
}

# Too little wear for the use: a pack that reports ~100% after hundreds of cycles has a gauge that is not calibrated.
# Its real capacity is then lower, and the charge level falls faster than the gauge expects, which makes the
# measured power use look too high as well. Such a battery cannot be judged until it has been calibrated.
$gaugeSuspect = $false
if ($designTotal -gt 0 -and $maxCycles -gt 0) {
    $flatTxt = ''
    $hc = @($history | Where-Object { $_.Cycles -gt 0 -and $_.Cycles -le $maxCycles })   # skip glitched cycle values
    if ($hc.Count -ge 3) {
        $cycDone = $hc[-1].Cycles - $hc[0].Cycles
        $hpLost  = $hc[0].HealthPct - $hc[-1].HealthPct
        $mo      = ($hc[-1].Date - $hc[0].Date).TotalDays / 30.44
        if ($cycDone -ge 500 -and $hpLost -lt 3) { $flatTxt = (", and the reported capacity stayed the same over the last {0:N0} months while it did {1} cycles" -f $mo, $cycDone) }
    }
    if (($maxCycles -ge $Config.CycleVeryHigh -and $healthPct -ge 95) -or ($flatTxt -and $healthPct -ge 90)) {
        $gaugeSuspect = $true
        foreach ($fd in @($findings | Where-Object { $_.Area -eq 'Battery' -and $_.Text -like 'The battery holds*' })) {
            $fd.Level = 'INFO'; $fd.Text = "The battery reports {0} of its original capacity, but this figure is probably not reliable (see the calibration finding)." -f (Format-Pct $healthPct)
        }
        Add-Finding 'ELEVATED' 'Battery' ("The battery reports {0} health after {1} cycles{2}. Real batteries wear with use, so the battery's gauge has most likely not been calibrated and the real capacity is lower than reported. This also makes the power use look higher than it is, because the charge level drops faster than the gauge expects. Calibrate the battery and run the test again: charge to 100%, use it on battery until it switches off, then charge to 100% without interruption (Lenovo Vantage and Dell/HP tools have a 'battery gauge reset' that does this)." -f (Format-Pct $healthPct), $maxCycles, $flatTxt)
    }
}

# ---------------------------------------------------------------------------------------------
# 2. Measured power consumption (battery report, no admin)
# ---------------------------------------------------------------------------------------------
$active = @(); $standby = @(); $ignoredActive = @()
# On Modern Standby systems 'Suspend' entries are hibernation (near-zero drain); on S3 systems they are sleep.
$standbyTypes = $(if ($system.Standby -like 'Modern*') { @('ConnectedStandby') } else { @('Suspend', 'Standby', 'ConnectedStandby') })
foreach ($u in (Get-Nodes $br 'UsageEntry')) {
    if ($u.GetAttribute('Ac') -ne '0') { continue }
    $sec = (Num $u.GetAttribute('Duration')) / 1e7          # 100-ns ticks
    $mwh = Num $u.GetAttribute('Discharge')
    $fcc = Num $u.GetAttribute('FullChargeCapacity')
    if ($sec -lt $Config.MinSessionSeconds -or $mwh -lt 0) { continue }
    $row = [pscustomobject]@{
        Start = $u.GetAttribute('LocalTimestamp'); Type = $u.GetAttribute('EntryType')
        Hours = $sec / 3600; mWh = $mwh; FCC = $fcc
        W = ($mwh / 1000) / ($sec / 3600)
        PctPerHour = $(if ($fcc -gt 0) { 100 * ($mwh / $fcc) / ($sec / 3600) } else { 0 })
    }
    if ($row.Type -eq 'Active') { if ($row.W -lt $Config.MinActiveW) { $ignoredActive += $row } else { $active += $row } }
    elseif ($row.Type -in $standbyTypes) { $standby += $row }
}

$selfWakes = @(); $selfWakePattern = $false
try { $selfWakes = Find-SelfWakes $BatteryReportXml; $selfWakePattern = Test-SelfWakePattern $selfWakes } catch { Write-Warning "Wake-up check skipped: $_" }

$activeHours  = (Sum-Of $active 'Hours'); if (-not $activeHours)  { $activeHours = 0 }
$ignoredHours = (Sum-Of $ignoredActive 'Hours'); if (-not $ignoredHours) { $ignoredHours = 0 }
if ($ignoredHours -ge 1) {
    Add-Finding 'INFO' 'Consumption' ("Windows logged {0} as 'in use' while the laptop drew almost no power (under {1:N0} W), so it was really off, asleep or hibernated. This time is left out of the consumption and runtime figures." -f (Format-Hours $ignoredHours), $Config.MinActiveW)
}
$standbyHours = (Sum-Of $standby 'Hours'); if (-not $standbyHours) { $standbyHours = 0 }
$activeW = $null; $standbyPctH = $null; $standbyMW = $null
$activeLevel = 'UNKNOWN'; $standbyLevel = 'UNKNOWN'

if ($activeHours * 60 -ge $Config.MinActiveMinutes) {
    $activeW = ((Sum-Of $active 'mWh') / 1000) / $activeHours
    if     ($activeW -gt $Config.ActiveHighW)     { $activeLevel = 'HIGH' }
    elseif ($activeW -gt $Config.ActiveElevatedW) { $activeLevel = 'ELEVATED' }
    else                                          { $activeLevel = 'NORMAL' }
    $msg = "In use on battery ({0} measured), the laptop drew an average of {1:N1} W." -f (Format-Hours $activeHours), $activeW
    switch ($activeLevel) {
        'NORMAL'   { Add-Finding 'OK' 'Consumption' "$msg This is normal." }
        'ELEVATED' { Add-Finding 'ELEVATED' 'Consumption' "$msg This is above normal for light use (about $($Config.ActiveReferenceW) W). Check background apps, screen brightness, the power plan and the dedicated GPU." }
        'HIGH'     { Add-Finding 'HIGH' 'Consumption' "$msg This is high. Unless the customer runs heavy software (games, video editing), something is wasting power: background processes, malware, the dGPU always on, or a 'Best performance' power mode." }
    }
    if ($gaugeSuspect) { $findings[$findings.Count - 1].Text += " Note: this is measured through the battery's gauge, which does not look calibrated, so the real figure may be lower." }
} else {
    Add-Finding 'INFO' 'Consumption' ("There is too little battery use in the last {0} days to measure consumption ({1} measured, at least {2} min needed). Ask the customer to use the laptop on battery for an hour, or run the test at intake before the laptop is charged." -f $Days, (Format-Hours $activeHours), $Config.MinActiveMinutes)
}

$standbySource = 'battery report'
if ($standbyHours * 60 -ge $Config.MinStandbyMinutes) {
    $standbyMW = (Sum-Of $standby 'mWh') / $standbyHours
}

# Runtime projection & attribution of the shortfall
# Windows hibernates or shuts down at the 'critical battery level' (usually 5%), so not all capacity is usable.
$criticalPct = 5
if ($BatteryReportXml -like "$workDir*") {
    try {
        $q = Invoke-Powercfg '/query SCHEME_CURRENT SUB_BATTERY BATLEVELCRIT' 15 'powercfg /query'
        $m = [regex]::Matches($q, '0x([0-9a-fA-F]{8})')
        if ($m.Count -ge 1) { $v = [Convert]::ToInt32($m[$m.Count - 1].Groups[1].Value, 16); if ($v -ge 0 -and $v -le 50) { $criticalPct = $v } }  # last value = on battery (DC)
    } catch {}
}
$usable = 1 - $criticalPct / 100
# Heavier use: the power level that the busiest 20% of the measured time on battery was at or above (time-weighted)
$heavyW = $null
if ($active.Count -ge 2) {
    $sorted = @($active | Sort-Object W -Descending); $acc = 0.0
    foreach ($a in $sorted) { $acc += $a.Hours; if ($acc -ge 0.2 * $activeHours) { $heavyW = $a.W; break } }
}
# Windows' own estimate from the battery report (runtime at full charge, based on its recent drain)
$winEstimate = $null
$fccEst = @($br.SelectNodes("//*[local-name()='RuntimeEstimates']/*[local-name()='FullChargeCapacity']/*[local-name()='ActiveRuntime']"))
if ($fccEst.Count -and $fccEst[0].InnerText) { try { $winEstimate = [Xml.XmlConvert]::ToTimeSpan($fccEst[0].InnerText).TotalHours } catch {} }

$runtime = $null
if ($activeW -and $activeW -gt 0 -and $fullTotal -gt 0) {
    $refW = $Config.ActiveReferenceW
    $runtime = [ordered]@{
        NowAtObserved       = $usable * ($fullTotal / 1000) / $activeW
        NewAtObserved       = $usable * ($designTotal / 1000) / $activeW
        NowAtReference      = $usable * ($fullTotal / 1000) / $refW
        NewAtReference      = $usable * ($designTotal / 1000) / $refW
        NowHeavy            = $(if ($heavyW -and $heavyW -gt $activeW) { $usable * ($fullTotal / 1000) / $heavyW } else { $null })
        HeavyW              = $heavyW
        CriticalPct         = $criticalPct
        WindowsEstimateHours = $winEstimate
        ReferenceW          = $refW
        GainMinutes         = $null
        WearSharePct        = $null
        ConsumptionSharePct = $null
    }
    # Runtime = capacity / power, so the shortfall vs. "new battery at normal consumption" splits cleanly in log space
    $lnWear = [math]::Log($designTotal / $fullTotal)
    $lnCons = [math]::Log($activeW / $refW)
    $pos = [math]::Max(0.0, $lnWear) + [math]::Max(0.0, $lnCons)
    if ($pos -gt 0.01) {
        $runtime.WearSharePct        = 100 * [math]::Max(0.0, $lnWear) / $pos
        $runtime.ConsumptionSharePct = 100 * [math]::Max(0.0, $lnCons) / $pos
    }
    $gain = ($runtime.NewAtObserved - $runtime.NowAtObserved) * 60
    $gainPct = 100 * ($designTotal / $fullTotal - 1)
    $runtime.GainMinutes = $gain
    $g = [math]::Round($gain)
    $meaning = $(if ($gain -le 1) { "A new battery would not make it last longer, so the battery is not the reason for the short runtime." }
                 elseif ($batteryLevel -in 'WORN', 'REPLACE') { "A new battery would add $g minutes, a clear improvement." }
                 elseif ($gainPct -lt 10) { "A new battery would add only $g minutes, so the battery is not the reason for the short runtime." }
                 elseif ($batteryLevel -eq 'FAIR') { "A new battery would add $g minutes, a moderate improvement. The battery shows normal ageing, which alone does not justify a replacement." }
                 else { "A new battery would add $g minutes, a modest improvement. The battery is still in good condition." })
    $heavyTxt = $(if ($runtime.NowHeavy) { " With heavier use, like the busiest fifth of the measured time ({0:N1} W), expect about {1}." -f $heavyW, (Format-Hours $runtime.NowHeavy) } else { '' })
    if ($gaugeSuspect) { $meaning = "These figures rely on the battery's gauge, which does not look calibrated (see the battery findings), so they may be wrong." }
    $newTxt = $(if ($gainPct -lt 10) { "At this power use, even a new battery would only last about {0}." } else { "With a new battery it would last about {0}." }) -f (Format-Hours $runtime.NewAtObserved)
    Add-Finding 'INFO' 'Runtime' ("At the measured average of {0:N1} W, a full charge lasts about {1} (Windows stops at {2}%).{3} {4} {5} With normal consumption ({6} W), a new battery would last {7}." -f $activeW, (Format-Hours $runtime.NowAtObserved), $criticalPct, $heavyTxt, $newTxt, $meaning, $refW, (Format-Hours $runtime.NewAtReference))
}

# ---------------------------------------------------------------------------------------------
# 3. Sleep study (advanced)
# ---------------------------------------------------------------------------------------------
$ss = $null
if ($SleepStudyXml -and (Test-Path $SleepStudyXml)) {
    Write-Host 'Analysing sleep study ...'
    $sd = New-Object Xml.XmlDocument
    $sd.Load((Resolve-Path $SleepStudyXml).Path)

    $sessions = @()
    $blockerAgg = @{}; $appWakeAgg = @{}
    $dcSleepTicks = 0.0

    # Why each standby on battery ended: by the user, by the charger, by itself (maintenance, timers, network)
    $wakeAgg = @{}; $firstWake = $null; $lastWake = $null
    foreach ($s in (Get-Nodes $sd 'ScenarioInstance')) {
        if ($s.GetAttribute('Type') -ne 'Sleep' -or $s.GetAttribute('Ac') -ne '0') { continue }
        $reason = $s.GetAttribute('CsExitReason'); if (-not $reason) { continue }
        $label = $(if ($reason -match '\((.+)\)\s*$') { $Matches[1] } else { $reason })
        $kind = $(if ($label -match 'Power Button|Lid|Input|Resume from Hibernate|User') { 'User' }
                  elseif ($label -match 'AC/DC|Charger') { 'Charger' }
                  elseif ($label -match 'Drain Budget') { 'Drain' }
                  elseif ($label -match 'Hibernate|Shutdown|Sleep') { 'Ended' }
                  else { 'Automatic' })
        if (-not $wakeAgg.ContainsKey($label)) { $wakeAgg[$label] = [pscustomobject]@{ Reason = $label; Kind = $kind; Count = 0; AwakeCount = 0 } }
        $wakeAgg[$label].Count++
        $wt = Num $s.GetAttribute('Duration')
        if ($wt -gt 0 -and (Num $s.GetAttribute('HwLowPowerStateTime')) / $wt -lt 0.5) { $wakeAgg[$label].AwakeCount++ }   # never really slept
        $ts = $s.GetAttribute('LocalTimestamp')
        if (-not $firstWake -or $ts -lt $firstWake) { $firstWake = $ts }
        if (-not $lastWake -or $ts -gt $lastWake) { $lastWake = $ts }
    }

    foreach ($s in (Get-Nodes $sd 'ScenarioInstance')) {
        if ($s.GetAttribute('Type') -ne 'Sleep' -or $s.GetAttribute('Ac') -ne '0') { continue }
        $ticks = Num $s.GetAttribute('Duration')
        if ($ticks / 1e7 -lt 600) { continue }
        $hours = $ticks / 36e9
        $dis   = Num $s.GetAttribute('Discharge')
        $fcc   = Num $s.GetAttribute('FullChargeCapacity'); if ($fcc -le 0) { $fcc = $fullTotal }
        $hw    = Num $s.GetAttribute('HwLowPowerStateTime')
        $sw    = Num $s.GetAttribute('LowPowerStateTime')
        $dcSleepTicks += $ticks
        $top = @()
        foreach ($tb in @($s.SelectNodes("*[local-name()='TopBlockers']/*[local-name()='TopBlocker']"))) {
            $name = $tb.GetAttribute('Name'); $type = $tb.GetAttribute('Type'); $at = Num $tb.GetAttribute('ActiveTime')
            $top += $name
            if (-not $blockerAgg.ContainsKey($name)) { $blockerAgg[$name] = [pscustomobject]@{ Name = $name; Type = $type; Ticks = 0.0; Sessions = 0 } }
            $blockerAgg[$name].Ticks += $at; $blockerAgg[$name].Sessions++
        }
        # Software that woke the system (activators -> blockers -> app)
        foreach ($act in @($s.SelectNodes("*[local-name()='Activators']/*[local-name()='Activator']"))) {
            foreach ($bl in @($act.SelectNodes("*[local-name()='Blockers']/*[local-name()='Blocker']"))) {
                $n = Get-ShortAppName $bl.GetAttribute('Name')
                if (-not $appWakeAgg.ContainsKey($n)) { $appWakeAgg[$n] = [pscustomobject]@{ Name = $n; Via = $act.GetAttribute('Name'); Ticks = 0.0 } }
                $appWakeAgg[$n].Ticks += Num $bl.GetAttribute('ActiveTime')
            }
        }
        # Processor-level reason the chip could not reach its deepest state (e.g. 'NoHwDrips.C2-C3')
        $soc = @($s.SelectNodes("*[local-name()='SocSubsystems']/*") | Sort-Object { -(Num $_.GetAttribute('ScenarioActiveTimePercent')) } | Select-Object -First 1)
        $hwPct = $(if ($ticks -gt 0) { 100 * $hw / $ticks } else { 0 })
        $swPct = $(if ($ticks -gt 0) { 100 * $sw / $ticks } else { 0 })
        # Asleep: hardware in deep sleep. HwOnly: Windows asleep, hardware not. Awake: something kept the system busy.
        $pattern = $(if ($hwPct -ge $Config.DripsLowPct) { 'Asleep' } elseif ($swPct -ge $Config.DripsLowPct -and $hwPct -lt 50) { 'HwOnly' } else { 'Awake' })
        $sessions += [pscustomobject]@{
            Start      = $s.GetAttribute('LocalTimestamp')
            Hours      = $hours
            mWh        = $dis
            mW         = $(if ($hours -gt 0) { $dis / $hours } else { 0 })
            PctPerHour = $(if ($fcc -gt 0 -and $hours -gt 0) { 100 * ($dis / $fcc) / $hours } else { 0 })
            HwDripsPct = $hwPct
            SwDripsPct = $swPct
            Pattern    = $pattern
            SocBlocker = $(if ($soc.Count -and (Num $soc[0].GetAttribute('ScenarioActiveTimePercent')) -ge 10) { "{0} ({1}%)" -f $soc[0].GetAttribute('Name'), $soc[0].GetAttribute('ScenarioActiveTimePercent') } else { '' })
            TopBlocker = ($top | Select-Object -First 2) -join '; '
        }
    }

    # Energy per app while in use on battery (SRUM power estimation, mW averaged over each session)
    $appEnergy = @{}; $dcActiveHoursSs = 0.0
    foreach ($r in (Get-Nodes $sd 'RecentUsageInstance')) {
        if ($r.GetAttribute('Ac') -ne '0' -or $r.GetAttribute('Type') -ne 'Active') { continue }
        $hours = (Num $r.GetAttribute('Duration')) / 36e9
        if ($hours -le 0) { continue }
        $dcActiveHoursSs += $hours
        foreach ($p in @($r.SelectNodes(".//*[local-name()='PowerEstimationRecord']"))) {
            $mw = Num $p.GetAttribute('TotalPowerConsumption')
            if ($mw -le 0) { continue }
            $n = Get-ShortAppName $p.GetAttribute('AppId')
            if (-not $appEnergy.ContainsKey($n)) { $appEnergy[$n] = [pscustomobject]@{ Name = $n; mWh = 0.0; Cpu = 0.0; Display = 0.0; Net = 0.0; Disk = 0.0 } }
            $e = $appEnergy[$n]
            $e.mWh     += $mw * $hours
            $e.Cpu     += (Num $p.GetAttribute('CpuPowerConsumption')) * $hours
            $e.Display += (Num $p.GetAttribute('DisplayPowerConsumption')) * $hours
            $e.Net     += ((Num $p.GetAttribute('NetworkPowerConsumption')) + (Num $p.GetAttribute('MbbPowerConsumption'))) * $hours
            $e.Disk    += (Num $p.GetAttribute('DiskPowerConsumption')) * $hours
        }
    }
    $unattributed = 0.0
    if ($appEnergy.ContainsKey('(Unattributed)')) { $unattributed = $appEnergy['(Unattributed)'].mWh; $appEnergy.Remove('(Unattributed)') }
    $appTotal = (Sum-Of $appEnergy.Values 'mWh'); if (-not $appTotal) { $appTotal = 0 }
    $unattributedPct = $(if ($appTotal + $unattributed -gt 0) { 100 * $unattributed / ($appTotal + $unattributed) } else { 0 })

    $ss = [ordered]@{
        Sessions   = $sessions
        Blockers   = @($blockerAgg.Values | Sort-Object Ticks -Descending | Select-Object -First 8 | ForEach-Object {
                        [pscustomobject]@{ Name = $_.Name; Type = $_.Type; Sessions = $_.Sessions; Hours = $_.Ticks / 36e9; PctOfSleep = $(if ($dcSleepTicks -gt 0) { 100 * $_.Ticks / $dcSleepTicks } else { 0 }) } })
        WakeApps   = @($appWakeAgg.Values | Sort-Object Ticks -Descending | Select-Object -First 8 | ForEach-Object {
                        [pscustomobject]@{ Name = $_.Name; Via = $_.Via; Minutes = $_.Ticks / 6e8 } })
        Apps       = @($appEnergy.Values | Sort-Object mWh -Descending | Select-Object -First 12 | ForEach-Object {
                        [pscustomobject]@{ Name = $_.Name; mWh = $_.mWh; SharePct = $(if ($appTotal -gt 0) { 100 * $_.mWh / $appTotal } else { 0 }); AvgW = $(if ($dcActiveHoursSs -gt 0) { $_.mWh / 1000 / $dcActiveHoursSs } else { 0 }); Main = (@(@{k='CPU';v=$_.Cpu}, @{k='Display';v=$_.Display}, @{k='Network';v=$_.Net}, @{k='Disk';v=$_.Disk}) | Sort-Object { $_.v } -Descending | Select-Object -First 1).k } })
        ActiveHours= $dcActiveHoursSs
        UnattributedPct = $unattributedPct
        AvgHwDrips = $null
        StandbyPatterns = $null
        Wakes      = @($wakeAgg.Values | Sort-Object @{ e = { if ($_.Kind -eq 'Automatic' -or ($_.Kind -eq 'Drain' -and $_.AwakeCount -gt 0)) { 0 } else { 1 } } }, @{ e = 'Count'; Descending = $true })
    }

    if ($sessions.Count -gt 0) {
        $totH = (Sum-Of $sessions 'Hours')
        $ss.AvgHwDrips = (Sum-Of @($sessions | ForEach-Object { $_.HwDripsPct * $_.Hours }) $null) / $totH
        if ($system.Standby -like 'Modern*') {
            # Standby sessions fall into three patterns that need different fixes, so judge them separately
            $groups = @{}
            foreach ($pt in 'Asleep', 'HwOnly', 'Awake') {
                $g = @($sessions | Where-Object { $_.Pattern -eq $pt })
                $h = Sum-Of $g 'Hours'
                $groups[$pt] = [pscustomobject]@{ Count = $g.Count; Hours = $h; mW = $(if ($h -gt 0) { (Sum-Of $g 'mWh') / $h } else { 0 }); Items = $g }
            }
            $asleep = $groups['Asleep']; $hwOnly = $groups['HwOnly']; $awake = $groups['Awake']
            $pctH = { param($mw) if ($fullTotal -gt 0) { 100 * $mw / $fullTotal } else { 0 } }
            $ss.StandbyPatterns = [ordered]@{
                AsleepSessions = $asleep.Count; AsleepmW = $asleep.mW; AsleepHours = $asleep.Hours; HwOnlyHours = $hwOnly.Hours; AwakeHours = $awake.Hours
                HwOnlySessions = $hwOnly.Count; HwOnlymW = $hwOnly.mW
                AwakeSessions  = $awake.Count;  AwakemW  = $awake.mW
            }
            $sess = { param($n) if ($n -eq 1) { '1 standby session' } else { "$n standby sessions" } }

            if ($asleep.Count -gt 0) {
                $floorPct = & $pctH $asleep.mW
                if ($floorPct -gt $Config.StandbyElevatedPctH) {
                    Add-Finding 'ELEVATED' 'Standby' ("In {0} the laptop slept properly (hardware in deep sleep), but it still used about {1:N0} mW ({2:N1}% of the battery per hour). No software is active in deep sleep, so this drain comes from the hardware itself: a connected accessory, outdated firmware or drivers, or a battery that loses charge on its own. Install all firmware and driver updates, test with nothing connected, and compare with another laptop of the same model." -f (& $sess $asleep.Count), $asleep.mW, $floorPct)
                } else {
                    Add-Finding 'OK' 'Standby' ("In {0} the laptop slept properly and used only {1:N0} mW ({2:N1}% per hour). Deep sleep works as it should." -f (& $sess $asleep.Count), $asleep.mW, $floorPct)
                }
            }
            if ($hwOnly.Count -gt 0) {
                $soc = @($hwOnly.Items | Where-Object { $_.SocBlocker } | Select-Object -ExpandProperty SocBlocker -First 1)
                $times = $(if ($asleep.Count -gt 0 -and $asleep.mW -gt 0) { " That is {0:N1} times as much as when it slept properly." -f ($hwOnly.mW / $asleep.mW) } else { '' })
                $lvl = $(if ($hwOnly.Hours -ge 0.25 * $totH) { 'HIGH' } else { 'ELEVATED' })
                Add-Finding $lvl 'Standby' ("In {0} Windows was asleep, but the processor never reached its deepest sleep state{1}. It used about {2:N0} mW ({3:N1}% per hour).{4} No app was active, so this is not an app problem: a driver, firmware or connected device (for example a USB stick left in) keeps the hardware partly awake. Install all firmware and driver updates, remove USB devices and accessories, and test again." -f (& $sess $hwOnly.Count), $(if ($soc) { " (reported as '$($soc[0])')" } else { '' }), $hwOnly.mW, (& $pctH $hwOnly.mW), $times)
            }
            if ($awake.Count -gt 0) {
                $blk = @($awake.Items | Sort-Object Hours -Descending | Select-Object -ExpandProperty TopBlocker -First 1)
                $blkTxt = $(if ($blk -and $blk[0]) { $blk[0] } else { 'not reported' })
                $longAwake = @($awake.Items | Where-Object { $_.Hours -ge $ShortBurstHours })
                if ($longAwake.Count -eq 0 -and $asleep.Hours -ge 1) {
                    Add-Finding 'INFO' 'Standby' ("In {0} (all shorter than {1} minutes) software or devices kept the laptop briefly awake, using about {2:N0} mW. Short bursts like this are common, for example while Windows finishes tasks after going to sleep, and the rest of the standby time was spent properly asleep. The main cause: {3}." -f (& $sess $awake.Count), $Config.ShortBurstMinutes, $awake.mW, $blkTxt)
                } else {
                    $lvl = $(if ($awake.Hours -ge 0.25 * $totH) { 'HIGH' } else { 'ELEVATED' })
                    Add-Finding $lvl 'Standby' ("In {0} software or devices kept the laptop awake, using about {1:N0} mW ({2:N1}% per hour). The main cause: {3}. See the 'top blockers' table." -f (& $sess $awake.Count), $awake.mW, (& $pctH $awake.mW), $blkTxt)
                }
            }
        }
        if ($ss.Blockers.Count -gt 0 -and $ss.Blockers[0].PctOfSleep -ge 20) {
            $b0 = $ss.Blockers[0]
            Add-Finding 'ELEVATED' 'Standby' ("The main thing keeping the laptop awake in standby is '{0}' ({1}), which was active {2} of standby time. Update or reinstall its driver or firmware, unplug USB devices, or check its power settings." -f $b0.Name, $b0.Type, (Format-Pct $b0.PctOfSleep))
        }
    } else {
        Add-Finding 'INFO' 'Standby' 'The sleep study has no standby sessions on battery of 10 minutes or longer in this period.'
    }

    $auto = @($ss.Wakes | Where-Object { $_.Kind -eq 'Automatic' })
    if ($auto.Count -gt 0) {
        $autoN = Sum-Of $auto 'Count'
        $spanDays = 1.0
        try { $spanDays = [math]::Max(1.0, ([datetime]$lastWake - [datetime]$firstWake).TotalDays) } catch {}
        $list = ($auto | ForEach-Object { "{0} ({1}x)" -f $_.Reason, $_.Count }) -join ', '
        $lvl = $(if ($autoN / $spanDays -gt 2) { 'ELEVATED' } else { 'INFO' })
        Add-Finding $lvl 'Wake-ups' ("On battery, the laptop came out of standby by itself {0} time(s) in this period: {1}. 'Maintenance Scheduler' is Windows' automatic maintenance; 'Network' points to Wake-on-LAN or a network adapter allowed to wake the laptop. See 'Why standby ended' for all reasons." -f $autoN, $list)
    }
    $drain = @($ss.Wakes | Where-Object { $_.Kind -eq 'Drain' })
    $drainAwake = $(if ($drain.Count) { Sum-Of $drain 'AwakeCount' } else { 0 })
    if ($drainAwake -gt 0) {
        Add-Finding 'ELEVATED' 'Wake-ups' ("Windows ended standby {0} time(s) because the laptop used too much power while it should have been asleep ('drain budget exceeded'). Nothing woke the laptop: it never got into deep sleep and kept running with the screen off. To the user this looks like it switches itself on, or it comes out of the bag warm and empty. The standby findings show what kept it awake." -f $drainAwake)
    } elseif ($drain.Count -gt 0 -and -not $selfWakePattern) {   # with the self-wake pattern the battery-report finding explains it
        Add-Finding 'INFO' 'Wake-ups' ("Windows moved the laptop from standby into hibernate {0} time(s) after the standby battery budget was used up. The laptop slept properly before that, so this is normal after many hours in standby." -f (Sum-Of $drain 'Count'))
    }
    if ($auto.Count -eq 0 -and $drain.Count -eq 0 -and $ss.Wakes.Count -gt 0 -and -not $selfWakePattern) {
        Add-Finding 'OK' 'Wake-ups' 'On battery, the laptop never came out of standby by itself in this period: every wake-up came from the user or the charger, or ended in hibernate.'
    }

    if ($ss.Apps.Count -gt 0) {
        $a0 = $ss.Apps[0]
        $topNames = ($ss.Apps | Select-Object -First 3 | ForEach-Object { "{0} ({1:N0}%)" -f $_.Name, $_.SharePct }) -join ', '
        Add-Finding 'INFO' 'Consumption' "The biggest energy users while on battery were $topNames."
        $bg = @($ss.Apps | Where-Object { $_.SharePct -ge 10 -and $_.Main -ne 'Display' -and $_.Name -notmatch '^(System|System Interrupts|dwm\.exe|explorer\.exe|Idle|\(Unattributed\))$' -and $_.Name -notmatch 'chrome|msedge|firefox|teams|outlook|winword|excel|powerpnt|zoom' })
        foreach ($x in $bg) {
            Add-Finding 'ELEVATED' 'Consumption' ("'{0}' used {1:N0}% of the measured app energy, mostly through {2}. If the customer does not actively use it, it is a likely cause of short battery life (it may be background software, a sync or backup tool, or malware)." -f $x.Name, $x.SharePct, $x.Main)
        }
    }
}

# Self-wakes seen in the battery report (any period, no admin needed)
$selfWakeInfo = $null
if ($selfWakePattern) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $first = $selfWakes[0]; $last = $selfWakes[-1]
    $gaps = @(for ($i = 1; $i -lt $selfWakes.Count; $i++) { $g = ($selfWakes[$i].Time - $selfWakes[$i - 1].Time).TotalMinutes; if ($g -le 360) { $g } })
    $gapMin = $(if ($gaps.Count) { @($gaps | Sort-Object)[[int][math]::Floor($gaps.Count / 2)] } else { $null })
    $avgMin = (Sum-Of $selfWakes 'Minutes') / $selfWakes.Count
    $night = @($selfWakes | Where-Object { $_.Time.Hour -lt 6 }).Count
    # The longest unbroken run (wake-ups less than 4 h apart): how much battery it cost while nobody used the laptop
    $runs = @(); $cur = @($selfWakes[0])
    for ($i = 1; $i -lt $selfWakes.Count; $i++) {
        if (($selfWakes[$i].Time - $selfWakes[$i - 1].Time).TotalHours -le 4) { $cur += $selfWakes[$i] } else { $runs += ,$cur; $cur = @($selfWakes[$i]) }
    }
    $runs += ,$cur
    $run = @($runs | Sort-Object Count -Descending)[0]
    $runTxt = ''
    if ($run.Count -ge 4 -and $run[0].FCC -gt 0) {
        $fromPct = 100 * $run[0].ChargeBefore / $run[0].FCC; $toPct = [math]::Max(0, 100 * $run[-1].ChargeAfter / $run[-1].FCC)
        if ($fromPct - $toPct -ge 5) {
            $runTxt = " From {0} to {1} it did this {2} times in a row while nobody used it, and the battery went from {3:N0}% to {4:N0}%." -f $run[0].Time.ToString('d MMM HH:mm', $inv), $run[-1].End.ToString('d MMM HH:mm', $inv), $run.Count, $fromPct, $toPct
        }
    }
    $ssFrom = $null
    if ($ss) { $ts = @(Get-Nodes $sd 'ScenarioInstance' | ForEach-Object { $_.GetAttribute('LocalTimestamp') } | Where-Object { $_ } | Sort-Object); if ($ts.Count) { $ssFrom = [datetime]::Parse($ts[0], $inv) } }
    # Match the wake-ups the sleep study covers to the reason standby ended at that moment
    $matchTxt = ''
    if ($ss) {
        $ends = @(foreach ($x in (Get-Nodes $sd 'ScenarioInstance')) {
            if ($x.GetAttribute('Type') -ne 'Sleep' -or $x.GetAttribute('Ac') -ne '0') { continue }
            $r = $x.GetAttribute('CsExitReason'); if (-not $r) { continue }
            [pscustomobject]@{ End = [datetime]::Parse($x.GetAttribute('LocalTimestamp'), $inv).AddSeconds((Num $x.GetAttribute('Duration')) / 1e7)
                               Reason = $(if ($r -match '\((.+)\)\s*$') { $Matches[1] } else { $r }) }
        })
        $hits = @(foreach ($w in $selfWakes) { $m = @($ends | Where-Object { [math]::Abs(($_.End - $w.Time).TotalMinutes) -le 3 }); if ($m.Count) { $m[0].Reason } })
        if ($hits.Count -gt 0) {
            $top = @($hits | Group-Object | Sort-Object Count -Descending)[0]
            $matchTxt = " The sleep study covers {0} of these wake-ups; {1} of them started with '{2}'." -f $hits.Count, $top.Count, $top.Name
            if ($top.Name -match 'Drain Budget') {
                $matchTxt += " That means Windows ended standby to protect the battery, but the laptop did not go into hibernate: it woke up for a few minutes and went back to sleep, over and over. The laptop sleeps properly in between, so the wake-ups themselves cost the battery. Check that hibernate works (power options: 'Hibernate after'), and install the latest Windows and firmware updates."
            }
        }
    }
    $causeTxt = $(if ($matchTxt) { $matchTxt }
                  elseif ($ssFrom -and $ssFrom -le $first.Time) { " The sleep study covers this period: see 'Why standby ended' for what woke it." }
                  elseif ($ssFrom) { " The sleep study only goes back to {0}, so it cannot show what woke the laptop then. Usual causes are a network adapter, USB or Bluetooth device allowed to wake the laptop, wake timers, or an app or driver. Check the wake settings, or test again within 3 days of it happening." -f $ssFrom.ToString('d MMM', $inv) }
                  else { " Usual causes are a network adapter, USB or Bluetooth device allowed to wake the laptop, wake timers, or an app or driver." })
    $everyTxt = $(if ($gapMin) { "about every $(Format-Hours ($gapMin / 60)), " } else { '' })
    $nightTxt = $(if ($night -gt 0) { "also at night, " } else { '' })
    Add-Finding 'ELEVATED' 'Wake-ups' ("The battery report shows the laptop waking itself up from standby {0} times on battery between {1} and {2}: {3}{4}for about {5:N0} minutes each time, then back to sleep.{6} This is a software or settings problem, not a battery problem.{7}" -f $selfWakes.Count, $first.Time.ToString('d MMM', $inv), $last.Time.ToString('d MMM', $inv), $everyTxt, $nightTxt, $avgMin, $runTxt, $causeTxt)
    $selfWakeInfo = [ordered]@{ Count = $selfWakes.Count; First = $first.Time.ToString('s'); Last = $last.Time.ToString('s'); TypicalGapMinutes = $gapMin; AvgMinutes = $avgMin; AtNight = $night
                                Times = @($selfWakes | ForEach-Object { $_.Time.ToString('s') }) }
}

# ---------------------------------------------------------------------------------------------
# Standby verdict (sleep study sessions are more precise than the battery report, so they win when present)
# ---------------------------------------------------------------------------------------------
$standbyExcluded = ''
if ($ss -and $ss.Sessions.Count -gt 0) {
    $counted = @($ss.Sessions)
    # A short burst of activity (often right after going to sleep) says little about how the laptop sleeps;
    # judge the standby drain on the rest, but only when the laptop also slept properly for at least an hour
    $bursts = @($ss.Sessions | Where-Object { $_.PSObject.Properties['Pattern'] -and $_.Pattern -eq 'Awake' -and $_.Hours -lt $ShortBurstHours })
    $rest   = @($ss.Sessions | Where-Object { $bursts -notcontains $_ })
    $restAsleep = @($rest | Where-Object { $_.Pattern -eq 'Asleep' })
    if ($bursts.Count -gt 0 -and (Sum-Of $restAsleep 'Hours') -ge 1) {
        $counted = $rest
        $standbyExcluded = " Not counted: {0} short wake-up(s) of under {1} minutes ({2:N0} min in total), which are common and say little about how the laptop sleeps." -f $bursts.Count, $Config.ShortBurstMinutes, ((Sum-Of $bursts 'Hours') * 60)
    }
    $ssHours = (Sum-Of $counted 'Hours')
    if ($ssHours * 60 -ge 30) {
        $standbyHours = $ssHours
        $standbyMW = (Sum-Of $counted 'mWh') / $ssHours
        $standbySource = 'sleep study'
    } else { $standbyExcluded = '' }
}
if ($standbyMW -ne $null) {
    $standbyPctH = $(if ($fullTotal -gt 0) { 100 * $standbyMW / $fullTotal } else { 0 })
    if     ($standbyPctH -gt $Config.StandbyHighPctH)     { $standbyLevel = 'HIGH' }
    elseif ($standbyPctH -gt $Config.StandbyElevatedPctH) { $standbyLevel = 'ELEVATED' }
    else                                                  { $standbyLevel = 'NORMAL' }
    $msg = "In standby on battery ({0} measured, from the {4}), the laptop lost {1:N2}% of its charge per hour ({2:N0} mW). That is {3:N0}% per 24 hours." -f (Format-Hours $standbyHours), $standbyPctH, $standbyMW, ([math]::Min(100.0, $standbyPctH * 24)), $standbySource
    $msg += $standbyExcluded
    $more = $(if ($ss) { 'See the sleep study section for the cause.' } else { 'Run the advanced (sleep study) analysis to find the cause.' })
    switch ($standbyLevel) {
        'NORMAL'   { Add-Finding 'OK' 'Standby' "$msg This is normal." }
        'ELEVATED' {
            if ($ss -and $ss.StandbyPatterns) { Add-Finding 'ELEVATED' 'Standby' "$msg This is above normal. The other standby findings show where it comes from." }
            else { Add-Finding 'ELEVATED' 'Standby' "$msg This is above normal: something is waking the laptop or keeping it awake during sleep. $more" }
        }
        'HIGH'     {
            if ($ss -and $ss.StandbyPatterns) { Add-Finding 'HIGH' 'Standby' "$msg This is high, which explains a battery that runs down with the lid closed. It is not a battery problem. The other standby findings show where it comes from." }
            else { Add-Finding 'HIGH' 'Standby' "$msg This is high: the laptop is not really sleeping, which explains a battery that is 'empty overnight'. This is a software or configuration problem, not a battery problem. $more" }
        }
    }
} else {
    Add-Finding 'INFO' 'Standby' ("There is too little standby time on battery to judge standby drain ({0} measured)." -f (Format-Hours $standbyHours))
}

# ---------------------------------------------------------------------------------------------
# 4. Overall verdict
# ---------------------------------------------------------------------------------------------
$powerLevel = Max-Level @($activeLevel, $standbyLevel)
if ($ss -and $ss.AvgHwDrips -ne $null -and $system.Standby -like 'Modern*' -and $ss.AvgHwDrips -lt $Config.DripsLowPct) { $powerLevel = Max-Level @($powerLevel, 'ELEVATED') }
if ($selfWakePattern) { $powerLevel = Max-Level @($powerLevel, 'ELEVATED') }   # waking itself up drains the battery while nobody uses it
$powerBad   = $powerLevel -in 'ELEVATED', 'HIGH'
$wearShare  = $(if ($runtime -and $runtime.WearSharePct -ne $null) { $runtime.WearSharePct } else { $null })

switch ($batteryLevel) {
    'REPLACE' {
        if ($powerBad) { $verdict = 'REPLACE + FIX SOFTWARE'; $color = 'red'
            $summary = 'The battery is worn out and should be replaced. Power consumption is also above normal, so a new battery alone will not fully fix the runtime. Fix the software or configuration problem too (see below).' }
        else { $verdict = 'REPLACE BATTERY'; $color = 'red'
            $summary = 'The battery is worn out. Battery wear is the cause of the short runtime, and replacing the battery is recommended.' }
    }
    'WORN' {
        if (-not $powerBad) { $verdict = 'REPLACEMENT JUSTIFIED'; $color = 'orange'
            $summary = 'The battery is clearly worn and power consumption is normal. Wear is the main cause of the short runtime, so replacing the battery will noticeably improve it.' + $(if ($runtime -and $runtime.GainMinutes -ge 1) { " A new battery would add about $([math]::Round($runtime.GainMinutes)) minutes per charge." } else { '' }) }
        elseif ($wearShare -ne $null -and $wearShare -ge 50) { $verdict = 'REPLACE + FIX SOFTWARE'; $color = 'orange'
            $summary = 'The battery is clearly worn and is the main cause of the short runtime. Power consumption is also above normal, so fix that as well to get the full benefit of a new battery.' }
        else { $verdict = 'FIX SOFTWARE FIRST'; $color = 'orange'
            $summary = 'The battery shows wear, but most of the runtime loss comes from high power consumption. Fix the software or configuration problem first, then decide whether a new battery is still needed.' }
    }
    'UNKNOWN' { $verdict = 'INCONCLUSIVE'; $color = 'grey'
        $summary = 'The battery does not report usable capacity data. Test with a known-good battery or the manufacturer''s diagnostics.' }
    default {   # OK / FAIR
        if ($powerBad) { $verdict = 'DO NOT REPLACE - SOFTWARE/CONFIG'; $color = 'blue'
            # Say exactly what was found: high use while working, too much drain in standby, or both
            $activeBad  = $activeLevel -in 'ELEVATED', 'HIGH'
            $standbyBad = ($standbyLevel -in 'ELEVATED', 'HIGH') -or ($ss -and $ss.AvgHwDrips -ne $null -and $system.Standby -like 'Modern*' -and $ss.AvgHwDrips -lt $Config.DripsLowPct)
            $cause = $(if ($activeBad -and $standbyBad) { 'The laptop uses too much power, both while in use and while asleep (standby). This is caused by software, background processes or settings.' }
                       elseif ($activeBad) { 'The laptop uses more power than normal while in use. This is caused by software, background processes or settings.' }
                       else { 'The laptop loses too much charge while asleep (standby): devices or software keep it awake. This explains a battery that runs down with the lid closed.' })
            $newBattery = ' A new battery would not solve it.'
            # With a sleep study, name the standby pattern that accounts for most of the time
            if (-not $activeBad -and $ss -and $ss.StandbyPatterns) {
                $sp = $ss.StandbyPatterns
                $floorBad = $sp.AsleepSessions -gt 0 -and $fullTotal -gt 0 -and (100 * $sp.AsleepmW / $fullTotal) -gt $Config.StandbyElevatedPctH
                $dom = @(@{ k = 'Asleep'; h = $(if ($floorBad) { $sp.AsleepHours } else { 0 }) }, @{ k = 'HwOnly'; h = $sp.HwOnlyHours }, @{ k = 'Awake'; h = $sp.AwakeHours } | Sort-Object { -$_.h })[0]
                if ($dom.h -gt 0) {
                    $cause = switch ($dom.k) {
                        'Asleep' { $newBattery = ' The battery capacity does not justify a replacement. If the drain stays high after firmware and driver updates and with nothing connected, the battery may be losing charge on its own.'
                                   'The laptop loses too much charge while asleep (standby), even when it sleeps properly. That points to firmware, drivers, a connected accessory or the battery itself, not to an app.' }
                        'HwOnly' { 'The laptop loses too much charge while asleep (standby), because the hardware does not reach its deepest sleep state. That points to a driver, firmware or a connected device, not to an app.' }
                        default  { 'The laptop loses too much charge while asleep (standby): devices or software keep it awake. This explains a battery that runs down with the lid closed.' }
                    }
                }
            }
            if ($selfWakePattern) {
                $wakeCause = 'The laptop keeps waking itself up from standby, so the battery runs down while nobody is using it. This is caused by a setting, a device or software.'
                if (-not $activeBad -and -not $standbyBad) { $cause = $wakeCause } else { $cause += ' It also keeps waking itself up from standby.' }
            }
            $notMeasured = $(if ($activeLevel -eq 'UNKNOWN') { ' Power use while working could not be measured, because the laptop has not been used on battery recently.' } else { '' })
            $summary = $(if ($batteryLevel -eq 'OK') { 'The battery is in good condition.' } else { 'The battery shows normal wear for its age, which is not enough on its own to explain the complaint.' }) + " $cause$newBattery$notMeasured" }
        elseif ($powerLevel -eq 'UNKNOWN') { $verdict = 'BATTERY OK'; $color = 'green'
            $summary = 'The battery is in good condition, so replacement is not needed. There was not enough battery usage to judge power consumption. If the customer still has a complaint, collect more usage data and run the test again.' }
        else { $verdict = 'NO FAULT FOUND'; $color = 'green'
            $summary = 'The battery and power consumption are both within normal ranges, so no repair is needed. The runtime matches how the laptop is used.' }
    }
}

# An uncalibrated gauge makes both the health and the power use unreliable: do not send the customer either way yet
if ($gaugeSuspect) {
    if ($powerBad -or $batteryLevel -in 'WORN', 'REPLACE') {
        $verdict = 'CALIBRATE BATTERY, THEN RETEST'; $color = 'grey'
        $summary = "The battery reports almost no wear after $maxCycles cycles, which is not realistic: its gauge is most likely not calibrated. That makes the figures unreliable. The high power use may really be a worn battery whose charge level drops fast. Calibrate the battery (see the battery findings) and run the test again before deciding on a replacement."
    } else {
        $summary += " Note: the battery reports almost no wear after $maxCycles cycles, so its gauge is probably not calibrated. Calibrate it and test again if the customer still has a complaint."
    }
}

if ($abnormalWear -and $batteryLevel -ne 'REPLACE') {
    $summary += ' Note: the battery is wearing faster than normal for its cycle count. Recalibrate it and check the warranty.'
}

$safeName = ($system.Computer -replace '[^\w\-]', '_')
$resultId = "BatteryCheck-$safeName-$stamp"
$rawDirFinal = $(if ($BatteryReportXml -like "$workDir*") { Split-Path $workDir -Leaf } else { Split-Path (Split-Path (Resolve-Path $BatteryReportXml).Path -Parent) -Leaf })

$result = [ordered]@{
    Tool        = 'BatteryCheck 1.1'
    Id          = $resultId
    CreatedAt   = (Get-Date).ToString('s')
    Color       = $color
    Meta        = $meta
    Files       = [ordered]@{ Html = "$resultId.html"; RawDir = $rawDirFinal }
    Mode        = $(if ($ss) { 'Advanced (battery report + sleep study)' } else { 'Basic (battery report)' })
    Verdict     = $verdict
    Summary     = $summary
    BatteryLevel= $(if ($gaugeSuspect) { 'UNKNOWN' } else { $batteryLevel })
    PowerLevel  = $powerLevel
    System      = $system
    Battery     = [ordered]@{ DesignmWh = $designTotal; FullChargemWh = $fullTotal; HealthPct = [math]::Round($healthPct, 1); WearPct = [math]::Round($wearPct, 1); Cycles = $maxCycles; Packs = $batteries; TrendPctPerMonth = $trendPerMonth
                    History = @($history | ForEach-Object { [ordered]@{ Date = $_.Date.ToString('yyyy-MM-dd'); HealthPct = [math]::Round($_.HealthPct, 1); Cycles = $_.Cycles } }) }
    Consumption = [ordered]@{ ActiveAvgW = $activeW; ActiveHoursMeasured = $activeHours; StandbyPctPerHour = $standbyPctH; StandbymW = $standbyMW; StandbyHoursMeasured = $standbyHours; ActiveLevel = $activeLevel; StandbyLevel = $standbyLevel
                    Sessions = @(@($active) + @($standby) | Sort-Object Start -Descending | ForEach-Object { [ordered]@{ Start = $_.Start; Type = $_.Type; Hours = $_.Hours; mWh = $_.mWh; W = $_.W; PctPerHour = $_.PctPerHour } }) }
    Runtime     = $runtime
    SleepStudy  = $ss
    Findings    = $findings
    WakeConfig  = $wakeConfig
    SelfWakes   = $selfWakeInfo
    Thresholds  = $Config
}

Write-Host 'Step: writing report'
# ---------------------------------------------------------------------------------------------
# 5. HTML report
# ---------------------------------------------------------------------------------------------
$levelCss = @{ OK = 'ok'; NORMAL = 'ok'; FAIR = 'fair'; ELEVATED = 'warn'; WORN = 'warn'; HIGH = 'bad'; REPLACE = 'bad'; INFO = 'info'; UNKNOWN = 'info' }
function Badge([string]$l) { "<span class='badge $($levelCss[$l])'>$(Enc $l)</span>" }

function Build-HistorySvg {
    if ($history.Count -lt 2) { return '' }
    $w = 640; $hgt = 180; $pl = 36; $pr = 10; $pt = 10; $pb = 24
    $t0 = $history[0].Date.Ticks; $t1 = $history[-1].Date.Ticks; if ($t1 -eq $t0) { $t1 = $t0 + 1 }
    $min = [math]::Max(0.0, [math]::Floor(((Min-Of $history 'HealthPct') - 5) / 10) * 10)
    # top of the chart: 100%, or higher when the gauge reported more (glitches up to ~120% happen)
    $max = [math]::Max(100.0, [math]::Ceiling((Max-Of $history 'HealthPct') / 10) * 10)
    $x = { param($t) $pl + ($w - $pl - $pr) * ($t - $t0) / ($t1 - $t0) }
    $y = { param($v) $pt + ($hgt - $pt - $pb) * (1 - ($v - $min) / ($max - $min)) }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append("<svg viewBox='0 0 $w $hgt' class='chart' role='img' aria-label='Battery health over time'>")
    $mid = ($min + $max) / 2
    foreach ($g in @($min, $mid, $max, $Config.HealthOkPct, $Config.HealthReplacePct)) {
        if ($g -lt $min) { continue }
        $gy = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F1}', (& $y $g))
        $cls = $(if ($g -eq $Config.HealthOkPct) { 'th-ok' } elseif ($g -eq $Config.HealthReplacePct) { 'th-bad' } else { 'grid' })
        [void]$sb.Append("<line x1='$pl' x2='$($w-$pr)' y1='$gy' y2='$gy' class='$cls'/><text x='$($pl-4)' y='$gy' class='ax' text-anchor='end' dominant-baseline='middle'>$([int]$g)%</text>")
    }
    $pts = ($history | ForEach-Object { [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F1},{1:F1}', (& $x $_.Date.Ticks), (& $y $_.HealthPct)) }) -join ' '
    [void]$sb.Append("<polyline points='$pts' class='line'/>")
    [void]$sb.Append("<text x='$pl' y='$($hgt-6)' class='ax'>$($history[0].Date.ToString('yyyy-MM-dd'))</text><text x='$($w-$pr)' y='$($hgt-6)' class='ax' text-anchor='end'>$($history[-1].Date.ToString('yyyy-MM-dd'))</text>")
    [void]$sb.Append('</svg>')
    return $sb.ToString()
}

function Gauge([double]$pct) {
    $p = [math]::Max(0.0, [math]::Min(100.0, $pct))
    $cls = $(if ($pct -ge $Config.HealthOkPct) { 'ok' } elseif ($pct -ge $Config.HealthFairPct) { 'fair' } elseif ($pct -ge $Config.HealthReplacePct) { 'warn' } else { 'bad' })
    "<div class='gauge'><div class='fill $cls' style='width:$([int]$p)%'></div><div class='mark' style='left:$($Config.HealthReplacePct)%'></div><div class='mark' style='left:$($Config.HealthOkPct)%'></div></div>"
}

$sb = New-Object Text.StringBuilder
$css = @'
:root{--bg:#f6f7f9;--card:#fff;--text:#1d2330;--muted:#5d6675;--line:#e3e6eb;--ok:#1f8a4c;--fair:#6c9a1f;--warn:#c47a00;--bad:#c62f2f;--info:#3d6fb6;--grey:#6b7280}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
.wrap{max-width:960px;margin:0 auto;padding:24px 16px 48px}
h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:0 0 12px}.muted{color:var(--muted)}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:18px 20px;margin-top:16px}
.verdict{border-left:8px solid var(--grey)}.verdict.red{border-color:var(--bad)}.verdict.orange{border-color:var(--warn)}.verdict.blue{border-color:var(--info)}.verdict.green{border-color:var(--ok)}
.verdict .v{font-size:24px;font-weight:700;letter-spacing:.3px}.verdict.red .v{color:var(--bad)}.verdict.orange .v{color:var(--warn)}.verdict.blue .v{color:var(--info)}.verdict.green .v{color:var(--ok)}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
.kpi{border:1px solid var(--line);border-radius:8px;padding:10px 12px}.kpi .l{font-size:12px;color:var(--muted)}.kpi .n{font-size:22px;font-weight:600;font-variant-numeric:tabular-nums}
.badge{display:inline-block;font-size:11px;font-weight:700;padding:1px 8px;border-radius:99px;color:#fff;vertical-align:middle}
.badge.ok{background:var(--ok)}.badge.fair{background:var(--fair)}.badge.warn{background:var(--warn)}.badge.bad{background:var(--bad)}.badge.info{background:var(--grey)}
table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{font-size:12px;color:var(--muted);font-weight:600}td.r,th.r{text-align:right}
.find td:first-child{width:90px}.find td:nth-child(2){width:110px;color:var(--muted)}
.gauge{position:relative;height:14px;background:#eceef2;border-radius:7px;overflow:hidden;margin:8px 0 4px}.gauge .fill{height:100%}.gauge .fill.ok{background:var(--ok)}.gauge .fill.fair{background:var(--fair)}.gauge .fill.warn{background:var(--warn)}.gauge .fill.bad{background:var(--bad)}
.gauge .mark{position:absolute;top:0;bottom:0;width:2px;background:rgba(0,0,0,.35)}
.split{display:flex;height:22px;border-radius:6px;overflow:hidden;margin:8px 0}.split div{color:#fff;font-size:12px;font-weight:600;display:flex;align-items:center;justify-content:center;white-space:nowrap;overflow:hidden}
.split .w{background:var(--bad)}.split .c{background:var(--info)}
.chart{width:100%;height:auto}.chart .grid{stroke:var(--line)}.chart .th-ok{stroke:var(--ok);stroke-dasharray:4 4}.chart .th-bad{stroke:var(--bad);stroke-dasharray:4 4}.chart .line{fill:none;stroke:var(--info);stroke-width:2.5}.chart .ax{font-size:11px;fill:var(--muted)}
.cols{display:grid;grid-template-columns:1fr 1fr;gap:16px}@media(max-width:720px){.cols{grid-template-columns:1fr}}
.small{font-size:12px}.scroll{overflow-x:auto}
.cols>div{min-width:0}td{overflow-wrap:anywhere}td.r,th.r,td.nw{overflow-wrap:normal;white-space:nowrap}
@media print{body{background:#fff}.card{break-inside:avoid;border-color:#ccc}.wrap{padding:0}}
'@
[void]$sb.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Battery Check - $(Enc $system.Model)</title><style>$css</style></head><body><div class='wrap'>")
[void]$sb.Append("<h1>Battery Health Check</h1><div class='muted'>$(Enc $system.Manufacturer) $(Enc $system.Model) &middot; $(Enc $system.Computer) &middot; report from $(Enc $system.ReportTime) &middot; $(Enc $result.Mode)</div>")
$metaBits = @(); if ($meta.Ticket) { $metaBits += "Ticket <b>$(Enc $meta.Ticket)</b>" }; if ($meta.Customer) { $metaBits += "Customer <b>$(Enc $meta.Customer)</b>" }; if ($meta.Technician) { $metaBits += "Technician <b>$(Enc $meta.Technician)</b>" }
if ($metaBits.Count) { [void]$sb.Append("<div class='muted' style='margin-top:4px'>$($metaBits -join ' &middot; ')</div>") }

# Verdict
[void]$sb.Append("<div class='card verdict $color'><div class='muted small'>RECOMMENDATION</div><div class='v'>$(Enc $verdict)</div><p>$(Enc $summary)</p>")
[void]$sb.Append("<div>Battery condition: $(Badge $batteryLevel) &nbsp; Power consumption: $(Badge $powerLevel)</div></div>")

# KPIs
$cyclesTxt = $(if ($maxCycles -gt 0) { $maxCycles } else { 'n/a' })
$activeTxt = $(if ($activeW) { '{0:N1} W' -f $activeW } else { 'n/a' })
$sbTxt     = $(if ($standbyPctH -ne $null) { '{0:N2} %/h' -f $standbyPctH } else { 'n/a' })
[void]$sb.Append("<div class='card'><h2>Key figures</h2><div class='kpis'>")
[void]$sb.Append("<div class='kpi'><div class='l'>Battery health</div><div class='n'>$(Format-Pct $healthPct 1)</div>$(Gauge $healthPct)</div>")
[void]$sb.Append("<div class='kpi'><div class='l'>Wear</div><div class='n'>$(Format-Pct $wearPct 1)</div><div class='small muted'>$('{0:N0}' -f $fullTotal) of $('{0:N0}' -f $designTotal) mWh</div></div>")
[void]$sb.Append("<div class='kpi'><div class='l'>Cycle count</div><div class='n'>$cyclesTxt</div></div>")
[void]$sb.Append("<div class='kpi'><div class='l'>Avg draw in use (battery)</div><div class='n'>$activeTxt</div><div class='small muted'>$(Format-Hours $activeHours) measured</div></div>")
[void]$sb.Append("<div class='kpi'><div class='l'>Standby drain</div><div class='n'>$sbTxt</div><div class='small muted'>$(Format-Hours $standbyHours) measured</div></div>")
[void]$sb.Append('</div></div>')

# Runtime & attribution
if ($runtime) {
    [void]$sb.Append("<div class='card'><h2>Runtime: what would a new battery change?</h2><table><tr><th></th><th class='r'>Current battery</th><th class='r'>New battery</th></tr>")
    [void]$sb.Append("<tr><td>At measured consumption ($('{0:N1}' -f $activeW) W)</td><td class='r'>$(Format-Hours $runtime.NowAtObserved)</td><td class='r'>$(Format-Hours $runtime.NewAtObserved)</td></tr>")
    [void]$sb.Append("<tr><td>At normal consumption ($($runtime.ReferenceW) W)</td><td class='r'>$(Format-Hours $runtime.NowAtReference)</td><td class='r'>$(Format-Hours $runtime.NewAtReference)</td></tr></table>")
    if ($runtime.WearSharePct -ne $null) {
        $ws = [math]::Round($runtime.WearSharePct); $cs = 100 - $ws
        [void]$sb.Append("<p style='margin-bottom:0'>Compared with a new battery at normal consumption, the runtime shortfall is caused by:</p><div class='split'>")
        if ($ws -gt 0) { [void]$sb.Append("<div class='w' style='width:$ws%'>Battery wear $ws%</div>") }
        if ($cs -gt 0) { [void]$sb.Append("<div class='c' style='width:$cs%'>Power consumption $cs%</div>") }
        [void]$sb.Append("</div><div class='small muted'>Runtime = capacity &divide; power, so the shortfall splits into the capacity lost and the extra power drawn. The 'normal' reference is $($runtime.ReferenceW) W (light office use). It can be set in the script for heavier laptops.</div>")
    } else {
        [void]$sb.Append("<p class='small muted'>The measured runtime is at or above the reference (a new battery at $($runtime.ReferenceW) W), so there is no shortfall to explain.</p>")
    }
    [void]$sb.Append('</div>')
}

# Findings
[void]$sb.Append("<div class='card'><h2>Findings</h2><table class='find'>")
foreach ($f in ($findings | Sort-Object { -$LevelRank[$(if ($_.Level -eq 'INFO') { 'UNKNOWN' } else { $_.Level })] })) {
    [void]$sb.Append("<tr><td>$(Badge $f.Level)</td><td>$(Enc $f.Area)</td><td>$(Enc $f.Text)</td></tr>")
}
[void]$sb.Append('</table></div>')

# Battery details + history
[void]$sb.Append("<div class='card'><h2>Battery details</h2><div class='scroll'><table><tr><th>Battery</th><th>Manufacturer</th><th>Chemistry</th><th>Serial</th><th class='r'>Design</th><th class='r'>Full charge</th><th class='r'>Health</th><th class='r'>Cycles</th></tr>")
foreach ($b in $batteries) {
    [void]$sb.Append("<tr><td>$(Enc $b.Id)</td><td>$(Enc $b.Manufacturer)</td><td>$(Enc $b.Chemistry)</td><td>$(Enc $b.Serial)</td><td class='r'>$('{0:N0}' -f $b.Design) mWh</td><td class='r'>$('{0:N0}' -f $b.Full) mWh</td><td class='r'>$(Format-Pct $b.HealthPct 1)</td><td class='r'>$($b.Cycles)</td></tr>")
}
[void]$sb.Append('</table></div>')
if ($history.Count -ge 2) { [void]$sb.Append("<h2 style='margin-top:16px'>Capacity history</h2>$(Build-HistorySvg)<div class='small muted'>Dashed lines: $($Config.HealthOkPct)% (good) and $($Config.HealthReplacePct)% (replace).</div>") }
[void]$sb.Append('</div>')

# Usage sessions
if ($active.Count -gt 0 -or $standby.Count -gt 0) {
    [void]$sb.Append("<div class='card'><h2>Measured battery sessions (last $Days days)</h2><div class='scroll'><table><tr><th>Start</th><th>State</th><th class='r'>Duration</th><th class='r'>Used</th><th class='r'>Avg power</th><th class='r'>%/hour</th></tr>")
    foreach ($r in (@($active) + @($standby) | Sort-Object Start -Descending | Select-Object -First 25)) {
        [void]$sb.Append("<tr><td>$(Enc ($r.Start -replace 'T',' '))</td><td>$(Enc $r.Type)</td><td class='r'>$(Format-Hours $r.Hours)</td><td class='r'>$('{0:N0}' -f $r.mWh) mWh</td><td class='r'>$('{0:N1}' -f $r.W) W</td><td class='r'>$('{0:N1}' -f $r.PctPerHour)</td></tr>")
    }
    [void]$sb.Append("</table></div><div class='small muted'>Only sessions on battery of 5 minutes or longer are shown (most recent 25).</div></div>")
}

# Sleep study
if ($ss) {
    [void]$sb.Append("<div class='card'><h2>Advanced: sleep study analysis</h2>")
    if ($ss.Sessions.Count -gt 0) {
        [void]$sb.Append("<h2 style='font-size:14px'>Standby sessions on battery</h2><div class='scroll'><table><tr><th>Start</th><th class='r'>Duration</th><th class='r'>Used</th><th class='r'>%/hour</th><th class='r'>HW low-power</th><th class='r'>Windows sleep</th><th>Pattern</th><th>Top blocker</th></tr>")
        foreach ($s in ($ss.Sessions | Sort-Object Start -Descending | Select-Object -First 15)) {
            $dcls = $(if ($s.HwDripsPct -lt $Config.DripsLowPct) { " style='color:var(--bad);font-weight:600'" } else { '' })
            $patLabel = @{ Asleep = 'Slept properly'; HwOnly = 'Windows asleep, hardware not'; Awake = 'Kept awake' }[$(if ($s.PSObject.Properties['Pattern']) { $s.Pattern } else { 'Awake' })]
            $blockTxt = $s.TopBlocker; if ($s.PSObject.Properties['SocBlocker'] -and $s.SocBlocker) { $blockTxt = "$($s.SocBlocker); $blockTxt" }
            [void]$sb.Append("<tr><td>$(Enc ($s.Start -replace 'T',' '))</td><td class='r'>$(Format-Hours $s.Hours)</td><td class='r'>$('{0:N0}' -f $s.mWh) mWh</td><td class='r'>$('{0:N2}' -f $s.PctPerHour)</td><td class='r'$dcls>$(Format-Pct $s.HwDripsPct)</td><td class='r'>$(Format-Pct $s.SwDripsPct)</td><td class='small'>$(Enc $patLabel)</td><td class='small'>$(Enc $blockTxt)</td></tr>")
        }
        [void]$sb.Append("</table></div><div class='small muted'>HW low-power is the share of standby time the hardware spent in its deepest sleep state (DRIPS). A healthy system is above $($Config.DripsLowPct)%.</div>")
    } else { [void]$sb.Append("<p class='muted'>There are no standby sessions on battery in this period.</p>") }

    [void]$sb.Append("<div class='cols' style='margin-top:16px'>")
    [void]$sb.Append("<div><h2 style='font-size:14px'>Top blockers in standby (battery)</h2>")
    if ($ss.Blockers.Count -gt 0) {
        [void]$sb.Append("<table><tr><th>Component</th><th>Type</th><th class='r'>% of standby</th></tr>")
        foreach ($b in $ss.Blockers) { [void]$sb.Append("<tr><td class='small'>$(Enc $b.Name)</td><td class='small nw'>$(Enc $b.Type)</td><td class='r'>$(Format-Pct $b.PctOfSleep)</td></tr>") }
        [void]$sb.Append('</table>')
    } else { [void]$sb.Append("<p class='muted'>None recorded.</p>") }
    [void]$sb.Append("<h2 style='font-size:14px;margin-top:14px'>Why standby ended (on battery)</h2>")
    if ($ss.Wakes.Count -gt 0) {
        $kindTxt = @{ User = 'By the user'; Charger = 'Charger'; Ended = 'Standby ended (hibernate)'; Drain = 'Windows stopped it (drained too fast)'; Automatic = 'By itself' }
        [void]$sb.Append("<table><tr><th>Reason</th><th>Kind</th><th class='r'>Times</th></tr>")
        foreach ($w in $ss.Wakes) {
            $st = $(if ($w.Kind -eq 'Automatic' -or ($w.Kind -eq 'Drain' -and $w.AwakeCount -gt 0)) { " style='color:var(--warn);font-weight:600'" } else { '' })
            [void]$sb.Append("<tr><td class='small'>$(Enc $w.Reason)</td><td class='small nw'$st>$(Enc $kindTxt[$w.Kind])</td><td class='r'>$($w.Count)</td></tr>")
        }
        [void]$sb.Append('</table>')
    } else { [void]$sb.Append("<p class='muted'>None recorded.</p>") }
    if ($wakeConfig.ArmedDevices.Count -gt 0 -or $wakeConfig.Timers.Count -gt 0) {
        [void]$sb.Append("<div class='small' style='margin-top:6px'><b>Allowed to wake the laptop:</b> $(Enc ($(if ($wakeConfig.ArmedDevices.Count) { $wakeConfig.ArmedDevices -join '; ' } else { 'no devices' })))$(if ($wakeConfig.Timers.Count) { '<br><b>Wake timers:</b> ' + (Enc ($wakeConfig.Timers -join ' | ')) } else { '' })</div>")
    }
    [void]$sb.Append("<h2 style='font-size:14px;margin-top:14px'>Software activity during standby</h2>")
    if ($ss.WakeApps.Count -gt 0) {
        [void]$sb.Append("<table><tr><th>App / service</th><th>Via</th><th class='r'>Active</th></tr>")
        foreach ($a in $ss.WakeApps) { [void]$sb.Append("<tr><td class='small'>$(Enc $a.Name)</td><td class='small'>$(Enc $a.Via)</td><td class='r'>$('{0:N1}' -f $a.Minutes) min</td></tr>") }
        [void]$sb.Append('</table>')
    } else { [void]$sb.Append("<p class='muted'>None recorded.</p>") }
    [void]$sb.Append("</div><div><h2 style='font-size:14px'>Energy per app while in use on battery</h2>")
    if ($ss.Apps.Count -gt 0) {
        [void]$sb.Append("<table><tr><th>App / process</th><th>Mainly</th><th class='r'>Share</th><th class='r'>Avg W</th></tr>")
        foreach ($a in $ss.Apps) { [void]$sb.Append("<tr><td class='small'>$(Enc $a.Name)</td><td class='small'>$(Enc $a.Main)</td><td class='r'>$(Format-Pct $a.SharePct)</td><td class='r'>$('{0:N2}' -f $a.AvgW)</td></tr>") }
        [void]$sb.Append("</table><div class='small muted'>These are Windows' own (SRUM) estimates over $(Format-Hours $ss.ActiveHours) of use on battery. Shares are of the energy Windows could assign to an app ($(Format-Pct (100 - $ss.UnattributedPct)) of the estimate). The rest, plus the screen's base power and idle hardware, is not assigned to any app.</div>")
    } else { [void]$sb.Append("<p class='muted'>No use on battery was recorded in this period.</p>") }
    [void]$sb.Append('</div></div></div>')
}

# System
[void]$sb.Append("<div class='card'><h2>System</h2><table>")
foreach ($k in $system.Keys) { [void]$sb.Append("<tr><td style='width:160px' class='muted'>$(Enc $k)</td><td>$(Enc $system[$k])</td></tr>") }
[void]$sb.Append("</table><div class='small muted' style='margin-top:10px'>BatteryCheck 1.0. Data comes from Windows powercfg$(if ($ss) { ' (battery report and sleep study)' } else { ' (battery report)' }). The raw reports are saved next to this file.</div></div>")
[void]$sb.Append('</div></body></html>')

$htmlPath = Join-Path $OutputDir "$resultId.html"
$jsonPath = Join-Path $OutputDir "$resultId.json"
[IO.File]::WriteAllText($htmlPath, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($jsonPath, ($result | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
if ((Get-ChildItem $workDir -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) { Remove-Item $workDir -Force }

# ---------------------------------------------------------------------------------------------
# Console summary
# ---------------------------------------------------------------------------------------------
$fg = @{ red = 'Red'; orange = 'Yellow'; blue = 'Cyan'; green = 'Green'; grey = 'Gray' }[$color]
Write-Host ''
Write-Host '  BATTERY HEALTH CHECK' -ForegroundColor White
Write-Host ("  {0} {1}" -f $system.Manufacturer, $system.Model) -ForegroundColor Gray
Write-Host ''
Write-Host ("  Health       : {0}  ({1:N0} / {2:N0} mWh, wear {3})" -f (Format-Pct $healthPct 1), $fullTotal, $designTotal, (Format-Pct $wearPct 1))
Write-Host ("  Cycles       : {0}" -f $cyclesTxt)
Write-Host ("  Use (battery): {0}   Standby: {1}" -f $activeTxt, $sbTxt)
if ($runtime) { Write-Host ("  Runtime      : now {0}, new battery {1}" -f (Format-Hours $runtime.NowAtObserved), (Format-Hours $runtime.NewAtObserved)) }
Write-Host ''
Write-Host "  >> $verdict" -ForegroundColor $fg
Write-Host "     $summary"
Write-Host ''
Write-Host "  Report: $htmlPath" -ForegroundColor Gray
if (-not $NoOpen) { Start-Process $htmlPath }
if ($PauseAtEnd) { Read-Host 'Press Enter to close' | Out-Null }
