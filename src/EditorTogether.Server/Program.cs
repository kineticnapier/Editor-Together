using System.Collections.Concurrent;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var rooms = new ConcurrentDictionary<string, RoomState>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.MapGet("/", () => Results.Text("EditorTogether.Server is running."));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket required.");
        return;
    }

    var roomName = context.Request.Query["room"].ToString();
    if (string.IsNullOrWhiteSpace(roomName)) roomName = "default";
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = Guid.NewGuid();
    var room = rooms.GetOrAdd(roomName, _ => new RoomState());
    room.Clients[connectionId] = socket;
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] + {connectionId:N} room={roomName} clients={room.Clients.Count}");

    byte[]? initialSnapshot;
    lock (room.Sync) initialSnapshot = room.LatestSnapshot;
    if (initialSnapshot != null && socket.State == WebSocketState.Open)
    {
        await socket.SendAsync(new ArraySegment<byte>(initialSnapshot), WebSocketMessageType.Text, true, context.RequestAborted);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] < initial snapshot -> {connectionId:N} room={roomName} bytes={initialSnapshot.Length}");
    }

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
            lock (room.Sync) room.LatestSnapshot = payload;
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] > {connectionId:N} room={roomName} bytes={payload.Length} (saved as latest)");
            foreach (var peer in room.Clients.ToArray())
            {
                if (peer.Key == connectionId || peer.Value.State != WebSocketState.Open) continue;
                try { await peer.Value.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, context.RequestAborted); }
                catch (Exception ex) { Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! send {peer.Key:N}: {ex.Message}"); }
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException ex) { Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ! socket {connectionId:N}: {ex.Message}"); }
    finally
    {
        room.Clients.TryRemove(connectionId, out _);
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
        socket.Dispose();
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] - {connectionId:N} room={roomName} clients={room.Clients.Count}");
    }
});

app.Run("http://0.0.0.0:38241");

sealed class RoomState
{
    public ConcurrentDictionary<Guid, WebSocket> Clients { get; } = new();
    public object Sync { get; } = new();
    public byte[]? LatestSnapshot { get; set; }
}
