using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Roslyn compile-from-source for the opt-in dev hot-reload path. Kept in its own class so
    /// its Roslyn assembly references only resolve when it is actually called (a player build
    /// with hotReloadRoslyn=false never JITs this, so the game loads without the Roslyn DLLs).
    ///
    /// CAVEAT: Roslyn on .NET Framework 4.8 inside Bannerlord can bind-conflict with ButterLib's
    /// older System.Collections.Immutable / System.Reflection.Metadata. If Emit throws, the
    /// engine falls back to the prebuilt payload DLL. This is why the prebuilt path is primary.
    /// </summary>
    internal static class PayloadCompiler
    {
        internal static byte[] CompileFromSource(string sourceDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                Log.Info("[HOTRELOAD] Roslyn: source dir not found: " + sourceDir);
                return null;
            }

            string[] files = Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Log.Info("[HOTRELOAD] Roslyn: no .cs files in " + sourceDir);
                return null;
            }

            List<SyntaxTree> trees = new List<SyntaxTree>();
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(text), path: file));
            }

            List<MetadataReference> refs = new List<MetadataReference>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic)
                    {
                        continue;
                    }
                    string loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc) && File.Exists(loc) && seen.Add(loc))
                    {
                        refs.Add(MetadataReference.CreateFromFile(loc));
                    }
                }
                catch
                {
                }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                "BLTDeploymentCrashGuard.Payload",
                trees,
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

            using (MemoryStream ms = new MemoryStream())
            {
                EmitResult result = compilation.Emit(ms);
                if (!result.Success)
                {
                    int shown = 0;
                    foreach (Diagnostic d in result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error))
                    {
                        Log.Info("[HOTRELOAD] Roslyn ERROR " + d.Id + " " + d.GetMessage() + " @ " + d.Location);
                        if (++shown >= 15)
                        {
                            break;
                        }
                    }
                    Log.Screen("hot-reload: compile error (see CrashGuard.log) — kept previous generation");
                    return null;
                }
                return ms.ToArray();
            }
        }
    }
}
