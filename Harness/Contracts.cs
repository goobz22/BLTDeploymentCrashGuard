using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Contract between the stable harness (loaded once by Bannerlord) and a hot-reloadable
    /// payload generation. The harness owns the game lifecycle and forwards it here; each
    /// reload creates a fresh payload instance from a newly-loaded assembly. Public so the
    /// payload assembly (which references the harness) can implement it.
    /// </summary>
    public interface IPayload
    {
        /// <summary>Install all guards/fixes/tracers using the generation's Harmony instance.
        /// Called once when this generation becomes current.</summary>
        void Apply(Harmony harmony, ISharedState shared);

        /// <summary>Per-frame tick (self-throttled internally). Forwarded from the harness.</summary>
        void Tick();

        void OnGameStart();
        void OnMissionInit();
        void OnBeforeInitialModuleScreen();
    }

    /// <summary>
    /// A key/value bag OWNED BY THE HARNESS, so state that must survive a payload reload
    /// (which resets all payload statics) persists across generations — guard fire counts,
    /// the launch session id, and BattleMode's foreign-patch stash. Public for cross-assembly
    /// use from the payload.
    /// </summary>
    public interface ISharedState
    {
        T Get<T>(string key);
        object GetObject(string key);
        void Set(string key, object value);
        bool Has(string key);
    }
}
