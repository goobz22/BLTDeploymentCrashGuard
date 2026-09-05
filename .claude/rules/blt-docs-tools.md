---
paths: ["docs/**", "tools/**", "README.md", "CHANGELOG.md"]
---

# Docs and tools — which fact goes where

One fact, one home. If a fact already lives in a document, extend that entry and cross-reference;
do not add a second copy (`docs/ENGINE-NOTES.md:20-23`). Citations are the authority: these
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
| `docs/FIX-REFERENCE.md` | Developer per-fix entries + the four indexes | Player-facing prose |
| `docs/MODDING-GUIDE.md` | Reusable public techniques, each in production here | What went wrong |
| `docs/MODDING-PITFALLS.md` | What bit us: mistakes, reverted attempts, gotchas, where each rule is now enforced | Techniques that just work |
| `CHANGELOG.md` | Per-version, user-visible: what changed and why | Ongoing investigation notes |
| `tools/il-probes/README.md` | How to build and run the probes | Findings the probes produced |

## Every newly proven engine fact goes to ENGINE-NOTES

Shape of an entry (`docs/ENGINE-NOTES.md:20-23`): put it in the subsystem section it belongs to as
a precise statement, the **evidence** (`file:line`, the IL member names, or the trace that captured
it), the game members involved, and the **date**. "Proven" means IL from the installed build, a live
tracer, a runtime probe, or a self-test that pins the value — not a web result
(`docs/ENGINE-NOTES.md:9-18`, `docs/DIAGNOSTICS.md` § 5). BT-side facts go to `docs/BT-INTERNALS.md`
instead, which is pinned to BT v0.5.0.1 on game 1.4.8.119303 (`docs/BT-INTERNALS.md:13-16`); note the
pin if a finding is version-specific.

## Every new fix touches five places

1. `README.md` — a numbered item under Crash fixes / Co-op & gameplay fixes / Diagnostics.
2. The guard's `[TAG]` — README's grep-tag list (`README.md:246-248`) and the log-tag index in
   `docs/FIX-REFERENCE.md`.
3. `README.md` `## Config` table **and** `GuardConfig.DefaultJson` — a row and a documented key,
   if the fix adds one.
4. `docs/FIX-REFERENCE.md` — a full entry: the header fields (README item · Source · Class · Tag ·
   Config · Scope) then Mechanism, Patched members, Limitations, Self-test
   (`docs/FIX-REFERENCE.md:10-25`), plus the index rows.
5. `CHANGELOG.md` — an entry under the version being released, saying the symptom, the proven cause
   and the fix.

Newly proven behaviour discovered on the way → ENGINE-NOTES / BT-INTERNALS; a reverted attempt or a
trap → MODDING-PITFALLS; a technique worth reusing → MODDING-GUIDE.

## Probes live in `tools/il-probes`

Standalone net472 console exes that read the **installed** assemblies without a decompiler; each
loads a target DLL by path and resolves dependencies from the game `bin` + module folders
(`tools/il-probes/README.md:3-6`). The game path is hardcoded near the top of each `.cs`.

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
the repo (`CLAUDE.md:92-93`). A new tool gets a row in `tools/il-probes/README.md`.
