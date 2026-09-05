#!/usr/bin/env bash
# Guards the three player-facing scripts against drift (audit 2026-09-04 found the Steam-library
# search list copy-pasted three times and already diverged: 11 entries in two scripts, 6 in the
# third). Checks:
#   1. install.cmd, share-log.cmd and collect-diagnostics.cmd carry IDENTICAL Steam path lists;
#   2. install.cmd downloads exactly the files dist/manifest.txt lists (when a manifest exists).
# Exit 1 on any drift. Run before committing a script or a release.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

extract() { # the quoted path lines between "for %%D in (" and ") do"
  awk '/^for %%D in \(/{f=1;next} f&&/^\) do/{f=0} f' "$1" | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//'
}
a=$(extract "$ROOT/install.cmd")
b=$(extract "$ROOT/share-log.cmd")
c=$(extract "$ROOT/collect-diagnostics.cmd")
fail=0
if [ "$a" != "$b" ]; then echo "DRIFT: install.cmd vs share-log.cmd Steam path lists differ"; diff <(echo "$a") <(echo "$b") || true; fail=1; fi
if [ "$a" != "$c" ]; then echo "DRIFT: install.cmd vs collect-diagnostics.cmd Steam path lists differ"; diff <(echo "$a") <(echo "$c") || true; fail=1; fi
n=$(printf '%s\n' "$a" | grep -c 'Bannerlord"' || true)
echo "steam path entries: $n per script; identical in all three: $([ "$fail" -eq 0 ] && echo yes || echo NO)"

if [ -f "$ROOT/dist/manifest.txt" ]; then
  count=0
  while read -r hash name; do
    [ -n "$name" ] || continue
    count=$((count + 1))
    if ! grep -q "dist/$name\"" "$ROOT/install.cmd"; then echo "install.cmd does not download $name (listed in dist/manifest.txt)"; fail=1; fi
    if ! grep -q "\"$name\"" "$ROOT/install.cmd"; then echo "install.cmd does not verify $name against the manifest"; fail=1; fi
    # 3. dist/ must match its own manifest byte-for-byte — install.cmd treats a mismatch as fatal for
    #    every player, so dist/ may never be committed out of sync (review 2026-09-04).
    if [ ! -f "$ROOT/dist/$name" ]; then echo "dist/$name is in the manifest but missing"; fail=1
    else
      actual=$(sha256sum "$ROOT/dist/$name" | cut -c1-64)
      if [ "$actual" != "$hash" ]; then echo "dist/$name does not match dist/manifest.txt (run tools/release.sh; never hand-copy into dist/)"; fail=1; fi
    fi
    # 4. What GitHub serves must hash to what the manifest says. Git normalises line endings of
    #    files it classifies as text at check-in (core.autocrlf), so a CRLF SubModule.xml hashed on
    #    disk was stored and served as LF, and every install.cmd rejected the first v1.3.2 push
    #    (2026-09-04). dist/ must be stored verbatim: .gitattributes `dist/** -text`, checked here,
    #    plus — once dist/ is committed — the committed blob itself must hash to the manifest value.
    attr=$(env -u GIT_DIR git -C "$ROOT" check-attr text -- "dist/$name" 2>/dev/null | sed -E 's/.*: text: //')
    if [ "$attr" != "unset" ]; then echo "dist/$name is not marked -text in .gitattributes (text attribute is '$attr'): git would normalise its line endings on check-in and GitHub would serve bytes that do not match dist/manifest.txt"; fail=1; fi
    if env -u GIT_DIR git -C "$ROOT" cat-file -e "HEAD:dist/$name" 2>/dev/null && env -u GIT_DIR git -C "$ROOT" diff --quiet HEAD -- "dist/$name" 2>/dev/null; then
      blob=$(env -u GIT_DIR git -C "$ROOT" show "HEAD:dist/$name" | sha256sum | cut -c1-64)
      if [ "$blob" != "$hash" ]; then echo "HEAD:dist/$name (the bytes GitHub serves) hashes to $blob but dist/manifest.txt says $hash — the committed blob was normalised; re-add it with dist/** -text in .gitattributes"; fail=1; fi
    fi
  done < <(awk 'NF==2{print $1, $2}' "$ROOT/dist/manifest.txt")
  [ "$count" -eq 3 ] || { echo "dist/manifest.txt lists $count file(s); expected 3 (harness DLL, payload DLL, SubModule.xml)"; fail=1; }
  echo "manifest cross-checked against install.cmd and dist/ hashes ($count files)"
fi

if [ "$fail" -eq 0 ]; then echo "lint-scripts: OK"; else echo "lint-scripts: FAIL"; exit 1; fi
