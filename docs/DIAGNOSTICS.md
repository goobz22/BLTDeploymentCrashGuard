# Diagnostics playbook

How to investigate a crash, freeze, or wrong behaviour in this mod without guessing. The rule
this whole file exists to enforce: **prove the root cause from IL and logs before changing code.**
The location where a symptom manifests is almost never the root-cause location — find both.

Two investigation surfaces: **static** (read the installed assemblies) and **runtime** (turn on
tracing and read `CrashGuard.log`).

---

## 1. Static analysis — read the installed game

Use the IL probes in `../tools/il-probes/` (build once, see that folder's README). Typical flow:

1. `NameSearch.exe <dll> <term>` — locate the type/method when you only know a concept.
2. `Inspect.exe <dll> <FullTypeName>` — list its members and signatures.
3. `IlDump.exe <dll> "<Type>::<Method>"` — disassemble to IL; this proves control flow, the exact
   field/property a line touches, and where a null-deref can occur. Supports `.cctor` and `.ctor`.
4. `Callers.exe <dll> <member>` — find who calls a member (ordering, trigger paths).

What to look for:

- **Null-deref sites**: a `callvirt`/`ldfld` on a value produced by a getter that can return null.
- **Type-init fragility**: a `beforefieldinit` type (check `Type.Attributes`) whose `.cctor`
  depends on runtime state (e.g. `Mission.Current`) — the runtime can run it at an unsafe time.
  See `ENGINE-NOTES.md` § MovementOrder.
- **Ordering**: whether field X is set before method Y runs — dump the caller (`Callers.exe`) and
  read the call sequence.

Confirming a type is a struct / beforefieldinit (no probe needed, quick dotnet):

```csharp
var t = asm.GetType("TaleWorlds.MountAndBlade.MovementOrder");
t.IsValueType;                                            // struct?
(t.Attributes & TypeAttributes.BeforeFieldInit) != 0;    // lazy cctor timing?
```

---

## 2. Runtime tracing — read CrashGuard.log

Turn tracing on in `guardconfig.json` (module root):

```json
{ "tracing": true, "selfTest": true }
```

With hot-reload on, flipping `tracing` and dropping a rebuilt payload turns tracers on mid-session
(the flag is read fresh from disk on each apply). Log lives at
`Modules/BLTDeploymentCrashGuard/CrashGuard.log`.

### Log tags (grep targets)

Startup/health: `MOD HEALTH`, `[SELFTEST]`, `[HOTRELOAD]`, `[BATTLE-MODE]`.
Fixes each own a tag — `[SIEGE-CMD]`, `[COOP-CMD]`, `[IDENTITY]`, `[TIME-GUARD]`, `[GATE]`, etc.
Diagnostics added in the 2026-09-04 investigation:

| Tag | What it gives you |
|---|---|
| `[CHARGEN]` | Character-creation lifecycle + a **session-wide first-chance exception capture** with the full inner-exception chain and the throwing frames. |
| `[MO-PROBE]` | (dev) logs each `MovementOrder` construction + Mission.Current state — an origin probe for the type-init crash. |
| `[MO-INIT]` | The `MovementOrder` type-init guard's result at load: "initialized safely" or "already poisoned". |
| `[DIAG]` | Memory + engine-state heartbeat (WS/private/managed, GC counts, handles, threads; Mission/GameState/Campaign) every ~15 s and at every mission transition. Use it to see a leak/balloon build up before a symptom. |
| `[repeat] … ×N` | A high-frequency line coalesced by `TraceThrottle` (see below). |

### Reading a first-chance exception line

`CharacterCreationTrace` arms an `AppDomain.FirstChanceException` observer for the whole session.
Every exception in game code is logged **even if the game swallows it**, with:

- the full **inner-exception chain** (`<- INNER:` lines) — a `TypeInitializationException`'s real
  cause is always its inner exception;
- `CONTEXT:` — the engine state at the throw (is `Mission.Current` null, what state, etc.);
- a memory line;
- `LIVE …` frames — the **actual executing call stack** at the throw, which shows *who triggered*
  it. The exception's own stack is truncated to the throw point and hides the trigger; the live
  stack does not.

Key subtlety: a **failed type initializer is cached**. .NET runs a `.cctor` once; if it throws,
every later access re-throws the *same* exception without re-running the ctor. So a logged
type-init throw may be a **re-throw** whose `CONTEXT` (mission live) differs from the **origin**
(mission null, earlier). If a probe on the constructor never fires but the crash still happens,
the type was poisoned before the probe/handler existed — look earlier (load time), not at the
manifested frame.

---

## 3. The log itself: rotation + throttling

- **Rotation** (harness `Log.cs`): a segment rolls past 8 MB into a rolling window
  `CrashGuard.log.1 … .6` (~48 MB of history), checked every 256 writes. A burst no longer
  discards the evidence being chased.
- **Throttling** (`TraceThrottle`): identical repeated tracer lines are coalesced — first
  occurrence logs in full, repeats collapse to `[repeat] key ×N in Ys (collapsed)` at most every
  5 s. This is why a per-tick fight (e.g. BT re-requesting a time mode our guard blocks) no longer
  floods the log. When adding a high-frequency tracer, route it through `TraceThrottle.Emit(key, msg)`.

---

## 4. Crash reports on disk

- TaleWorlds logs: `C:/ProgramData/Mount and Blade II Bannerlord/logs/rgl_log_*.txt` (and
  `rgl_log_errors_*`, `watchdog_*`). `watchdog_*` carries the GPU/build tags; an
  `Unhandled Exception Code 0xE0434352` in `rgl_log_*` is a managed exception that killed the process.
- ButterLib crash reports (when it catches one): under the user's Documents Bannerlord folder.
  Note ButterLib does **not** catch every fatal — the mod's own first-chance capture is the backstop.

---

## 5. Discipline (learned the hard way, 2026-09-04)

- **Do not pattern-match a web result onto this repo.** A known vanilla symptom name is a lead,
  not a diagnosis. Prove it in *these* logs/IL.
- **The manifested frame is not the root cause.** `Formation.ResetAux` was where the crash
  surfaced; the cause was a `beforefieldinit` struct initialized while `Mission.Current` was null.
- **A quiet fix attempt built on a wrong theory is worse than none.** Revert speculative changes
  the moment the evidence contradicts them; keep the diagnostics.
- **Instrument to catch the class, not the instance.** When two failures rhyme (an
  AccessViolation in a finalizer, a null-at-transition), suspect one shared class and add telemetry
  that would reveal it (here: session-wide first-chance + memory heartbeat).
