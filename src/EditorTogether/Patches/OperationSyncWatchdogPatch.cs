using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace EditorTogether.Patches
{
    /// <summary>
    /// Some editor mods mutate LevelData directly and never call scnEditor.SaveState.
    /// OperationSyncManager cannot produce an operation diff for those edits because no
    /// pre-edit state exists. Detect those otherwise invisible mutations with a cheap
    /// structural fingerprint and fall back to a full-state message.
    ///
    /// Normal ADOFAI edits still use the fast operation path. The watchdog deliberately
    /// runs at a low frequency and never calls LevelData.Encode unless a real unhooked
    /// mutation is detected.
    /// </summary>
    [HarmonyPatch]
    internal static class OperationSyncWatchdogPatch
    {
        private const float ScanIntervalSeconds = 0.25f;
        private const float SaveStateGraceSeconds = 0.60f;

        private static readonly FieldInfo SaveStateLastFrameField = AccessTools.Field(typeof(scnEditor), "saveStateLastFrame");
        private static readonly FieldInfo LevelEventDataField = AccessTools.Field(typeof(LevelEvent), "data");
        private static readonly FieldInfo TransportField = AccessTools.Field(typeof(CollaborationController), "transport");
        private static readonly MethodInfo CanPublishLocalMethod = AccessTools.Method(typeof(OperationSyncManager), "CanPublishLocal");
        private static readonly MethodInfo QueueFullStateFallbackMethod = AccessTools.Method(typeof(OperationSyncManager), "QueueFullStateFallback");

        private static scnEditor trackedEditor;
        private static string trackedLevelId = string.Empty;
        private static long trackedRevision = -1;
        private static int trackedSaveStateFrame = int.MinValue;
        private static ulong trackedStamp;
        private static bool stampInitialized;
        private static float nextScanAt;
        private static float saveStateGraceUntil;

        private static MethodBase TargetMethod() => AccessTools.Method(typeof(scnEditor), "LateUpdate");

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(scnEditor __instance)
        {
            CollaborationController controller = Main.Controller;
            if (__instance == null || controller == null || !controller.IsConnected)
            {
                Reset();
                return;
            }

            if (controller.IsApplyingRemote) return;

            string levelId = controller.LevelId ?? string.Empty;
            if (!ReferenceEquals(trackedEditor, __instance) || !string.Equals(trackedLevelId, levelId, StringComparison.Ordinal))
            {
                trackedEditor = __instance;
                trackedLevelId = levelId;
                trackedRevision = controller.Revision;
                trackedSaveStateFrame = ReadSaveStateFrame(__instance);
                trackedStamp = ComputeStamp(__instance);
                stampInitialized = true;
                nextScanAt = Time.unscaledTime + ScanIntervalSeconds;
                saveStateGraceUntil = 0f;
                return;
            }

            float now = Time.unscaledTime;
            if (now < nextScanAt) return;
            nextScanAt = now + ScanIntervalSeconds;

            int saveStateFrame = ReadSaveStateFrame(__instance);
            long revision = controller.Revision;
            ulong currentStamp = ComputeStamp(__instance);

            if (!stampInitialized)
            {
                trackedStamp = currentStamp;
                trackedRevision = revision;
                trackedSaveStateFrame = saveStateFrame;
                stampInitialized = true;
                return;
            }

            // A normal local edit called SaveState. OperationSyncManager owns it, so keep
            // the watchdog out of the way while the diff is flushed and acknowledged.
            if (saveStateFrame != trackedSaveStateFrame)
            {
                trackedSaveStateFrame = saveStateFrame;
                trackedStamp = currentStamp;
                trackedRevision = revision;
                saveStateGraceUntil = now + SaveStateGraceSeconds;
                return;
            }

            // Remote operations/snapshots and accepted local batches advance Revision.
            // Refresh the baseline so we never echo a remote mutation back to the room.
            if (revision != trackedRevision)
            {
                trackedRevision = revision;
                trackedStamp = currentStamp;
                return;
            }

            if (now < saveStateGraceUntil)
            {
                trackedStamp = currentStamp;
                return;
            }

            if (currentStamp == trackedStamp) return;

            trackedStamp = currentStamp;
            if (!CanPublishLocal(controller))
            {
                Main.ModEntry?.Logger.Warning("[CollabOps] watchdog saw an unhooked LevelData mutation while publishing is blocked; waiting for authoritative sync");
                return;
            }

            WebSocketTransport transport = GetTransport(controller);
            if (transport == null || QueueFullStateFallbackMethod == null)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] watchdog detected an unhooked mutation but full-state fallback is unavailable");
                return;
            }

            try
            {
                QueueFullStateFallbackMethod.Invoke(null, new object[] { controller, transport, __instance });
                Main.ModEntry?.Logger.Warning("[CollabOps] watchdog detected a LevelData mutation that bypassed SaveState; queued full-state fallback");
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] watchdog full-state fallback failed: " + ex);
            }
        }

        private static bool CanPublishLocal(CollaborationController controller)
        {
            try
            {
                if (CanPublishLocalMethod != null)
                    return (bool)CanPublishLocalMethod.Invoke(null, new object[] { controller });
            }
            catch { }

            return controller.IsConnected && !string.IsNullOrEmpty(controller.LevelId) &&
                   (controller.IsHost || controller.IsSynchronized);
        }

        private static WebSocketTransport GetTransport(CollaborationController controller)
        {
            try { return TransportField?.GetValue(controller) as WebSocketTransport; }
            catch { return null; }
        }

        private static int ReadSaveStateFrame(scnEditor editor)
        {
            try
            {
                if (SaveStateLastFrameField == null || editor == null) return int.MinValue;
                return (int)SaveStateLastFrameField.GetValue(editor);
            }
            catch { return int.MinValue; }
        }

        private static ulong ComputeStamp(scnEditor editor)
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                Mix(ref hash, editor.levelData == null ? 0 : 1);
                if (editor.levelData == null) return hash;

                MixString(ref hash, editor.levelData.pathData ?? string.Empty);

                if (editor.levelData.angleData == null)
                {
                    Mix(ref hash, -1);
                }
                else
                {
                    Mix(ref hash, editor.levelData.angleData.Count);
                    for (int i = 0; i < editor.levelData.angleData.Count; i++)
                        Mix(ref hash, editor.levelData.angleData[i].GetHashCode());
                }

                MixEvents(ref hash, editor.events);
                MixEvents(ref hash, editor.decorations);
                return hash;
            }
        }

        private static void MixEvents(ref ulong hash, IList<LevelEvent> events)
        {
            if (events == null)
            {
                Mix(ref hash, -1);
                return;
            }

            Mix(ref hash, events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent evnt = events[i];
                if (evnt == null)
                {
                    Mix(ref hash, 0);
                    continue;
                }

                Mix(ref hash, evnt.floor);
                Mix(ref hash, evnt.eventType.GetHashCode());
                MixValue(ref hash, evnt.disabled, 0);

                Dictionary<string, object> data = null;
                try { data = LevelEventDataField?.GetValue(evnt) as Dictionary<string, object>; }
                catch { }

                MixValue(ref hash, data, 0);
            }
        }

        private static void MixValue(ref ulong hash, object value, int depth)
        {
            if (value == null)
            {
                Mix(ref hash, 0x13579BDF);
                return;
            }

            if (depth > 3)
            {
                Mix(ref hash, value.GetHashCode());
                return;
            }

            if (value is string text)
            {
                MixString(ref hash, text);
                return;
            }

            if (value is IDictionary dictionary)
            {
                Mix(ref hash, dictionary.Count);
                foreach (DictionaryEntry entry in dictionary)
                {
                    MixValue(ref hash, entry.Key, depth + 1);
                    MixValue(ref hash, entry.Value, depth + 1);
                }
                return;
            }

            if (value is IList list)
            {
                Mix(ref hash, list.Count);
                for (int i = 0; i < list.Count; i++) MixValue(ref hash, list[i], depth + 1);
                return;
            }

            if (value is Array array)
            {
                Mix(ref hash, array.Length);
                foreach (object item in array) MixValue(ref hash, item, depth + 1);
                return;
            }

            Mix(ref hash, value.GetHashCode());
        }

        private static void MixString(ref ulong hash, string value)
        {
            Mix(ref hash, value == null ? -1 : value.Length);
            if (value == null) return;
            for (int i = 0; i < value.Length; i++) Mix(ref hash, value[i]);
        }

        private static void Mix(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
            }
        }

        private static void Reset()
        {
            trackedEditor = null;
            trackedLevelId = string.Empty;
            trackedRevision = -1;
            trackedSaveStateFrame = int.MinValue;
            trackedStamp = 0;
            stampInitialized = false;
            nextScanAt = 0f;
            saveStateGraceUntil = 0f;
        }
    }
}
