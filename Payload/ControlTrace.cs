using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Log-only tracer for BATTLE COMMAND ASSIGNMENT — who controls which agents,
    /// teams, and formations. Purpose: in co-op sieges the client player sometimes
    /// receives command of the HOST's army; these hooks record every native control
    /// handoff (with caller stacks) plus a full control-map dump when deployment
    /// finishes, so the misassignment can be located and then patched.
    ///
    /// Hooks (all rare, deployment/handover-time events):
    ///  - Agent.set_Controller            → logged only when an agent becomes Player-controlled
    ///  - Mission.set_MainAgent           → which agent is "the player" locally
    ///  - OrderController.set_Owner       → who commands a team's orders
    ///  - Formation.set_PlayerOwner       → per-formation ownership
    ///  - Team.set_GeneralAgent           → team general assignment
    ///  - Team.AssignPlayerAsSergeantOfFormation
    ///  - Mission.OnDeploymentFinished    → full control-map dump
    /// </summary>
    internal static class ControlTrace
    {
        internal static void Apply(Harmony harmony)
        {
            int applied = 0;
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Agent", "set_Controller", nameof(AgentControllerPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Mission", "set_MainAgent", nameof(MainAgentPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.OrderController", "set_Owner", nameof(OrderOwnerPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Formation", "set_PlayerOwner", nameof(FormationOwnerPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Team", "set_GeneralAgent", nameof(GeneralAgentPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Team", "AssignPlayerAsSergeantOfFormation", nameof(SergeantAssignPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Mission", "OnDeploymentFinished", null, nameof(DeploymentFinishedPostfix));
            // Siege-command evidence (2026-09-03): who flips a formation to/from AI control, who
            // re-assigns the player's role, the death hand-off, and the tactic's troop shuffles.
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Formation", "SetControlledByAI", nameof(FormationAiControlPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Team", "SetPlayerRole", nameof(PlayerRolePrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Team", "DelegateCommandToAI", nameof(DelegateCommandPrefix));
            applied += PatchByName(harmony, "TaleWorlds.MountAndBlade.Formation", "TransferUnits", nameof(TransferUnitsPrefix));
            Log.Info("[CONTROL] control tracer active on " + applied + " method(s)");
        }

        private static int PatchByName(Harmony harmony, string typeName, string methodName, string prefixName, string postfixName = null)
        {
            int count = 0;
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Log.Info("[CONTROL] type not found: " + typeName);
                    return 0;
                }
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != methodName || method.IsAbstract)
                    {
                        continue;
                    }
                    try
                    {
                        HarmonyMethod prefix = prefixName != null ? new HarmonyMethod(typeof(ControlTrace), prefixName) : null;
                        HarmonyMethod postfix = postfixName != null ? new HarmonyMethod(typeof(ControlTrace), postfixName) : null;
                        harmony.Patch(method, prefix, postfix);
                        count++;
                    }
                    catch (Exception exOne)
                    {
                        Log.Info("[CONTROL] could not patch " + typeName + "." + methodName + ": " + exOne.Message);
                    }
                }
                if (count == 0)
                {
                    Log.Info("[CONTROL] no patchable method " + typeName + "." + methodName);
                }
            }
            catch (Exception ex)
            {
                Log.Info("[CONTROL] patch-by-name failed for " + typeName + "." + methodName + ": " + ex.Message);
            }
            return count;
        }

        // ---- hooks ----

        private static void AgentControllerPrefix(Agent __instance, AgentControllerType value)
        {
            try
            {
                if (value != AgentControllerType.Player)
                {
                    return; // only takeovers are signal; AI churn is noise
                }
                Log.Info("[CONTROL] Agent.Controller=Player -> " + DescribeAgent(__instance) + Stack());
            }
            catch
            {
            }
        }

        private static void MainAgentPrefix(object value)
        {
            try
            {
                Log.Info("[CONTROL] Mission.MainAgent = " + DescribeAgent(value as Agent) + Stack());
            }
            catch
            {
            }
        }

        private static void OrderOwnerPrefix(object __instance, object value)
        {
            try
            {
                Log.Info("[CONTROL] OrderController.Owner = " + DescribeAgent(value as Agent) + Stack());
            }
            catch
            {
            }
        }

        private static void FormationOwnerPrefix(Formation __instance, object value)
        {
            try
            {
                Log.Info("[CONTROL] Formation[" + SafeFormation(__instance) + "].PlayerOwner = " + DescribeAgent(value as Agent) + Stack());
            }
            catch
            {
            }
        }

        /// <summary>Logged only when the value actually flips (the setter early-returns otherwise).</summary>
        private static void FormationAiControlPrefix(Formation __instance, bool isControlledByAI)
        {
            try
            {
                if (__instance == null || __instance.IsAIControlled == isControlledByAI)
                {
                    return;
                }
                Log.Info("[CONTROL] Formation[" + SafeFormation(__instance) + "].IsAIControlled " + __instance.IsAIControlled + " -> " + isControlledByAI +
                         " (units " + __instance.CountOfUnits + ", team " + SafeTeam(__instance.Team) + ")" + Stack());
            }
            catch
            {
            }
        }

        private static void PlayerRolePrefix(Team __instance, bool isPlayerGeneral, bool isPlayerSergeant)
        {
            try
            {
                Log.Info("[CONTROL] Team(" + SafeTeam(__instance) + ").SetPlayerRole(general=" + isPlayerGeneral + ", sergeant=" + isPlayerSergeant + ")" + Stack());
            }
            catch
            {
            }
        }

        private static void DelegateCommandPrefix(Team __instance)
        {
            try
            {
                Log.Info("[CONTROL] Team(" + SafeTeam(__instance) + ").DelegateCommandToAI — every formation to the AI" + Stack());
            }
            catch
            {
            }
        }

        private static void TransferUnitsPrefix(Formation __instance, Formation target, int unitCount)
        {
            try
            {
                Log.Info("[CONTROL] tactic TransferUnits " + unitCount + " from Formation[" + SafeFormation(__instance) + "] (AI " +
                         (__instance != null && __instance.IsAIControlled) + ") to Formation[" + SafeFormation(target) + "] (AI " +
                         (target != null && target.IsAIControlled) + ")" + Stack());
            }
            catch
            {
            }
        }

        private static void GeneralAgentPrefix(Team __instance, object value)
        {
            try
            {
                Log.Info("[CONTROL] Team(" + SafeTeam(__instance) + ").GeneralAgent = " + DescribeAgent(value as Agent) + Stack());
            }
            catch
            {
            }
        }

        private static void SergeantAssignPrefix(Team __instance, object[] __args)
        {
            try
            {
                StringBuilder sb = new StringBuilder("[CONTROL] Team(" + SafeTeam(__instance) + ").AssignPlayerAsSergeantOfFormation(");
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        Agent agent = __args[i] as Agent;
                        sb.Append(agent != null ? DescribeAgent(agent) : (__args[i] != null ? __args[i].ToString() : "null"));
                    }
                }
                sb.Append(')');
                Log.Info(sb + Stack());
            }
            catch
            {
            }
        }

        private static void DeploymentFinishedPostfix()
        {
            DumpControlMap("deployment-finished");
        }

        // ---- control map ----

        internal static void DumpControlMap(string reason)
        {
            try
            {
                Mission mission = Mission.Current;
                if (mission == null)
                {
                    return;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append("[CONTROL] ===== control map (").Append(reason).Append(") =====");
                sb.Append("\n      MainAgent=").Append(DescribeAgent(mission.MainAgent));
                sb.Append(" InitialPlayerAgent=").Append(DescribeAgent(mission.InitialPlayerAgent));
                foreach (Team team in mission.Teams)
                {
                    if (team == null)
                    {
                        continue;
                    }
                    sb.Append("\n      Team ").Append(SafeTeam(team))
                      .Append(" isPlayerTeam=").Append(team == mission.PlayerTeam ? 1 : 0)
                      .Append(" general=").Append(DescribeAgent(SafeGet(() => team.GeneralAgent)));
                    OrderController orders = SafeGet(() => team.PlayerOrderController);
                    sb.Append(" playerOrderControllerOwner=").Append(DescribeAgent(orders != null ? SafeGet(() => orders.Owner) : null));
                    foreach (Formation formation in team.FormationsIncludingSpecialAndEmpty)
                    {
                        if (formation == null || SafeCount(formation) <= 0)
                        {
                            continue;
                        }
                        sb.Append("\n        Formation ").Append(SafeFormation(formation))
                          .Append(" units=").Append(SafeCount(formation))
                          .Append(" playerOwner=").Append(DescribeAgent(SafeGet(() => formation.PlayerOwner)))
                          .Append(" captain=").Append(DescribeAgent(SafeGet(() => formation.Captain)))
                          .Append(" ai=").Append(SafeGet(() => formation.IsAIControlled) ? 1 : 0);
                    }
                }
                Log.Info(sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Info("[CONTROL] control-map dump failed: " + ex.Message);
            }
        }

        // ---- helpers ----

        private static string DescribeAgent(Agent agent)
        {
            if (agent == null)
            {
                return "null";
            }
            try
            {
                string name = agent.Character != null && agent.Character.Name != null ? agent.Character.Name.ToString() : ("agent#" + agent.Index);
                string team = agent.Team != null ? agent.Team.Side.ToString() : "noteam";
                return name + "(" + team + (agent.IsMainAgent ? ",MAIN" : "") + ")";
            }
            catch
            {
                return "agent(?)";
            }
        }

        private static string SafeTeam(Team team)
        {
            try
            {
                return team.Side.ToString();
            }
            catch
            {
                return "?";
            }
        }

        private static string SafeFormation(Formation formation)
        {
            try
            {
                return formation.FormationIndex.ToString();
            }
            catch
            {
                return "?";
            }
        }

        private static int SafeCount(Formation formation)
        {
            try
            {
                return formation.CountOfUnits;
            }
            catch
            {
                return -1;
            }
        }

        private static T SafeGet<T>(Func<T> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return default(T);
            }
        }

        private static string Stack()
        {
            try
            {
                StackFrame[] frames = new StackTrace(2, false).GetFrames();
                if (frames == null)
                {
                    return "";
                }
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    if (method == null)
                    {
                        continue;
                    }
                    Type declaring = method.DeclaringType;
                    string typeName = declaring != null ? declaring.FullName : null;
                    if (typeName != null)
                    {
                        if (typeName.StartsWith("HarmonyLib", StringComparison.Ordinal) ||
                            typeName.StartsWith("BLTDeploymentCrashGuard", StringComparison.Ordinal) ||
                            typeName.StartsWith("System.", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        sb.Append("\n      at ").Append(typeName).Append('.').Append(method.Name);
                    }
                    else
                    {
                        sb.Append("\n      at ").Append(method.Name);
                    }
                    if (++shown >= 14)
                    {
                        break;
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }
    }
}
