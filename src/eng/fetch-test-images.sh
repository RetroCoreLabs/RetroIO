#!/usr/bin/env bash
# Downloads the disk-image test corpus that is NOT stored in git and unpacks it where the tests look.
#   tests/demo/test-images/              the Commodore and NDFS images (BIGDISK0-L.IMG, tor-disk.img, SMD-ID-100.IMG, WD0.img, ...)
#   tests/RetroIO.Tests/TestData/        BIGDISK0-K-201users.img.gz
# The zip is checked against the SHA-256 in eng/test-images.sha256, so a changed or damaged download is refused.
# Override the download address with RETROIO_TEST_IMAGES_URL. Safe to run again.
set -euo pipefail
cd "$(dirname "$0")/.."

URL="${RETROIO_TEST_IMAGES_URL:-https://hackercorp.blob.core.windows.net/upload/retroio-test-images/retroio-test-images-v2.zip}"
EXPECTED="$(cut -d' ' -f1 eng/test-images.sha256 | head -n 1)"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Downloading $URL"
curl -fL --retry 3 -o "$TMP/images.zip" "$URL"

ACTUAL="$(sha256sum "$TMP/images.zip" | cut -d' ' -f1)"
if [ "$ACTUAL" != "$EXPECTED" ]; then
  echo "SHA-256 mismatch: expected $EXPECTED, got $ACTUAL" >&2
  exit 1
fi

unzip -q "$TMP/images.zip" -d "$TMP/x"
mkdir -p tests/demo/test-images tests/RetroIO.Tests/TestData
cp -a "$TMP/x/tools/RetroIO.Demo/test-images/." tests/demo/test-images/
cp -a "$TMP/x/tests/RetroIO.Tests/TestData/." tests/RetroIO.Tests/TestData/
echo "Done: test images are in tests/demo/test-images and tests/RetroIO.Tests/TestData"
