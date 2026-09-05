#!/usr/bin/env bash
# BLT Deployment Crash Guard — build + deploy + manifest + verify, in one step.
#
# Why this exists (audit 2026-09-04): the release is THREE files in TWO places (the game module
# and dist/), nothing stamped dist/SubModule.xml, and nothing cross-checked that the harness and
# payload in dist/ came from the same build — install.cmd fetches each file separately, so a
# half-updated dist/ shipped a mismatched pair silently. This script produces the whole set from
# ONE build, writes dist/manifest.txt (SHA256 of each file + the version) which install.cmd
# verifies, and refuses to call the tree release-ready unless every copy hash-matches.
#
# Usage:  tools/release.sh            # build both, deploy, manifest, verify
#         tools/release.sh --no-build # deploy + manifest + verify from the existing build output
# Env:    BANNERLORD_DIR              # game root (default: the Steam path below)
#
# Pushing dist/ to GitHub IS the release (install.cmd downloads from dist/ on main).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${BANNERLORD_DIR:-/c/Program Files (x86)/Steam/steamapps/common/Mount & Blade II Bannerlord}"
MOD="$GAME/Modules/BLTDeploymentCrashGuard"
BIN="$MOD/bin/Win64_Shipping_Client"
DIST="$ROOT/dist"

VERSION=$(sed -nE 's#.*<Version>([^<]+)</Version>.*#\1#p' "$ROOT/Directory.Build.props" | head -1)
[ -n "$VERSION" ] || { echo "cannot read <Version> from Directory.Build.props"; exit 1; }
echo "== release v$VERSION"

if [ "${1:-}" != "--no-build" ]; then
  echo "== build harness"; (cd "$ROOT/Harness" && dotnet build -c Release --nologo -v q)
  echo "== build payload"; (cd "$ROOT/Payload" && dotnet build -c Release --nologo -v q)
fi

H="$ROOT/Harness/bin/Release/BLTDeploymentCrashGuard.dll"
P="$ROOT/Payload/bin/Release/BLTDeploymentCrashGuard.Payload.dll"
X="$ROOT/SubModule.xml"
for f in "$H" "$P" "$X"; do [ -f "$f" ] || { echo "missing build output: $f"; exit 1; }; done
grep -q "v$VERSION" "$X" || { echo "SubModule.xml is not stamped v$VERSION — build the harness first"; exit 1; }

mkdir -p "$DIST" "$BIN"
LOCKED=0
deploy() { # src dst
  if cp "$1" "$2" 2>/dev/null; then echo "  -> $2"; else echo "  LOCKED (game running?): $2 — left as is"; LOCKED=1; fi
}
echo "== deploy to game module"
deploy "$H" "$BIN/BLTDeploymentCrashGuard.dll"
deploy "$P" "$BIN/BLTDeploymentCrashGuard.Payload.dll"
deploy "$X" "$MOD/SubModule.xml"
echo "== deploy to dist/"
cp "$H" "$DIST/BLTDeploymentCrashGuard.dll"
cp "$P" "$DIST/BLTDeploymentCrashGuard.Payload.dll"
cp "$X" "$DIST/SubModule.xml"

sha() { sha256sum "$1" | cut -c1-64; }
{
  echo "version=$VERSION"
  for f in BLTDeploymentCrashGuard.dll BLTDeploymentCrashGuard.Payload.dll SubModule.xml; do
    printf '%s  %s\n' "$(sha "$DIST/$f")" "$f"
  done
} > "$DIST/manifest.txt"
echo "== dist/manifest.txt"; sed 's/^/   /' "$DIST/manifest.txt"

echo "== verify (SHA256 must match across build / dist / game module)"
fail=0
for f in BLTDeploymentCrashGuard.dll BLTDeploymentCrashGuard.Payload.dll SubModule.xml; do
  case "$f" in
    SubModule.xml) src="$X"; mod="$MOD/$f";;
    BLTDeploymentCrashGuard.dll) src="$H"; mod="$BIN/$f";;
    *) src="$P"; mod="$BIN/$f";;
  esac
  a=$(sha "$src"); b=$(sha "$DIST/$f"); c=$( [ -f "$mod" ] && sha "$mod" || echo none )
  if [ "$a" = "$b" ] && [ "$a" = "$c" ]; then echo "  OK        $f"; else echo "  MISMATCH  $f  build=${a:0:12} dist=${b:0:12} module=${c:0:12}"; fail=1; fi
done
if [ "$fail" -ne 0 ]; then
  echo "== NOT release-ready (a copy does not match the build)."
  [ "$LOCKED" -eq 0 ] || echo "   The game is running: harness/SubModule copies were skipped. Close the game and re-run with --no-build."
  exit 1
fi
echo "== release-ready. Commit dist/ (incl. manifest.txt) + CHANGELOG, then push — pushing == releasing."
