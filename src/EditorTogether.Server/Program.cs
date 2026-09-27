using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

const long MaxAssetBytes = 1024L * 1024L * 1024L;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxAssetBytes);
var app = builder.Build();
var rooms = new ConcurrentDictionary<string, RoomState>();
var assetRoot = Environment.GetEnvironmentVariable("EDITOR_TOGETHER_ASSET_CACHE");
if (string.IsNullOrWhiteSpace(assetRoot)) assetRoot = Path.Combine(AppContext.BaseDirectory, "asset-cache");
Directory.CreateDirectory(assetRoot);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.MapGet("/", () => Results.Text("EditorTogether.Server is running."));

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
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; await context.Response.WriteAsync("WebSocket required."); return; }
    var roomName = context.Request.Query["room"].ToString(); if (string.IsNullOrWhiteSpace(roomName)) roomName = "default";
    bool wantsHost = string.Equals(context.Request.Query["role"], "host", StringComparison.OrdinalIgnoreCase);

    RoomState room;
    if (wantsHost)
    {
        var fresh = new RoomState();
        if (!rooms.TryAdd(roomName, fresh)) { context.Response.StatusCode = 409; await context.Response.WriteAsync("Room already exists."); return; }
        room = fresh;
    }
    else if (!rooms.TryGetValue(roomName, out room))
    {
        context.Response.StatusCode = 404; await context.Response.WriteAsync("Room does not exist."); return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid();
    room.Clients[connectionId] = socket;
    if (wantsHost) room.HostConnectionId = connectionId;
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] + {connectionId:N} room={roomName} role={(wantsHost ? "host" : "client")} clients={room.Clients.Count}");

    byte[]? initialSnapshot;
    byte[]? initialAssetManifest;
    lock (room.Sync)
    {
        initialSnapshot = room.LatestSnapshot;
        initialAssetManifest = room.LatestAssetManifest;
    }
    if (!wantsHost && socket.State == WebSocketState.Open)
    {
        if (initialSnapshot != null)
            await socket.SendAsync(new ArraySegment<byte>(initialSnapshot), WebSocketMessageType.Text, true, context.RequestAborted);
        if (initialAssetManifest != null)
            await socket.SendAsync(new ArraySegment<byte>(initialAssetManifest), WebSocketMessageType.Text, true, context.RequestAborted);
    }

    var buffer = new byte[64 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream(); WebSocketReceiveResult result;
            do { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted); if (result.MessageType == WebSocketMessageType.Close) break; await message.WriteAsync(buffer.AsMemory(0, result.Count), context.RequestAborted); } while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var payload = message.ToArray();

            string type = "";
            try
            {
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.TryGetProperty("type", out var typeElement)) type = typeElement.GetString() ?? "";
            }
            catch { }

            bool relay = true;
            if (type is "snapshot" or "level-switch")
            {
                lock (room.Sync) room.LatestSnapshot = payload;
            }
            else if (type == "asset-manifest")
            {
                if (room.HostConnectionId != connectionId)
                {
                    relay = false;
                    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! ignored client asset-manifest room={roomName}");
                }
                else
                {
                    lock (room.Sync) room.LatestAssetManifest = payload;
                }
            }

            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] > {connectionId:N} room={roomName} type={type} bytes={payload.Length}");
            if (!relay) continue;

            foreach (var peer in room.Clients.ToArray())
            {
                if (peer.Key == connectionId || peer.Value.State != WebSocketState.Open) continue;
                try { await peer.Value.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, context.RequestAborted); } catch { }
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException ex) { Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! socket {connectionId:N}: {ex.Message}"); }
    finally
    {
        room.Clients.TryRemove(connectionId, out _);
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
            lock (room.Sync)
            {
                room.LatestSnapshot = null;
                room.LatestAssetManifest = null;
            }
        }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) { try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { } }
        socket.Dispose();
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] - {connectionId:N} room={roomName} clients={room.Clients.Count}");
    }
});

app.Run("http://0.0.0.0:38241");

string AssetPath(string hash) => Path.Combine(assetRoot!, hash.ToLowerInvariant());

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
    public object Sync { get; } = new();
    public Guid HostConnectionId { get; set; }
    public byte[]? LatestSnapshot { get; set; }
    public byte[]? LatestAssetManifest { get; set; }
}
