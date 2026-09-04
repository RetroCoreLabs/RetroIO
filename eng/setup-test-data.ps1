# Creates the directory junctions that make today's test data appear where the walk-up code expects it.
# Nothing is copied. See Migration/RetroIO code migration.md section 8. Idempotent.
param(
  [string]$RetroFsImages = 'E:\Dev\Ronny\RetroFS\demo\test-images',
  [string]$XmsgDoc       = 'E:\Dev\Ronny\NDInsight\SINTRAN\XMSG\DOC',
  [string]$X25Pcap       = 'E:\Dev\Ronny\X25Emulator\pcap',
  [string]$RclRoot       = 'E:\Dev\RCL'
)
# Every junction sits BESIDE the repos, never inside RetroIO. The test code walks UP from its own
# binary to the drive root looking for these folder names, so one level above the repo is found
# just as well - and the RetroIO working tree stays free of links. (Ronny's rule, 2026-09-04.)
$links = @(
  @{ Link = Join-Path $RclRoot 'demo\test-images';                 Target = $RetroFsImages },
  @{ Link = Join-Path $RclRoot 'DOC';                               Target = $XmsgDoc },
  @{ Link = Join-Path $RclRoot 'X25Emulator\pcap';                 Target = $X25Pcap },
  @{ Link = Join-Path $RclRoot 'RetroCommander\demo\test-images'; Target = $RetroFsImages }
)
foreach ($l in $links) {
  if (-not (Test-Path $l.Target)) { Write-Warning "target missing, skipped: $($l.Target)"; continue }
  if (Test-Path $l.Link) { Write-Host "exists: $($l.Link)"; continue }
  New-Item -ItemType Directory -Force (Split-Path -Parent $l.Link) | Out-Null
  New-Item -ItemType Junction -Path $l.Link -Target $l.Target | Out-Null
  Write-Host "junction: $($l.Link) -> $($l.Target)"
}
