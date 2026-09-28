using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EditorTogether.Patches
{
    /// <summary>
    /// SaveStateScope marks the beginning of an editor transaction, not necessarily the
    /// frame in which its LevelData mutation is finished. Decoration dragging is the
    /// clearest stock example: the undo snapshot is created in DragDecorationsStart,
    /// while position changes continue in later DragDecorations calls.
    ///
    /// Keep the captured pre-edit state alive until LevelData has actually changed and
    /// then remained unchanged for one full editor frame. This prevents a zero-op flush
    /// at drag start and coalesces continuous inspector/drag edits into one operation diff.
    /// </summary>
    [HarmonyPatch]
    internal static class OperationSyncStabilityPatch
    {
        private const int RequiredStableFrames = 1;
        private const float NoChangeCleanupSeconds = 1f;

        private static readonly FieldInfo PendingBeforeField = AccessTools.Field(typeof(OperationSyncManager), "pendingBefore");
        private static readonly FieldInfo PendingEditorField = AccessTools.Field(typeof(OperationSyncManager), "pendingEditor");
        private static readonly FieldInfo MutationSerialField = AccessTools.Field(typeof(OperationSyncManager), "mutationSerial");
        private static readonly FieldInfo HandledMutationSerialField = AccessTools.Field(typeof(OperationSyncManager), "handledMutationSerial");
        private static readonly FieldInfo DraggingField = AccessTools.Field(typeof(scnEditor), "dragging");
        private static readonly FieldInfo PointerDownObjectTypeField = AccessTools.Field(typeof(scnEditor), "pointerDownObjectType");
        private static readonly MethodInfo LegacyPublishSnapshotMethod = AccessTools.Method(typeof(CollaborationController), "PublishSnapshot");

        private static readonly Type DiffType = typeof(OperationSyncManager).GetNestedType("OperationDiff", BindingFlags.NonPublic);
        private static readonly Type DiffResultType = typeof(OperationSyncManager).GetNestedType("OperationDiffResult", BindingFlags.NonPublic);
        private static readonly MethodInfo DiffBuildMethod = DiffType?.GetMethod("Build", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo DiffSupportedField = DiffResultType?.GetField("Supported", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo DiffOperationsField = DiffResultType?.GetField("Operations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static object trackedBefore;
        private static scnEditor trackedEditor;
        private static int trackedMutationSerial = -1;
        private static bool changeObserved;
        private static string lastFingerprint = string.Empty;
        private static int stableFrames;
        private static float noChangeElapsed;
        private static bool loggedInspectionFailure;

        private sealed class FlushState
        {
            public bool OriginalRan;
            public int MutationSerial;
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(OperationSyncManager), "FlushLocalMutation");

        [HarmonyPrefix]
        private static bool Prefix(CollaborationController controller, WebSocketTransport transport, scnEditor editor, ref FlushState __state)
        {
            __state = new FlushState();

            object before = ReadObject(PendingBeforeField);
            int serial = ReadInt(MutationSerialField);
            if (before == null || editor == null || editor.levelData == null)
            {
                ResetTracking();
                __state.OriginalRan = true;
                __state.MutationSerial = serial;
                return true;
            }

            if (!ReferenceEquals(before, trackedBefore) || !ReferenceEquals(editor, trackedEditor) || serial != trackedMutationSerial)
            {
                trackedBefore = before;
                trackedEditor = editor;
                trackedMutationSerial = serial;
                changeObserved = false;
                lastFingerprint = string.Empty;
                stableFrames = 0;
                noChangeElapsed = 0f;
            }

            if (!changeObserved)
            {
                if (!TryHasMeaningfulChange(before, editor, out bool changed))
                {
                    if (!loggedInspectionFailure)
                    {
                        loggedInspectionFailure = true;
                        Main.ModEntry?.Logger.Warning("[CollabOps] mutation stability inspection unavailable; using immediate operation flush");
                    }
                    ResetTracking();
                    __state.OriginalRan = true;
                    __state.MutationSerial = serial;
                    return true;
                }

                if (!changed)
                {
                    if (IsInteractionActive(editor))
                    {
                        noChangeElapsed = 0f;
                    }
                    else
                    {
                        noChangeElapsed += Math.Max(0f, Time.unscaledDeltaTime);
                        if (noChangeElapsed >= NoChangeCleanupSeconds)
                        {
                            DiscardNoOpCapture(serial);
                            ResetTracking();
                        }
                    }
                    return false;
                }

                changeObserved = true;
                noChangeElapsed = 0f;
                lastFingerprint = FingerprintCurrentLevel(editor);
                stableFrames = 0;
                return false;
            }

            string fingerprint = FingerprintCurrentLevel(editor);
            if (!string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
            {
                lastFingerprint = fingerprint;
                stableFrames = 0;
                return false;
            }

            stableFrames++;
            if (stableFrames < RequiredStableFrames) return false;

            ResetTracking();
            __state.OriginalRan = true;
            __state.MutationSerial = serial;
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(CollaborationController controller, scnEditor editor, FlushState __state)
        {
            if (__state == null || !__state.OriginalRan || controller == null || editor == null) return;
            if (ReadInt(HandledMutationSerialField) == __state.MutationSerial) return;

            // FlushLocalMutation catches its own exceptions. Because legacy snapshots are
            // suppressed while a capture is pending, explicitly restore the old safety net
            // if the operation path returned without marking this mutation handled.
            try
            {
                if (LegacyPublishSnapshotMethod == null) return;
                Main.ModEntry?.Logger.Warning("[CollabOps] operation flush did not handle mutation; invoking legacy snapshot fallback");
                LegacyPublishSnapshotMethod.Invoke(controller, new object[] { editor, false });
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] legacy snapshot fallback after operation failure also failed: " + ex);
            }
        }

        private static bool TryHasMeaningfulChange(object before, scnEditor editor, out bool changed)
        {
            changed = false;
            if (before == null || editor == null || DiffBuildMethod == null || DiffSupportedField == null || DiffOperationsField == null)
                return false;

            try
            {
                object result = DiffBuildMethod.Invoke(null, new[] { before, (object)editor });
                if (result == null) return false;

                bool supported = (bool)DiffSupportedField.GetValue(result);
                if (!supported)
                {
                    changed = true;
                    return true;
                }

                IList operations = DiffOperationsField.GetValue(result) as IList;
                if (operations == null) return false;
                changed = operations.Count > 0;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] mutation stability diff inspection failed: " + (ex.InnerException ?? ex));
                return false;
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] mutation stability diff inspection failed: " + ex);
                return false;
            }
        }

        private static string FingerprintCurrentLevel(scnEditor editor)
        {
            string encoded = editor?.levelData?.Encode() ?? string.Empty;
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < encoded.Length; i++)
                {
                    char c = encoded[i];
                    hash ^= (byte)c;
                    hash *= 1099511628211UL;
                    hash ^= (byte)(c >> 8);
                    hash *= 1099511628211UL;
                }
                return encoded.Length.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("X16", CultureInfo.InvariantCulture);
            }
        }

        private static bool IsInteractionActive(scnEditor editor)
        {
            if (editor == null) return false;

            try
            {
                if (DraggingField != null && (bool)DraggingField.GetValue(editor)) return true;
            }
            catch { }

            try
            {
                object pointerType = PointerDownObjectTypeField?.GetValue(editor);
                if (pointerType != null && !string.Equals(pointerType.ToString(), "None", StringComparison.Ordinal)) return true;
            }
            catch { }

            return false;
        }

        private static void DiscardNoOpCapture(int serial)
        {
            try
            {
                PendingBeforeField?.SetValue(null, null);
                PendingEditorField?.SetValue(null, null);
                HandledMutationSerialField?.SetValue(null, serial);
                Main.ModEntry?.Logger.Log("[CollabOps] discarded no-op editor capture after waiting for a LevelData change");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] failed to discard no-op editor capture: " + ex);
            }
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

        private static void ResetTracking()
        {
            trackedBefore = null;
            trackedEditor = null;
            trackedMutationSerial = -1;
            changeObserved = false;
            lastFingerprint = string.Empty;
            stableFrames = 0;
            noChangeElapsed = 0f;
        }
    }
}
