using System.Runtime.CompilerServices;

// The payload (prebuilt DLL and the Roslyn-compiled generation both named
// "BLTDeploymentCrashGuard.Payload") needs the harness's internal services:
// Log, Diag, GuardConfig, SelfHealing.
// The harness API the payload uses (Log/Diag/GuardConfig/SelfHealing/contracts) is PUBLIC:
// payload builds carry a per-build assembly name (see the payload csproj), which an
// InternalsVisibleTo entry — matched by exact name — could never cover.
[assembly: InternalsVisibleTo("BLTDeploymentCrashGuard.Payload")]
