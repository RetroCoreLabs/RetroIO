<#
.SYNOPSIS
  Runs the live C# <-> reference (WSL C) cross-environment interop tests.

  A C# X25Client calls the reference echo service (23420001) through the running
  reference switch over XOT and asserts the echo returns; the reference traffic
  generator calls a C# echo; the C# monitor client decodes the topology of the
  reference Switch A. Proves both wire directions interoperate.

.DESCRIPTION
  Prerequisites:
    - The reference (WSL) demo is running. In the x25emu repository:  ./demo/demo.sh start
      The tests expect THAT network (demo/switch_a.conf, switch_b.conf, switch_c.conf):
      Switch A access port 2001, the C# dial-in port 2007, Switch A monitor port 4101.
    - This repository is built (dotnet test builds it if needed).

  The reference host address is detected from WSL (hostname -I) because the
  WSL IP changes across reboots. Override with -WslHost if needed.

  The x25emu checkout is found as ~/repos/os/x25emu inside WSL, worked out by WSL
  itself ($HOME), so no machine-specific path is written here. Override with -WslRepo.

.EXAMPLE
  ./tests/demo/crossenv-test.ps1
  ./tests/demo/crossenv-test.ps1 -WslHost 172.23.178.242 -Port 2001
#>
param(
    [string]$WslHost = "",
    [int]$Port = 2001,
    [int]$MonitorPort = 4101,
    [string]$WslRepo = ""
)

$ErrorActionPreference = 'Stop'
# This script lives in src/tests/demo; the test project is src/tests/RetroIO.Protocols.X25.Tests.
# Until 2026-11-18 it pointed at Tests/X25Emulator.Tests.csproj, a project from before the import
# into RetroIO that no longer exists.
$TestsRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $TestsRoot 'RetroIO.Protocols.X25.Tests\RetroIO.Protocols.X25.Tests.csproj'
if (-not (Test-Path $Project)) {
    Write-Host "Test project not found: $Project" -ForegroundColor Red
    exit 1
}

if (-not $WslHost) {
    Write-Host "Detecting the reference (WSL) host address..."
    $out = (wsl.exe -e bash -lc "hostname -I") 2>$null
    $WslHost = ($out -split '\s+' | Where-Object { $_ -match '\d+\.\d+\.\d+\.\d+' } | Select-Object -First 1)
    if (-not $WslHost) {
        Write-Host "Could not detect the WSL IP. Pass it with -WslHost." -ForegroundColor Red
        exit 1
    }
}

if (-not $WslRepo) {
    # Expanded by bash inside WSL. A '~' passed on would NOT be expanded: the test quotes the path
    # in single quotes when it runs the reference traffic generator.
    $WslRepo = ((wsl.exe -e bash -lc 'echo $HOME/repos/os/x25emu') 2>$null | Select-Object -First 1).Trim()
}

Write-Host "Reference switch: ${WslHost}:${Port}, monitor ${WslHost}:${MonitorPort}, repository $WslRepo"

# Reachability check.
$probe = Test-NetConnection -ComputerName $WslHost -Port $Port -WarningAction SilentlyContinue
if (-not $probe.TcpTestSucceeded) {
    Write-Host "Cannot reach ${WslHost}:${Port}." -ForegroundColor Red
    Write-Host "Start the reference demo first (in the x25emu repository):  ./demo/demo.sh start" -ForegroundColor Yellow
    exit 1
}
Write-Host "Reachable. Running the cross-environment interop tests (both directions)..."

$env:CROSSENV_WSL_HOST = $WslHost
$env:CROSSENV_WSL_PORT = "$Port"
$env:CROSSENV_WSL_MON_HOST = $WslHost
$env:CROSSENV_WSL_MON_PORT = "$MonitorPort"
# CROSSENV_WSL_REPO lets the reverse test drive the reference traffic generator via wsl.exe.
$env:CROSSENV_WSL_REPO = $WslRepo
# The tests that start their own x25emu switch (the restart, PVC and C-client ones) read X25EMU_DIR instead. Until
# 2026-11-23 this script did not set it, so six of its ten tests were skipped on every run unless the shell happened to
# have it from src/eng/test-env.local.bat. An X25EMU_DIR already set is kept.
if (-not $env:X25EMU_DIR) {
    $env:X25EMU_DIR = $WslRepo
}
dotnet test $Project --nologo -nodeReuse:false --filter 'TestCategory=CrossEnvLive'
exit $LASTEXITCODE
