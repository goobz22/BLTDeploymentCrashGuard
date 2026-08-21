using System;
using HarmonyLib;

namespace BLTDeploymentCrashGuard
{
    /// <summary>
    /// Guards the conversation-camera NRE that CTD'd the marriage proposal (field crash
    /// 2026-08-21 16:39): SandBox.View.Missions.MissionConversationCameraView.
    /// MakeSpeakerLookToListener dereferences the speaker/listener conversation agents, and
    /// when one is removed mid-conversation — e.g. the spouse's state changes the moment a
    /// BT-routed marriage applies — the camera tick NREs and takes the game down.
    ///
    /// SELF-DISABLING finalizers on MakeSpeakerLookToListener and UpdateAgentLooksForConversation:
    /// no-ops unless the exception occurs; on an escaping NRE the camera update is skipped for
    /// that frame instead of crashing (the conversation ends or the camera recovers next tick).
    /// </summary>
    internal static class ConversationCameraCrashGuard
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type view = AccessTools.TypeByName("SandBox.View.Missions.MissionConversationCameraView");
                if (view == null)
                {
                    Log.Info("[CONVO-CAM] MissionConversationCameraView not found — guard inactive (game update?)");
                    Diag.Report("conversation-camera-guard", false, "view type not found");
                    return;
                }
                int patched = 0;
                foreach (string methodName in new[] { "MakeSpeakerLookToListener", "UpdateAgentLooksForConversation" })
                {
                    var method = AccessTools.Method(view, methodName);
                    if (method != null)
                    {
                        harmony.Patch(method, null, null, null, new HarmonyMethod(typeof(ConversationCameraCrashGuard), nameof(Finalizer)));
                        patched++;
                    }
                }
                if (patched == 0)
                {
                    Log.Info("[CONVO-CAM] no camera methods resolved — guard inactive (game update?)");
                    Diag.Report("conversation-camera-guard", false, "no methods resolved");
                    return;
                }
                Log.Info("[CONVO-CAM] conversation-camera crash guard active on " + patched + " method(s) (self-disables when the underlying NRE stops occurring)");
                Diag.Report("conversation-camera-guard", true, "");
                SelfHealing.RegisterTest(SelfTest);
            }
            catch (Exception ex)
            {
                Log.Info("[CONVO-CAM] apply failed: " + ex.Message);
                Diag.Report("conversation-camera-guard", false, ex.Message);
            }
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null; // no bug this frame — guard inert
            }
            SelfHealing.RecordFire("conversation-camera-guard");
            Log.Info("[CONVO-CAM] SUPPRESSED conversation-camera crash (agent removed mid-conversation): " + __exception.Message);
            return null; // skip this frame's camera look update instead of CTD
        }

        private static SelfHealing.TestResult SelfTest()
        {
            Type view = AccessTools.TypeByName("SandBox.View.Missions.MissionConversationCameraView");
            bool methodExists = view != null && AccessTools.Method(view, "MakeSpeakerLookToListener") != null;
            bool inertOnNull = Finalizer(null) == null;
            bool pass = methodExists && inertOnNull;
            return SelfHealing.TestResult.Of("conversation-camera-guard.contract", pass,
                pass ? "target re-resolved; finalizer inert on null exception"
                     : "methodExists=" + methodExists + " inertOnNull=" + inertOnNull + " (game update?)");
        }
    }
}
