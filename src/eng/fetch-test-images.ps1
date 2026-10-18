# Downloads the disk-image test corpus that is NOT stored in git and unpacks it where the tests look.
#   tests\demo\test-images\            the Commodore and NDFS images (BIGDISK0-L.IMG, tor-disk.img, SMD-ID-100.IMG, WD0.img, ...)
#   tests\RetroIO.Tests\TestData\      BIGDISK0-K-201users.img.gz
# The zip is checked against the SHA-256 in src\eng\test-images.sha256, so a changed or damaged download is refused.
# Override the download address with -Url or the RETROIO_TEST_IMAGES_URL environment variable. Safe to run again.
param(
  [string]$Url = $(if ($env:RETROIO_TEST_IMAGES_URL) { $env:RETROIO_TEST_IMAGES_URL } else { 'https://hackercorp.blob.core.windows.net/upload/retroio-test-images/retroio-test-images-v2.zip' })
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expected = ((Get-Content (Join-Path $PSScriptRoot 'test-images.sha256') -TotalCount 1) -split '\s+')[0].ToLowerInvariant()

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('retroio-images-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
  $zip = Join-Path $tmp 'images.zip'
  Write-Host "Downloading $Url"
  $ProgressPreference = 'SilentlyContinue'
  Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

  $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }

  $x = Join-Path $tmp 'x'
  Expand-Archive -Path $zip -DestinationPath $x -Force
  $images = Join-Path $root 'tests\demo\test-images'
  $data   = Join-Path $root 'tests\RetroIO.Tests\TestData'
  New-Item -ItemType Directory -Force $images, $data | Out-Null
  Copy-Item -Path (Join-Path $x 'tools\RetroIO.Demo\test-images\*') -Destination $images -Recurse -Force
  Copy-Item -Path (Join-Path $x 'tests\RetroIO.Tests\TestData\*')   -Destination $data   -Recurse -Force
  Write-Host 'Done: test images are in tests\demo\test-images and tests\RetroIO.Tests\TestData'
}
finally {
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
