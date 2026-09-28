using System;
using System.Reflection;
using HarmonyLib;

namespace EditorTogether.Patches
{
    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveState))]
    internal static class OperationSyncSaveStatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(scnEditor __instance, bool clearRedo = true, bool dataHasChanged = true)
        {
            // Keep the direct SaveState hook as a compatibility path. Stock editor edits
            // primarily go through SaveStateScope, which is patched separately.
            OperationSyncManager.CaptureBeforeMutation(__instance, dataHasChanged);
        }
    }

    [HarmonyPatch]
    internal static class OperationSyncEditorUpdatePatch
    {
        // Observe/flush after the editor frame has finished mutating LevelData. Continuous
        // edits may be held by OperationSyncStabilityPatch until their data stops changing.
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(scnEditor), "LateUpdate");

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(scnEditor __instance)
        {
            OperationSyncManager.Update(__instance);
        }
    }

    [HarmonyPatch]
    internal static class OperationSyncLegacySnapshotPatch
    {
        private static readonly FieldInfo MutationSerialField = AccessTools.Field(typeof(OperationSyncManager), "mutationSerial");
        private static readonly FieldInfo HandledMutationSerialField = AccessTools.Field(typeof(OperationSyncManager), "handledMutationSerial");
        private static readonly FieldInfo PendingBeforeField = AccessTools.Field(typeof(OperationSyncManager), "pendingBefore");
        private static string trackedLevelId = string.Empty;
        private static int lastObservedMutation = -1;
        private static int lastSuppressedMutation = -1;

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "PublishSnapshot");

        [HarmonyPrefix]
        private static bool Prefix(scnEditor editor, bool levelSwitch = false)
        {
            CollaborationController controller = Main.Controller;
            if (levelSwitch || controller == null || !controller.IsConnected)
            {
                if (levelSwitch) Reset(controller?.LevelId);
                return true;
            }

            int mutation = ReadInt(MutationSerialField);
            int handled = ReadInt(HandledMutationSerialField);
            string levelId = controller.LevelId ?? string.Empty;

            if (!string.Equals(levelId, trackedLevelId, StringComparison.Ordinal) || mutation < lastObservedMutation)
                Reset(levelId);

            lastObservedMutation = mutation;

            // SaveStateScope is an edit-start boundary. While OperationSync still owns its
            // captured pre-edit state, do not let the controller's debounce publish a
            // mid-drag/mid-slider full snapshot. The stability patch will either publish
            // operations/full-state itself or explicitly fall back to this path on failure.
            if (mutation > 0 && ReadObject(PendingBeforeField) != null)
                return false;

            // Once OperationSync positively handled a mutation, suppress the one legacy
            // snapshot that the controller may already have scheduled for that mutation.
            if (mutation > 0 && handled == mutation && handled != lastSuppressedMutation)
            {
                lastSuppressedMutation = handled;
                return false;
            }

            return true;
        }

        private static object ReadObject(FieldInfo field)
        {
            try { return field?.GetValue(null); }
            catch { return null; }
        }

        private static int ReadInt(FieldInfo field)
        {
            try { return field == null ? -1 : (int)field.GetValue(null); }
            catch { return -1; }
        }

        private static void Reset(string levelId)
        {
            trackedLevelId = levelId ?? string.Empty;
            lastObservedMutation = -1;
            lastSuppressedMutation = -1;
        }
    }
}
