using System;
using System.Reflection;
using ADOFAI;
using HarmonyLib;

namespace EditorTogether.Patches
{
    /// <summary>
    /// A joined client must never silently leave the room's authoritative chart.
    /// scnEditor can load another chart without creating a new editor instance, so the
    /// old editor-instance safety barrier alone is not sufficient. Detect LevelData/path
    /// identity changes before the watchdog can treat them as an ordinary unhooked edit,
    /// and force a room resync instead.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientChartSwitchSafetyPatch
    {
        private static readonly MethodInfo RequestResyncMethod = AccessTools.Method(typeof(OperationSyncManager), "RequestResync");

        private static scnEditor trackedEditor;
        private static LevelData trackedLevelData;
        private static string trackedLevelPath = string.Empty;
        private static string trackedLevelId = string.Empty;
        private static long trackedRevision = -1;

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(scnEditor), "LateUpdate");

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(scnEditor __instance)
        {
            CollaborationController controller = Main.Controller;
            if (__instance == null || controller == null || !controller.IsConnected)
            {
                Reset();
                return;
            }

            string levelId = controller.LevelId ?? string.Empty;
            long revision = controller.Revision;
            LevelData levelData = __instance.levelData;
            string levelPath = ADOBase.levelPath ?? string.Empty;

            if (!ReferenceEquals(trackedEditor, __instance) ||
                !string.Equals(trackedLevelId, levelId, StringComparison.Ordinal) ||
                trackedRevision != revision)
            {
                Track(__instance, levelData, levelPath, levelId, revision);
                return;
            }

            bool chartIdentityChanged = !ReferenceEquals(trackedLevelData, levelData) ||
                                        !string.Equals(trackedLevelPath, levelPath, StringComparison.OrdinalIgnoreCase);
            if (!chartIdentityChanged) return;

            Track(__instance, levelData, levelPath, levelId, revision);

            // Host chart changes are authoritative and CollaborationController will turn
            // them into a level-switch. Unsynchronized clients are already protected.
            if (controller.IsHost || !controller.IsSynchronized || controller.IsApplyingRemote)
                return;

            try
            {
                if (RequestResyncMethod == null)
                    throw new MissingMethodException(typeof(OperationSyncManager).FullName, "RequestResync");

                RequestResyncMethod.Invoke(null, new object[] { controller, "client opened or replaced the local chart while connected" });
                Main.ModEntry?.Logger.Warning("[CollabSafety] client chart identity changed while connected; local chart discarded and authoritative room resync requested");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabSafety] failed to enter resync after client chart switch: " + ex);
            }
        }

        private static void Track(scnEditor editor, LevelData levelData, string levelPath, string levelId, long revision)
        {
            trackedEditor = editor;
            trackedLevelData = levelData;
            trackedLevelPath = levelPath ?? string.Empty;
            trackedLevelId = levelId ?? string.Empty;
            trackedRevision = revision;
        }

        private static void Reset()
        {
            trackedEditor = null;
            trackedLevelData = null;
            trackedLevelPath = string.Empty;
            trackedLevelId = string.Empty;
            trackedRevision = -1;
        }
    }
}
