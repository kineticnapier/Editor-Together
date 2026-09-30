using System;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace EditorTogether.Patches
{
    [HarmonyPatch(typeof(WebSocketTransport), nameof(WebSocketTransport.DisconnectAsync))]
    internal static class FastDisconnectPatch
    {
        private const int CloseTimeoutMs = 500;
        private static readonly FieldInfo SocketField = AccessTools.Field(typeof(WebSocketTransport), "socket");
        private static readonly FieldInfo CancellationField = AccessTools.Field(typeof(WebSocketTransport), "cancellation");
        private static readonly MethodInfo CleanupSocketMethod = AccessTools.Method(typeof(WebSocketTransport), "CleanupSocket");

        [HarmonyPrefix]
        private static bool Prefix(WebSocketTransport __instance, string reason, ref Task __result)
        {
            __result = DisconnectWithTimeoutAsync(__instance, reason);
            return false;
        }

        private static async Task DisconnectWithTimeoutAsync(WebSocketTransport instance, string reason)
        {
            ClientWebSocket current = null;
            CancellationTokenSource lifetime = null;
            try
            {
                current = SocketField?.GetValue(instance) as ClientWebSocket;
                lifetime = CancellationField?.GetValue(instance) as CancellationTokenSource;

                if (current != null && (current.State == WebSocketState.Open || current.State == WebSocketState.CloseReceived))
                {
                    Task closeTask = null;
                    try
                    {
                        closeTask = current.CloseAsync(WebSocketCloseStatus.NormalClosure, reason ?? "Disconnected", CancellationToken.None);
                    }
                    catch { }

                    if (closeTask != null)
                    {
                        Task winner = await Task.WhenAny(closeTask, Task.Delay(CloseTimeoutMs)).ConfigureAwait(false);
                        if (winner == closeTask)
                        {
                            try { await closeTask.ConfigureAwait(false); } catch { }
                        }
                        else
                        {
                            Main.ModEntry?.Logger.Warning("[Collab] WebSocket close handshake timed out; aborting after 500ms");
                            try { current.Abort(); } catch { }
                            closeTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        }
                    }
                }

                try
                {
                    if (current != null && (current.State == WebSocketState.Open || current.State == WebSocketState.CloseSent || current.State == WebSocketState.Connecting))
                        current.Abort();
                }
                catch { }

                try { lifetime?.Cancel(); } catch { }
            }
            finally
            {
                try { CleanupSocketMethod?.Invoke(instance, null); } catch { }
            }
        }
    }
}
