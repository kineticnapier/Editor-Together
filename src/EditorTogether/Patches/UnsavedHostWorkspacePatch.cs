using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace EditorTogether.Patches
{
    /// <summary>
    /// The stock editor refuses song/image/video import while ADOBase.levelPath is empty.
    /// While hosting an unsaved chart, give the editor a private collaboration workspace
    /// file so stock relative-asset handling works. Ctrl+S is still redirected to Save As,
    /// so the temporary file is never mistaken for the user's real save destination.
    /// </summary>
    internal static class UnsavedHostWorkspace
    {
        private static string shadowPath = string.Empty;
        private static scnEditor shadowEditor;

        public static void Ensure(scnEditor editor)
        {
            if (editor == null || editor.customLevel == null || editor.levelData == null) return;
            if (!string.IsNullOrEmpty(ADOBase.levelPath))
            {
                if (!IsCurrentShadow(editor)) ClearTracking();
                return;
            }

            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "EditorTogether",
                    "UnsavedHost",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "main.adofai");
                File.WriteAllText(path, editor.levelData.Encode());
                editor.customLevel.levelPath = path;
                shadowEditor = editor;
                shadowPath = Path.GetFullPath(path);
                Main.ModEntry?.Logger.Log("[CollabAssets] unsaved host chart materialized in a private workspace so stock asset import can be used");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabAssets] failed to create unsaved host workspace: " + ex);
            }
        }

        public static bool IsCurrentShadow(scnEditor editor)
        {
            if (editor == null || !ReferenceEquals(editor, shadowEditor) || string.IsNullOrEmpty(shadowPath)) return false;
            string current = editor.customLevel?.levelPath ?? string.Empty;
            if (string.IsNullOrEmpty(current)) return false;
            try { return string.Equals(Path.GetFullPath(current), shadowPath, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static void RestoreUnsavedIdentity(scnEditor editor)
        {
            if (!IsCurrentShadow(editor))
            {
                ClearTracking();
                return;
            }

            try
            {
                editor.customLevel.levelPath = string.Empty;
                AccessTools.Method(typeof(scnEditor), "RefreshFilenameText")?.Invoke(editor, null);
                Main.ModEntry?.Logger.Log("[CollabAssets] restored unsaved chart identity after leaving the room");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Warning("[CollabAssets] failed to restore unsaved chart identity: " + ex.Message);
            }
            finally
            {
                ClearTracking();
            }
        }

        private static void ClearTracking()
        {
            shadowEditor = null;
            shadowPath = string.Empty;
        }
    }

    [HarmonyPatch]
    internal static class UnsavedHostBeginLevelPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "BeginNewLevel");

        [HarmonyPrefix]
        private static void Prefix(CollaborationController __instance, scnEditor editor)
        {
            if (__instance != null && __instance.IsHost)
                UnsavedHostWorkspace.Ensure(editor);
        }
    }

    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveLevel))]
    internal static class UnsavedHostSavePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(scnEditor __instance)
        {
            if (!UnsavedHostWorkspace.IsCurrentShadow(__instance)) return true;

            // Preserve normal unsaved-chart semantics: Ctrl+S must still ask the user for
            // a real destination instead of silently saving into LocalAppData.
            Main.ModEntry?.Logger.Log("[CollabAssets] Save on collaboration workspace redirected to Save As");
            __instance.SaveLevelAs(false, null);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class UnsavedHostDisconnectPatch
    {
        private static readonly FieldInfo LastEditorField = AccessTools.Field(typeof(CollaborationController), "lastEditor");
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "DisconnectAsync");

        [HarmonyPrefix]
        private static void Prefix(CollaborationController __instance)
        {
            scnEditor editor = null;
            try { editor = LastEditorField?.GetValue(__instance) as scnEditor; } catch { }
            UnsavedHostWorkspace.RestoreUnsavedIdentity(editor);
        }
    }
}
