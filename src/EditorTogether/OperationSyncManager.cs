using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace EditorTogether
{
    /// <summary>
    /// Protocol v2 operation synchronization.
    /// Normal editor mutations are diffed at the end of the frame and sent as compact
    /// operations. Full LevelData snapshots are reserved for level switches, resync,
    /// unsupported edits, and periodic host checkpoints.
    /// </summary>
    internal static class OperationSyncManager
    {
        public const int ProtocolVersion = 2;
        private const int MaxOperationsPerBatch = 64;
        private const int MaxBatchPayloadBytes = 28 * 1024;
        private const int CheckpointBatchInterval = 128;
        private const float CheckpointSeconds = 30f;

        private static readonly FieldInfo TransportField = AccessTools.Field(typeof(CollaborationController), "transport");
        private static readonly PropertyInfo RevisionProperty = AccessTools.Property(typeof(CollaborationController), "Revision");
        private static readonly MethodInfo RequireResyncMethod = AccessTools.Method(typeof(CollaborationController), "RequireClientResync");
        private static readonly FieldInfo ChangingStateField = AccessTools.Field(typeof(scnEditor), "changingState");
        private static readonly PropertyInfo IsOldLevelProperty = AccessTools.Property(typeof(scnEditor), "isOldLevel");
        private static readonly MethodInfo UpdateDecorationObjectsMethod = AccessTools.Method(typeof(scnEditor), "UpdateDecorationObjects");
        private static readonly MethodInfo ApplyEventsToFloorsMethod = AccessTools.Method(typeof(scnEditor), "ApplyEventsToFloors");
        private static readonly FieldInfo LevelEventDataField = AccessTools.Field(typeof(LevelEvent), "data");

        private static EditorStateCapture pendingBefore;
        private static scnEditor pendingEditor;
        private static int mutationSerial;
        private static int handledMutationSerial;
        private static string trackedLevelId = string.Empty;
        private static long predictedRevision;
        private static readonly Dictionary<string, long> localBatches = new Dictionary<string, long>();
        private static readonly List<OperationBatchMessage> deferredIncoming = new List<OperationBatchMessage>();
        private static long syncTargetRevision = -1;
        private static string syncTargetLevelId = string.Empty;
        private static bool syncReadySent;
        private static bool wasConnected;
        private static int batchesSinceCheckpoint;
        private static float checkpointElapsed;
        private static Task sendTail = Task.CompletedTask;
        private static readonly object SendTailLock = new object();

        public static void CaptureBeforeMutation(scnEditor editor, bool dataHasChanged)
        {
            if (!dataHasChanged || editor == null || editor.levelData == null) return;
            CollaborationController controller = Main.Controller;
            if (controller == null || !controller.IsConnected || controller.IsApplyingRemote) return;

            RefreshTracking(controller);
            mutationSerial++;
            if (pendingBefore != null && ReferenceEquals(pendingEditor, editor)) return;

            try
            {
                pendingBefore = EditorStateCapture.Capture(editor);
                pendingEditor = editor;
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] failed to capture pre-edit state: " + ex);
                pendingBefore = null;
                pendingEditor = null;
            }
        }

        public static void Update(scnEditor editor)
        {
            CollaborationController controller = Main.Controller;
            if (controller == null) return;
            WebSocketTransport transport = GetTransport(controller);
            if (transport == null) return;

            if (!controller.IsConnected)
            {
                if (wasConnected) ResetDisconnected();
                wasConnected = false;
                return;
            }

            wasConnected = true;
            RefreshTracking(controller);
            DrainControlMessages(controller, transport, editor);
            TryApplyDeferred(controller, transport, editor);
            TryCompleteSyncTarget(controller, transport);

            if (pendingBefore != null && ReferenceEquals(pendingEditor, editor) && !controller.IsApplyingRemote)
                FlushLocalMutation(controller, transport, editor);

            if (controller.IsHost)
            {
                checkpointElapsed += Time.unscaledDeltaTime;
                if ((batchesSinceCheckpoint >= CheckpointBatchInterval || (batchesSinceCheckpoint > 0 && checkpointElapsed >= CheckpointSeconds)) &&
                    pendingBefore == null && localBatches.Count == 0 && predictedRevision == controller.Revision)
                {
                    PublishCheckpoint(controller, transport, editor);
                }
            }
        }

        public static bool AllowLegacySnapshot(scnEditor editor, bool levelSwitch)
        {
            if (levelSwitch) return true;
            CollaborationController controller = Main.Controller;
            if (controller == null || !controller.IsConnected) return true;

            return handledMutationSerial != mutationSerial;
        }

        private static void FlushLocalMutation(CollaborationController controller, WebSocketTransport transport, scnEditor editor)
        {
            EditorStateCapture before = pendingBefore;
            pendingBefore = null;
            pendingEditor = null;

            try
            {
                if (!CanPublishLocal(controller))
                {
                    handledMutationSerial = mutationSerial;
                    Main.ModEntry?.Logger.Warning("[CollabOps] local edit occurred while sync barrier is active; edit will not be published");
                    return;
                }

                OperationDiffResult diff = OperationDiff.Build(before, editor);
                if (!diff.Supported)
                {
                    Main.ModEntry?.Logger.Log("[CollabOps] falling back to full state: " + diff.Reason);
                    QueueFullStateFallback(controller, transport, editor);
                    handledMutationSerial = mutationSerial;
                    return;
                }

                if (diff.Operations.Count == 0)
                {
                    handledMutationSerial = mutationSerial;
                    return;
                }

                List<List<Dictionary<string, object>>> batches = SplitBatches(diff.Operations);
                if (batches == null)
                {
                    Main.ModEntry?.Logger.Log("[CollabOps] operation payload too large; falling back to full state");
                    QueueFullStateFallback(controller, transport, editor);
                    handledMutationSerial = mutationSerial;
                    return;
                }

                string levelId = controller.LevelId;
                for (int i = 0; i < batches.Count; i++)
                {
                    long baseRevision = predictedRevision;
                    long expectedRevision = baseRevision + 1;
                    string batchId = Guid.NewGuid().ToString("N");
                    predictedRevision = expectedRevision;
                    localBatches[batchId] = expectedRevision;
                    List<Dictionary<string, object>> batchOperations = batches[i];
                    QueueSend(() => transport.SendOperationBatchAsync(levelId, baseRevision, batchId, batchOperations));
                }

                Main.ModEntry?.Logger.Log($"[CollabOps] published {diff.Operations.Count} op(s) in {batches.Count} batch(es); predictedRevision={predictedRevision}");
                handledMutationSerial = mutationSerial;
            }
            catch (Exception ex)
            {
                Main.ModEntry?.Logger.Error("[CollabOps] diff/send failed; legacy snapshot remains enabled for this edit: " + ex);
            }
        }

        private static void QueueFullStateFallback(CollaborationController controller, WebSocketTransport transport, scnEditor editor)
        {
            if (editor == null || editor.levelData == null || string.IsNullOrEmpty(controller.LevelId)) return;
            long baseRevision = predictedRevision;
            long expectedRevision = baseRevision + 1;
            string batchId = Guid.NewGuid().ToString("N");
            string encoded = editor.levelData.Encode();
            string level = controller.LevelId;
            predictedRevision = expectedRevision;
            localBatches[batchId] = expectedRevision;
            QueueSend(() => transport.SendFullStateAsync(level, baseRevision, batchId, encoded));
        }

        private static void PublishCheckpoint(CollaborationController controller, WebSocketTransport transport, scnEditor editor)
        {
            try
            {
                string encoded = editor.levelData.Encode();
                string level = controller.LevelId;
                long revision = controller.Revision;
                QueueSend(() => transport.SendCheckpointAsync(level, revision, encoded));
                batchesSinceCheckpoint = 0;
                checkpointElapsed = 0f;
                Main.ModEntry?.Logger.Log($"[CollabOps] checkpoint queued; level={controller.LevelId}, revision={controller.Revision}, bytes={encoded.Length}");
            }
            catch (Exception ex)
            {
                checkpointElapsed = 0f;
                Main.ModEntry?.Logger.Error("[CollabOps] checkpoint failed: " + ex);
            }
        }

        private static void DrainControlMessages(CollaborationController controller, WebSocketTransport transport, scnEditor editor)
        {
            while (transport.TryDequeueOperationAck(out OperationAckMessage ack))
            {
                localBatches.Remove(ack.BatchId);
                SetRevision(controller, Math.Max(controller.Revision, ack.Revision));
                predictedRevision = Math.Max(predictedRevision, ack.Revision);
                batchesSinceCheckpoint++;
            }

            while (transport.TryDequeueOperationRejected(out OperationRejectedMessage rejected))
            {
                localBatches.Remove(rejected.BatchId);
                predictedRevision = rejected.ServerRevision;
                Main.ModEntry?.Logger.Warning($"[CollabOps] batch rejected: {rejected.Reason}; serverRevision={rejected.ServerRevision}. Requesting resync.");
                RequestResync(controller, "operation rejected: " + rejected.Reason);
            }

            while (transport.TryDequeueSyncTarget(out SyncTargetMessage target))
            {
                syncTargetLevelId = target.LevelId;
                syncTargetRevision = target.Revision;
                syncReadySent = false;
                if (string.Equals(controller.LevelId, target.LevelId, StringComparison.Ordinal))
                    predictedRevision = Math.Max(predictedRevision, controller.Revision);
                Main.ModEntry?.Logger.Log($"[CollabOps] sync target received; level={target.LevelId}, revision={target.Revision}");
            }

            while (transport.TryDequeueOperationBatch(out OperationBatchMessage batch))
                deferredIncoming.Add(batch);
        }

        private static void TryApplyDeferred(CollaborationController controller, WebSocketTransport transport, scnEditor editor)
        {
            if (editor == null || deferredIncoming.Count == 0) return;
            deferredIncoming.Sort((a, b) => a.Revision.CompareTo(b.Revision));

            bool progressed;
            do
            {
                progressed = false;
                for (int i = 0; i < deferredIncoming.Count; i++)
                {
                    OperationBatchMessage batch = deferredIncoming[i];
                    if (!string.Equals(batch.LevelId, controller.LevelId, StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrEmpty(controller.LevelId) && batch.Revision <= controller.Revision)
                        {
                            deferredIncoming.RemoveAt(i--);
                            progressed = true;
                        }
                        continue;
                    }

                    if (batch.Revision <= controller.Revision)
                    {
                        deferredIncoming.RemoveAt(i--);
                        progressed = true;
                        continue;
                    }

                    if (batch.BaseRevision != controller.Revision) continue;

                    try
                    {
                        ApplyBatch(controller, editor, batch);
                        SetRevision(controller, batch.Revision);
                        predictedRevision = Math.Max(predictedRevision, batch.Revision);
                        batchesSinceCheckpoint++;
                        deferredIncoming.RemoveAt(i--);
                        progressed = true;
                    }
                    catch (Exception ex)
                    {
                        Main.ModEntry?.Logger.Error("[CollabOps] remote batch apply failed: " + ex);
                        deferredIncoming.Clear();
                        RequestResync(controller, "remote operation divergence");
                        return;
                    }
                }
            } while (progressed);

            if (deferredIncoming.Count > 0)
            {
                OperationBatchMessage next = deferredIncoming[0];
                if (string.Equals(next.LevelId, controller.LevelId, StringComparison.Ordinal) && next.BaseRevision < controller.Revision && next.Revision > controller.Revision)
                {
                    Main.ModEntry?.Logger.Warning("[CollabOps] revision gap detected; requesting resync");
                    deferredIncoming.Clear();
                    RequestResync(controller, "operation revision gap");
                }
            }
        }

        private static void ApplyBatch(CollaborationController controller, scnEditor editor, OperationBatchMessage batch)
        {
            controller.ApplyRemote(() =>
            {
                int oldChangingState = 0;
                bool changedStateField = false;
                if (ChangingStateField != null)
                {
                    try
                    {
                        oldChangingState = (int)ChangingStateField.GetValue(editor);
                        ChangingStateField.SetValue(editor, Math.Max(1, oldChangingState));
                        changedStateField = true;
                    }
                    catch { }
                }

                bool floorChanged = false;
                bool eventChanged = false;
                bool decorationChanged = false;
                try
                {
                    for (int i = 0; i < batch.Operations.Length; i++)
                        ApplyOperation(editor, batch.Operations[i], ref floorChanged, ref eventChanged, ref decorationChanged);

                    if (floorChanged)
                    {
                        editor.RemakePath(true, true);
                    }
                    else if (eventChanged && ApplyEventsToFloorsMethod != null)
                    {
                        ApplyEventsToFloorsMethod.Invoke(editor, null);
                    }

                    if (decorationChanged && UpdateDecorationObjectsMethod != null)
                        UpdateDecorationObjectsMethod.Invoke(editor, null);
                }
                finally
                {
                    if (changedStateField)
                    {
                        try { ChangingStateField.SetValue(editor, oldChangingState); } catch { }
                    }
                }
            });

            Main.ModEntry?.Logger.Log($"[CollabOps] applied batch revision={batch.Revision}, ops={batch.Operations.Length}, from={batch.ClientId}");
        }

        private static void ApplyOperation(scnEditor editor, Dictionary<string, object> op, ref bool floorChanged, ref bool eventChanged, ref bool decorationChanged)
        {
            string type = GetString(op, "op");
            switch (type)
            {
                case "add-floor":
                {
                    int index = GetInt(op, "index");
                    string mode = GetString(op, "mode");
                    if (mode == "char")
                    {
                        string value = GetString(op, "value");
                        if (value.Length != 1 || index < 0 || index > editor.levelData.pathData.Length) throw new InvalidOperationException("Invalid add-floor(char)");
                        SimulateEventInsert(editor.events, editor.decorations, index, true);
                        editor.levelData.pathData = editor.levelData.pathData.Insert(index, value);
                    }
                    else
                    {
                        float angle = GetFloat(op, "angle");
                        if (index < 0 || index > editor.levelData.angleData.Count) throw new InvalidOperationException("Invalid add-floor(angle)");
                        SimulateEventInsert(editor.events, editor.decorations, index, false);
                        editor.levelData.angleData.Insert(index, angle);
                    }
                    floorChanged = true;
                    eventChanged = true;
                    decorationChanged = true;
                    break;
                }
                case "remove-floor":
                {
                    int index = GetInt(op, "index");
                    string mode = GetString(op, "mode");
                    int sequenceId = index + 1;
                    RemoveEventsAtFloorAndShift(editor.events, sequenceId);
                    ShiftFloors(editor.decorations, sequenceId, -1);
                    if (mode == "char")
                    {
                        if (index < 0 || index >= editor.levelData.pathData.Length) throw new InvalidOperationException("Invalid remove-floor(char)");
                        editor.levelData.pathData = editor.levelData.pathData.Remove(index, 1);
                    }
                    else
                    {
                        if (index < 0 || index >= editor.levelData.angleData.Count) throw new InvalidOperationException("Invalid remove-floor(angle)");
                        editor.levelData.angleData.RemoveAt(index);
                    }
                    floorChanged = true;
                    eventChanged = true;
                    decorationChanged = true;
                    break;
                }
                case "set-angle":
                {
                    int index = GetInt(op, "index");
                    if (index < 0 || index >= editor.levelData.angleData.Count) throw new InvalidOperationException("Invalid set-angle");
                    editor.levelData.angleData[index] = GetFloat(op, "angle");
                    floorChanged = true;
                    break;
                }
                case "set-floor-char":
                {
                    int index = GetInt(op, "index");
                    string value = GetString(op, "value");
                    if (value.Length != 1 || index < 0 || index >= editor.levelData.pathData.Length) throw new InvalidOperationException("Invalid set-floor-char");
                    editor.levelData.pathData = editor.levelData.pathData.Remove(index, 1).Insert(index, value);
                    floorChanged = true;
                    break;
                }
                case "add-event": ApplyAddEvent(editor.events, op); eventChanged = true; break;
                case "remove-event": ApplyRemoveEvent(editor.events, op); eventChanged = true; break;
                case "update-event": ApplyUpdateEvent(editor.events, op); eventChanged = true; break;
                case "add-decoration": ApplyAddEvent(editor.decorations, op); decorationChanged = true; break;
                case "remove-decoration": ApplyRemoveEvent(editor.decorations, op); decorationChanged = true; break;
                case "update-decoration": ApplyUpdateEvent(editor.decorations, op); decorationChanged = true; break;
                default: throw new InvalidOperationException("Unknown operation: " + type);
            }
        }

        private static void ApplyAddEvent(IList<LevelEvent> list, Dictionary<string, object> op)
        {
            int index = GetInt(op, "index");
            if (index < 0 || index > list.Count) throw new InvalidOperationException("Invalid event insert index");
            if (!(op.TryGetValue("event", out object encoded) && encoded is Dictionary<string, object> eventData)) throw new InvalidOperationException("Missing encoded event");
            list.Insert(index, EventCodec.Decode(eventData));
        }

        private static void ApplyRemoveEvent(IList<LevelEvent> list, Dictionary<string, object> op)
        {
            int index = GetInt(op, "index");
            if (index < 0 || index >= list.Count) throw new InvalidOperationException("Invalid event remove index");
            string expected = GetString(op, "expectedHash");
            string actual = EventCodec.Fingerprint(list[index]);
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException($"Event fingerprint mismatch at {index}: expected={expected}, actual={actual}");
            list.RemoveAt(index);
        }

        private static void ApplyUpdateEvent(IList<LevelEvent> list, Dictionary<string, object> op)
        {
            int index = GetInt(op, "index");
            if (index < 0 || index >= list.Count) throw new InvalidOperationException("Invalid event update index");
            string expected = GetString(op, "expectedHash");
            string actual = EventCodec.Fingerprint(list[index]);
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException($"Event fingerprint mismatch at {index}: expected={expected}, actual={actual}");
            if (!(op.TryGetValue("event", out object encoded) && encoded is Dictionary<string, object> eventData)) throw new InvalidOperationException("Missing encoded event");
            list[index] = EventCodec.Decode(eventData);
        }

        private static void SimulateEventInsert(IList<LevelEvent> events, IList<LevelEvent> decorations, int sequenceId, bool adjustTileRanges)
        {
            if (adjustTileRanges)
                for (int i = 0; i < events.Count; i++) AdjustTileRangesForInsert(events[i], sequenceId);
            ShiftFloors(events, sequenceId, 1);
            ShiftFloors(decorations, sequenceId, 1);
        }

        private static void ShiftFloors(IList<LevelEvent> list, int startFloorId, int offset)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].floor > startFloorId) list[i].floor += offset;
        }

        private static void RemoveEventsAtFloorAndShift(IList<LevelEvent> list, int sequenceId)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].floor == sequenceId) list.RemoveAt(i);
            ShiftFloors(list, sequenceId, -1);
        }

        private static void AdjustTileRangesForInsert(LevelEvent levelEvent, int sequenceId)
        {
            AdjustTileRangeProperty(levelEvent, "startTile", sequenceId);
            AdjustTileRangeProperty(levelEvent, "endTile", sequenceId);
        }

        private static void AdjustTileRangeProperty(LevelEvent levelEvent, string key, int sequenceId)
        {
            if (levelEvent == null) return;
            Dictionary<string, object> eventData = GetEventData(levelEvent);
            if (!eventData.TryGetValue(key, out object value) || value == null) return;
            Tuple<int, TileRelativeTo> tuple = scnGame.StringToTile(value.ToString());
            if (tuple.Item2 == TileRelativeTo.Start && tuple.Item1 > sequenceId)
                eventData[key] = new Tuple<int, TileRelativeTo>(tuple.Item1 + 1, TileRelativeTo.Start);
            else if (tuple.Item2 == TileRelativeTo.End && tuple.Item1 <= sequenceId)
                eventData[key] = new Tuple<int, TileRelativeTo>(tuple.Item1 - 1, TileRelativeTo.End);
        }

        private static void TryCompleteSyncTarget(CollaborationController controller, WebSocketTransport transport)
        {
            if (controller.IsHost || syncTargetRevision < 0 || syncReadySent) return;
            if (!string.Equals(controller.LevelId, syncTargetLevelId, StringComparison.Ordinal)) return;
            if (controller.Revision != syncTargetRevision) return;
            if (deferredIncoming.Any(x => string.Equals(x.LevelId, syncTargetLevelId, StringComparison.Ordinal) && x.Revision <= syncTargetRevision)) return;

            syncReadySent = true;
            predictedRevision = controller.Revision;
            string level = controller.LevelId;
            long revision = controller.Revision;
            QueueSend(() => transport.SendSyncReadyAsync(level, revision));
            Main.ModEntry?.Logger.Log($"[CollabOps] replay complete; sync-ready revision={controller.Revision}");
        }

        private static bool CanPublishLocal(CollaborationController controller)
        {
            if (!controller.IsConnected || string.IsNullOrEmpty(controller.LevelId)) return false;
            if (controller.IsHost) return true;
            if (!controller.IsSynchronized) return false;
            if (syncTargetRevision >= 0 && (!syncReadySent || controller.Revision != syncTargetRevision)) return false;
            return true;
        }

        private static void RequestResync(CollaborationController controller, string reason)
        {
            pendingBefore = null;
            pendingEditor = null;
            deferredIncoming.Clear();
            localBatches.Clear();
            syncTargetRevision = -1;
            syncTargetLevelId = string.Empty;
            syncReadySent = false;
            predictedRevision = controller.Revision;
            try { RequireResyncMethod?.Invoke(controller, new object[] { reason }); }
            catch (Exception ex) { Main.ModEntry?.Logger.Error("[CollabOps] failed to request resync: " + ex); }
        }

        private static void RefreshTracking(CollaborationController controller)
        {
            string currentLevel = controller.LevelId ?? string.Empty;
            if (string.Equals(currentLevel, trackedLevelId, StringComparison.Ordinal))
            {
                if (localBatches.Count == 0 && controller.Revision > predictedRevision)
                    predictedRevision = controller.Revision;
                return;
            }

            trackedLevelId = currentLevel;
            predictedRevision = controller.Revision;
            pendingBefore = null;
            pendingEditor = null;
            localBatches.Clear();
            deferredIncoming.Clear();
            syncTargetRevision = -1;
            syncTargetLevelId = string.Empty;
            syncReadySent = false;
            batchesSinceCheckpoint = 0;
            checkpointElapsed = 0f;
            mutationSerial = 0;
            handledMutationSerial = 0;
        }

        private static void ResetDisconnected()
        {
            pendingBefore = null;
            pendingEditor = null;
            localBatches.Clear();
            deferredIncoming.Clear();
            trackedLevelId = string.Empty;
            predictedRevision = 0;
            syncTargetRevision = -1;
            syncTargetLevelId = string.Empty;
            syncReadySent = false;
            mutationSerial = 0;
            handledMutationSerial = 0;
            batchesSinceCheckpoint = 0;
            checkpointElapsed = 0f;
        }

        private static WebSocketTransport GetTransport(CollaborationController controller)
        {
            try { return TransportField?.GetValue(controller) as WebSocketTransport; }
            catch { return null; }
        }

        private static void SetRevision(CollaborationController controller, long revision)
        {
            try
            {
                MethodInfo setter = RevisionProperty?.GetSetMethod(true);
                setter?.Invoke(controller, new object[] { revision });
            }
            catch (Exception ex) { Main.ModEntry?.Logger.Error("[CollabOps] failed to set revision: " + ex.Message); }
        }

        private static void QueueSend(Func<Task> send)
        {
            if (send == null) return;
            lock (SendTailLock)
            {
                sendTail = sendTail.ContinueWith(async _ =>
                {
                    try { await send().ConfigureAwait(false); }
                    catch (Exception ex) { Main.ModEntry?.Logger.Error("[CollabOps] send failed: " + ex); }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        private static List<List<Dictionary<string, object>>> SplitBatches(List<Dictionary<string, object>> operations)
        {
            var result = new List<List<Dictionary<string, object>>>();
            var current = new List<Dictionary<string, object>>();
            int bytes = 0;

            for (int i = 0; i < operations.Count; i++)
            {
                int opBytes = Encoding.UTF8.GetByteCount(RuntimeJson.Serialize(operations[i]));
                if (opBytes > MaxBatchPayloadBytes) return null;
                if (current.Count >= MaxOperationsPerBatch || (current.Count > 0 && bytes + opBytes > MaxBatchPayloadBytes))
                {
                    result.Add(current);
                    current = new List<Dictionary<string, object>>();
                    bytes = 0;
                }
                current.Add(operations[i]);
                bytes += opBytes;
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }

        private static string GetString(Dictionary<string, object> dictionary, string key)
            => dictionary.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        private static int GetInt(Dictionary<string, object> dictionary, string key)
            => dictionary.TryGetValue(key, out object value) ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        private static float GetFloat(Dictionary<string, object> dictionary, string key)
            => dictionary.TryGetValue(key, out object value) ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : 0f;

        private static Dictionary<string, object> GetEventData(LevelEvent evnt)
        {
            if (evnt == null) throw new ArgumentNullException(nameof(evnt));
            if (LevelEventDataField == null) throw new MissingFieldException(typeof(LevelEvent).FullName, "data");
            var data = LevelEventDataField.GetValue(evnt) as Dictionary<string, object>;
            if (data == null) throw new InvalidOperationException("LevelEvent.data was unavailable or had an unexpected type.");
            return data;
        }

        private sealed class EditorStateCapture
        {
            public bool IsOldLevel;
            public string PathData;
            public float[] Angles;
            public List<EventSnapshot> Events;
            public List<EventSnapshot> Decorations;
            public string SettingsFingerprint;

            public static EditorStateCapture Capture(scnEditor editor)
            {
                var capture = new EditorStateCapture();
                capture.IsOldLevel = ReadIsOldLevel(editor);
                capture.PathData = editor.levelData.pathData ?? string.Empty;
                capture.Angles = editor.levelData.angleData == null ? Array.Empty<float>() : editor.levelData.angleData.ToArray();
                capture.Events = CaptureEvents(editor.events);
                capture.Decorations = CaptureEvents(editor.decorations);
                capture.SettingsFingerprint = LevelSettingsFingerprint(editor.levelData);
                return capture;
            }

            private static List<EventSnapshot> CaptureEvents(IEnumerable<LevelEvent> source)
            {
                var list = new List<EventSnapshot>();
                if (source == null) return list;
                foreach (LevelEvent evnt in source)
                {
                    LevelEvent copy = evnt.Copy();
                    list.Add(new EventSnapshot(copy));
                }
                return list;
            }
        }

        private sealed class EventSnapshot
        {
            public LevelEvent Event;
            public string Hash => EventCodec.Fingerprint(Event);
            public EventSnapshot(LevelEvent evnt) { Event = evnt; }
        }

        private sealed class OperationDiffResult
        {
            public bool Supported = true;
            public string Reason = string.Empty;
            public readonly List<Dictionary<string, object>> Operations = new List<Dictionary<string, object>>();
        }

        private static class OperationDiff
        {
            public static OperationDiffResult Build(EditorStateCapture before, scnEditor editor)
            {
                var result = new OperationDiffResult();
                if (before == null || editor == null || editor.levelData == null)
                {
                    result.Supported = false;
                    result.Reason = "missing editor state";
                    return result;
                }

                if (before.IsOldLevel != ReadIsOldLevel(editor))
                {
                    result.Supported = false;
                    result.Reason = "level path format changed";
                    return result;
                }

                string settings = LevelSettingsFingerprint(editor.levelData);
                if (!string.Equals(before.SettingsFingerprint, settings, StringComparison.Ordinal))
                {
                    result.Supported = false;
                    result.Reason = "level settings changed";
                    return result;
                }

                List<EventSnapshot> expectedEvents = CloneSnapshots(before.Events);
                List<EventSnapshot> expectedDecorations = CloneSnapshots(before.Decorations);

                if (before.IsOldLevel)
                    DiffCharFloors(before.PathData, editor.levelData.pathData ?? string.Empty, expectedEvents, expectedDecorations, result);
                else
                    DiffAngleFloors(before.Angles, editor.levelData.angleData.ToArray(), expectedEvents, expectedDecorations, result);

                if (!result.Supported) return result;

                List<EventSnapshot> currentEvents = CaptureCurrent(editor.events);
                List<EventSnapshot> currentDecorations = CaptureCurrent(editor.decorations);
                DiffEvents(expectedEvents, currentEvents, false, result);
                DiffEvents(expectedDecorations, currentDecorations, true, result);
                return result;
            }

            private static void DiffAngleFloors(float[] oldValues, float[] newValues, List<EventSnapshot> events, List<EventSnapshot> decorations, OperationDiffResult result)
            {
                int prefix = CommonPrefix(oldValues, newValues);
                if (newValues.Length > oldValues.Length)
                {
                    int added = newValues.Length - oldValues.Length;
                    for (int i = 0; i < added; i++)
                    {
                        int index = prefix + i;
                        result.Operations.Add(new Dictionary<string, object> { ["op"] = "add-floor", ["mode"] = "angle", ["index"] = index, ["angle"] = newValues[index] });
                        SimulateInsert(events, decorations, index, false);
                    }
                    float[] transformed = oldValues.ToList().TapInsert(prefix, newValues.Skip(prefix).Take(added)).ToArray();
                    AddAngleUpdates(transformed, newValues, result);
                }
                else if (newValues.Length < oldValues.Length)
                {
                    int removed = oldValues.Length - newValues.Length;
                    var transformed = oldValues.ToList();
                    for (int i = 0; i < removed; i++)
                    {
                        result.Operations.Add(new Dictionary<string, object> { ["op"] = "remove-floor", ["mode"] = "angle", ["index"] = prefix });
                        SimulateRemove(events, decorations, prefix + 1);
                        transformed.RemoveAt(prefix);
                    }
                    AddAngleUpdates(transformed.ToArray(), newValues, result);
                }
                else AddAngleUpdates(oldValues, newValues, result);
            }

            private static void AddAngleUpdates(float[] oldValues, float[] newValues, OperationDiffResult result)
            {
                if (oldValues.Length != newValues.Length)
                {
                    result.Supported = false;
                    result.Reason = "non-contiguous floor edit";
                    return;
                }
                for (int i = 0; i < oldValues.Length; i++)
                    if (Math.Abs(oldValues[i] - newValues[i]) > 0.0001f)
                        result.Operations.Add(new Dictionary<string, object> { ["op"] = "set-angle", ["index"] = i, ["angle"] = newValues[i] });
            }

            private static void DiffCharFloors(string oldValues, string newValues, List<EventSnapshot> events, List<EventSnapshot> decorations, OperationDiffResult result)
            {
                oldValues = oldValues ?? string.Empty;
                newValues = newValues ?? string.Empty;
                int prefix = 0;
                while (prefix < oldValues.Length && prefix < newValues.Length && oldValues[prefix] == newValues[prefix]) prefix++;
                if (newValues.Length > oldValues.Length)
                {
                    int added = newValues.Length - oldValues.Length;
                    string transformed = oldValues;
                    for (int i = 0; i < added; i++)
                    {
                        int index = prefix + i;
                        result.Operations.Add(new Dictionary<string, object> { ["op"] = "add-floor", ["mode"] = "char", ["index"] = index, ["value"] = newValues[index].ToString() });
                        SimulateInsert(events, decorations, index, true);
                        transformed = transformed.Insert(index, newValues[index].ToString());
                    }
                    AddCharUpdates(transformed, newValues, result);
                }
                else if (newValues.Length < oldValues.Length)
                {
                    int removed = oldValues.Length - newValues.Length;
                    string transformed = oldValues;
                    for (int i = 0; i < removed; i++)
                    {
                        result.Operations.Add(new Dictionary<string, object> { ["op"] = "remove-floor", ["mode"] = "char", ["index"] = prefix });
                        SimulateRemove(events, decorations, prefix + 1);
                        transformed = transformed.Remove(prefix, 1);
                    }
                    AddCharUpdates(transformed, newValues, result);
                }
                else AddCharUpdates(oldValues, newValues, result);
            }

            private static void AddCharUpdates(string oldValues, string newValues, OperationDiffResult result)
            {
                if (oldValues.Length != newValues.Length)
                {
                    result.Supported = false;
                    result.Reason = "non-contiguous legacy floor edit";
                    return;
                }
                for (int i = 0; i < oldValues.Length; i++)
                    if (oldValues[i] != newValues[i])
                        result.Operations.Add(new Dictionary<string, object> { ["op"] = "set-floor-char", ["index"] = i, ["value"] = newValues[i].ToString() });
            }

            private static void DiffEvents(List<EventSnapshot> oldList, List<EventSnapshot> newList, bool decoration, OperationDiffResult result)
            {
                string addOp = decoration ? "add-decoration" : "add-event";
                string removeOp = decoration ? "remove-decoration" : "remove-event";
                string updateOp = decoration ? "update-decoration" : "update-event";

                if (newList.Count == oldList.Count)
                {
                    for (int i = 0; i < oldList.Count; i++) AddEventUpdateIfNeeded(oldList[i], newList[i], i, updateOp, result);
                    return;
                }

                if (newList.Count > oldList.Count)
                {
                    int added = newList.Count - oldList.Count;
                    int insertion = FindPureInsertion(oldList, newList, added);
                    if (insertion < 0)
                    {
                        result.Supported = false;
                        result.Reason = (decoration ? "decoration" : "event") + " insertion mixed with updates/reorder";
                        return;
                    }

                    for (int i = 0; i < added; i++)
                    {
                        int index = insertion + i;
                        result.Operations.Add(new Dictionary<string, object> { ["op"] = addOp, ["index"] = index, ["event"] = EventCodec.Encode(newList[index].Event) });
                    }
                    return;
                }

                int removed = oldList.Count - newList.Count;
                int removal = FindPureRemoval(oldList, newList, removed);
                if (removal < 0)
                {
                    result.Supported = false;
                    result.Reason = (decoration ? "decoration" : "event") + " removal mixed with updates/reorder";
                    return;
                }

                for (int i = 0; i < removed; i++)
                    result.Operations.Add(new Dictionary<string, object> { ["op"] = removeOp, ["index"] = removal, ["expectedHash"] = oldList[removal + i].Hash });
            }

            private static int FindPureInsertion(List<EventSnapshot> oldList, List<EventSnapshot> newList, int added)
            {
                for (int insertion = 0; insertion <= oldList.Count; insertion++)
                {
                    bool matches = true;
                    for (int i = 0; i < insertion; i++)
                    {
                        if (!string.Equals(oldList[i].Hash, newList[i].Hash, StringComparison.Ordinal)) { matches = false; break; }
                    }
                    if (!matches) continue;
                    for (int i = insertion; i < oldList.Count; i++)
                    {
                        if (!string.Equals(oldList[i].Hash, newList[i + added].Hash, StringComparison.Ordinal)) { matches = false; break; }
                    }
                    if (matches) return insertion;
                }
                return -1;
            }

            private static int FindPureRemoval(List<EventSnapshot> oldList, List<EventSnapshot> newList, int removed)
            {
                for (int removal = 0; removal <= newList.Count; removal++)
                {
                    bool matches = true;
                    for (int i = 0; i < removal; i++)
                    {
                        if (!string.Equals(oldList[i].Hash, newList[i].Hash, StringComparison.Ordinal)) { matches = false; break; }
                    }
                    if (!matches) continue;
                    for (int i = removal; i < newList.Count; i++)
                    {
                        if (!string.Equals(oldList[i + removed].Hash, newList[i].Hash, StringComparison.Ordinal)) { matches = false; break; }
                    }
                    if (matches) return removal;
                }
                return -1;
            }

            private static void AddEventUpdateIfNeeded(EventSnapshot oldEvent, EventSnapshot newEvent, int index, string opName, OperationDiffResult result)
            {
                if (string.Equals(oldEvent.Hash, newEvent.Hash, StringComparison.Ordinal)) return;
                result.Operations.Add(new Dictionary<string, object>
                {
                    ["op"] = opName,
                    ["index"] = index,
                    ["expectedHash"] = oldEvent.Hash,
                    ["event"] = EventCodec.Encode(newEvent.Event)
                });
            }

            private static int CommonPrefix(float[] a, float[] b)
            {
                int i = 0;
                while (i < a.Length && i < b.Length && Math.Abs(a[i] - b[i]) <= 0.0001f) i++;
                return i;
            }

            private static List<EventSnapshot> CloneSnapshots(List<EventSnapshot> source)
            {
                var result = new List<EventSnapshot>(source.Count);
                for (int i = 0; i < source.Count; i++) result.Add(new EventSnapshot(source[i].Event.Copy()));
                return result;
            }

            private static List<EventSnapshot> CaptureCurrent(IEnumerable<LevelEvent> source)
            {
                var result = new List<EventSnapshot>();
                foreach (LevelEvent evnt in source) result.Add(new EventSnapshot(evnt.Copy()));
                return result;
            }

            private static void SimulateInsert(List<EventSnapshot> events, List<EventSnapshot> decorations, int sequenceId, bool adjustTileRanges)
            {
                if (adjustTileRanges)
                    for (int i = 0; i < events.Count; i++) AdjustTileRangesForInsert(events[i].Event, sequenceId);
                for (int i = 0; i < events.Count; i++) if (events[i].Event.floor > sequenceId) events[i].Event.floor++;
                for (int i = 0; i < decorations.Count; i++) if (decorations[i].Event.floor > sequenceId) decorations[i].Event.floor++;
            }

            private static void SimulateRemove(List<EventSnapshot> events, List<EventSnapshot> decorations, int sequenceId)
            {
                for (int i = events.Count - 1; i >= 0; i--) if (events[i].Event.floor == sequenceId) events.RemoveAt(i);
                for (int i = 0; i < events.Count; i++) if (events[i].Event.floor > sequenceId) events[i].Event.floor--;
                for (int i = 0; i < decorations.Count; i++) if (decorations[i].Event.floor > sequenceId) decorations[i].Event.floor--;
            }
        }

        private static bool ReadIsOldLevel(scnEditor editor)
        {
            try { return IsOldLevelProperty != null && (bool)IsOldLevelProperty.GetValue(editor, null); }
            catch { return false; }
        }

        private static string LevelSettingsFingerprint(LevelData levelData)
        {
            var builder = new StringBuilder(2048);
            FieldInfo[] fields = typeof(LevelData).GetFields(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                string name = field.Name;
                if (name == "angleData" || name == "pathData" || name == "levelEvents" || name == "decorations") continue;
                object value;
                try { value = field.GetValue(levelData); } catch { continue; }
                builder.Append(name).Append('=').Append(EventCodec.Canonicalize(EventCodec.EncodeValueLenient(value))).Append(';');
            }
            return Fnv64(builder.ToString());
        }

        private static class EventCodec
        {
            public static Dictionary<string, object> Encode(LevelEvent evnt)
            {
                if (evnt == null) throw new InvalidOperationException("Cannot encode null event");
                Dictionary<string, object> sourceData = GetEventData(evnt);
                var data = new Dictionary<string, object>();
                foreach (string key in sourceData.Keys.OrderBy(x => x, StringComparer.Ordinal)) data[key] = EncodeValue(sourceData[key]);
                var disabled = new Dictionary<string, object>();
                foreach (string key in evnt.disabled.Keys.OrderBy(x => x, StringComparer.Ordinal)) disabled[key] = evnt.disabled[key];
                return new Dictionary<string, object>
                {
                    ["floor"] = evnt.floor,
                    ["eventType"] = evnt.eventType.ToString(),
                    ["data"] = data,
                    ["disabled"] = disabled
                };
            }

            public static LevelEvent Decode(Dictionary<string, object> encoded)
            {
                int floor = encoded.TryGetValue("floor", out object floorObj) ? Convert.ToInt32(floorObj, CultureInfo.InvariantCulture) : -1;
                string typeName = encoded.TryGetValue("eventType", out object typeObj) ? Convert.ToString(typeObj, CultureInfo.InvariantCulture) : "None";
                if (!Enum.TryParse(typeName, true, out LevelEventType eventType)) throw new InvalidOperationException("Unknown LevelEventType: " + typeName);
                var evnt = new LevelEvent(floor, eventType);
                Dictionary<string, object> targetData = GetEventData(evnt);

                if (encoded.TryGetValue("data", out object dataObj) && dataObj is Dictionary<string, object> data)
                {
                    foreach (var pair in data)
                    {
                        Type expected = targetData.TryGetValue(pair.Key, out object current) && current != null ? current.GetType() : null;
                        targetData[pair.Key] = DecodeValue(pair.Value, expected);
                    }
                }

                if (encoded.TryGetValue("disabled", out object disabledObj) && disabledObj is Dictionary<string, object> disabled)
                {
                    foreach (var pair in disabled) evnt.disabled[pair.Key] = Convert.ToBoolean(pair.Value, CultureInfo.InvariantCulture);
                }
                return evnt;
            }

            public static string Fingerprint(LevelEvent evnt) => Fnv64(Canonicalize(Encode(evnt)));

            public static object EncodeValueLenient(object value)
            {
                try { return EncodeValue(value); }
                catch { return value == null ? null : new Dictionary<string, object> { ["$t"] = "opaque", ["type"] = value.GetType().FullName, ["value"] = Convert.ToString(value, CultureInfo.InvariantCulture) }; }
            }

            private static object EncodeValue(object value)
            {
                if (value == null) return null;
                Type type = value.GetType();
                if (value is string || value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal) return value;
                if (value is char ch) return ch.ToString();
                if (type.IsEnum) return new Dictionary<string, object> { ["$t"] = "enum", ["type"] = type.FullName, ["value"] = value.ToString() };
                if (value is Vector2 v2) return new Dictionary<string, object> { ["$t"] = "vec2", ["x"] = v2.x, ["y"] = v2.y };
                if (value is Vector3 v3) return new Dictionary<string, object> { ["$t"] = "vec3", ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                if (value is Vector4 v4) return new Dictionary<string, object> { ["$t"] = "vec4", ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                if (value is Color color) return new Dictionary<string, object> { ["$t"] = "color", ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a };

                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Tuple<,>))
                {
                    PropertyInfo item1 = type.GetProperty("Item1");
                    PropertyInfo item2 = type.GetProperty("Item2");
                    return new Dictionary<string, object> { ["$t"] = "tuple2", ["a"] = EncodeValue(item1.GetValue(value, null)), ["b"] = EncodeValue(item2.GetValue(value, null)) };
                }

                if (value is IDictionary dictionary)
                {
                    var result = new Dictionary<string, object>();
                    foreach (DictionaryEntry entry in dictionary) result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = EncodeValue(entry.Value);
                    return result;
                }

                if (value is IList list)
                {
                    var result = new List<object>(list.Count);
                    for (int i = 0; i < list.Count; i++) result.Add(EncodeValue(list[i]));
                    return result;
                }

                if (type.IsArray && value is IEnumerable enumerable)
                {
                    var result = new List<object>();
                    foreach (object item in enumerable) result.Add(EncodeValue(item));
                    return result;
                }

                throw new NotSupportedException("Unsupported event value type: " + type.FullName);
            }

            private static object DecodeValue(object token, Type expectedType)
            {
                if (token == null) return null;
                if (token is Dictionary<string, object> marker && marker.TryGetValue("$t", out object tagObj))
                {
                    string tag = Convert.ToString(tagObj, CultureInfo.InvariantCulture);
                    if (tag == "enum")
                    {
                        Type enumType = expectedType != null && expectedType.IsEnum ? expectedType : ResolveType(GetMarkerString(marker, "type"));
                        if (enumType == null || !enumType.IsEnum) throw new InvalidOperationException("Cannot resolve enum type");
                        return Enum.Parse(enumType, GetMarkerString(marker, "value"), true);
                    }
                    if (tag == "vec2") return new Vector2(GetMarkerFloat(marker, "x"), GetMarkerFloat(marker, "y"));
                    if (tag == "vec3") return new Vector3(GetMarkerFloat(marker, "x"), GetMarkerFloat(marker, "y"), GetMarkerFloat(marker, "z"));
                    if (tag == "vec4") return new Vector4(GetMarkerFloat(marker, "x"), GetMarkerFloat(marker, "y"), GetMarkerFloat(marker, "z"), GetMarkerFloat(marker, "w"));
                    if (tag == "color") return new Color(GetMarkerFloat(marker, "r"), GetMarkerFloat(marker, "g"), GetMarkerFloat(marker, "b"), GetMarkerFloat(marker, "a"));
                    if (tag == "tuple2")
                    {
                        if (expectedType == null || !expectedType.IsGenericType || expectedType.GetGenericTypeDefinition() != typeof(Tuple<,>)) throw new InvalidOperationException("Tuple requires expected type");
                        Type[] args = expectedType.GetGenericArguments();
                        object a = DecodeValue(marker["a"], args[0]);
                        object b = DecodeValue(marker["b"], args[1]);
                        return Activator.CreateInstance(expectedType, a, b);
                    }
                    if (tag == "opaque") throw new InvalidOperationException("Opaque value cannot be decoded safely");
                }

                if (expectedType != null)
                {
                    if (expectedType == typeof(string)) return Convert.ToString(token, CultureInfo.InvariantCulture);
                    if (expectedType == typeof(bool)) return Convert.ToBoolean(token, CultureInfo.InvariantCulture);
                    if (expectedType == typeof(int)) return Convert.ToInt32(token, CultureInfo.InvariantCulture);
                    if (expectedType == typeof(long)) return Convert.ToInt64(token, CultureInfo.InvariantCulture);
                    if (expectedType == typeof(float)) return Convert.ToSingle(token, CultureInfo.InvariantCulture);
                    if (expectedType == typeof(double)) return Convert.ToDouble(token, CultureInfo.InvariantCulture);
                    if (expectedType.IsEnum) return Enum.Parse(expectedType, Convert.ToString(token, CultureInfo.InvariantCulture), true);

                    if (token is IList list && expectedType.IsArray)
                    {
                        Type elementType = expectedType.GetElementType();
                        Array array = Array.CreateInstance(elementType, list.Count);
                        for (int i = 0; i < list.Count; i++) array.SetValue(DecodeValue(list[i], elementType), i);
                        return array;
                    }

                    if (token is IList genericList && expectedType.IsGenericType && expectedType.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        Type elementType = expectedType.GetGenericArguments()[0];
                        IList result = (IList)Activator.CreateInstance(expectedType);
                        for (int i = 0; i < genericList.Count; i++) result.Add(DecodeValue(genericList[i], elementType));
                        return result;
                    }
                }
                return token;
            }

            private static Type ResolveType(string fullName)
            {
                if (string.IsNullOrEmpty(fullName)) return null;
                Type direct = Type.GetType(fullName, false);
                if (direct != null) return direct;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        Type type = assembly.GetType(fullName, false);
                        if (type != null) return type;
                    }
                    catch { }
                }
                return null;
            }

            private static string GetMarkerString(Dictionary<string, object> marker, string key) => marker.TryGetValue(key, out object value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
            private static float GetMarkerFloat(Dictionary<string, object> marker, string key) => marker.TryGetValue(key, out object value) ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : 0f;

            public static string Canonicalize(object value)
            {
                if (value == null) return "null";
                if (value is string s) return "s:" + s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s;
                if (value is bool b) return b ? "true" : "false";
                if (value is float f) return "f:" + f.ToString("R", CultureInfo.InvariantCulture);
                if (value is double d) return "d:" + d.ToString("R", CultureInfo.InvariantCulture);
                if (value is decimal dec) return "m:" + dec.ToString(CultureInfo.InvariantCulture);
                if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
                    return "n:" + Convert.ToString(value, CultureInfo.InvariantCulture);
                if (value is IDictionary dictionary)
                {
                    var entries = new List<KeyValuePair<string, object>>();
                    foreach (DictionaryEntry entry in dictionary) entries.Add(new KeyValuePair<string, object>(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), entry.Value));
                    entries.Sort((a, b2) => string.CompareOrdinal(a.Key, b2.Key));
                    var sb = new StringBuilder("{");
                    for (int i = 0; i < entries.Count; i++) sb.Append(Canonicalize(entries[i].Key)).Append(':').Append(Canonicalize(entries[i].Value)).Append(',');
                    return sb.Append('}').ToString();
                }
                if (value is IEnumerable enumerable && !(value is string))
                {
                    var sb = new StringBuilder("[");
                    foreach (object item in enumerable) sb.Append(Canonicalize(item)).Append(',');
                    return sb.Append(']').ToString();
                }
                return "o:" + value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static string Fnv64(string text)
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }
    }

    internal static class ListExtensionsForOperationSync
    {
        public static List<T> TapInsert<T>(this List<T> list, int index, IEnumerable<T> values)
        {
            list.InsertRange(index, values);
            return list;
        }
    }
}
