using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// The stable harness. This is the ONLY assembly Bannerlord loads (SubModule.xml). It owns
    /// the game lifecycle and forwards it to the current hot-reloadable payload generation via
    /// the HotReload engine. Keep this thin — changing it still needs a game restart, so almost
    /// all logic lives in the payload (which reloads without a restart).
    /// </summary>
    public class SubModule : MBSubModuleBase
    {
        private static HotReload _engine;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info(Diag.Banner());
            _engine = new HotReload();
            _engine.Start();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            if (_engine != null)
            {
                _engine.OnBeforeInitialModuleScreen();
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (_engine != null)
            {
                _engine.OnGameStart();
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            if (_engine != null)
            {
                _engine.OnMissionInit();
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            if (_engine != null)
            {
                _engine.Tick();
            }
        }
    }
}
