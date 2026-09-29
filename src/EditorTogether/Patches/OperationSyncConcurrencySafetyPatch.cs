using System;
using System.Reflection;
using ADOFAI;
using HarmonyLib;

namespace EditorTogether.Patches
{
    /// <summary>
    /// A SaveStateScope captures the local pre-edit state before the edit is necessarily
    /// complete. If a remote operation is applied while that capture is pending, the
    /// subsequent local diff can accidentally include the remote change and publish it
    /// back as if it were local. Keep queued remote operations deferred until the pending
    /// local transaction has either been flushed or discarded.
    /// </summary>
    [HarmonyPatch]
    internal static class DeferRemoteOperationsDuringLocalMutationPatch
    {
        private static readonly FieldInfo PendingBeforeField = AccessTools.Field(typeof(OperationSyncManager), "pendingBefore");

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(OperationSyncManager), "TryApplyDeferred");

        [HarmonyPrefix]
        private static bool Prefix()
        {
            try
            {
                return PendingBeforeField == null || PendingBeforeField.GetValue(null) == null;
            }
            catch
            {
                // If reflection ever stops matching, preserve the existing behavior rather
                // than deadlocking incoming operation processing.
                return true;
            }
        }
    }

    /// <summary>
    /// During resync the retained snapshot may be an older checkpoint followed by an
    /// operation replay. ApplyRemoteSnapshot normally keeps Revision monotonic with
    /// Math.Max, which makes replay operations look already-applied when the local revision
    /// was ahead. After a successfully applied authoritative resync snapshot, reset the
    /// local revision to that snapshot's exact revision so the replay can advance from it.
    /// </summary>
    [HarmonyPatch]
    internal static class AuthoritativeResyncRevisionPatch
    {
        private static readonly PropertyInfo RevisionProperty = AccessTools.Property(typeof(CollaborationController), "Revision");

        private sealed class ApplyState
        {
            public bool AuthoritativeResync;
            public LevelData BeforeLevelData;
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "ApplyRemoteSnapshot");

        [HarmonyPrefix]
        private static void Prefix(CollaborationController __instance, scnEditor editor, SnapshotMessage snapshot, ref ApplyState __state)
        {
            __state = new ApplyState
            {
                AuthoritativeResync = __instance != null && !__instance.IsHost && !__instance.IsSynchronized &&
                                      editor != null && !editor.playMode && snapshot != null,
                BeforeLevelData = editor?.levelData
            };
        }

        [HarmonyPostfix]
        private static void Postfix(CollaborationController __instance, scnEditor editor, SnapshotMessage snapshot, ApplyState __state)
        {
            if (__state == null || !__state.AuthoritativeResync || __instance == null || editor == null || snapshot == null) return;

            // ApplyRemoteSnapshot replaces LevelData with a newly decoded instance only on
            // a successful apply. If playback deferred the snapshot, or decode/apply failed,
            // leave the revision untouched.
            if (ReferenceEquals(__state.BeforeLevelData, editor.levelData)) return;
            if (!string.Equals(__instance.LevelId, snapshot.LevelId, StringComparison.Ordinal)) return;

            try
            {
                MethodInfo setter = RevisionProperty?.GetSetMethod(true);
                if (setter == null) return;
                setter.Invoke(__instance, new object[] { snapshot.Revision });
                Main.ModEntry?.Logger.Log($"[CollabOps] authoritative resync base revision reset to {snapshot.Revision}");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] failed to reset authoritative resync revision: " + ex);
            }
        }
    }
}
