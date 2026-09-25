using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityModManagerNet;

namespace EditorCollaboration
{
    internal sealed class WebSocketTransport : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
        private readonly ConcurrentQueue<SnapshotMessage> received = new ConcurrentQueue<SnapshotMessage>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly string clientId = Guid.NewGuid().ToString("N");
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket socket;

        public string ClientId => clientId;
        public bool IsConnected => socket != null && socket.State == WebSocketState.Open;

        public WebSocketTransport(UnityModManager.ModEntry.ModLogger logger)
        {
            this.logger = logger;
        }

        public async Task ConnectAsync(string url)
        {
            if (IsConnected)
                return;

            socket?.Dispose();
            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(url), cancellation.Token).ConfigureAwait(false);
            logger.Log($"[Collab] connected to {url} as {clientId}");
            _ = ReceiveLoopAsync(socket, cancellation.Token);
        }

        public async Task SendSnapshotAsync(long revision, string encodedLevel)
        {
            ClientWebSocket current = socket;
            if (current == null || current.State != WebSocketState.Open)
                return;

            var envelope = new Dictionary<string, object>
            {
                ["type"] = "snapshot",
                ["clientId"] = clientId,
                ["revision"] = revision,
                ["levelData"] = encodedLevel
            };
            byte[] payload = Encoding.UTF8.GetBytes(Json.Serialize(envelope));

            await sendLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                await current.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        public bool TryDequeue(out SnapshotMessage message) => received.TryDequeue(out message);

        private async Task ReceiveLoopAsync(ClientWebSocket current, CancellationToken token)
        {
            byte[] buffer = new byte[64 * 1024];
            try
            {
                while (!token.IsCancellationRequested && current.State == WebSocketState.Open)
                {
                    using (var stream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await current.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await current.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                                return;
                            }
                            stream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType != WebSocketMessageType.Text)
                            continue;

                        string json = Encoding.UTF8.GetString(stream.ToArray());
                        var obj = Json.Deserialize(json) as Dictionary<string, object>;
                        if (obj == null || !obj.TryGetValue("type", out object type) || !string.Equals(type as string, "snapshot", StringComparison.Ordinal))
                            continue;

                        string sender = obj.TryGetValue("clientId", out object senderValue) ? senderValue as string : null;
                        if (string.IsNullOrEmpty(sender) || sender == clientId)
                            continue;
                        if (!obj.TryGetValue("revision", out object revisionValue) || !obj.TryGetValue("levelData", out object levelValue))
                            continue;

                        long revision = Convert.ToInt64(revisionValue);
                        string levelData = levelValue as string;
                        if (!string.IsNullOrEmpty(levelData))
                            received.Enqueue(new SnapshotMessage(sender, revision, levelData));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.Error($"[Collab] receive loop failed: {ex}");
            }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            try { socket?.Dispose(); } catch { }
            sendLock.Dispose();
            cancellation.Dispose();
        }
    }

    internal sealed class SnapshotMessage
    {
        public readonly string ClientId;
        public readonly long Revision;
        public readonly string LevelData;

        public SnapshotMessage(string clientId, long revision, string levelData)
        {
            ClientId = clientId;
            Revision = revision;
            LevelData = levelData;
        }
    }
}
