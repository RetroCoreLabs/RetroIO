<#
.SYNOPSIS
  Runs the live C# <-> reference (WSL C) cross-environment interop test.

  A C# X25Client calls the reference echo service (23420001) through the running
  reference switch over XOT and asserts the echo returns. Proves both wire
  directions interoperate.

.DESCRIPTION
  Prerequisites:
    - The reference (WSL) demo is running. In the WSL repo:  ./demo/demo.sh start
    - This repo is built (the test builds it if needed).

  The reference host address is auto-detected from WSL (hostname -I) because the
  WSL IP changes across reboots. Override with -WslHost if needed.

.EXAMPLE
  ./demo/crossenv-test.ps1
  ./demo/crossenv-test.ps1 -WslHost 172.23.178.242 -Port 2001
#>
param(
    [string]$WslHost = "",
    [int]$Port = 2001,
    [string]$WslRepo = "/home/ronny/repos/os/x25emu"
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

if (-not $WslHost) {
    Write-Host "Detecting the reference (WSL) host address..."
    $out = (wsl.exe -e bash -lc "hostname -I") 2>$null
    $WslHost = ($out -split '\s+' | Where-Object { $_ -match '\d+\.\d+\.\d+\.\d+' } | Select-Object -First 1)
    if (-not $WslHost) {
        Write-Host "Could not detect the WSL IP. Pass it with -WslHost." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Reference switch: ${WslHost}:${Port}"

# Reachability check.
$probe = Test-NetConnection -ComputerName $WslHost -Port $Port -WarningAction SilentlyContinue
if (-not $probe.TcpTestSucceeded) {
    Write-Host "Cannot reach ${WslHost}:${Port}." -ForegroundColor Red
    Write-Host "Start the reference demo first (in the WSL repo):  ./demo/demo.sh start" -ForegroundColor Yellow
    exit 1
}
Write-Host "Reachable. Running the cross-environment interop tests (both directions)..."

$env:CROSSENV_WSL_HOST = $WslHost
$env:CROSSENV_WSL_PORT = "$Port"
# CROSSENV_WSL_REPO lets the reverse test drive the reference traffic generator via wsl.exe.
$env:CROSSENV_WSL_REPO = $WslRepo
dotnet test (Join-Path $Root 'Tests/X25Emulator.Tests.csproj') --nologo --filter 'TestCategory=CrossEnvLive'
