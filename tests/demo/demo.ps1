<#
.SYNOPSIS
  X.25 C# demo network: three chained switches (A -> B -> C) with distinct DNICs
  plus an echo service on Switch C. Windows/PowerShell counterpart of demo.sh.

  Switch A  DNIC 2350  access 2200   mgmt 9101   monitor 4001
  Switch B  DNIC 2351  trunk  2201   mgmt 9102   monitor 4002
  Switch C  DNIC 2352  trunk  2202   mgmt 9103   monitor 4003
  echo      answers on 23520001, connected to Switch C access port 2203

  The PAD connects to Switch A; CALL 23520001 routes A -> B -> C -> echo.

.EXAMPLE
  ./demo/demo.ps1 start
  ./demo/demo.ps1 pad
  ./demo/demo.ps1 stop
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'pad', 'gui', 'cli-a', 'cli-b', 'cli-c')]
    [string]$Command = 'status'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Demo = $PSScriptRoot
$Logs = Join-Path $Demo 'logs'
$PidFile = Join-Path $Demo '.pids'
$Bin = 'bin/Debug/net10.0'

$Switch = Join-Path $Root "apps/X25Emulator.Switch/$Bin/x25switch.dll"
$Echo = Join-Path $Root "apps/X25Emulator.Echo/$Bin/x25echo.dll"
$Pad = Join-Path $Root "apps/X25Emulator.Pad/$Bin/x25pad.dll"
$Gui = Join-Path $Root "apps/X25Emulator.MonitorGui/$Bin/X25Emulator.MonitorGui.dll"

function Invoke-Build {
    Write-Host 'Building...'
    dotnet build (Join-Path $Root 'X25Emulator.sln') -c Debug --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

function Start-One {
    param([string]$Log, [object[]]$DotnetArgs)
    $out = Join-Path $Logs $Log
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList $DotnetArgs -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError "$out.err" -WindowStyle Hidden
    Add-Content -Path $PidFile -Value $proc.Id
}

function Start-Demo {
    if (Test-Path $PidFile) {
        Write-Host "Demo already running (see $PidFile). Run './demo/demo.ps1 stop' first."
        return
    }
    Invoke-Build
    New-Item -ItemType Directory -Force -Path $Logs | Out-Null
    Set-Content -Path $PidFile -Value $null

    # Recompute paths locally so nothing depends on outer-scope resolution.
    $sw = Join-Path $Root "apps/X25Emulator.Switch/$Bin/x25switch.dll"
    $ec = Join-Path $Root "apps/X25Emulator.Echo/$Bin/x25echo.dll"

    Write-Host 'Starting Switch C (DNIC=2352, mgmt=9103)...'
    Start-One 'switch-c.log' @($sw, (Join-Path $Demo 'switch-c.ini'))
    Start-Sleep -Seconds 2

    Write-Host 'Starting Switch B (DNIC=2351, mgmt=9102)...'
    Start-One 'switch-b.log' @($sw, (Join-Path $Demo 'switch-b.ini'))
    Start-Sleep -Seconds 2

    Write-Host 'Starting Switch A (DNIC=2350, mgmt=9101)...'
    Start-One 'switch-a.log' @($sw, (Join-Path $Demo 'switch-a.ini'))
    Start-Sleep -Seconds 2

    Write-Host 'Starting echo service (address 23520001, via Switch C port 2203)...'
    Start-One 'echo.log' @($ec, '-x', '127.0.0.1:2203', '-a', '23520001')
    Start-Sleep -Seconds 1

    Write-Host ''
    Write-Host 'Network ready!'
    Write-Host ''
    Write-Host 'To test:'
    Write-Host '  ./demo/demo.ps1 pad         - Launch PAD client (connects to Switch A)'
    Write-Host '  Then at the * prompt:       CALL 23520001'
    Write-Host '  Type anything - it echoes back!'
    Write-Host ''
    Write-Host '  ./demo/demo.ps1 gui         - Launch the Avalonia monitor GUI'
    Write-Host '  In the GUI select a switch (127.0.0.1 : 4001/4002/4003) and Connect.'
    Write-Host ''
    Write-Host 'Management consoles (telnet):'
    Write-Host '  ./demo/demo.ps1 cli-a       - Switch A (telnet localhost 9101)'
    Write-Host '  ./demo/demo.ps1 cli-b       - Switch B (telnet localhost 9102)'
    Write-Host '  ./demo/demo.ps1 cli-c       - Switch C (telnet localhost 9103)'
    Write-Host ''
    Write-Host '  ./demo/demo.ps1 stop        - Stop the whole network'
}

function Stop-Demo {
    if (-not (Test-Path $PidFile)) {
        Write-Host 'Demo not running.'
        return
    }
    Write-Host 'Stopping demo...'
    foreach ($line in Get-Content $PidFile) {
        $id = $line.Trim()
        if ($id) {
            try { Stop-Process -Id ([int]$id) -Force -ErrorAction Stop } catch { }
        }
    }
    Remove-Item $PidFile -Force
    Write-Host 'Stopped.'
}

switch ($Command) {
    'start' { Start-Demo }
    'stop' { Stop-Demo }
    'status' {
        if (Test-Path $PidFile) { Write-Host 'Demo PIDs:'; Get-Content $PidFile }
        else { Write-Host 'Demo not running.' }
    }
    'pad' { & dotnet $Pad -x 127.0.0.1:2200 }
    'gui' { & dotnet $Gui }
    'cli-a' { telnet localhost 9101 }
    'cli-b' { telnet localhost 9102 }
    'cli-c' { telnet localhost 9103 }
}
