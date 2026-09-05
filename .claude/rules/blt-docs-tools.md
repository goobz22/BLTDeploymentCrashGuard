---
paths: ["docs/**", "tools/**", "README.md", "CHANGELOG.md", "HOTRELOAD.md", "UPSTREAM_BUG_REPORT.md"]
---

# Docs and tools — which fact goes where

One fact, one home. If a fact already lives in a document, extend that entry and cross-reference;
do not add a second copy (`docs/ENGINE-NOTES.md:27-28`). Citations are the authority: these
documents summarise code, the code does not summarise them — when they disagree the code is right
(`docs/MODDING-GUIDE.md:17-18`). Every claim carries a repo-relative `file:line`.

## DOC_MAP

| Document | Holds | Does not hold |
|---|---|---|
| `README.md` | Player-facing: install, every fix as a numbered item, the config table, log-tag legend, troubleshooting, known issues | Internals, IL evidence |
| `CLAUDE.md` | Always-loaded agent operating guide — architecture, version source, build/deploy, house rules. Kept short; pointers out | Per-fix detail |
| `HOTRELOAD.md` | The dev hot-reload workflow and its trade-offs | Deployment/release steps |
| `docs/DIAGNOSTICS.md` | How to investigate: probes, tracing, first-chance capture, log tags, rotation | Engine facts themselves |
| `docs/ENGINE-NOTES.md` | Bannerlord engine facts **proven** from IL, per subsystem, with evidence + date | Speculation, BT internals |
| `docs/BT-INTERNALS.md` | BannerlordTogether internals as observed from IL — unofficial, version-pinned | Vanilla engine facts |
| `docs/FIX-REFERENCE.md` | Developer per-fix entries + the **six** indexes (0 co-op scope · 1 log tag → file · 2 config key → file · 3 patched member → fix · 4 on-screen message → file · 5 `MOD HEALTH` / `SELFTEST` component id → file) | Player-facing prose |
| `docs/RELEASE.md` | THE release checklist: bump → `tools/release.sh` → hand-edited version literals → CHANGELOG → `tools/lint-scripts.sh` → fresh-launch gate → commit → push (== release); "Known doc-sync items" | Per-fix detail, engine facts |
| `docs/MODDING-GUIDE.md` | Reusable public techniques, each in production here | What went wrong |
| `docs/MODDING-PITFALLS.md` | What bit us: mistakes, reverted attempts, gotchas, where each rule is now enforced | Techniques that just work |
| `docs/SPEC-pregnancy-coop-sync.md` | The design spec for the co-op pregnancy/birth sync feature: problem, goal, wire format, host-authoritative flow | Per-fix reference rows; BT internals beyond what the spec needs |
| `docs/UPSTREAM_CONTRIBUTION.md` | Which of our fixes are worth offering to the BT authors, ranked by value and self-containedness | The bug evidence itself |
| `UPSTREAM_BUG_REPORT.md` (repo root) | The BT-side bug report as sent: symptoms, logs, the pinned environment. It is the environment-of-record other docs cite (`docs/BT-INTERNALS.md:15`, `docs/ENGINE-NOTES.md:32`) | Our own fix mechanics |
| `CHANGELOG.md` | Per-version, user-visible: what changed and why | Ongoing investigation notes |
| `tools/il-probes/README.md` | How to build and run the probes | Findings the probes produced |

Which rule loads when you edit one of these: `docs/**`, `tools/**`, `README.md`, `CHANGELOG.md`,
`HOTRELOAD.md` and `UPSTREAM_BUG_REPORT.md` load this file; `Payload/**` and `tests/**` load
`.claude/rules/blt-payload-guards.md`; `Harness/**`, `Directory.Build.props`, `SubModule.xml`,
`dist/**` and the three root `.cmd` scripts load `.claude/rules/blt-harness.md`. `CLAUDE.md` needs
no `paths:` entry — it is always loaded.

## Every newly proven engine fact goes to ENGINE-NOTES

Shape of an entry (`docs/ENGINE-NOTES.md:25-30`): put it in the subsystem section it belongs to as
a precise statement, the **evidence** (`file:line`, the IL member names, or the trace that captured
it), the game members involved, and the **date**. "Proven" means IL from the installed build, a live
tracer, a runtime probe, a self-test that pins the value, or — for the few entries that say so —
decompilation; never a web result (`docs/ENGINE-NOTES.md:8-23`, `docs/DIAGNOSTICS.md` § 5
*Discipline*). BT-side facts go to `docs/BT-INTERNALS.md`
instead, which is pinned to BT v0.5.0.1 on game 1.4.8.119303 (`docs/BT-INTERNALS.md:13-16`); note the
pin if a finding is version-specific.

## Every new fix touches six places

Cite these documents by **section heading**, never by line number — README.md and HOTRELOAD.md
line anchors drifted three times in one day (2026-09-04).

1. `README.md` — a numbered item under § *Crash fixes*, § *Co-op & gameplay fixes* or
   § *Diagnostics & robustness*.
2. The guard's `[TAG]` — README's grep-tag legend (§ *Diagnostics & robustness*, the tag legend
   item) and `docs/FIX-REFERENCE.md` § *Index 1: log tag → file*. `[GATE]` and `[IDENTITY]` are
   already shared by two components each; a new fix takes its own tag rather than joining either.
3. `README.md` § *Config* table **and** `GuardConfig.DefaultJson` — a row and a documented key with
   its `_key` explanation, if the fix adds one.
4. `docs/FIX-REFERENCE.md` — a full entry: the header fields (README item · Source · Class · Tag ·
   Config · Scope) then Mechanism, Patched members, Limitations, Self-test, plus a row in each of
   the **six** indexes that applies: Index 0 co-op scope, Index 1 log tag → file, Index 2 config
   key → file, Index 3 patched member → fix, Index 4 on-screen message → file, Index 5
   `MOD HEALTH` / `SELFTEST` component id → file (every `Diag.Report` component id,
   `SelfHealing.RecordFire` id and `RegisterTest` name gets a row).
5. `CHANGELOG.md` — an entry under the version being released, saying the symptom, the proven cause
   and the fix.
6. `docs/RELEASE.md` — nothing per fix, but the release itself follows that checklist; a fix that
   adds a hand-maintained version literal or a new shipped file adds a line there.

Newly proven behaviour discovered on the way → ENGINE-NOTES / BT-INTERNALS; a reverted attempt or a
trap → MODDING-PITFALLS; a technique worth reusing → MODDING-GUIDE.

## Probes live in `tools/il-probes`

Standalone net472 console exes that read the **installed** assemblies without a decompiler
(`tools/il-probes/README.md:3-6`). The four IL readers — `NameSearch`, `Inspect`, `IlDump`,
`Callers` — load a target DLL by path, hardcode the Steam game path near the top of their `.cs`
(`tools/il-probes/Inspect/Inspect.cs:9-10`) and install an `AssemblyResolve` handler that probes the
game `bin` + module folders (`Inspect.cs:20-25`). `VerCheck` is the exception: five lines that read
assembly identity only, with no game path and no resolver (`tools/il-probes/VerCheck/VerCheck.cs:1-5`),
which is why it works on any DLL. `tools/il-probes/README.md` says the same, and also records
where each game DLL actually lives (module folders, not `<Game>/bin`) and that a probe's
`NOT FOUND` is inconclusive until the target's dependency closure resolves — read it before
concluding a member is gone.

```
cd tools/il-probes/<Tool> && dotnet build -c Release
# exe at tools/il-probes/<Tool>/bin/Release/net472/<Tool>.exe
```

| Tool | Usage |
|---|---|
| `NameSearch` | `NameSearch.exe <dll> <term>` — find types/methods/fields containing a term |
| `Inspect` | `Inspect.exe <dll> <FullTypeName> [more…]` — members, signatures, enum values |
| `IlDump` | `IlDump.exe <dll> "<Ns.Type>::<Method>"` — IL; supports `.cctor` and `.ctor` |
| `Callers` | `Callers.exe <dll> <memberName>` — who calls a member |
| `VerCheck` | `VerCheck.exe <dll>` — assembly version identity |

Durable tooling belongs in `tools/`; throwaway probe scripts belong in the session scratchpad, not
the repo (`CLAUDE.md` § *Working discipline*). A new tool gets a row in `tools/il-probes/README.md`.
