using System;
using System.Collections.Generic;
using System.Diagnostics;
using ADOFAI;
using HarmonyLib;
using UnityModManagerNet;
using UnityEngine;

namespace EditorTogether
{
    internal sealed class CollaborationController : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
        private readonly WebSocketTransport transport;
        private readonly RemotePresenceOverlay presenceOverlay = new RemotePresenceOverlay();
        private static readonly System.Reflection.FieldInfo SaveStateLastFrameField = AccessTools.Field(typeof(scnEditor), "saveStateLastFrame");
        private readonly Dictionary<string, RemotePresenceState> remotePresence = new Dictionary<string, RemotePresenceState>();
        private readonly List<int> lastPresenceFloors = new List<int>();
        private scnEditor lastEditor;
        private LevelData observedLevelData;
        private bool dirty;
        private float dirtyElapsed;
        private int observedSaveStateFrame = int.MinValue;
        private const float DebounceDelay = 0.20f;
        private bool isHost;
        private string levelId = string.Empty;
        private float presenceHeartbeatElapsed;
        private float presenceCleanupElapsed;
        private const float PresenceHeartbeatSeconds = 2f;
        private const float PresenceTimeoutSeconds = 6f;

        private long updateCalls;
        private long updateTicks;
        private long findEditorCalls;
        private long findEditorTicks;
        private long saveStateCalls;
        private long publishCalls;
        private long publishTicks;
        private long appliedSnapshots;
        private float diagnosticsElapsed;

        public bool IsApplyingRemote { get; private set; }
        public long Revision { get; private set; }
        public bool IsConnected => transport.IsConnected;
        public bool IsHost => isHost;
        public string ClientId => transport.ClientId;
        public string LevelId => levelId;

        public CollaborationController(UnityModManager.ModEntry.ModLogger logger) { this.logger = logger; transport = new WebSocketTransport(logger); }

        public async System.Threading.Tasks.Task CreateRoomAsync(string url)
        {
            isHost = true;
            await ConnectCoreAsync(AddRole(url, "host")).ConfigureAwait(false);
            TryFindEditor();
            if (lastEditor == null) throw new InvalidOperationException("Open a chart in the editor before creating a room.");
            BeginNewLevel(lastEditor, true);
        }

        public async System.Threading.Tasks.Task JoinRoomAsync(string url)
        {
            isHost = false;
            levelId = string.Empty;
            Revision = 0;
            await ConnectCoreAsync(AddRole(url, "client")).ConfigureAwait(false);
            TryFindEditor();
            if (lastEditor != null) ResetObservation(lastEditor);
            ForcePresenceRefresh();
            logger.Log("[Collab] joined room; waiting for host snapshot");
        }

        public async System.Threading.Tasks.Task DisconnectAsync()
        {
            dirty = false; dirtyElapsed = 0f;
            try
            {
                if (transport.IsConnected)
                    await transport.SendPresenceAsync(levelId, Array.Empty<int>()).ConfigureAwait(false);
            }
            catch { }
            await transport.DisconnectAsync(isHost ? "Host disconnected" : "Client disconnected").ConfigureAwait(false);
            isHost = false; levelId = string.Empty; Revision = 0;
            lastEditor = null; observedLevelData = null; observedSaveStateFrame = int.MinValue;
            ClearPresence();
            logger.Log("[Collab] disconnected");
        }

        private static string AddRole(string url, string role) => url + (url.Contains("?") ? "&" : "?") + "role=" + role;
        private async System.Threading.Tasks.Task ConnectCoreAsync(string url) { try { await transport.ConnectAsync(url).ConfigureAwait(false); } catch (Exception ex) { logger.Error($"[Collab] connection failed: {ex.Message}"); throw; } }

        private void TryFindEditor()
        {
            long started = Stopwatch.GetTimestamp();
            findEditorCalls++;
            try
            {
                scnEditor found;
                try { found = UnityEngine.Object.FindFirstObjectByType<scnEditor>(); }
                catch { found = UnityEngine.Object.FindObjectOfType<scnEditor>(); }
                if (found == null) return;
                if (!ReferenceEquals(found, lastEditor)) { lastEditor = found; ResetObservation(found); ForcePresenceRefresh(); }
            }
            finally { findEditorTicks += Stopwatch.GetTimestamp() - started; }
        }

        private static int ReadSaveStateLastFrame(scnEditor editor) => editor == null || SaveStateLastFrameField == null ? int.MinValue : (int)SaveStateLastFrameField.GetValue(editor);
        private void ResetObservation(scnEditor editor) { observedSaveStateFrame = ReadSaveStateLastFrame(editor); observedLevelData = editor?.levelData; }

        private void ObserveEditor(scnEditor editor)
        {
            if (!transport.IsConnected || IsApplyingRemote) return;
            if (!ReferenceEquals(observedLevelData, editor.levelData))
            {
                observedLevelData = editor.levelData;
                observedSaveStateFrame = ReadSaveStateLastFrame(editor);
                dirty = false; dirtyElapsed = 0f;
                ClearRemotePresenceOnly();
                if (isHost) BeginNewLevel(editor, true);
                return;
            }

            int frame = ReadSaveStateLastFrame(editor);
            if (frame == int.MinValue || frame == observedSaveStateFrame) return;
            observedSaveStateFrame = frame;
            dirty = true; dirtyElapsed = 0f;
        }

        public void OnEditorStateSaved(scnEditor editor)
        {
            saveStateCalls++;
            if (editor != null) lastEditor = editor;
        }

        private void BeginNewLevel(scnEditor editor, bool publish)
        {
            levelId = Guid.NewGuid().ToString("N");
            Revision = 0;
            ResetObservation(editor);
            ClearRemotePresenceOnly();
            ForcePresenceRefresh();
            logger.Log($"[Collab] host level changed; levelId={levelId}");
            if (publish) PublishSnapshot(editor, true);
        }

        private void PublishSnapshot(scnEditor editor, bool levelSwitch = false)
        {
            long started = Stopwatch.GetTimestamp();
            publishCalls++;
            try
            {
                if (!transport.IsConnected) return;
                if (string.IsNullOrEmpty(levelId)) levelId = Guid.NewGuid().ToString("N");
                string encodedLevel = editor.levelData.Encode();
                Revision++;
                logger.Log($"[Collab] {(levelSwitch ? "level switch" : "snapshot")} published; level={levelId}, revision={Revision}, bytes={encodedLevel.Length}");
                _ = SendSnapshotAsync(Revision, levelId, encodedLevel, levelSwitch);
            }
            finally { publishTicks += Stopwatch.GetTimestamp() - started; }
        }

        private async System.Threading.Tasks.Task SendSnapshotAsync(long revision, string id, string encodedLevel, bool levelSwitch)
        {
            try { await transport.SendSnapshotAsync(revision, id, encodedLevel, levelSwitch).ConfigureAwait(false); }
            catch (Exception ex) { logger.Error($"[Collab] send failed: {ex.Message}"); }
        }

        public void Update(float deltaTime = 0f)
        {
            long started = Stopwatch.GetTimestamp();
            updateCalls++;
            float dt = deltaTime > 0f ? deltaTime : UnityEngine.Time.unscaledDeltaTime;
            diagnosticsElapsed += dt;
            try
            {
                if (!transport.IsConnected)
                {
                    if (remotePresence.Count != 0) ClearPresence();
                    return;
                }

                if (lastEditor == null) TryFindEditor();
                if (lastEditor == null) return;

                SnapshotMessage newest = null;
                while (transport.TryDequeue(out SnapshotMessage message)) newest = message;
                if (newest != null) ApplyRemoteSnapshot(lastEditor, newest);

                while (transport.TryDequeuePresence(out PresenceMessage presence))
                    ApplyPresence(lastEditor, presence);

                UpdateLocalPresence(lastEditor, dt);
                CleanupPresence(dt);
                ObserveEditor(lastEditor);
                if (!dirty || IsApplyingRemote) return;
                dirtyElapsed += dt;
                if (dirtyElapsed < DebounceDelay) return;
                dirty = false; dirtyElapsed = 0f;
                try { logger.Log("[Collab] editor mutation settled; publishing snapshot"); PublishSnapshot(lastEditor); }
                catch (Exception ex) { logger.Error("[Collab] live snapshot failed: " + ex.Message); }
            }
            finally
            {
                updateTicks += Stopwatch.GetTimestamp() - started;
                if (diagnosticsElapsed >= 5f) FlushDiagnostics();
            }
        }

        private void UpdateLocalPresence(scnEditor editor, float dt)
        {
            var floors = new List<int>();
            if (editor.selectedFloors != null)
            {
                for (int i = 0; i < editor.selectedFloors.Count; i++)
                {
                    scrFloor floor = editor.selectedFloors[i];
                    if (floor != null) floors.Add(floor.seqID);
                }
            }
            floors.Sort();

            presenceHeartbeatElapsed += dt;
            bool changed = floors.Count != lastPresenceFloors.Count;
            if (!changed)
            {
                for (int i = 0; i < floors.Count; i++)
                    if (floors[i] != lastPresenceFloors[i]) { changed = true; break; }
            }

            if (!changed && presenceHeartbeatElapsed < PresenceHeartbeatSeconds) return;
            lastPresenceFloors.Clear();
            lastPresenceFloors.AddRange(floors);
            presenceHeartbeatElapsed = 0f;
            _ = SendPresenceAsync(floors);
        }

        private async System.Threading.Tasks.Task SendPresenceAsync(IReadOnlyList<int> floors)
        {
            try { await transport.SendPresenceAsync(levelId, floors).ConfigureAwait(false); }
            catch (Exception ex) { logger.Error("[Collab] presence send failed: " + ex.Message); }
        }

        private void ApplyPresence(scnEditor editor, PresenceMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.ClientId)) return;
            if (string.IsNullOrEmpty(levelId) || message.LevelId != levelId) return;

            if (message.SelectedFloors.Length == 0)
            {
                remotePresence.Remove(message.ClientId);
                presenceOverlay.Clear(message.ClientId);
                return;
            }

            remotePresence[message.ClientId] = new RemotePresenceState(message.SelectedFloors, Time.realtimeSinceStartup);
            presenceOverlay.Show(editor, message.ClientId, message.SelectedFloors);
        }

        private void CleanupPresence(float dt)
        {
            presenceCleanupElapsed += dt;
            if (presenceCleanupElapsed < 1f) return;
            presenceCleanupElapsed = 0f;
            float now = Time.realtimeSinceStartup;
            var stale = new List<string>();
            foreach (var pair in remotePresence)
                if (now - pair.Value.LastSeen > PresenceTimeoutSeconds) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++)
            {
                remotePresence.Remove(stale[i]);
                presenceOverlay.Clear(stale[i]);
            }
        }

        private void ForcePresenceRefresh()
        {
            lastPresenceFloors.Clear();
            presenceHeartbeatElapsed = PresenceHeartbeatSeconds;
        }

        private void ClearRemotePresenceOnly()
        {
            remotePresence.Clear();
            presenceOverlay.ClearAll();
        }

        private void ClearPresence()
        {
            ClearRemotePresenceOnly();
            lastPresenceFloors.Clear();
            presenceHeartbeatElapsed = 0f;
            presenceCleanupElapsed = 0f;
        }

        private void FlushDiagnostics()
        {
            double tickMs = 1000.0 / Stopwatch.Frequency;
            long memory = GC.GetTotalMemory(false);
            logger.Log($"[CollabPerf] connected={transport.IsConnected} enabled={Main.Enabled} window={diagnosticsElapsed:F1}s " +
                       $"update={updateCalls} calls/{updateTicks * tickMs:F2}ms " +
                       $"findEditor={findEditorCalls} calls/{findEditorTicks * tickMs:F2}ms " +
                       $"SaveState={saveStateCalls} publish={publishCalls}/{publishTicks * tickMs:F2}ms " +
                       $"apply={appliedSnapshots} peers={remotePresence.Count} managedMem={memory / (1024.0 * 1024.0):F1}MiB");
            diagnosticsElapsed = 0f;
            updateCalls = updateTicks = findEditorCalls = findEditorTicks = saveStateCalls = publishCalls = publishTicks = appliedSnapshots = 0;
        }

        private void ApplyRemoteSnapshot(scnEditor editor, SnapshotMessage snapshot)
        {
            if (!snapshot.IsLevelSwitch && !string.IsNullOrEmpty(levelId) && snapshot.LevelId != levelId)
            {
                logger.Log($"[Collab] ignored stale snapshot for level={snapshot.LevelId}");
                return;
            }

            if (snapshot.IsLevelSwitch) ClearRemotePresenceOnly();
            appliedSnapshots++;
            ApplyRemote(() =>
            {
                var dictionary = RuntimeJson.Deserialize(snapshot.LevelData) as Dictionary<string, object>;
                if (dictionary == null) throw new InvalidOperationException("Remote LevelData was not a JSON object.");
                var data = new LevelData(); data.Setup(); LoadResult loadResult; data.Decode(dictionary, out loadResult);
                editor.customLevel.levelData = data;
                editor.RemakePath(true, true);
                AccessTools.Method(typeof(scnEditor), "UpdateDecorationObjects")?.Invoke(editor, null);
                levelId = snapshot.LevelId;
                Revision = snapshot.IsLevelSwitch ? snapshot.Revision : Math.Max(Revision, snapshot.Revision);
                dirty = false; dirtyElapsed = 0f; ResetObservation(editor); ForcePresenceRefresh();
                logger.Log($"[Collab] applied {(snapshot.IsLevelSwitch ? "level switch" : "snapshot")}; level={levelId}, revision={snapshot.Revision}, from={snapshot.ClientId}, loadResult={loadResult}");
            });
        }

        public void ApplyRemote(Action apply) { if (apply == null) throw new ArgumentNullException(nameof(apply)); IsApplyingRemote = true; try { apply(); } catch (Exception ex) { logger.Error($"[Collab] remote apply failed: {ex}"); } finally { IsApplyingRemote = false; } }
        public void Dispose() { presenceOverlay.Dispose(); transport.Dispose(); }

        private sealed class RemotePresenceState
        {
            public int[] Floors { get; }
            public float LastSeen { get; }
            public RemotePresenceState(int[] floors, float lastSeen) { Floors = floors ?? Array.Empty<int>(); LastSeen = lastSeen; }
        }
    }
}
