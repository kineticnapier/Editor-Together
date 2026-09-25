using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var rooms = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, WebSocket>>(StringComparer.Ordinal);

app.MapGet("/", () => Results.Text("EditorCollaboration relay is running."));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    string room = context.Request.Query["room"].FirstOrDefault() ?? "default";
    if (room.Length > 64)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    Guid connectionId = Guid.NewGuid();
    var clients = rooms.GetOrAdd(room, _ => new ConcurrentDictionary<Guid, WebSocket>());
    clients[connectionId] = socket;
    Console.WriteLine($"[{room}] + {connectionId} ({clients.Count} clients)");

    byte[] buffer = new byte[64 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            byte[] message = stream.ToArray();
            foreach (var pair in clients.ToArray())
            {
                WebSocket peer = pair.Value;
                if (peer.State != WebSocketState.Open)
                    continue;

                try
                {
                    await peer.SendAsync(message, WebSocketMessageType.Text, true, context.RequestAborted);
                }
                catch
                {
                    clients.TryRemove(pair.Key, out _);
                }
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException ex)
    {
        Console.WriteLine($"[{room}] websocket error for {connectionId}: {ex.Message}");
    }
    finally
    {
        clients.TryRemove(connectionId, out _);
        if (clients.IsEmpty)
            rooms.TryRemove(room, out _);
        Console.WriteLine($"[{room}] - {connectionId} ({clients.Count} clients)");
    }
});

app.Run("http://0.0.0.0:38241");
