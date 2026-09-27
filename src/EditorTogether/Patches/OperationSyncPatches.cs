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
            // Some editor paths/mods call SaveState(..., dataHasChanged: false) and then
            // mutate LevelData anyway. saveStateLastFrame still advances, so the legacy
            // observer notices the edit, but OperationSync previously skipped its pre-edit
            // capture and then suppressed that legacy snapshot. Capture every SaveState;
            // a true no-op simply produces an empty diff at LateUpdate.
            OperationSyncManager.CaptureBeforeMutation(__instance, true);
        }
    }

    [HarmonyPatch]
    internal static class OperationSyncEditorUpdatePatch
    {
        // Run after all normal Update work (including Unity UI callbacks) has had a chance
        // to mutate LevelData. Running this from scnEditor.Update could flush the SaveState
        // pre-edit capture before a button/inspector callback actually changed the chart,
        // producing a zero-op diff and suppressing the legacy fallback snapshot.
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
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "PublishSnapshot");

        [HarmonyPrefix]
        private static bool Prefix(scnEditor editor, bool levelSwitch = false)
        {
            return OperationSyncManager.AllowLegacySnapshot(editor, levelSwitch);
        }
    }
}
