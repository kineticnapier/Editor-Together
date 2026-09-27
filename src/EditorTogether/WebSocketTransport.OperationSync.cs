using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EditorTogether
{
    internal sealed partial class WebSocketTransport
    {
        private readonly ConcurrentQueue<OperationBatchMessage> incomingOperationBatches = new ConcurrentQueue<OperationBatchMessage>();
        private readonly ConcurrentQueue<OperationAckMessage> incomingOperationAcks = new ConcurrentQueue<OperationAckMessage>();
        private readonly ConcurrentQueue<OperationRejectedMessage> incomingOperationRejected = new ConcurrentQueue<OperationRejectedMessage>();
        private readonly ConcurrentQueue<SyncTargetMessage> incomingSyncTargets = new ConcurrentQueue<SyncTargetMessage>();

        public Task SendOperationBatchAsync(string levelId, long baseRevision, string batchId, IReadOnlyList<Dictionary<string, object>> operations)
        {
            var encodedOperations = new List<object>();
            if (operations != null)
            {
                for (int i = 0; i < operations.Count; i++)
                    encodedOperations.Add(operations[i]);
            }

            return SendEnvelopeAsync(new Dictionary<string, object>
            {
                ["type"] = "ops",
                ["clientId"] = ClientId,
                ["levelId"] = levelId ?? string.Empty,
                ["baseRevision"] = baseRevision,
                ["batchId"] = batchId ?? string.Empty,
                ["ops"] = encodedOperations
            });
        }

        public Task SendFullStateAsync(string levelId, long baseRevision, string batchId, string levelData)
        {
            return SendEnvelopeAsync(new Dictionary<string, object>
            {
                ["type"] = "full-state",
                ["clientId"] = ClientId,
                ["levelId"] = levelId ?? string.Empty,
                ["baseRevision"] = baseRevision,
                ["batchId"] = batchId ?? string.Empty,
                ["levelData"] = levelData ?? string.Empty
            });
        }

        public Task SendCheckpointAsync(string levelId, long revision, string levelData)
        {
            return SendEnvelopeAsync(new Dictionary<string, object>
            {
                ["type"] = "checkpoint",
                ["clientId"] = ClientId,
                ["levelId"] = levelId ?? string.Empty,
                ["revision"] = revision,
                ["levelData"] = levelData ?? string.Empty
            });
        }

        public bool TryDequeueOperationBatch(out OperationBatchMessage message) => incomingOperationBatches.TryDequeue(out message);
        public bool TryDequeueOperationAck(out OperationAckMessage message) => incomingOperationAcks.TryDequeue(out message);
        public bool TryDequeueOperationRejected(out OperationRejectedMessage message) => incomingOperationRejected.TryDequeue(out message);
        public bool TryDequeueSyncTarget(out SyncTargetMessage message) => incomingSyncTargets.TryDequeue(out message);

        private bool TryHandleOperationEnvelope(Dictionary<string, object> json, string type, string clientId, string levelId)
        {
            if (type == "ops")
            {
                long baseRevision = ReadInt64(json, "baseRevision");
                long revision = ReadInt64(json, "revision");
                string batchId = ReadString(json, "batchId");
                var operations = new List<Dictionary<string, object>>();
                if (json.TryGetValue("ops", out object opsObj) && opsObj is IList list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is Dictionary<string, object> operation)
                            operations.Add(operation);
                    }
                }
                incomingOperationBatches.Enqueue(new OperationBatchMessage(clientId, levelId, baseRevision, revision, batchId, operations.ToArray()));
                return true;
            }

            if (type == "ops-ack")
            {
                incomingOperationAcks.Enqueue(new OperationAckMessage(ReadString(json, "batchId"), ReadInt64(json, "revision")));
                return true;
            }

            if (type == "ops-rejected")
            {
                incomingOperationRejected.Enqueue(new OperationRejectedMessage(
                    ReadString(json, "batchId"),
                    ReadString(json, "reason"),
                    ReadInt64(json, "serverRevision")));
                return true;
            }

            if (type == "sync-target")
            {
                incomingSyncTargets.Enqueue(new SyncTargetMessage(levelId, ReadInt64(json, "revision")));
                return true;
            }

            if (type == "protocol-error")
            {
                logger.Error("[CollabProtocol] " + ReadString(json, "message"));
                return true;
            }

            return false;
        }

        private static string ReadString(Dictionary<string, object> json, string key)
            => json.TryGetValue(key, out object value) ? Convert.ToString(value) ?? string.Empty : string.Empty;

        private static long ReadInt64(Dictionary<string, object> json, string key)
        {
            if (!json.TryGetValue(key, out object value) || value == null) return 0L;
            try { return Convert.ToInt64(value); }
            catch { return 0L; }
        }

        private void ClearOperationQueues()
        {
            while (incomingOperationBatches.TryDequeue(out _)) { }
            while (incomingOperationAcks.TryDequeue(out _)) { }
            while (incomingOperationRejected.TryDequeue(out _)) { }
            while (incomingSyncTargets.TryDequeue(out _)) { }
        }
    }

    internal sealed class OperationBatchMessage
    {
        public string ClientId { get; }
        public string LevelId { get; }
        public long BaseRevision { get; }
        public long Revision { get; }
        public string BatchId { get; }
        public Dictionary<string, object>[] Operations { get; }

        public OperationBatchMessage(string clientId, string levelId, long baseRevision, long revision, string batchId, Dictionary<string, object>[] operations)
        {
            ClientId = clientId ?? string.Empty;
            LevelId = levelId ?? string.Empty;
            BaseRevision = baseRevision;
            Revision = revision;
            BatchId = batchId ?? string.Empty;
            Operations = operations ?? Array.Empty<Dictionary<string, object>>();
        }
    }

    internal sealed class OperationAckMessage
    {
        public string BatchId { get; }
        public long Revision { get; }
        public OperationAckMessage(string batchId, long revision) { BatchId = batchId ?? string.Empty; Revision = revision; }
    }

    internal sealed class OperationRejectedMessage
    {
        public string BatchId { get; }
        public string Reason { get; }
        public long ServerRevision { get; }
        public OperationRejectedMessage(string batchId, string reason, long serverRevision)
        {
            BatchId = batchId ?? string.Empty;
            Reason = reason ?? string.Empty;
            ServerRevision = serverRevision;
        }
    }

    internal sealed class SyncTargetMessage
    {
        public string LevelId { get; }
        public long Revision { get; }
        public SyncTargetMessage(string levelId, long revision) { LevelId = levelId ?? string.Empty; Revision = revision; }
    }
}
