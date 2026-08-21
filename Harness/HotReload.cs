using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// The reload engine (harness). Loads a payload generation, forwards ticks to it, and —
    /// in dev mode — watches for changes and hot-swaps generations WITHOUT a game restart.
    ///
    /// Mechanism (see plan): .NET Framework 4.8 cannot unload an assembly but CAN load a new
    /// one via Assembly.Load(bytes) — each generation gets fresh statics. Harmony keys patches
    /// by OWNER STRING, so a new generation (owner "…gen{N}") can UnpatchAll the previous
    /// generation's hooks. We apply the NEW generation first and only UnpatchAll the OLD one on
    /// success, so a failed reload never leaves the game unpatched.
    ///
    /// Two source modes:
    ///  - PREBUILT DLL (default, bulletproof, zero extra deps): read bytes from
    ///    BLTDeploymentCrashGuard.Payload.dll. Dev loop = `dotnet build` the payload, the engine
    ///    reloads it. This is also the player path (hot-reload off, load once).
    ///  - ROSLYN FROM SOURCE (opt-in, dev only): compile the payload .cs at runtime. Slicker
    ///    (edit-.cs), but Roslyn-in-Bannerlord can conflict with ButterLib's older
    ///    System.Collections.Immutable / System.Reflection.Metadata — if it fails, the engine
    ///    logs and falls back to the prebuilt DLL. See PayloadCompiler.
    ///
    /// Hard gate: hot-reload watching activates only when guardconfig hotReload=true AND a
    /// ".hotreload-dev" marker file sits in the module root — runtime code loading must be
    /// impossible on a normal player install.
    /// </summary>
    internal sealed class HotReload
    {
        private int _gen;
        private string _curGenId;
        private IPayload _current;
        private readonly ISharedState _shared = new SharedState();
        private volatile bool _pendingReload;

        private FileSystemWatcher _watcher;
        private string _moduleRoot;
        private string _prebuiltPath;
        private string _sourceDir;
        private bool _hotReloadEnabled;
        private bool _useRoslyn;
        private int _debounceTick;

        internal IPayload Current
        {
            get { return _current; }
        }

        internal void Start()
        {
            try
            {
                // Bannerlord loads module DLLs (this harness, 0Harmony, BT) via LoadFrom, which is
                // INVISIBLE to the default probing that resolves a byte-loaded payload's references.
                // Without this redirect the binder finds/loads a SECOND copy of the harness, the
                // payload implements THAT copy's IPayload, and the identity split surfaces as
                // "Method 'Apply' in PayloadEntry does not have an implementation" (field-hit
                // 2026-08-21 15:14 — the whole payload silently failed to load). Reusing the
                // already-loaded instance keeps every type single-identity.
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLoadedAssemblies;

                string binDir = Path.GetDirectoryName(typeof(HotReload).Assembly.Location);
                _moduleRoot = Path.GetFullPath(Path.Combine(binDir, "..", ".."));
                _prebuiltPath = Path.Combine(binDir, "BLTDeploymentCrashGuard.Payload.dll");

                bool marker = File.Exists(Path.Combine(_moduleRoot, ".hotreload-dev"));
                _hotReloadEnabled = GuardConfig.Bool("hotReload", false) && marker;
                _useRoslyn = _hotReloadEnabled && GuardConfig.Bool("hotReloadRoslyn", false) && PayloadCompiler.CompiledIn;
                _sourceDir = GuardConfig.String("payloadSourceDir", Path.Combine(_moduleRoot, "PayloadSource"));

                Log.Info("[HOTRELOAD] engine start — hotReload=" + _hotReloadEnabled + " roslyn=" + _useRoslyn +
                         " prebuilt=" + File.Exists(_prebuiltPath) + " sourceDir=" + (Directory.Exists(_sourceDir) ? _sourceDir : "(none)"));

                LoadGeneration("initial");

                if (_hotReloadEnabled)
                {
                    StartWatcher();
                }
            }
            catch (Exception ex)
            {
                Log.Info("[HOTRELOAD] start failed: " + ex);
            }
        }

        internal void Tick()
        {
            // Do the actual reload on the MAIN thread (Harmony patching is not thread-safe from
            // the watcher thread). Debounce ~400ms after the last change event.
            if (_pendingReload)
            {
                int now = Environment.TickCount;
                if (_debounceTick == 0 || now - _debounceTick >= 400 || now < _debounceTick)
                {
                    _pendingReload = false;
                    _debounceTick = 0;
                    LoadGeneration("reload");
                }
            }
            try
            {
                if (_current != null)
                {
                    _current.Tick();
                }
            }
            catch (Exception ex)
            {
                Log.Info("[HOTRELOAD] payload tick error: " + ex.Message);
            }
        }

        internal void OnGameStart()
        {
            EnsureLoaded("game-start");
            Safe(delegate { if (_current != null) _current.OnGameStart(); }, "OnGameStart");
        }

        internal void OnMissionInit()
        {
            Safe(delegate { if (_current != null) _current.OnMissionInit(); }, "OnMissionInit");
        }

        internal void OnBeforeInitialModuleScreen()
        {
            EnsureLoaded("module-screen");
            Safe(delegate { if (_current != null) _current.OnBeforeInitialModuleScreen(); }, "OnBeforeInitialModuleScreen");
        }

        private static Assembly ResolveFromLoadedAssemblies(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name;
                if (simpleName == "BLTDeploymentCrashGuard.Payload")
                {
                    return null; // generations load by bytes only — never redirect the payload name
                }
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!loaded.IsDynamic && loaded.GetName().Name == simpleName)
                    {
                        Log.Info("[HOTRELOAD] resolver: '" + args.Name + "' -> already-loaded " + loaded.FullName + " @ " + SafeLocation(loaded));
                        return loaded;
                    }
                }
                Log.Info("[HOTRELOAD] resolver: '" + args.Name + "' -> no loaded match (deferring to other resolvers)");
            }
            catch
            {
            }
            return null;
        }

        /// <summary>The evidence pack for a payload type-load failure: every loaded copy of the
        /// assemblies whose identity could have split, plus what the payload actually references.
        /// Written once per failure so the log answers "who supplied the duplicate".</summary>
        private static void DumpBindingDiagnostics(Assembly payloadAsm, Exception failure)
        {
            try
            {
                Assembly harness = typeof(HotReload).Assembly;
                Log.Info("[HOTRELOAD][DIAG] type-load failure: " + failure.GetType().Name + ": " + failure.Message);
                Log.Info("[HOTRELOAD][DIAG] this harness = " + harness.FullName + " @ " + SafeLocation(harness));
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loaded.IsDynamic)
                    {
                        continue;
                    }
                    string name = loaded.GetName().Name;
                    if (name == "BLTDeploymentCrashGuard" || name == "BLTDeploymentCrashGuard.Payload" || name == "0Harmony")
                    {
                        Log.Info("[HOTRELOAD][DIAG] loaded: " + loaded.FullName + " @ " + SafeLocation(loaded) +
                                 (ReferenceEquals(loaded, harness) ? " (THIS harness)" : "") +
                                 (ReferenceEquals(loaded, payloadAsm) ? " (the new payload)" : ""));
                    }
                }
                foreach (AssemblyName referenced in payloadAsm.GetReferencedAssemblies())
                {
                    if (referenced.Name == "BLTDeploymentCrashGuard" || referenced.Name == "0Harmony")
                    {
                        Log.Info("[HOTRELOAD][DIAG] payload references: " + referenced.FullName);
                    }
                }
            }
            catch (Exception exDiag)
            {
                Log.Info("[HOTRELOAD][DIAG] diagnostics failed: " + exDiag.Message);
            }
        }

        private static string SafeLocation(Assembly assembly)
        {
            try
            {
                return string.IsNullOrEmpty(assembly.Location) ? "(byte-loaded, no path)" : assembly.Location;
            }
            catch
            {
                return "(unknown)";
            }
        }

        /// <summary>
        /// A payload that fails to load means the game runs with ZERO guards — that must never
        /// be silent (the initial-load failure was file-log-only and the operator played a whole
        /// unprotected session). Retry at the lifecycle points, and warn ON SCREEN if still down.
        /// </summary>
        private void EnsureLoaded(string where)
        {
            if (_current != null)
            {
                return;
            }
            LoadGeneration("retry-" + where);
            if (_current == null)
            {
                Log.Screen("CRASH GUARD NOT ACTIVE — payload failed to load, all fixes are OFF (see CrashGuard.log)");
            }
        }

        private static void Safe(Action a, string what)
        {
            try { a(); }
            catch (Exception ex) { Log.Info("[HOTRELOAD] payload " + what + " error: " + ex.Message); }
        }

        private void LoadGeneration(string reason)
        {
            try
            {
                Assembly asm = null;
                // FIRST generation loads via LoadFrom at the canonical path: LoadFrom-context
                // dependency probing checks the requesting assembly's own directory, so the
                // payload's harness reference binds to THIS already-loaded file by path — no
                // AssemblyResolve chain involved. (Byte-loading gen1 field-failed 2026-08-21:
                // the harness reference bound to a different copy and PayloadEntry's IPayload
                // split identities — "Method 'Apply' does not have an implementation".)
                // Later generations (dev hot-reload) must byte-load for fresh statics, because
                // LoadFrom dedups by assembly identity and would return gen1 again.
                if (_current == null && !_useRoslyn && File.Exists(_prebuiltPath))
                {
                    try
                    {
                        // LoadFrom locks its file for the process lifetime, which would break the
                        // dev build-and-drop loop (copy over the canonical DLL fails with a sharing
                        // violation and no reload ever fires). Load a per-process SHADOW copy in
                        // the SAME directory instead — same-dir keeps LoadFrom dependency probing
                        // pointed at this harness; the canonical file stays writable.
                        CleanStaleShadows();
                        string shadowPath = _prebuiltPath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".gen1";
                        File.Copy(_prebuiltPath, shadowPath, overwrite: true);
                        asm = Assembly.LoadFrom(shadowPath);
                    }
                    catch (Exception exFrom)
                    {
                        Log.Info("[HOTRELOAD] shadow LoadFrom failed (" + exFrom.GetType().Name + ": " + exFrom.Message + ") — falling back to byte load");
                    }
                }
                if (asm == null)
                {
                    byte[] bytes = LoadPayloadBytes(reason);
                    if (bytes == null)
                    {
                        Log.Info("[HOTRELOAD] no payload bytes (" + reason + ") — keeping current generation");
                        return;
                    }
                    asm = Assembly.Load(bytes);
                }
                Type entryType;
                try
                {
                    entryType = asm.GetType("BLTDeploymentCrashGuard.PayloadEntry", throwOnError: true);
                }
                catch (Exception exType)
                {
                    DumpBindingDiagnostics(asm, exType);
                    throw;
                }
                IPayload payload = Activator.CreateInstance(entryType) as IPayload;
                if (payload == null)
                {
                    Log.Info("[HOTRELOAD] PayloadEntry is not an IPayload — keeping current generation");
                    return;
                }

                int newGen = _gen + 1;
                string newGenId = "bltogether.crashguard.gen" + newGen;
                Harmony harmony = new Harmony(newGenId);

                // Per-generation reset so a reload doesn't accumulate health/self-test entries.
                Diag.ResetHealth();
                SelfHealing.ResetTests();

                // Apply the NEW generation FIRST. If it throws, we keep the old one (below).
                payload.Apply(harmony, _shared);

                // Success — swap current, then remove the previous generation's hooks.
                string prevGenId = _curGenId;
                _current = payload;
                _curGenId = newGenId;
                _gen = newGen;
                if (prevGenId != null)
                {
                    try { new Harmony(prevGenId).UnpatchAll(prevGenId); }
                    catch (Exception exUn) { Log.Info("[HOTRELOAD] unpatch of " + prevGenId + " failed: " + exUn.Message); }
                }

                Log.Info("[HOTRELOAD] gen" + newGen + " applied (" + reason + ")" +
                         (prevGenId != null ? ", unpatched " + prevGenId : "") + " | " + Diag.HealthSummary());
                if (reason == "reload")
                {
                    Log.Screen("hot-reloaded gen" + newGen + " (no restart)");
                }
            }
            catch (Exception ex)
            {
                Log.Info("[HOTRELOAD] generation load FAILED (" + reason + ") — KEEPING current generation: " + ex);
                if (reason == "reload")
                {
                    Log.Screen("hot-reload FAILED — kept previous generation (see CrashGuard.log)");
                }
            }
        }

        /// <summary>Best-effort removal of shadow copies left by previous/other processes; a
        /// still-running sibling's locked shadow just stays (its delete throws and is skipped).</summary>
        private void CleanStaleShadows()
        {
            try
            {
                string dir = Path.GetDirectoryName(_prebuiltPath);
                foreach (string stale in Directory.GetFiles(dir, Path.GetFileName(_prebuiltPath) + ".*.gen1"))
                {
                    try { File.Delete(stale); }
                    catch { }
                }
            }
            catch
            {
            }
        }

        private byte[] LoadPayloadBytes(string reason)
        {
            if (_useRoslyn)
            {
                try
                {
                    byte[] compiled = PayloadCompiler.CompileFromSource(_sourceDir);
                    if (compiled != null)
                    {
                        return compiled;
                    }
                    Log.Info("[HOTRELOAD] Roslyn compile produced nothing — falling back to prebuilt DLL");
                }
                catch (Exception ex)
                {
                    Log.Info("[HOTRELOAD] Roslyn compile failed (" + ex.GetType().Name + ": " + ex.Message + ") — falling back to prebuilt DLL");
                }
            }
            try
            {
                if (File.Exists(_prebuiltPath))
                {
                    return File.ReadAllBytes(_prebuiltPath);
                }
                Log.Info("[HOTRELOAD] prebuilt payload not found at " + _prebuiltPath);
            }
            catch (Exception ex)
            {
                Log.Info("[HOTRELOAD] reading prebuilt payload failed: " + ex.Message);
            }
            return null;
        }

        private void StartWatcher()
        {
            try
            {
                // Watch whichever we reload from: the source dir (Roslyn) or the prebuilt DLL.
                string dir;
                string filter;
                if (_useRoslyn && Directory.Exists(_sourceDir))
                {
                    dir = _sourceDir;
                    filter = "*.cs";
                }
                else
                {
                    dir = Path.GetDirectoryName(_prebuiltPath);
                    filter = "BLTDeploymentCrashGuard.Payload.dll";
                }
                _watcher = new FileSystemWatcher(dir, filter);
                _watcher.IncludeSubdirectories = _useRoslyn;
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.Renamed += OnChanged;
                _watcher.EnableRaisingEvents = true;
                Log.Info("[HOTRELOAD] watching " + dir + " (" + filter + ") — edit and the mod reloads with no restart");
            }
            catch (Exception ex)
            {
                Log.Info("[HOTRELOAD] watcher setup failed: " + ex.Message);
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            _pendingReload = true;
            _debounceTick = Environment.TickCount;
        }
    }
}
