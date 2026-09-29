using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

const int ProtocolVersion = 2;
const int MaxOperationsPerBatch = 64;
const int MaxOperationBatchBytes = 64 * 1024;
const long MaxAssetBytes = 1024L * 1024L * 1024L;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxAssetBytes);
var app = builder.Build();
var rooms = new ConcurrentDictionary<string, RoomState>();
var sendGates = new ConcurrentDictionary<WebSocket, SemaphoreSlim>();
var assetRoot = Environment.GetEnvironmentVariable("EDITOR_TOGETHER_ASSET_CACHE");
if (string.IsNullOrWhiteSpace(assetRoot)) assetRoot = Path.Combine(AppContext.BaseDirectory, "asset-cache");
Directory.CreateDirectory(assetRoot);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.MapGet("/", () => Results.Text($"EditorTogether.Server is running. protocol={ProtocolVersion}"));

app.MapMethods("/assets/{hash}", new[] { "HEAD" }, (string hash) =>
{
    if (!IsValidSha256(hash)) return Results.BadRequest();
    string path = AssetPath(hash);
    return File.Exists(path) ? Results.Ok() : Results.NotFound();
});

app.MapGet("/assets/{hash}", (string hash) =>
{
    if (!IsValidSha256(hash)) return Results.BadRequest();
    string path = AssetPath(hash);
    if (!File.Exists(path)) return Results.NotFound();
    return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
});

app.MapPut("/assets/{hash}", async (HttpContext context, string hash) =>
{
    if (!IsValidSha256(hash)) return Results.BadRequest("Invalid SHA-256 hash.");
    if (context.Request.ContentLength is long length && length > MaxAssetBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    string target = AssetPath(hash);
    if (File.Exists(target)) return Results.Ok();

    string temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
    long total = 0;
    try
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            byte[] buffer = new byte[128 * 1024];
            while (true)
            {
                int read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestAborted);
                if (read <= 0) break;
                total += read;
                if (total > MaxAssetBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }

        string actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Asset hash mismatch.");

        if (File.Exists(target)) File.Delete(temp);
        else File.Move(temp, target);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] asset + {hash[..8]} bytes={total}");
        return Results.Ok();
    }
    finally
    {
        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
    }
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket required.");
        return;
    }

    if (!int.TryParse(context.Request.Query["protocol"], out int requestedProtocol) || requestedProtocol != ProtocolVersion)
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsync($"Protocol mismatch. server={ProtocolVersion}");
        return;
    }

    var roomName = context.Request.Query["room"].ToString();
    if (string.IsNullOrWhiteSpace(roomName)) roomName = "default";
    bool wantsHost = string.Equals(context.Request.Query["role"], "host", StringComparison.OrdinalIgnoreCase);

    RoomState room;
    if (wantsHost)
    {
        var fresh = new RoomState();
        if (!rooms.TryAdd(roomName, fresh))
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsync("Room already exists.");
            return;
        }
        room = fresh;
    }
    else if (!rooms.TryGetValue(roomName, out room))
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Room does not exist.");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid();
    room.Clients[connectionId] = socket;
    room.ClientCanPublish[connectionId] = wantsHost;
    if (wantsHost) room.HostConnectionId = connectionId;
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] + {connectionId:N} room={roomName} role={(wantsHost ? "host" : "client")} protocol={ProtocolVersion} clients={room.Clients.Count}");

    var buffer = new byte[64 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                await message.WriteAsync(buffer.AsMemory(0, result.Count), context.RequestAborted);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var payload = message.ToArray();

            string type = string.Empty;
            string messageLevelId = string.Empty;
            string messageClientId = string.Empty;
            string batchId = string.Empty;
            string levelData = string.Empty;
            long messageRevision = 0;
            long baseRevision = 0;
            int messageProtocol = 0;
            JsonElement operations = default;
            bool hasOperations = false;

            try
            {
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (root.TryGetProperty("type", out var typeElement)) type = typeElement.GetString() ?? string.Empty;
                if (root.TryGetProperty("levelId", out var levelElement)) messageLevelId = levelElement.GetString() ?? string.Empty;
                if (root.TryGetProperty("clientId", out var clientElement)) messageClientId = clientElement.GetString() ?? string.Empty;
                if (root.TryGetProperty("batchId", out var batchElement)) batchId = batchElement.GetString() ?? string.Empty;
                if (root.TryGetProperty("levelData", out var dataElement)) levelData = dataElement.GetString() ?? string.Empty;
                if (root.TryGetProperty("revision", out var revisionElement) && revisionElement.TryGetInt64(out long parsedRevision)) messageRevision = parsedRevision;
                if (root.TryGetProperty("baseRevision", out var baseElement) && baseElement.TryGetInt64(out long parsedBase)) baseRevision = parsedBase;
                if (root.TryGetProperty("protocolVersion", out var protocolElement) && protocolElement.TryGetInt32(out int parsedProtocol)) messageProtocol = parsedProtocol;
                if (root.TryGetProperty("ops", out var opsElement))
                {
                    operations = opsElement.Clone();
                    hasOperations = true;
                }
            }
            catch
            {
                await SendBytesAsync(socket, ProtocolError("Malformed JSON message."), context.RequestAborted);
                continue;
            }

            if (messageProtocol != ProtocolVersion)
            {
                await SendBytesAsync(socket, ProtocolError($"Protocol mismatch. server={ProtocolVersion} client={messageProtocol}"), context.RequestAborted);
                continue;
            }

            if (type == "sync-request")
            {
                if (wantsHost) continue;
                room.ClientCanPublish[connectionId] = false;
                byte[]? snapshot;
                byte[]? manifest;
                byte[][] opLog;
                string targetLevel;
                long targetRevision;
                lock (room.Sync)
                {
                    snapshot = room.LatestSnapshot;
                    manifest = room.LatestAssetManifest;
                    opLog = room.OperationLog.ToArray();
                    targetLevel = room.CurrentLevelId;
                    targetRevision = room.CurrentRevision;
                }

                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] sync -> {connectionId:N} room={roomName} snapshot={(snapshot != null)} ops={opLog.Length} target={targetRevision} assets={(manifest != null)}");
                if (snapshot != null && socket.State == WebSocketState.Open) await SendBytesAsync(socket, snapshot, context.RequestAborted);
                if (manifest != null && socket.State == WebSocketState.Open) await SendBytesAsync(socket, manifest, context.RequestAborted);
                for (int i = 0; i < opLog.Length && socket.State == WebSocketState.Open; i++) await SendBytesAsync(socket, opLog[i], context.RequestAborted);
                if (!string.IsNullOrEmpty(targetLevel) && socket.State == WebSocketState.Open) await SendBytesAsync(socket, SyncTarget(targetLevel, targetRevision), context.RequestAborted);
                continue;
            }

            if (type == "sync-ready")
            {
                if (wantsHost) continue;
                bool matches;
                string targetLevel;
                long targetRevision;
                lock (room.Sync)
                {
                    targetLevel = room.CurrentLevelId;
                    targetRevision = room.CurrentRevision;
                    matches = !string.IsNullOrEmpty(targetLevel) &&
                              string.Equals(messageLevelId, targetLevel, StringComparison.Ordinal) &&
                              messageRevision == targetRevision;
                }
                room.ClientCanPublish[connectionId] = matches;
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] sync-ready {connectionId:N} room={roomName} level={messageLevelId} revision={messageRevision} accepted={matches}");
                if (!matches && !string.IsNullOrEmpty(targetLevel) && socket.State == WebSocketState.Open)
                    await SendBytesAsync(socket, SyncTarget(targetLevel, targetRevision), context.RequestAborted);
                continue;
            }

            if (type == "ops")
            {
                bool canPublish = wantsHost || (room.ClientCanPublish.TryGetValue(connectionId, out bool enabled) && enabled);
                string rejectReason = string.Empty;
                long serverRevision;
                long acceptedRevision = 0;
                byte[]? authoritative = null;

                lock (room.Sync)
                {
                    serverRevision = room.CurrentRevision;
                    if (!canPublish) rejectReason = "sync-barrier";
                    else if (string.IsNullOrEmpty(room.CurrentLevelId) || !string.Equals(messageLevelId, room.CurrentLevelId, StringComparison.Ordinal)) rejectReason = "level-mismatch";
                    else if (baseRevision != room.CurrentRevision) rejectReason = "revision-mismatch";
                    else if (!hasOperations || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0) rejectReason = "empty-ops";
                    else if (operations.GetArrayLength() > MaxOperationsPerBatch || payload.Length > MaxOperationBatchBytes) rejectReason = "batch-too-large";
                    else
                    {
                        acceptedRevision = room.CurrentRevision + 1;
                        authoritative = OperationEnvelope(messageClientId, room.CurrentLevelId, baseRevision, acceptedRevision, batchId, operations);
                        room.CurrentRevision = acceptedRevision;
                        room.OperationLog.Add(authoritative);
                        serverRevision = acceptedRevision;
                    }
                }

                if (authoritative == null)
                {
                    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! ops rejected room={roomName} reason={rejectReason} base={baseRevision} current={serverRevision}");
                    await SendBytesAsync(socket, OperationRejected(batchId, rejectReason, serverRevision), context.RequestAborted);
                    continue;
                }

                await SendBytesAsync(socket, OperationAck(batchId, acceptedRevision), context.RequestAborted);
                await BroadcastAsync(room, connectionId, authoritative, context.RequestAborted);
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ops room={roomName} revision={acceptedRevision} bytes={authoritative.Length}");
                continue;
            }

            if (type == "full-state")
            {
                bool canPublish = wantsHost || (room.ClientCanPublish.TryGetValue(connectionId, out bool enabled) && enabled);
                string rejectReason = string.Empty;
                long serverRevision;
                long acceptedRevision = 0;
                byte[]? snapshot = null;

                lock (room.Sync)
                {
                    serverRevision = room.CurrentRevision;
                    if (!canPublish) rejectReason = "sync-barrier";
                    else if (string.IsNullOrEmpty(room.CurrentLevelId) || !string.Equals(messageLevelId, room.CurrentLevelId, StringComparison.Ordinal)) rejectReason = "level-mismatch";
                    else if (baseRevision != room.CurrentRevision) rejectReason = "revision-mismatch";
                    else if (string.IsNullOrEmpty(levelData)) rejectReason = "empty-state";
                    else
                    {
                        acceptedRevision = room.CurrentRevision + 1;
                        snapshot = SnapshotEnvelope(messageClientId, room.CurrentLevelId, acceptedRevision, levelData, false);
                        room.CurrentRevision = acceptedRevision;
                        room.LatestSnapshot = snapshot;
                        room.OperationLog.Clear();
                        serverRevision = acceptedRevision;
                    }
                }

                if (snapshot == null)
                {
                    await SendBytesAsync(socket, OperationRejected(batchId, rejectReason, serverRevision), context.RequestAborted);
                    continue;
                }

                await SendBytesAsync(socket, OperationAck(batchId, acceptedRevision), context.RequestAborted);
                await BroadcastAsync(room, connectionId, snapshot, context.RequestAborted);
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] full-state room={roomName} revision={acceptedRevision} bytes={snapshot.Length}");
                continue;
            }

            if (type == "checkpoint")
            {
                bool accepted = false;
                if (wantsHost && !string.IsNullOrEmpty(levelData))
                {
                    lock (room.Sync)
                    {
                        if (string.Equals(messageLevelId, room.CurrentLevelId, StringComparison.Ordinal) && messageRevision == room.CurrentRevision)
                        {
                            room.LatestSnapshot = SnapshotEnvelope(messageClientId, room.CurrentLevelId, room.CurrentRevision, levelData, false);
                            room.OperationLog.Clear();
                            accepted = true;
                        }
                    }
                }
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] checkpoint room={roomName} revision={messageRevision} accepted={accepted}");
                continue;
            }

            bool relay = true;
            bool sendTargetAfterRelay = false;
            byte[] relayPayload = payload;

            if (type == "level-switch")
            {
                if (!wantsHost)
                {
                    relay = false;
                    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! blocked client level-switch room={roomName}");
                }
                else
                {
                    lock (room.Sync)
                    {
                        room.CurrentLevelId = messageLevelId;
                        room.CurrentRevision = messageRevision;
                        room.LatestSnapshot = payload;
                        room.LatestAssetManifest = null;
                        room.OperationLog.Clear();
                    }
                    foreach (var peerId in room.Clients.Keys)
                        if (peerId != connectionId) room.ClientCanPublish[peerId] = false;
                    sendTargetAfterRelay = true;
                }
            }
            else if (type == "snapshot")
            {
                bool canPublish = wantsHost || (room.ClientCanPublish.TryGetValue(connectionId, out bool enabled) && enabled);
                lock (room.Sync)
                {
                    if (!canPublish || string.IsNullOrEmpty(room.CurrentLevelId) || !string.Equals(messageLevelId, room.CurrentLevelId, StringComparison.Ordinal) || messageRevision != room.CurrentRevision + 1)
                    {
                        relay = false;
                    }
                    else
                    {
                        room.CurrentRevision = messageRevision;
                        room.LatestSnapshot = payload;
                        room.OperationLog.Clear();
                    }
                }
                if (!relay) Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! blocked legacy snapshot room={roomName} level={messageLevelId} revision={messageRevision}");
            }
            else if (type == "asset-manifest")
            {
                string currentLevelId;
                lock (room.Sync) currentLevelId = room.CurrentLevelId;
                if (room.HostConnectionId != connectionId || string.IsNullOrEmpty(currentLevelId) || !string.Equals(messageLevelId, currentLevelId, StringComparison.Ordinal))
                {
                    relay = false;
                    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! ignored invalid asset-manifest room={roomName} level={messageLevelId} current={currentLevelId}");
                }
                else
                {
                    lock (room.Sync) room.LatestAssetManifest = payload;
                }
            }

            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] > {connectionId:N} room={roomName} type={type} bytes={payload.Length}");
            if (!relay) continue;

            await BroadcastAsync(room, connectionId, relayPayload, context.RequestAborted);
            if (sendTargetAfterRelay)
            {
                string targetLevel;
                long targetRevision;
                lock (room.Sync) { targetLevel = room.CurrentLevelId; targetRevision = room.CurrentRevision; }
                if (!string.IsNullOrEmpty(targetLevel))
                    await BroadcastAsync(room, connectionId, SyncTarget(targetLevel, targetRevision), context.RequestAborted);
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException ex) { Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! socket {connectionId:N}: {ex.Message}"); }
    finally
    {
        room.Clients.TryRemove(connectionId, out _);
        room.ClientCanPublish.TryRemove(connectionId, out _);
        bool hostLeft = room.HostConnectionId == connectionId;
        if (hostLeft && rooms.TryRemove(new KeyValuePair<string, RoomState>(roomName, room)))
        {
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] x host left; closing room={roomName}");
            foreach (var peer in room.Clients.ToArray())
            {
                try { if (peer.Value.State == WebSocketState.Open) await peer.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "Host disconnected", CancellationToken.None); } catch { }
                try { peer.Value.Dispose(); } catch { }
            }
            room.Clients.Clear();
            room.ClientCanPublish.Clear();
            lock (room.Sync)
            {
                room.CurrentLevelId = string.Empty;
                room.CurrentRevision = 0;
                room.LatestSnapshot = null;
                room.LatestAssetManifest = null;
                room.OperationLog.Clear();
            }
        }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
        socket.Dispose();
        sendGates.TryRemove(socket, out _);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] - {connectionId:N} room={roomName} clients={room.Clients.Count}");
    }
});

app.Run("http://0.0.0.0:38241");

string AssetPath(string hash) => Path.Combine(assetRoot!, hash.ToLowerInvariant());

async Task SendBytesAsync(WebSocket socket, byte[] payload, CancellationToken token)
{
    SemaphoreSlim gate = sendGates.GetOrAdd(socket, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync(token);
    try
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token);
    }
    finally
    {
        gate.Release();
    }
}

async Task BroadcastAsync(RoomState room, Guid sourceConnectionId, byte[] payload, CancellationToken token)
{
    foreach (var peer in room.Clients.ToArray())
    {
        if (peer.Key == sourceConnectionId || peer.Value.State != WebSocketState.Open) continue;
        try { await SendBytesAsync(peer.Value, payload, token); } catch { }
    }
}

static byte[] ProtocolError(string message) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
{
    ["type"] = "protocol-error",
    ["protocolVersion"] = ProtocolVersion,
    ["message"] = message
});

static byte[] OperationAck(string batchId, long revision) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
{
    ["type"] = "ops-ack",
    ["protocolVersion"] = ProtocolVersion,
    ["batchId"] = batchId,
    ["revision"] = revision
});

static byte[] OperationRejected(string batchId, string reason, long serverRevision) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
{
    ["type"] = "ops-rejected",
    ["protocolVersion"] = ProtocolVersion,
    ["batchId"] = batchId,
    ["reason"] = reason,
    ["serverRevision"] = serverRevision
});

static byte[] SyncTarget(string levelId, long revision) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
{
    ["type"] = "sync-target",
    ["protocolVersion"] = ProtocolVersion,
    ["levelId"] = levelId,
    ["revision"] = revision
});

static byte[] OperationEnvelope(string clientId, string levelId, long baseRevision, long revision, string batchId, JsonElement operations)
    => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
    {
        ["type"] = "ops",
        ["protocolVersion"] = ProtocolVersion,
        ["clientId"] = clientId,
        ["levelId"] = levelId,
        ["baseRevision"] = baseRevision,
        ["revision"] = revision,
        ["batchId"] = batchId,
        ["ops"] = operations
    });

static byte[] SnapshotEnvelope(string clientId, string levelId, long revision, string levelData, bool levelSwitch)
    => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
    {
        ["type"] = levelSwitch ? "level-switch" : "snapshot",
        ["protocolVersion"] = ProtocolVersion,
        ["clientId"] = clientId,
        ["levelId"] = levelId,
        ["revision"] = revision,
        ["levelData"] = levelData
    });

static bool IsValidSha256(string value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
    foreach (char c in value)
    {
        bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        if (!hex) return false;
    }
    return true;
}

sealed class RoomState
{
    public ConcurrentDictionary<Guid, WebSocket> Clients { get; } = new();
    public ConcurrentDictionary<Guid, bool> ClientCanPublish { get; } = new();
    public object Sync { get; } = new();
    public Guid HostConnectionId { get; set; }
    public string CurrentLevelId { get; set; } = string.Empty;
    public long CurrentRevision { get; set; }
    public byte[]? LatestSnapshot { get; set; }
    public byte[]? LatestAssetManifest { get; set; }
    public List<byte[]> OperationLog { get; } = new();
}
