using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityModManagerNet;

namespace EditorTogether
{
    internal sealed class WebSocketTransport : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private ClientWebSocket socket;
        private CancellationTokenSource cancellation;
        private readonly ConcurrentQueue<SnapshotMessage> incoming = new ConcurrentQueue<SnapshotMessage>();
        private readonly ConcurrentQueue<PresenceMessage> incomingPresence = new ConcurrentQueue<PresenceMessage>();
        public string ClientId { get; } = Guid.NewGuid().ToString("N");
        public bool IsConnected => socket != null && socket.State == WebSocketState.Open;

        public WebSocketTransport(UnityModManager.ModEntry.ModLogger logger) { this.logger = logger; }

        public async Task ConnectAsync(string url)
        {
            if (IsConnected) return;
            CleanupSocket();
            socket = new ClientWebSocket();
            cancellation = new CancellationTokenSource();
            await socket.ConnectAsync(new Uri(url), cancellation.Token).ConfigureAwait(false);
            logger.Log($"[Collab] connected to {url} as {ClientId}");
            _ = Task.Run(ReceiveLoopAsync);
        }

        public Task SendSnapshotAsync(long revision, string levelId, string levelData, bool levelSwitch)
        {
            var envelope = new Dictionary<string, object>
            {
                ["type"] = levelSwitch ? "level-switch" : "snapshot",
                ["clientId"] = ClientId,
                ["levelId"] = levelId ?? string.Empty,
                ["revision"] = revision,
                ["levelData"] = levelData
            };
            return SendEnvelopeAsync(envelope);
        }

        public Task SendPresenceAsync(string levelId, IReadOnlyList<int> selectedFloors)
        {
            var floors = new List<object>();
            if (selectedFloors != null)
                for (int i = 0; i < selectedFloors.Count; i++) floors.Add(selectedFloors[i]);

            var envelope = new Dictionary<string, object>
            {
                ["type"] = "presence",
                ["clientId"] = ClientId,
                ["levelId"] = levelId ?? string.Empty,
                ["selectedFloors"] = floors
            };
            return SendEnvelopeAsync(envelope);
        }

        private async Task SendEnvelopeAsync(Dictionary<string, object> envelope)
        {
            var current = socket;
            var currentCancellation = cancellation;
            if (current == null || currentCancellation == null || current.State != WebSocketState.Open) return;

            byte[] bytes = Encoding.UTF8.GetBytes(RuntimeJson.Serialize(envelope));
            await sendGate.WaitAsync(currentCancellation.Token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(current, socket) || current.State != WebSocketState.Open) return;
                await current.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, currentCancellation.Token).ConfigureAwait(false);
            }
            finally { sendGate.Release(); }
        }

        public bool TryDequeue(out SnapshotMessage message) => incoming.TryDequeue(out message);
        public bool TryDequeuePresence(out PresenceMessage message) => incomingPresence.TryDequeue(out message);

        public async Task DisconnectAsync(string reason = "Disconnected")
        {
            var current = socket;
            if (current == null) return;
            try
            {
                if (current.State == WebSocketState.Open || current.State == WebSocketState.CloseReceived)
                    await current.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            try { cancellation?.Cancel(); } catch { }
        }

        private async Task ReceiveLoopAsync()
        {
            var currentSocket = socket;
            var token = cancellation.Token;
            byte[] buffer = new byte[64 * 1024];
            try
            {
                while (!token.IsCancellationRequested && currentSocket.State == WebSocketState.Open)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await currentSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                try { await currentSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None).ConfigureAwait(false); } catch { }
                                return;
                            }
                            stream.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        string jsonText = Encoding.UTF8.GetString(stream.ToArray());
                        var json = RuntimeJson.Deserialize(jsonText) as Dictionary<string, object>;
                        if (json == null || !json.TryGetValue("type", out object typeObj)) continue;
                        string type = Convert.ToString(typeObj);
                        string clientId = json.TryGetValue("clientId", out object clientObj) ? Convert.ToString(clientObj) : string.Empty;
                        if (clientId == ClientId) continue;
                        string levelId = json.TryGetValue("levelId", out object levelIdObj) ? Convert.ToString(levelIdObj) : string.Empty;

                        if (type == "presence")
                        {
                            var floors = new List<int>();
                            if (json.TryGetValue("selectedFloors", out object floorsObj) && floorsObj is IList list)
                            {
                                for (int i = 0; i < list.Count; i++)
                                {
                                    try { floors.Add(Convert.ToInt32(list[i])); } catch { }
                                }
                            }
                            incomingPresence.Enqueue(new PresenceMessage(clientId, levelId, floors.ToArray()));
                            continue;
                        }

                        if (type != "snapshot" && type != "level-switch") continue;
                        long revision = json.TryGetValue("revision", out object revisionObj) ? Convert.ToInt64(revisionObj) : 0;
                        string levelData = json.TryGetValue("levelData", out object levelObj) ? Convert.ToString(levelObj) : string.Empty;
                        incoming.Enqueue(new SnapshotMessage(clientId, levelId, revision, levelData, type == "level-switch"));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.Error($"[Collab] receive loop failed: {ex}"); }
        }

        private void CleanupSocket()
        {
            try { cancellation?.Cancel(); } catch { }
            try { socket?.Dispose(); } catch { }
            try { cancellation?.Dispose(); } catch { }
            socket = null;
            cancellation = null;
            while (incoming.TryDequeue(out _)) { }
            while (incomingPresence.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            try { DisconnectAsync("Mod unloaded").Wait(500); } catch { }
            CleanupSocket();
            sendGate.Dispose();
        }
    }

    internal sealed class SnapshotMessage
    {
        public string ClientId { get; }
        public string LevelId { get; }
        public long Revision { get; }
        public string LevelData { get; }
        public bool IsLevelSwitch { get; }
        public SnapshotMessage(string clientId, string levelId, long revision, string levelData, bool isLevelSwitch)
        {
            ClientId = clientId; LevelId = levelId; Revision = revision; LevelData = levelData; IsLevelSwitch = isLevelSwitch;
        }
    }

    internal sealed class PresenceMessage
    {
        public string ClientId { get; }
        public string LevelId { get; }
        public int[] SelectedFloors { get; }
        public PresenceMessage(string clientId, string levelId, int[] selectedFloors)
        {
            ClientId = clientId; LevelId = levelId; SelectedFloors = selectedFloors ?? Array.Empty<int>();
        }
    }
}
