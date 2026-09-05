# Releasing

The one checklist. Work through it top to bottom; the last step is the release.

**Pushing is releasing.** `install.cmd` downloads the three shipped files from `dist/` on branch
`main`, and the README one-liners curl `install.cmd`, `share-log.cmd` and `collect-diagnostics.cmd`
from the repo root of `main`. There is no separate publish step and no staging: the moment the push
lands, the next player who runs the installer gets what is in `dist/`. Never push mid-investigation
(`CLAUDE.md` § *Working discipline*).

The release is three files in two places:

| File | Game module | Repo |
|---|---|---|
| `BLTDeploymentCrashGuard.dll` (harness) | `<Game>/Modules/BLTDeploymentCrashGuard/bin/Win64_Shipping_Client/` | `dist/` |
| `BLTDeploymentCrashGuard.Payload.dll` | same `bin/Win64_Shipping_Client/` | `dist/` |
| `SubModule.xml` | module root | `dist/` |

plus `dist/manifest.txt`, which `install.cmd` verifies the download against.

## Versioning policy

`Directory.Build.props` `<Version>` is the **single** source of truth. MSBuild stamps both
assemblies from it and the `StampSubModuleVersion` target pokes `SubModule.xml`'s
`/Module/Version/@value` to `v$(Version)` on every harness build; `Diag` reads the version back off
the assembly identity for the log banner. Never hardcode a version anywhere else.

- **Patch** (`1.3.2` → `1.3.3`) — bug fixes, diagnostics, hardening, docs.
- **Minor** (`1.3.x` → `1.4.0`) — a new fix, guard or feature players can see; a new config key.

A release that fixes a crash the previous build still hits **must** appear as a numbered README
item: the README and the changelog are the only things telling a player to update.

---

## 1. Bump the version

Edit `<Version>` in `Directory.Build.props`. That is the only place. The harness build re-stamps
`SubModule.xml`; step 2 places `dist/SubModule.xml` — do not hand-edit either, and never copy into
`dist/` by hand: the build deliberately never writes there.

## 2. Run `tools/release.sh`

```bash
tools/release.sh                # build both, deploy, manifest, verify
tools/release.sh --no-build     # deploy + manifest + verify from existing build output
BANNERLORD_DIR="/d/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord" tools/release.sh
```

Close the game first. The script:

1. reads `<Version>` out of `Directory.Build.props` and refuses to continue if it cannot;
2. builds the harness then the payload (`dotnet build -c Release`), unless `--no-build`;
3. checks the repo-root `SubModule.xml` is stamped `v<Version>` — it aborts with *"SubModule.xml is
   not stamped v… — build the harness first"* if not. **This is why a version bump must be followed
   by a full run, not `--no-build`:** only a harness build re-stamps the XML;
4. copies all three files into the game module — a file the running game holds open is reported as
   `LOCKED (game running?)` and left alone rather than failing the copy — then into `dist/`, where
   the copies are unguarded and a failure aborts the run;
5. writes `dist/manifest.txt` — `version=<Version>` followed by one `<sha256>  <file>` line per
   shipped file — and prints it;
6. verifies the SHA256 of each file matches across **build output, `dist/` and the game module**.

"**release-ready**" means step 6 found all three matching in all three places. Anything else prints
`NOT release-ready` and exits non-zero — and if any copy was `LOCKED`, it tells you to close the game
and re-run with `--no-build`. Do not push on a non-zero exit: a mismatch there is exactly the
half-updated `dist/` that ships a harness and payload from different builds with no error at install
or load time.

## 3. Re-stamp the hand-written version literals

The build stamps the assemblies and both `SubModule.xml` copies. Two places in the documentation
carry the version as literal text and must be edited by hand:

```bash
grep -rn "1\.3\.2" README.md docs/FIX-REFERENCE.md        # substitute the version you are leaving
```

| File | Where | The literal |
|---|---|---|
| `README.md` | the intro paragraph under the title, above § *Install (players)* | "This README documents **v1.3.2**." |
| `docs/FIX-REFERENCE.md` | § *Single-version-source enforcement (`StampSubModuleVersion`)* | the quoted `<Version>1.3.2</Version>` |

Every other hit is **historical and must not be bumped** — it records the version a fix shipped in
or a bug affected:

- `README.md` § *Crash fixes*, item 9 — "affects v1.3.0–v1.3.1", "Fixed in v1.3.2".
- `README.md` § *Known co-op issues still being tracked* — "v1.3.2 collapsed the resulting log
  flood".
- `docs/FIX-REFERENCE.md` § *Character-creation lifecycle tracer and first-chance capture* — the
  documentation-divergence note about the v1.3.2 changelog wording.
- `docs/MODDING-PITFALLS.md` § *P5 · Trusting the changelog or the spec over the code*.
- The `Payload/` headers that date a change to v1.3.2 — `BattleMode.cs` and `EncounterLoopGuard.cs`
  ("until v1.3.2"), `TracePatches.cs` ("literally true since v1.3.2").

## 4. Write the changelog entry

A `CHANGELOG.md` entry under the new version, at the top of the file, in the house shape: the
**symptom**, the **proven cause**, the **fix** — and "needs a fresh game launch" wherever that is
true (harness changes and load-time fixes; `HOTRELOAD.md` § *What a reload cannot do (fresh launch
required)* lists them). Older entries stay untouched except to correct something that was wrong,
which is marked inline with a bracketed *(corrected \<date\>)*.

`MovementOrderTypeInitGuard` shipped with README, tag, ENGINE-NOTES and FIX-REFERENCE all landed and
no changelog entry at all. The changelog is the step that gets forgotten; do it here, not last.

**Cite `CHANGELOG.md` by version heading, never by line.** A new entry goes on top, so it shifts
every line below it and every `CHANGELOG.md:<line>` anchor pointing into the file. The stable form is
`CHANGELOG.md` § *v1.3.2 — solo battles fixed with tracing off…*. Enumerate existing line anchors
with:

```bash
grep -rn 'CHANGELOG\.md:[0-9]' --include=*.md --include=*.cs . | grep -v '^./CHANGELOG.md'
```

## 5. Doc rows for anything new

Per `.claude/rules/blt-docs-tools.md` § *Every new fix touches five places*:

1. `README.md` — a numbered item under § *Crash fixes*, § *Co-op & gameplay fixes* or
   § *Diagnostics & robustness*.
2. The guard's `[TAG]` in the README grep-tag legend (inside § *Diagnostics & robustness*) and in
   the log-tag index of `docs/FIX-REFERENCE.md`. A new fix takes its own tag — `[GATE]` and
   `[IDENTITY]` are already shared by two components each and must not grow.
3. A new config key: a row in the README `## Config` table **and** the key with its `_key`
   explanation string in `GuardConfig.DefaultJson`. The template is only written when
   `guardconfig.json` does not exist, so an existing install keeps its old text — say so if the
   change is a correction to an explanation.
4. `docs/FIX-REFERENCE.md` — a full entry (README item · Source · Class · Tag · Config · Scope, then
   Mechanism / Patched members / Limitations / Self-test) plus a row in each of the six indexes
   that applies — the sixth maps every health, fire and self-test id to its file.
5. `docs/ENGINE-NOTES.md` for a newly IL-proven engine fact (statement, evidence, members, date);
   `docs/BT-INTERNALS.md` for a BannerlordTogether-side fact; `docs/MODDING-PITFALLS.md` for a
   reverted attempt or a trap; `docs/MODDING-GUIDE.md` for a technique worth reusing.

A new health component or self-test is player-visible through `MOD HEALTH:` and `[SELFTEST]`, so it
belongs in the README item and the FIX-REFERENCE *Self-test* field as well.

## 6. Lint the player-facing scripts

```bash
tools/lint-scripts.sh
```

It fails if `install.cmd`, `share-log.cmd` and `collect-diagnostics.cmd` do not carry identical
Steam-library search lists (they had already drifted to 11 / 11 / 6 entries), or if `install.cmd`
does not both download and verify every file listed in `dist/manifest.txt`. Run it after touching
any of the three scripts and before every release.

## 7. Pre-release verification gate

The mod must be run, not just built. **A fresh game launch** — not a hot-reload: harness changes and
load-time fixes (`MovementOrderTypeInitGuard`, `ClientBootstrapFix`, `ClanModeSoloFix`) cannot be
delivered by a reload. Set `"selfTest": true` in `guardconfig.json` for this launch.

Read `Modules/BLTDeploymentCrashGuard/CrashGuard.log` and require all of:

| Check | What the log must show |
|---|---|
| Right build loaded | the banner `===== BLT Deployment Crash Guard v<Version> …` on the first line |
| Health clean | `MOD HEALTH: <n> active, all resolved` — no `NOT resolved` entries |
| Self-tests clean | `[SELFTEST] <n> passed, 0 failed` |
| Type-init fix took | `[MO-INIT] MovementOrder initialized safely (patched 1 site(s))` — **1**, not 0 or 2 |
| Chokepoints hooked | `[BATTLE-MODE] battle chokepoints hooked — chokepoints StartBattle=True OpenNew=True; lift targets 24/24 method(s)` |
| Deployment guards armed | `[DEPLOY-GUARD] deployment crash guards active — SetupTeams=guarded FinishDeployment=guarded` |
| Loop breaker armed | `[ENCOUNTER-GUARD] encounter-request loop breaker active (<n> method(s); local-Finish stamp hooked=True)` — `True`, not `False`. With BannerlordTogether absent the component is healthy and inert and the line does not appear |

Then **load one battle hosting solo** and confirm both:

- a `[BATTLE-MODE] … battles active (…, start-battle)` line appears — the decision fired at the
  chokepoint, with `tracing` off. Hosting alone it must be the `VANILLA battles active` variant;
- the battle opens with your own troops on the field and no deployment crash.

Any `NOT resolved` detail, any `[SELFTEST] FAIL`, a `[MO-INIT]` line reporting anything other than
one patched site, or a `[BATTLE-MODE]` line reporting a missing chokepoint or an unresolved lift
target, is a blocker: it means a game or BannerlordTogether update moved a member the fix depends on.

## 8. Commit

Docs ship with the binary, in the **same commit**: the built `dist/` (all three files plus
`manifest.txt`), `CHANGELOG.md`, and the README / FIX-REFERENCE / ENGINE-NOTES rows.

Multi-line messages go through a file — never `-m "…"` containing backticks, and never
`--no-verify`:

```bash
git commit -F <file>
```

Keep the `Co-Authored-By` trailer. Never `reset`, `checkout -- <path>`, `restore`, `stash`, `clean`
or `revert`.

## 9. Push — this is the release

```bash
git push origin main
```

`install.cmd` reads `dist/` from `main`, so the release is live the moment this lands. Because the
installer fetches each file separately, `dist/manifest.txt` must be pushed **with** the files it
describes — a push that lands the DLLs without the manifest, or the other way round, is the
"release may be mid-update on GitHub" state the installer is written to detect and refuse.

## What a player sees on an update

They re-run the installer one-liner. It:

- renames the existing `BLTDeploymentCrashGuard.dll` and `BLTDeploymentCrashGuard.Payload.dll` to
  `*.dll.prev` before downloading (the game locks a loaded DLL, but a rename is still allowed, so
  the update works even with Bannerlord running) — the `.prev` files are the previous build and are
  safe to delete;
- downloads the three files, then verifies each against `dist/manifest.txt` with `certutil` and
  prints `Verified 3 file(s) against the release manifest.` A mismatch aborts with an explanation
  and the advice to run it again in a minute; with no manifest or no `certutil` it prints that it is
  skipping the check;
- reminds them to tick the mod in the launcher **after** BannerlordTogether.

**The new build only takes effect on the next game launch.** If they updated while playing, the
running session is still on the old DLLs.

Also expected in `bin/Win64_Shipping_Client/` on a player install: shadow copies named
`BLTDeploymentCrashGuard.Payload.dll.<pid>.gen1.<hex>` — the harness loads a throw-away copy so the
original stays unlocked, and each launch sweeps the previous run's leftovers (README § *Files this
mod writes and renames*).

## Known doc-sync items

Documents that still contradict what shipped. Correct each the next time that file is touched; this
list exists so the next reader does not re-derive a problem that is already fixed.

- `docs/FIX-REFERENCE.md` § *Single-version-source enforcement (`StampSubModuleVersion`)* — its
  *Limitations* say `dist/SubModule.xml` "must be copied by hand as part of deploy". The
  `StampSubModuleVersion` target now copies the stamped XML into `dist/` on every harness build.
- `.claude/rules/blt-harness.md` § *Deploy = the release* — still describes a manual `md5sum`
  cross-check and states that nothing cross-checks the harness/payload pair. `tools/release.sh` does
  the SHA256 cross-check, and `dist/manifest.txt` plus `install.cmd` carry it through to the player.
  The same section also still says `collect-diagnostics.cmd` searches only 6 Steam-library paths;
  all three scripts now carry the same 11, enforced by `tools/lint-scripts.sh`. Its line anchors
  have drifted too: `install.cmd:9,58-60` for the download (the repo URL is `install.cmd:12`, the
  three `curl` lines are `install.cmd:63-65`), `install.cmd:51-56` for the `.prev` rename (it is
  `install.cmd:56-61`), `collect-diagnostics.cmd:14-19` for the path list (it is
  `collect-diagnostics.cmd:22-33`), and `HOTRELOAD.md:139-147` for the reload-cannot-do list — cite
  that one as `HOTRELOAD.md` § *What a reload cannot do (fresh launch required)*, since its line
  anchors have drifted before.
- `docs/BT-INTERNALS.md` § *14. BT behaviours a companion mod's fixes are built on* and
  `docs/FIX-REFERENCE.md` § *Illness death guard* still say the `noSickness` guard "stands down"
  when the third-party NoSickness mod is present. It never did — it coexists, and
  `Harness/GuardConfig.cs:92` now says so. The false claim was removed from the generated
  `guardconfig.json` text in this release and corrected inline in `CHANGELOG.md` § *v1.2.x — fixes
  added on top of the harness/payload split*; `docs/MODDING-PITFALLS.md` already flags it as live
  drift.
- Every `CHANGELOG.md:<line>` anchor in `docs/BT-INTERNALS.md`, `docs/ENGINE-NOTES.md`,
  `docs/FIX-REFERENCE.md`, `docs/MODDING-GUIDE.md` and `docs/MODDING-PITFALLS.md` predates the
  rewritten v1.3.2 entry and now lands on unrelated prose. Re-anchor each to its version heading per
  § *4. Write the changelog entry*, whose `grep` enumerates them.
