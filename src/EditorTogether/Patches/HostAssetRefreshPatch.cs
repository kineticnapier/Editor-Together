using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace EditorTogether.Patches
{
    /// <summary>
    /// Refresh host asset manifests when referenced resources or their on-disk files change.
    /// The lightweight fingerprint uses path + size + LastWriteTimeUtc and only triggers the
    /// expensive SHA-256/upload path when something actually changed.
    /// </summary>
    [HarmonyPatch]
    internal static class HostAssetRefreshPatch
    {
        private const float ScanIntervalSeconds = 0.35f;
        private static readonly FieldInfo AssetSyncField = AccessTools.Field(typeof(CollaborationController), "assetSync");

        private static scnEditor trackedEditor;
        private static string trackedLevelId = string.Empty;
        private static string trackedFingerprint = string.Empty;
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
            if (!ReferenceEquals(trackedEditor, __instance) || !string.Equals(trackedLevelId, levelId, StringComparison.Ordinal))
            {
                trackedEditor = __instance;
                trackedLevelId = levelId;
                trackedFingerprint = BuildFingerprint(__instance);
                nextScanAt = Time.unscaledTime + ScanIntervalSeconds;
                return;
            }

            float now = Time.unscaledTime;
            if (now < nextScanAt) return;
            nextScanAt = now + ScanIntervalSeconds;

            string nextFingerprint = BuildFingerprint(__instance);
            if (string.Equals(trackedFingerprint, nextFingerprint, StringComparison.Ordinal)) return;
            trackedFingerprint = nextFingerprint;

            if (string.IsNullOrEmpty(levelId) || __instance.levelData == null || string.IsNullOrEmpty(ADOBase.levelPath)) return;

            try
            {
                AssetSyncManager assetSync = AssetSyncField?.GetValue(controller) as AssetSyncManager;
                if (assetSync == null) return;
                _ = assetSync.PublishHostManifestAsync(levelId, __instance.levelData, ADOBase.levelPath);
                Main.ModEntry?.Logger.Log("[CollabAssets] referenced asset set/file metadata changed; asset manifest refresh queued");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabAssets] failed to refresh host asset manifest: " + ex);
            }
        }

        private static string BuildFingerprint(scnEditor editor)
        {
            if (editor == null || editor.levelData == null) return string.Empty;

            HashSet<string> references = CollectReferencedAssets(editor.levelData);
            string levelPath = ADOBase.levelPath ?? string.Empty;
            string root = string.IsNullOrEmpty(levelPath) ? string.Empty : Path.GetDirectoryName(levelPath);
            var parts = new List<string>();

            foreach (string relativePath in references.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string normalized = NormalizeRelativePath(relativePath);
                if (!string.IsNullOrEmpty(root) && TryResolveInside(root, normalized, out string fullPath) && File.Exists(fullPath))
                {
                    try
                    {
                        var info = new FileInfo(fullPath);
                        parts.Add(normalized + "\u001f" + info.Length + "\u001f" + info.LastWriteTimeUtc.Ticks);
                    }
                    catch
                    {
                        parts.Add(normalized + "\u001fmetadata-error");
                    }
                }
                else
                {
                    parts.Add(normalized + "\u001fmissing");
                }
            }

            return string.Join("\u001e", parts);
        }

        private static HashSet<string> CollectReferencedAssets(LevelData data)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPath(paths, data.previewImage);
            AddPath(paths, data.previewIcon);
            AddPath(paths, data.artistPermission);
            AddPath(paths, data.bgImage);
            AddPath(paths, data.songFilename);
            AddPath(paths, data.bgVideo);

            if (data.levelEvents != null)
            {
                foreach (LevelEvent e in data.levelEvents)
                {
                    if (e == null) continue;
                    if (e.eventType == LevelEventType.CustomBackground) AddPath(paths, e.GetString("bgImage"));
                    else if (e.eventType == LevelEventType.ColorTrack) AddPath(paths, e.GetString("trackTexture"));
                    else if (e.eventType == LevelEventType.MoveDecorations) AddPath(paths, e.GetString("decorationImage"));
                }
            }

            if (data.decorations != null)
            {
                foreach (LevelEvent e in data.decorations)
                {
                    if (e == null) continue;
                    if (e.eventType == LevelEventType.AddDecoration || e.eventType == LevelEventType.AddParticle)
                        AddPath(paths, e.GetString("decorationImage"));
                }
            }

            return paths;
        }

        private static void AddPath(HashSet<string> paths, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = NormalizeRelativePath(value.Trim());
            if (normalized.Contains("://") || Path.IsPathRooted(normalized)) return;
            paths.Add(normalized);
        }

        private static string NormalizeRelativePath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private static bool TryResolveInside(string root, string relativePath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath)) return false;
            if (Path.IsPathRooted(relativePath)) return false;

            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = candidate;
            return true;
        }

        private static void Reset()
        {
            trackedEditor = null;
            trackedLevelId = string.Empty;
            trackedFingerprint = string.Empty;
            nextScanAt = 0f;
        }
    }
}
