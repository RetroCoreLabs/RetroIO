# Packs every packable RetroIO project into the local folder feed as 0.1.0-local.<timestamp>.
# The app repos reference 0.1.0-local.* so a fresh timestamp is always picked up and the NuGet cache never serves a stale DLL.
param([string]$Feed = '', [switch]$Clean)
$ErrorActionPreference = 'Stop'
# $root is src: the solution lives there, one level above this eng folder.
$root = Split-Path -Parent $PSScriptRoot
# Default the feed to a "localfeed" folder beside the repositories - the same place the
# app repos' src\nuget.config points at with its relative "..\..\localfeed". It is worked out
# from the script's own location rather than written as a drive path, because this
# repository is published and a drive letter here is only right on one machine.
if (-not $Feed) { $Feed = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'localfeed' }
$stamp = Get-Date -Format 'yyyyMMddHHmmss'
New-Item -ItemType Directory -Force $Feed | Out-Null

# `dotnet new sln` writes RetroIO.slnx (the XML solution format) on SDK 10 and RetroIO.sln on
# older ones, so take whichever is actually on disk rather than assuming the extension.
$sln = @('RetroIO.slnx', 'RetroIO.sln') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sln) { throw "no RetroIO.slnx or RetroIO.sln in $root" }

dotnet pack $sln -c Release -nodeReuse:false -o $Feed -p:VersionSuffix="local.$stamp"
if ($LASTEXITCODE -ne 0) { throw "pack failed" }
if ($Clean) {
  Get-ChildItem $Feed -Filter '*.nupkg' | Group-Object { $_.Name -replace '\.\d+\.\d+\.\d+-local\.\d+\.s?nupkg$','' } |
    ForEach-Object { $_.Group | Sort-Object Name -Descending | Select-Object -Skip 3 | Remove-Item }
}
Write-Host "packed as 0.1.0-local.$stamp into $Feed"
