#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/out"
RELEASE="$ROOT/release"

echo "=== Restore ==="
dotnet restore "$ROOT/Nanaininai.slnx"

echo "=== Build ==="
dotnet build "$ROOT/Nanaininai.slnx" -c Release --no-restore

echo "=== Publish Linux ==="
dotnet publish "$ROOT/src/Linux/Nanaininai.linux.csproj" \
  -c Release -r linux-x64 \
  -o "$OUT/linux" --no-restore --no-build

echo "=== Publish Windows ==="
dotnet publish "$ROOT/src/Nanaininai/Nanaininai.csproj" \
  -c Release -r win-x64 \
  -o "$OUT/windows" --no-restore --no-build

echo "=== Assemble release layout ==="
rm -rf "$RELEASE"
mkdir -p "$RELEASE/Linux/LabNetwork" "$RELEASE/Windows/LabNetwork"

cp "$OUT/linux/nanaininai-linux" "$RELEASE/Linux/LabNetwork/"
cp "$OUT/windows/Nanaininai.exe" "$RELEASE/Windows/LabNetwork/"

# Config + state from the sample deployment layout.
cp "$ROOT/usb-sample/LabNetwork/config.json" "$RELEASE/Linux/LabNetwork/"
cp "$ROOT/usb-sample/LabNetwork/state.json" "$RELEASE/Linux/LabNetwork/"
cp "$ROOT/usb-sample/LabNetwork/config.json" "$RELEASE/Windows/LabNetwork/"
cp "$ROOT/usb-sample/LabNetwork/state.json" "$RELEASE/Windows/LabNetwork/"

echo
echo "Done."
echo "Linux layout : $RELEASE/Linux/LabNetwork"
echo "Windows layout: $RELEASE/Windows/LabNetwork"
echo
echo "Linux binaries :"
ls -lh "$RELEASE/Linux/LabNetwork"
echo
echo "Windows binaries :"
ls -lh "$RELEASE/Windows/LabNetwork"
