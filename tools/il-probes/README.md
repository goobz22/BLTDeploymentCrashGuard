# IL probes

Small standalone reflection/IL tools for reading the **installed** game and mod assemblies
without a decompiler. They are how root causes get proven instead of guessed (see
`../../docs/DIAGNOSTICS.md`). Each is a self-contained net472 console exe that loads a target
DLL by path and resolves dependencies out of the game's `bin` + module folders.

## Build

```
cd tools/il-probes/<Tool> && dotnet build -c Release
# exe at tools/il-probes/<Tool>/bin/Release/net472/<Tool>.exe
```

The game path is hardcoded near the top of each `.cs` (Steam default). Edit it if your install
differs.

## The tools

| Tool | Purpose | Usage |
|---|---|---|
| **NameSearch** | Find every type/method/field whose name contains a term. First step when you don't know exact names. | `NameSearch.exe <dll> <term>` |
| **Inspect** | Dump one type's methods (with signatures), fields, properties; enum members with values. | `Inspect.exe <dll> <FullTypeName> [more types…]` |
| **IlDump** | Disassemble a method to IL. Supports `.cctor` (static ctor) and `.ctor` (instance ctors). This is what proves control flow and null-deref sites. | `IlDump.exe <dll> "<Ns.Type>::<Method>"` |
| **Callers** | Find methods that call a given member (substring match on the callee). | `Callers.exe <dll> <memberName>` |
| **VerCheck** | Print an assembly's version identity. | `VerCheck.exe <dll>` |

Common target DLLs:

- Engine: `<Game>/bin/Win64_Shipping_Client/TaleWorlds.*.dll`, `SandBox.*`, `StoryMode.*`
- SandBox module views: `<Game>/Modules/SandBox/bin/Win64_Shipping_Client/SandBox.View.dll`
- BannerlordTogether: `<Game>/Modules/BannerlordTogether/bin/Win64_Shipping_Client/BannerlordTogether.dll`

## Example: proving the 2026-09-04 MovementOrder crash

```
IlDump.exe TaleWorlds.MountAndBlade.dll "TaleWorlds.MountAndBlade.MovementOrder::.cctor"
# -> builds six defaults via newobj MovementOrder::.ctor
IlDump.exe TaleWorlds.MountAndBlade.dll "TaleWorlds.MountAndBlade.MovementOrder::.ctor"
# -> the one null-capable line: call Mission::get_Current; callvirt Mission::get_CurrentTime
```

That, plus a reflection check that `MovementOrder` is a `beforefieldinit` value type, was the
whole root cause. No decompiler, no guessing.
