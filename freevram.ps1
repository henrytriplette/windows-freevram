#Requires -Version 5.1
<#
.SYNOPSIS
    Show which processes are holding GPU VRAM on Windows, kill the ones you pick,
    and optionally restart the display driver to flush leaked allocations.

.DESCRIPTION
    Uses the WDDM performance counters (\GPU Process Memory\Dedicated Usage), which
    work for every vendor and don't need admin, unlike nvidia-smi which reports N/A
    per-process on WDDM.

.EXAMPLE
    .\freevram.ps1                     # ranked list of VRAM consumers
    .\freevram.ps1 -Interactive        # pick processes to kill from a numbered list
    .\freevram.ps1 -Kill opera,teams   # kill by name (or PID)
    .\freevram.ps1 -RestartDriver      # same as Win+Ctrl+Shift+B
    .\freevram.ps1 -Watch              # live refresh every 2s
#>
[CmdletBinding(DefaultParameterSetName = 'List')]
param(
    # Only show processes using at least this many MB of dedicated VRAM.
    [double]$MinMB = 1,

    # Max rows to display.
    [int]$Top = 25,

    # Numbered list; type the numbers of the processes to kill.
    [Parameter(ParameterSetName = 'Interactive')]
    [switch]$Interactive,

    # Kill by process name (no .exe) or PID. Comma-separated.
    [Parameter(ParameterSetName = 'Kill')]
    [string[]]$Kill,

    # Restart the graphics driver stack (Win+Ctrl+Shift+B). Screen will flicker.
    [switch]$RestartDriver,

    # Refresh the list continuously.
    [Parameter(ParameterSetName = 'Watch')]
    [switch]$Watch,

    # Seconds between refreshes in -Watch mode.
    [int]$Interval = 2,

    # Skip the safety confirmation when killing.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Killing these takes the desktop down with it. Never offer them.
$Protected = 'dwm', 'csrss', 'wininit', 'winlogon', 'System', 'Idle', 'smss', 'services', 'lsass', 'svchost', 'fontdrvhost'
# Killing these is survivable but annoying; require -Force.
$Fragile   = 'explorer', 'ShellExperienceHost', 'StartMenuExperienceHost', 'SearchHost', 'TextInputHost', 'ShellHost'

function Get-VramProcesses {
    $samples = (Get-Counter '\GPU Process Memory(*)\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples
    $shared  = @{}
    (Get-Counter '\GPU Process Memory(*)\Shared Usage' -ErrorAction SilentlyContinue).CounterSamples |
        ForEach-Object { $shared[$_.InstanceName] = $_.CookedValue }

    $byPid = @{}
    foreach ($s in $samples) {
        if ($s.InstanceName -notmatch 'pid_(\d+)') { continue }
        $id = [int]$Matches[1]
        if (-not $byPid[$id]) { $byPid[$id] = [pscustomobject]@{ PID = $id; Dedicated = 0.0; Shared = 0.0 } }
        $byPid[$id].Dedicated += $s.CookedValue
        $byPid[$id].Shared    += [double]($shared[$s.InstanceName])
    }

    foreach ($e in $byPid.Values) {
        $p = Get-Process -Id $e.PID -ErrorAction SilentlyContinue
        $name = if ($p) { $p.ProcessName } else { '<exited>' }
        [pscustomobject]@{
            PID         = $e.PID
            Name        = $name
            DedicatedMB = [math]::Round($e.Dedicated / 1MB, 1)
            SharedMB    = [math]::Round($e.Shared / 1MB, 1)
            Title       = if ($p -and $p.MainWindowTitle) { $p.MainWindowTitle } else { '' }
            Protected   = $Protected -contains $name
            Fragile     = $Fragile -contains $name
        }
    }
}

function Get-AdapterTotals {
    $ded = (Get-Counter '\GPU Adapter Memory(*)\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples |
        Measure-Object CookedValue -Sum | Select-Object -ExpandProperty Sum
    $total = $null
    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
        $line = (& nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>$null | Select-Object -First 1)
        if ($line -match '^\d+') { $total = [double]$line }
    }
    if (-not $total) {
        $total = (Get-CimInstance Win32_VideoController | Sort-Object AdapterRAM -Descending | Select-Object -First 1).AdapterRAM / 1MB
    }
    [pscustomobject]@{ UsedMB = [math]::Round($ded / 1MB); TotalMB = [math]::Round($total) }
}

function Show-List {
    param([object[]]$Rows, [switch]$Numbered)
    $t = Get-AdapterTotals
    $pct = if ($t.TotalMB) { [math]::Round(100 * $t.UsedMB / $t.TotalMB) } else { '?' }
    Write-Host ("`nVRAM in use: {0} MB / {1} MB ({2}%)`n" -f $t.UsedMB, $t.TotalMB, $pct) -ForegroundColor Cyan

    $i = 0
    $fmt = if ($Numbered) { '{0,3}  {1,7}  {2,-28} {3,10} {4,9}  {5}' } else { '     {1,7}  {2,-28} {3,10} {4,9}  {5}' }
    Write-Host ($fmt -f '#', 'PID', 'Name', 'Dedicated', 'Shared', 'Window') -ForegroundColor DarkGray
    foreach ($r in $Rows) {
        $i++
        $color = if ($r.Protected) { 'DarkGray' } elseif ($r.Fragile) { 'Yellow' } else { 'White' }
        $tag = if ($r.Protected) { ' [protected]' } elseif ($r.Fragile) { ' [shell]' } else { '' }
        $title = if ($r.Title.Length -gt 40) { $r.Title.Substring(0, 37) + '...' } else { $r.Title }
        Write-Host ($fmt -f $i, $r.PID, ($r.Name + $tag), ("{0:N1} MB" -f $r.DedicatedMB), ("{0:N0} MB" -f $r.SharedMB), $title) -ForegroundColor $color
    }
    Write-Host ''
}

function Stop-VramProcess {
    param([object[]]$Targets)
    foreach ($t in $Targets) {
        if ($t.Protected) {
            Write-Host "  skip  $($t.Name) ($($t.PID)) - protected system process" -ForegroundColor DarkGray
            continue
        }
        if ($t.Fragile -and -not $Force) {
            Write-Host "  skip  $($t.Name) ($($t.PID)) - shell process, pass -Force to kill" -ForegroundColor Yellow
            continue
        }
        try {
            Stop-Process -Id $t.PID -Force -ErrorAction Stop
            Write-Host ("  killed  {0} ({1})  freed ~{2:N0} MB" -f $t.Name, $t.PID, $t.DedicatedMB) -ForegroundColor Green
        } catch {
            Write-Host "  failed  $($t.Name) ($($t.PID)): $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

function Restart-GraphicsDriver {
    # There is no public API for the driver-reset hotkey; we synthesise Win+Ctrl+Shift+B.
    if (-not ('FreeVram.Input' -as [type])) {
        Add-Type -Namespace FreeVram -Name Input -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
'@
    }
    $VK_LWIN = 0x5B; $VK_CTRL = 0x11; $VK_SHIFT = 0x10; $VK_B = 0x42; $KEYUP = 0x2
    Write-Host 'Restarting graphics driver (screen will flicker)...' -ForegroundColor Cyan
    foreach ($k in $VK_LWIN, $VK_CTRL, $VK_SHIFT, $VK_B) { [FreeVram.Input]::keybd_event($k, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
    foreach ($k in $VK_B, $VK_SHIFT, $VK_CTRL, $VK_LWIN) { [FreeVram.Input]::keybd_event($k, 0, $KEYUP, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
    Start-Sleep -Seconds 3
}

# ---------------------------------------------------------------------------

if ($RestartDriver -and $PSCmdlet.ParameterSetName -eq 'List' -and -not $PSBoundParameters.ContainsKey('MinMB')) {
    $before = (Get-AdapterTotals).UsedMB
    Restart-GraphicsDriver
    $after = (Get-AdapterTotals).UsedMB
    Write-Host ("VRAM: {0} MB -> {1} MB ({2:+#;-#;0} MB)`n" -f $before, $after, ($after - $before)) -ForegroundColor Cyan
    return
}

$rows = Get-VramProcesses | Where-Object DedicatedMB -ge $MinMB | Sort-Object DedicatedMB -Descending | Select-Object -First $Top

switch ($PSCmdlet.ParameterSetName) {
    'List' {
        Show-List $rows
    }
    'Watch' {
        while ($true) {
            Clear-Host
            Show-List $rows
            Write-Host "refreshing every ${Interval}s - Ctrl+C to stop" -ForegroundColor DarkGray
            Start-Sleep -Seconds $Interval
            $rows = Get-VramProcesses | Where-Object DedicatedMB -ge $MinMB | Sort-Object DedicatedMB -Descending | Select-Object -First $Top
        }
    }
    'Kill' {
        $targets = foreach ($k in $Kill) {
            $k = $k -replace '\.exe$', ''
            if ($k -match '^\d+$') { $rows | Where-Object PID -eq [int]$k }
            else { $rows | Where-Object Name -like $k }
        }
        if (-not $targets) { Write-Host "No matching processes holding VRAM." -ForegroundColor Yellow; return }
        Stop-VramProcess $targets
    }
    'Interactive' {
        Show-List $rows -Numbered
        $ans = Read-Host 'Enter numbers to kill (e.g. 1,3,5), "d" to restart driver, or Enter to quit'
        if ([string]::IsNullOrWhiteSpace($ans)) { return }
        if ($ans -eq 'd') { Restart-GraphicsDriver; return }
        $idx = $ans -split '[,\s]+' | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ - 1 } | Where-Object { $_ -ge 0 -and $_ -lt $rows.Count }
        $targets = $rows[$idx]
        if (-not $targets) { return }
        Write-Host ("`nAbout to kill: " + (($targets | ForEach-Object { "$($_.Name)($($_.PID))" }) -join ', '))
        if (-not $Force -and (Read-Host 'Confirm? [y/N]') -notmatch '^y') { return }
        Stop-VramProcess $targets
    }
}

if ($RestartDriver) {
    Restart-GraphicsDriver
}

if ($PSCmdlet.ParameterSetName -in 'Kill', 'Interactive' -or $RestartDriver) {
    Start-Sleep -Milliseconds 500
    $t = Get-AdapterTotals
    Write-Host ("VRAM in use now: {0} MB / {1} MB`n" -f $t.UsedMB, $t.TotalMB) -ForegroundColor Cyan
}
