using System;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace EditorCollaboration
{
    public static class Main
    {
        internal static UnityModManager.ModEntry ModEntry;
        internal static Harmony Harmony;
        internal static CollaborationController Controller;

        private static string serverUrl = "ws://127.0.0.1:38241/ws";
        private static string room = "default";
        private static bool connecting;
        private static string status = "Disconnected";

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Controller = new CollaborationController(modEntry.Logger);
            Harmony = new Harmony(modEntry.Info.Id);
            Harmony.PatchAll(typeof(Main).Assembly);

            string overrideUrl = Environment.GetEnvironmentVariable("EDITORCOLLAB_URL");
            if (!string.IsNullOrWhiteSpace(overrideUrl))
                SplitUrlAndRoom(overrideUrl);

            modEntry.OnUpdate = OnUpdate;
            modEntry.OnGUI = OnGUI;
            modEntry.OnUnload = Unload;
            modEntry.Logger.Log("EditorCollaboration v0.0.2 prototype loaded.");
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            Controller?.Update();
            if (Controller != null && Controller.IsConnected)
                status = "Connected";
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Editor Collaboration");
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Server", GUILayout.Width(80f));
            serverUrl = GUILayout.TextField(serverUrl, GUILayout.MinWidth(360f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Room", GUILayout.Width(80f));
            room = GUILayout.TextField(room, GUILayout.Width(220f));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUI.enabled = Controller != null && !Controller.IsConnected && !connecting;
            if (GUILayout.Button(connecting ? "Connecting..." : "Connect", GUILayout.Width(130f)))
                _ = ConnectFromUiAsync();
            GUI.enabled = true;

            GUILayout.Space(6f);
            GUILayout.Label("Status: " + status);
            if (Controller != null)
            {
                GUILayout.Label("Client: " + Controller.ClientId);
                GUILayout.Label("Revision: " + Controller.Revision);
            }
            GUILayout.Space(4f);
            GUILayout.Label("Prototype: both users should open the same initial chart before connecting.");
        }

        private static async System.Threading.Tasks.Task ConnectFromUiAsync()
        {
            if (Controller == null || connecting)
                return;

            connecting = true;
            status = "Connecting...";
            try
            {
                string url = BuildRoomUrl(serverUrl, room);
                await Controller.ConnectAsync(url).ConfigureAwait(false);
                status = Controller.IsConnected ? "Connected" : "Connection failed (see log)";
            }
            catch (Exception ex)
            {
                status = "Connection failed";
                ModEntry?.Logger.Error("[Collab] UI connect failed: " + ex);
            }
            finally
            {
                connecting = false;
            }
        }

        private static string BuildRoomUrl(string baseUrl, string roomName)
        {
            string url = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(url))
                url = "ws://127.0.0.1:38241/ws";

            string escapedRoom = Uri.EscapeDataString(string.IsNullOrWhiteSpace(roomName) ? "default" : roomName.Trim());
            int roomIndex = url.IndexOf("room=", StringComparison.OrdinalIgnoreCase);
            if (roomIndex >= 0)
                return url;

            return url + (url.Contains("?") ? "&" : "?") + "room=" + escapedRoom;
        }

        private static void SplitUrlAndRoom(string url)
        {
            int query = url.IndexOf('?');
            if (query < 0)
            {
                serverUrl = url;
                return;
            }

            serverUrl = url.Substring(0, query);
            string queryText = url.Substring(query + 1);
            foreach (string part in queryText.Split('&'))
            {
                string[] pair = part.Split(new[] { '=' }, 2);
                if (pair.Length == 2 && string.Equals(pair[0], "room", StringComparison.OrdinalIgnoreCase))
                    room = Uri.UnescapeDataString(pair[1]);
            }
        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            Controller?.Dispose();
            Harmony?.UnpatchAll(modEntry.Info.Id);
            return true;
        }
    }
}
