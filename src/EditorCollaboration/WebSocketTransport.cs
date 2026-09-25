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
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly ConcurrentQueue<SnapshotMessage> incoming = new ConcurrentQueue<SnapshotMessage>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task receiveTask;
        public string ClientId { get; } = Guid.NewGuid().ToString("N");
        public bool IsConnected => socket.State == WebSocketState.Open;

        public WebSocketTransport(UnityModManager.ModEntry.ModLogger logger) { this.logger = logger; }

        public async Task ConnectAsync(string url)
        {
            if (IsConnected) return;
            await socket.ConnectAsync(new Uri(url), cancellation.Token).ConfigureAwait(false);
            logger.Log($"[Collab] connected to {url} as {ClientId}");
            receiveTask = Task.Run(ReceiveLoopAsync);
        }

        public async Task SendSnapshotAsync(long revision, string levelData)
        {
            if (!IsConnected) return;
            var envelope = new Dictionary<string, object>
            {
                ["type"] = "snapshot",
                ["clientId"] = ClientId,
                ["revision"] = revision,
                ["levelData"] = levelData
            };
            byte[] bytes = Encoding.UTF8.GetBytes(RuntimeJson.Serialize(envelope));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token).ConfigureAwait(false);
        }

        public bool TryDequeue(out SnapshotMessage message) => incoming.TryDequeue(out message);

        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[64 * 1024];
            try
            {
                while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) return;
                            stream.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        string jsonText = Encoding.UTF8.GetString(stream.ToArray());
                        var json = RuntimeJson.Deserialize(jsonText) as Dictionary<string, object>;
                        if (json == null || !json.TryGetValue("type", out object typeObj) || Convert.ToString(typeObj) != "snapshot") continue;
                        string clientId = json.TryGetValue("clientId", out object clientObj) ? Convert.ToString(clientObj) : string.Empty;
                        if (clientId == ClientId) continue;
                        long revision = json.TryGetValue("revision", out object revisionObj) ? Convert.ToInt64(revisionObj) : 0;
                        string levelData = json.TryGetValue("levelData", out object levelObj) ? Convert.ToString(levelObj) : string.Empty;
                        incoming.Enqueue(new SnapshotMessage(clientId, revision, levelData));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.Error($"[Collab] receive loop failed: {ex}"); }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            try { if (socket.State == WebSocketState.Open) socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Mod unloaded", CancellationToken.None).Wait(250); } catch { }
            socket.Dispose();
            cancellation.Dispose();
        }
    }

    internal sealed class SnapshotMessage
    {
        public string ClientId { get; }
        public long Revision { get; }
        public string LevelData { get; }
        public SnapshotMessage(string clientId, long revision, string levelData) { ClientId = clientId; Revision = revision; LevelData = levelData; }
    }
}
