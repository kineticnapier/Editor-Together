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
            OperationSyncManager.CaptureBeforeMutation(__instance, true);
        }
    }

    [HarmonyPatch]
    internal static class OperationSyncEditorUpdatePatch
    {
        // Flush after the editor frame has finished mutating LevelData.
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

            // Safety rule: a legacy snapshot is suppressed only once for a mutation that
            // OperationSync has positively finished handling. The old 0 == 0 test blocked
            // every snapshot when no operation capture happened at all.
            int mutation = ReadInt(MutationSerialField);
            int handled = ReadInt(HandledMutationSerialField);
            string levelId = controller.LevelId ?? string.Empty;

            if (!string.Equals(levelId, trackedLevelId, StringComparison.Ordinal) || mutation < lastObservedMutation)
                Reset(levelId);

            lastObservedMutation = mutation;
            if (mutation > 0 && handled == mutation && handled != lastSuppressedMutation)
            {
                lastSuppressedMutation = handled;
                return false;
            }

            return true;
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
