# IL probes

Small standalone reflection/IL tools for reading the **installed** game and mod assemblies
without a decompiler. They are how root causes get proven instead of guessed (see
`../../docs/DIAGNOSTICS.md`).

The four **IL readers** — `NameSearch`, `Inspect`, `IlDump`, `Callers` — are self-contained net472
console exes that load a target DLL by path and resolve its dependencies out of three hardcoded
directories: `Modules/BannerlordTogether`, `Modules/Bannerlord.Harmony` and the game `bin`
(`Inspect/Inspect.cs:9-25`). `VerCheck` is the exception: it prints assembly identity only, takes
**no game path** and needs **no dependency resolution**, which is why it works on any DLL anywhere
(`VerCheck/VerCheck.cs:1-5`).

## Build

```
cd tools/il-probes/<Tool> && dotnet build -c Release
# exe at tools/il-probes/<Tool>/bin/Release/net472/<Tool>.exe
```

The game path is hardcoded near the top of each IL reader's `.cs` (Steam default). Edit it if your
install differs. `VerCheck` has no path to edit.

## The tools

| Tool | Purpose | Usage |
|---|---|---|
| **NameSearch** | Find every type/method/field whose name contains a term. First step when you don't know exact names. | `NameSearch.exe <dll> <term>` |
| **Inspect** | Dump one type's methods (with signatures), fields, properties; enum members with values. | `Inspect.exe <dll> <FullTypeName> [more types…]` |
| **IlDump** | Disassemble a method to IL. Supports `.cctor` (static ctor) and `.ctor` (instance ctors). This is what proves control flow and null-deref sites. | `IlDump.exe <dll> "<Ns.Type>::<Method>"` |
| **Callers** | Find methods that call a given member (substring match on the callee). | `Callers.exe <dll> <memberName>` |
| **VerCheck** | Print an assembly's version identity. No game path, no dependency resolution — works on any DLL. | `VerCheck.exe <dll>` |

## Where the DLLs actually live

The game `bin` holds **only** the `TaleWorlds.*` assemblies (plus `0Harmony` and native/third-party
DLLs). Everything else is under a module, and pointing a probe at
`<Game>/bin/Win64_Shipping_Client/SandBox.dll` throws `FileNotFoundException`:

| Assemblies | Directory |
|---|---|
| `TaleWorlds.*` | `<Game>/bin/Win64_Shipping_Client/` |
| `SandBox.dll`, `SandBox.View.dll`, `SandBox.ViewModelCollection.dll`, `SandBox.GauntletUI*.dll` | `<Game>/Modules/SandBox/bin/Win64_Shipping_Client/` |
| `StoryMode.dll` and siblings | `<Game>/Modules/StoryMode/bin/Win64_Shipping_Client/` |
| `TaleWorlds.MountAndBlade.View.dll`, `TaleWorlds.MountAndBlade.GauntletUI.dll`, `TaleWorlds.MountAndBlade.Platform.PC.dll` | `<Game>/Modules/Native/bin/Win64_Shipping_Client/` |
| `BannerlordTogether.dll` | `<Game>/Modules/BannerlordTogether/bin/Win64_Shipping_Client/` |

The split is module-vs-app-base, not view-vs-engine. Full listing evidence:
`../../docs/ENGINE-NOTES.md` § *Target framework and the engine assembly split*.

## `NOT FOUND` is inconclusive, not proof of absence

An IL reader whose target's **dependency closure does not resolve** returns a *partial* type list, so
a type or member that exists reads as missing — silently, with no load error. The three hardcoded
resolver directories above are not the closure of an arbitrary module assembly; in particular
`Modules/Native` is **not** among them, and it holds
`TaleWorlds.MountAndBlade.View` / `.GauntletUI` / `.Platform.PC`, which the SandBox view assemblies
need. `Inspect` on `SandBox.View.dll` prints `NOT FOUND` for `SandBox.View.Map.MapScreen` from the
module folder; copying the SandBox module DLLs, the game-bin `TaleWorlds.*` DLLs and the
`Modules/Native` DLLs into one directory and probing there resolves the type and its members.

So: when a probe says `NOT FOUND`, assemble the closure and re-probe before concluding anything —
and never record "the game update removed this member" on a bare miss. The reproduction, including
the way `NameSearch` still prints *nested* type names from metadata after the outer type failed to
load, is in `../../docs/ENGINE-NOTES.md` § *A probe `NOT FOUND` is not proof of absence*.

## Example: proving the 2026-09-04 MovementOrder crash

```
IlDump.exe TaleWorlds.MountAndBlade.dll "TaleWorlds.MountAndBlade.MovementOrder::.cctor"
# -> builds six defaults via newobj MovementOrder::.ctor
IlDump.exe TaleWorlds.MountAndBlade.dll "TaleWorlds.MountAndBlade.MovementOrder::.ctor"
# -> the one null-capable line: call Mission::get_Current; callvirt Mission::get_CurrentTime
```

That, plus a reflection check that `MovementOrder` is a `beforefieldinit` value type, was the
whole root cause. No decompiler, no guessing.
