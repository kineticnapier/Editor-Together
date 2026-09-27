using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EditorTogether.Patches
{
    /// <summary>
    /// Asset manifests were previously only published when a chart was opened/switched.
    /// Refresh the manifest when the host changes the song while the room is already open.
    /// </summary>
    [HarmonyPatch]
    internal static class HostAssetRefreshPatch
    {
        private const float ScanIntervalSeconds = 0.35f;
        private static readonly FieldInfo AssetSyncField = AccessTools.Field(typeof(CollaborationController), "assetSync");

        private static scnEditor trackedEditor;
        private static string trackedLevelId = string.Empty;
        private static string trackedSong = string.Empty;
        private static float nextScanAt;

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(scnEditor), "LateUpdate");

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(scnEditor __instance)
        {
            CollaborationController controller = Main.Controller;
            if (__instance == null || controller == null || !controller.IsConnected || !controller.IsHost)
            {
                Reset();
                return;
            }

            string levelId = controller.LevelId ?? string.Empty;
            string song = __instance.levelData?.songFilename ?? string.Empty;
            if (!ReferenceEquals(trackedEditor, __instance) || !string.Equals(trackedLevelId, levelId, StringComparison.Ordinal))
            {
                trackedEditor = __instance;
                trackedLevelId = levelId;
                trackedSong = song;
                nextScanAt = Time.unscaledTime + ScanIntervalSeconds;
                return;
            }

            float now = Time.unscaledTime;
            if (now < nextScanAt) return;
            nextScanAt = now + ScanIntervalSeconds;

            if (string.Equals(trackedSong, song, StringComparison.Ordinal)) return;
            string oldSong = trackedSong;
            trackedSong = song;

            if (string.IsNullOrEmpty(levelId) || __instance.levelData == null || string.IsNullOrEmpty(ADOBase.levelPath)) return;

            try
            {
                AssetSyncManager assetSync = AssetSyncField?.GetValue(controller) as AssetSyncManager;
                if (assetSync == null) return;
                _ = assetSync.PublishHostManifestAsync(levelId, __instance.levelData, ADOBase.levelPath);
                Main.ModEntry?.Logger.Log($"[CollabAssets] host song changed '{oldSong}' -> '{song}'; asset manifest refresh queued");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabAssets] failed to refresh host asset manifest: " + ex);
            }
        }

        private static void Reset()
        {
            trackedEditor = null;
            trackedLevelId = string.Empty;
            trackedSong = string.Empty;
            nextScanAt = 0f;
        }
    }
}
