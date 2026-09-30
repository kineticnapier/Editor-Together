using System;
using System.Reflection;
using HarmonyLib;

namespace EditorTogether.Patches
{
    internal static class PlaybackSafetyState
    {
        internal static SnapshotMessage PendingSnapshot;
        internal static bool PendingAssetReload;
        internal static bool DeferralLogged;

        internal static void Clear()
        {
            PendingSnapshot = null;
            PendingAssetReload = false;
            DeferralLogged = false;
        }

        internal static void QueueSnapshot(SnapshotMessage snapshot)
        {
            if (snapshot == null) return;

            // If a level-switch is followed by newer snapshots for that same level while
            // playback is still active, keep the level-switch semantics while coalescing
            // to the newest chart body. Otherwise the final non-switch snapshot would be
            // rejected as stale against the previous local levelId.
            SnapshotMessage previous = PendingSnapshot;
            bool preserveLevelSwitch = snapshot.IsLevelSwitch ||
                (previous != null && previous.IsLevelSwitch && string.Equals(previous.LevelId, snapshot.LevelId, StringComparison.Ordinal));

            PendingSnapshot = preserveLevelSwitch && !snapshot.IsLevelSwitch
                ? new SnapshotMessage(snapshot.ClientId, snapshot.LevelId, snapshot.Revision, snapshot.LevelData, true)
                : snapshot;
        }
    }

    [HarmonyPatch]
    internal static class DeferRemoteSnapshotWhilePlayingPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "ApplyRemoteSnapshot");

        [HarmonyPrefix]
        private static bool Prefix(scnEditor editor, SnapshotMessage snapshot)
        {
            if (editor == null || !editor.playMode) return true;

            PlaybackSafetyState.QueueSnapshot(snapshot);
            if (!PlaybackSafetyState.DeferralLogged)
            {
                PlaybackSafetyState.DeferralLogged = true;
                Main.ModEntry?.Logger.Log("[CollabSafety] remote chart changes deferred until editor playback stops");
            }
            return false;
        }
    }

    [HarmonyPatch]
    internal static class DeferRemoteAssetReloadWhilePlayingPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "ReloadRemoteAssets");

        [HarmonyPrefix]
        private static bool Prefix(scnEditor editor)
        {
            if (editor == null || !editor.playMode) return true;
            PlaybackSafetyState.PendingAssetReload = true;
            if (!PlaybackSafetyState.DeferralLogged)
            {
                PlaybackSafetyState.DeferralLogged = true;
                Main.ModEntry?.Logger.Log("[CollabSafety] remote asset reload deferred until editor playback stops");
            }
            return false;
        }
    }

    [HarmonyPatch]
    internal static class DeferOperationSyncWhilePlayingPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(OperationSyncManager), nameof(OperationSyncManager.Update));

        [HarmonyPrefix]
        private static bool Prefix(scnEditor editor)
        {
            return editor == null || !editor.playMode;
        }
    }

    [HarmonyPatch]
    internal static class FlushDeferredRemoteChangesAfterPlaybackPatch
    {
        private static readonly MethodInfo ApplySnapshotMethod = AccessTools.Method(typeof(CollaborationController), "ApplyRemoteSnapshot");
        private static readonly MethodInfo ReloadAssetsMethod = AccessTools.Method(typeof(CollaborationController), "ReloadRemoteAssets");

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(scnEditor), "LateUpdate");

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(scnEditor __instance)
        {
            CollaborationController controller = Main.Controller;
            if (__instance == null || controller == null || !controller.IsConnected)
            {
                PlaybackSafetyState.Clear();
                return;
            }
            if (__instance.playMode) return;

            SnapshotMessage snapshot = PlaybackSafetyState.PendingSnapshot;
            bool reloadAssets = PlaybackSafetyState.PendingAssetReload;
            if (snapshot == null && !reloadAssets) return;

            PlaybackSafetyState.PendingSnapshot = null;
            PlaybackSafetyState.PendingAssetReload = false;
            PlaybackSafetyState.DeferralLogged = false;

            try
            {
                if (snapshot != null)
                {
                    ApplySnapshotMethod?.Invoke(controller, new object[] { __instance, snapshot });
                    // ApplyRemoteSnapshot reloads assets itself when the manifest is already ready.
                    reloadAssets = false;
                }

                if (reloadAssets)
                    ReloadAssetsMethod?.Invoke(controller, new object[] { __instance });

                Main.ModEntry?.Logger.Log("[CollabSafety] applied remote changes deferred during playback");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabSafety] failed to apply playback-deferred remote changes: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(CollaborationController), nameof(CollaborationController.DisconnectAsync))]
    internal static class ClearPlaybackDeferredStateOnDisconnectPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            PlaybackSafetyState.Clear();
        }
    }
}
