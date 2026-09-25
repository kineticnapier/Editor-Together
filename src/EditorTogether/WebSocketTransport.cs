using System;
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
        private ClientWebSocket socket;
        private CancellationTokenSource cancellation;
        private readonly ConcurrentQueue<SnapshotMessage> incoming = new ConcurrentQueue<SnapshotMessage>();
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

        public async Task SendSnapshotAsync(long revision, string levelId, string levelData, bool levelSwitch)
        {
            if (!IsConnected) return;
            var envelope = new Dictionary<string, object>
            {
                ["type"] = levelSwitch ? "level-switch" : "snapshot",
                ["clientId"] = ClientId,
                ["levelId"] = levelId ?? string.Empty,
                ["revision"] = revision,
                ["levelData"] = levelData
            };
            byte[] bytes = Encoding.UTF8.GetBytes(RuntimeJson.Serialize(envelope));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token).ConfigureAwait(false);
        }

        public bool TryDequeue(out SnapshotMessage message) => incoming.TryDequeue(out message);

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
                        if (type != "snapshot" && type != "level-switch") continue;
                        string clientId = json.TryGetValue("clientId", out object clientObj) ? Convert.ToString(clientObj) : string.Empty;
                        if (clientId == ClientId) continue;
                        string levelId = json.TryGetValue("levelId", out object levelIdObj) ? Convert.ToString(levelIdObj) : string.Empty;
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
        }

        public void Dispose()
        {
            try { DisconnectAsync("Mod unloaded").Wait(500); } catch { }
            CleanupSocket();
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
}
