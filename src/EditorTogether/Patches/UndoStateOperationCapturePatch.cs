using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ADOFAI;
using HarmonyLib;

namespace EditorTogether.Patches
{
    /// <summary>
    /// ADOFAI's stock editor writes a pre-edit LevelData.Copy() into undoStates and then
    /// updates saveStateLastFrame. Recover that copy at the end of the frame and feed it
    /// into OperationSync. This avoids relying on SaveStateScope being Harmony-patchable.
    /// </summary>
    [HarmonyPatch]
    internal static class UndoStateOperationCapturePatch
    {
        private static readonly FieldInfo SaveStateLastFrameField = AccessTools.Field(typeof(scnEditor), "saveStateLastFrame");

        private static readonly Type CaptureType = typeof(OperationSyncManager).GetNestedType("EditorStateCapture", BindingFlags.NonPublic);
        private static readonly Type EventSnapshotType = typeof(OperationSyncManager).GetNestedType("EventSnapshot", BindingFlags.NonPublic);
        private static readonly FieldInfo PendingBeforeField = AccessTools.Field(typeof(OperationSyncManager), "pendingBefore");
        private static readonly FieldInfo PendingEditorField = AccessTools.Field(typeof(OperationSyncManager), "pendingEditor");
        private static readonly FieldInfo MutationSerialField = AccessTools.Field(typeof(OperationSyncManager), "mutationSerial");
        private static readonly MethodInfo RefreshTrackingMethod = AccessTools.Method(typeof(OperationSyncManager), "RefreshTracking");
        private static readonly MethodInfo ReadIsOldLevelMethod = AccessTools.Method(typeof(OperationSyncManager), "ReadIsOldLevel");
        private static readonly MethodInfo SettingsFingerprintMethod = AccessTools.Method(typeof(OperationSyncManager), "LevelSettingsFingerprint");

        private static scnEditor trackedEditor;
        private static string trackedLevelId = string.Empty;
        private static int trackedSaveStateFrame = int.MinValue;
        private static int trackedUndoCount = -1;
        private static int trackedRedoCount = -1;
        private static bool loggedActive;

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(scnEditor), "LateUpdate");

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void Postfix(scnEditor __instance)
        {
            CollaborationController controller = Main.Controller;
            if (__instance == null || controller == null || !controller.IsConnected)
            {
                Reset();
                return;
            }

            if (controller.IsApplyingRemote)
            {
                RefreshBaseline(__instance, controller.LevelId);
                return;
            }

            string levelId = controller.LevelId ?? string.Empty;
            int frame = ReadSaveStateFrame(__instance);
            int undoCount = __instance.undoStates == null ? 0 : __instance.undoStates.Count;
            int redoCount = __instance.redoStates == null ? 0 : __instance.redoStates.Count;

            if (!ReferenceEquals(trackedEditor, __instance) || !string.Equals(trackedLevelId, levelId, StringComparison.Ordinal))
            {
                trackedEditor = __instance;
                trackedLevelId = levelId;
                trackedSaveStateFrame = frame;
                trackedUndoCount = undoCount;
                trackedRedoCount = redoCount;
                return;
            }

            if (frame == trackedSaveStateFrame && undoCount == trackedUndoCount && redoCount == trackedRedoCount)
                return;

            int previousUndoCount = trackedUndoCount;
            int previousRedoCount = trackedRedoCount;
            trackedSaveStateFrame = frame;
            trackedUndoCount = undoCount;
            trackedRedoCount = redoCount;

            Main.Controller?.OnEditorStateSaved(__instance);

            // Undo/redo moves entries between stacks. Let the safe full-state fallback own
            // those transitions for now; normal edits append a new undo state instead.
            bool looksLikeUndoRedo = (undoCount < previousUndoCount && redoCount > previousRedoCount) ||
                                     (undoCount > previousUndoCount && redoCount < previousRedoCount && previousRedoCount > 0);
            if (looksLikeUndoRedo) return;

            if (__instance.undoStates == null || undoCount <= 0) return;

            int candidateIndex;
            if (previousUndoCount >= 0 && undoCount > previousUndoCount)
                candidateIndex = Math.Min(previousUndoCount, undoCount - 1);
            else if (previousUndoCount == 100 && undoCount == 100 && frame != int.MinValue)
                candidateIndex = undoCount - 1; // 100-entry cap dropped the oldest state.
            else
                return;

            scnEditor.LevelState state = __instance.undoStates[candidateIndex];
            LevelData beforeData = state == null ? null : state.data;
            if (beforeData == null) return; // dataHasChanged=false: no LevelData snapshot exists.

            try
            {
                RefreshTrackingMethod?.Invoke(null, new object[] { controller });

                // If another hook already captured this mutation, keep the earlier capture.
                if (PendingBeforeField?.GetValue(null) != null) return;

                object capture = BuildCapture(__instance, beforeData);
                if (capture == null) return;

                PendingBeforeField.SetValue(null, capture);
                PendingEditorField.SetValue(null, __instance);
                int serial = MutationSerialField == null ? 0 : (int)MutationSerialField.GetValue(null);
                MutationSerialField?.SetValue(null, serial + 1);

                if (!loggedActive)
                {
                    loggedActive = true;
                    Main.ModEntry?.Logger.Log("[CollabOps] undo-state capture active; pre-edit LevelData recovered from scnEditor.undoStates");
                }
            }
            catch (Exception ex)
            {
                // Do not suppress the legacy snapshot when recovery fails.
                Main.ModEntry?.Logger.Error("[CollabOps] undo-state pre-edit recovery failed; legacy snapshot will remain available: " + ex);
            }
        }

        private static object BuildCapture(scnEditor editor, LevelData data)
        {
            if (CaptureType == null || EventSnapshotType == null || data == null) return null;

            object capture = Activator.CreateInstance(CaptureType, true);
            CaptureType.GetField("IsOldLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(capture, ReadIsOldLevelMethod != null && (bool)ReadIsOldLevelMethod.Invoke(null, new object[] { editor }));
            CaptureType.GetField("PathData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(capture, data.pathData ?? string.Empty);
            CaptureType.GetField("Angles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(capture, data.angleData == null ? Array.Empty<float>() : data.angleData.ToArray());
            CaptureType.GetField("Events", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(capture, BuildEventSnapshots(data.levelEvents));
            CaptureType.GetField("Decorations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(capture, BuildEventSnapshots(data.decorations));
            CaptureType.GetField("SettingsFingerprint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(capture, SettingsFingerprintMethod?.Invoke(null, new object[] { data }) as string ?? string.Empty);
            return capture;
        }

        private static object BuildEventSnapshots(IEnumerable<LevelEvent> events)
        {
            Type listType = typeof(List<>).MakeGenericType(EventSnapshotType);
            IList list = (IList)Activator.CreateInstance(listType);
            if (events == null) return list;

            ConstructorInfo ctor = EventSnapshotType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(LevelEvent) },
                null);
            if (ctor == null) throw new MissingMethodException(EventSnapshotType.FullName, ".ctor(LevelEvent)");

            foreach (LevelEvent evnt in events)
            {
                if (evnt == null) continue;
                list.Add(ctor.Invoke(new object[] { evnt.Copy() }));
            }
            return list;
        }

        private static int ReadSaveStateFrame(scnEditor editor)
        {
            try { return SaveStateLastFrameField == null || editor == null ? int.MinValue : (int)SaveStateLastFrameField.GetValue(editor); }
            catch { return int.MinValue; }
        }

        private static void RefreshBaseline(scnEditor editor, string levelId)
        {
            trackedEditor = editor;
            trackedLevelId = levelId ?? string.Empty;
            trackedSaveStateFrame = ReadSaveStateFrame(editor);
            trackedUndoCount = editor?.undoStates == null ? 0 : editor.undoStates.Count;
            trackedRedoCount = editor?.redoStates == null ? 0 : editor.redoStates.Count;
        }

        private static void Reset()
        {
            trackedEditor = null;
            trackedLevelId = string.Empty;
            trackedSaveStateFrame = int.MinValue;
            trackedUndoCount = -1;
            trackedRedoCount = -1;
        }
    }
}
