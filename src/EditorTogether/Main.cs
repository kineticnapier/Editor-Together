using System;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace EditorTogether
{
    public static class Main
    {
        internal static UnityModManager.ModEntry ModEntry;
        internal static Harmony Harmony;
        internal static CollaborationController Controller;
        internal static bool Enabled { get; private set; } = true;
        private static string serverUrl = "ws://127.0.0.1:38241/ws";
        private static string room = "default";
        private static string displayName = string.IsNullOrWhiteSpace(Environment.UserName) ? "Player" : Environment.UserName;
        private static bool connecting;
        private static string status = "Disconnected";

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Controller = new CollaborationController(modEntry.Logger);
            Controller.SetDisplayName(displayName);
            Harmony = new Harmony(modEntry.Info.Id);
            Harmony.PatchAll(typeof(Main).Assembly);
            Enabled = true;

            string overrideUrl = Environment.GetEnvironmentVariable("EDITORCOLLAB_URL");
            if (!string.IsNullOrWhiteSpace(overrideUrl)) SplitUrlAndRoom(overrideUrl);

            modEntry.OnToggle = OnToggle;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnGUI = OnGUI;
            modEntry.OnUnload = Unload;
            modEntry.Logger.Log("EditorTogether prototype loaded.");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (Enabled == value) return true;
            Enabled = value;

            if (value)
            {
                Harmony = new Harmony(modEntry.Info.Id);
                Harmony.PatchAll(typeof(Main).Assembly);
                status = "Disconnected";
                modEntry.Logger.Log("[Collab] enabled");
            }
            else
            {
                connecting = false;
                status = "Disabled";
                if (Controller != null && Controller.IsConnected)
                    _ = Controller.DisconnectAsync();
                Harmony?.UnpatchAll(modEntry.Info.Id);
                modEntry.Logger.Log("[Collab] disabled; patches removed");
            }

            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            if (!Enabled) return;
            Controller?.Update(deltaTime);
            if (!connecting && Controller != null && !Controller.IsConnected && status.StartsWith("Connected", StringComparison.Ordinal))
                status = "Disconnected";
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Editor Together"); GUILayout.Space(4f);
            GUILayout.BeginHorizontal(); GUILayout.Label("Name", GUILayout.Width(80f));
            string nextName = GUILayout.TextField(displayName, GUILayout.Width(220f));
            GUILayout.EndHorizontal();
            if (!string.Equals(nextName, displayName, StringComparison.Ordinal))
            {
                displayName = nextName;
                Controller?.SetDisplayName(displayName);
            }
            GUILayout.BeginHorizontal(); GUILayout.Label("Server", GUILayout.Width(80f)); serverUrl = GUILayout.TextField(serverUrl, GUILayout.MinWidth(360f)); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("Room", GUILayout.Width(80f)); room = GUILayout.TextField(room, GUILayout.Width(220f)); GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUI.enabled = Controller != null && !Controller.IsConnected && !connecting;
            if (GUILayout.Button(connecting ? "Connecting..." : "Create Room", GUILayout.Width(130f))) _ = ConnectFromUiAsync(true);
            if (GUILayout.Button(connecting ? "Connecting..." : "Join Room", GUILayout.Width(130f))) _ = ConnectFromUiAsync(false);
            GUI.enabled = Controller != null && Controller.IsConnected && !connecting;
            if (GUILayout.Button("Disconnect", GUILayout.Width(130f))) _ = DisconnectFromUiAsync();
            GUILayout.EndHorizontal(); GUI.enabled = true;
            GUILayout.Space(6f); GUILayout.Label("Status: " + status);
            if (Controller != null)
            {
                GUILayout.Label("Client: " + ShortId(Controller.ClientId));
                GUILayout.Label("Revision: " + Controller.Revision);
                GUILayout.Label("Level: " + Controller.LevelId);

                if (Controller.IsConnected)
                {
                    var participants = Controller.GetParticipants();
                    GUILayout.Space(6f);
                    GUILayout.Label("Participants (" + participants.Count + ")");
                    for (int i = 0; i < participants.Count; i++)
                    {
                        ParticipantInfo participant = participants[i];
                        GUILayout.BeginHorizontal();
                        Color oldColor = GUI.contentColor;
                        GUI.contentColor = RemotePresenceOverlay.ColorForClient(participant.ClientId);
                        GUILayout.Label("●", GUILayout.Width(18f));
                        GUI.contentColor = oldColor;

                        string suffix = participant.IsLocal ? " (You)" : string.Empty;
                        if (participant.IsHost) suffix += " [Host]";
                        GUILayout.Label(participant.DisplayName + suffix);
                        GUILayout.EndHorizontal();
                    }
                }
            }
            GUILayout.Space(4f); GUILayout.Label("Remote selected tiles are shown as colored outlines with player names.");
            GUILayout.Label("Host level changes are broadcast to everyone. Host disconnect closes the room.");
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "-";
            return id.Length <= 8 ? id : id.Substring(0, 8) + "...";
        }

        private static async System.Threading.Tasks.Task ConnectFromUiAsync(bool createRoom)
        {
            if (!Enabled || Controller == null || connecting) return;
            connecting = true; status = createRoom ? "Creating room..." : "Joining room...";
            try
            {
                Controller.SetDisplayName(displayName);
                string url = BuildRoomUrl(serverUrl, room);
                if (createRoom) await Controller.CreateRoomAsync(url).ConfigureAwait(false); else await Controller.JoinRoomAsync(url).ConfigureAwait(false);
                status = Controller.IsConnected ? (createRoom ? "Connected (Host)" : "Connected (Joined)") : "Connection failed (see log)";
            }
            catch (Exception ex) { status = "Connection failed"; ModEntry?.Logger.Error("[Collab] UI connect failed: " + ex); }
            finally { connecting = false; }
        }

        private static async System.Threading.Tasks.Task DisconnectFromUiAsync()
        {
            if (Controller == null || connecting) return;
            connecting = true; status = "Disconnecting...";
            try { await Controller.DisconnectAsync().ConfigureAwait(false); status = "Disconnected"; }
            catch (Exception ex) { status = "Disconnect failed"; ModEntry?.Logger.Error("[Collab] UI disconnect failed: " + ex); }
            finally { connecting = false; }
        }

        private static string BuildRoomUrl(string baseUrl, string roomName)
        {
            string url = (baseUrl ?? string.Empty).Trim(); if (string.IsNullOrEmpty(url)) url = "ws://127.0.0.1:38241/ws";
            string escapedRoom = Uri.EscapeDataString(string.IsNullOrWhiteSpace(roomName) ? "default" : roomName.Trim());
            if (url.IndexOf("room=", StringComparison.OrdinalIgnoreCase) >= 0) return url;
            return url + (url.Contains("?") ? "&" : "?") + "room=" + escapedRoom;
        }

        private static void SplitUrlAndRoom(string url)
        {
            int query = url.IndexOf('?'); if (query < 0) { serverUrl = url; return; }
            serverUrl = url.Substring(0, query); string queryText = url.Substring(query + 1);
            foreach (string part in queryText.Split('&')) { string[] pair = part.Split(new[] { '=' }, 2); if (pair.Length == 2 && string.Equals(pair[0], "room", StringComparison.OrdinalIgnoreCase)) room = Uri.UnescapeDataString(pair[1]); }
        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            Enabled = false;
            Controller?.Dispose();
            Harmony?.UnpatchAll(modEntry.Info.Id);
            return true;
        }
    }
}
