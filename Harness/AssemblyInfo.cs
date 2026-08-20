using System.Runtime.CompilerServices;

// The payload (prebuilt DLL and the Roslyn-compiled generation both named
// "BLTDeploymentCrashGuard.Payload") needs the harness's internal services:
// Log, Diag, GuardConfig, SelfHealing.
[assembly: InternalsVisibleTo("BLTDeploymentCrashGuard.Payload")]
