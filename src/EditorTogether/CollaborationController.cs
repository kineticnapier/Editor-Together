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
        private readonly AssetSyncManager assetSync;
        private readonly RemotePresenceOverlay presenceOverlay = new RemotePresenceOverlay();
        private static readonly System.Reflection.FieldInfo SaveStateLastFrameField = AccessTools.Field(typeof(scnEditor), "saveStateLastFrame");
        private readonly Dictionary<string, RemotePresenceState> remotePresence = new Dictionary<string, RemotePresenceState>();
        private readonly List<int> lastPresenceFloors = new List<int>();
        private scnEditor lastEditor;
        private LevelData observedLevelData;
        private string observedLevelPath = string.Empty;
        private bool dirty;
        private float dirtyElapsed;
        private int observedSaveStateFrame = int.MinValue;
        private const float DebounceDelay = 0.20f;
        private bool isHost;
        private string levelId = string.Empty;
        private string displayName = "Player";
        private float presenceHeartbeatElapsed;
        private float presenceCleanupElapsed;
        private const float PresenceHeartbeatSeconds = 2f;
        private const float PresenceTimeoutSeconds = 6f;

        // A client is not allowed to publish its local LevelData until it has applied the
        // room's authoritative snapshot. This prevents an empty/default editor from
        // overwriting everybody after rejoin or editor re-entry.
        private bool clientSynchronized = true;
        private bool syncRequestOutstanding;
        private int syncGeneration;
        private bool editorMissing;
        private float findEditorRetryElapsed;
        private const float FindEditorRetrySeconds = 0.50f;

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
        public bool IsSynchronized => isHost || clientSynchronized;
        public string ClientId => transport.ClientId;
        public string LevelId => levelId;
        public string DisplayName => displayName;
        public string AssetStatus => assetSync.Status;
        public string SyncStatus
        {
            get
            {
                if (!transport.IsConnected) return "Sync: Disconnected";
                if (isHost) return "Sync: Host authoritative";
                return clientSynchronized ? "Sync: Synchronized" : "Sync: Waiting for host state (local publishing blocked)";
            }
        }

        public CollaborationController(UnityModManager.ModEntry.ModLogger logger)
        {
            this.logger = logger;
            transport = new WebSocketTransport(logger);
            assetSync = new AssetSyncManager(logger, transport);
        }

        public void SetDisplayName(string value)
        {
            string next = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrEmpty(next)) next = "Player";
            if (next.Length > 32) next = next.Substring(0, 32);
            if (string.Equals(displayName, next, StringComparison.Ordinal)) return;
            displayName = next;
            ForcePresenceRefresh();
        }

        public IReadOnlyList<ParticipantInfo> GetParticipants()
        {
            var result = new List<ParticipantInfo>();
            if (transport.IsConnected)
                result.Add(new ParticipantInfo(transport.ClientId, displayName, isHost, true));

            foreach (var pair in remotePresence)
            {
                RemotePresenceState state = pair.Value;
                result.Add(new ParticipantInfo(pair.Key, state.DisplayName, state.IsHost, false));
            }

            result.Sort((a, b) =>
            {
                if (a.IsHost != b.IsHost) return a.IsHost ? -1 : 1;
                if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public async System.Threading.Tasks.Task CreateRoomAsync(string url)
        {
            isHost = true;
            clientSynchronized = true;
            syncRequestOutstanding = false;
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
            clientSynchronized = false;
            syncRequestOutstanding = false;
            syncGeneration++;
            await ConnectCoreAsync(AddQuery(AddRole(url, "client"), "sync", "explicit")).ConfigureAwait(false);
            TryFindEditor();
            if (lastEditor != null) ResetObservation(lastEditor);
            ForcePresenceRefresh();
            RequireClientResync("joined room");
            logger.Log("[Collab] joined room; explicit current-state sync requested");
        }

        public async System.Threading.Tasks.Task DisconnectAsync()
        {
            dirty = false; dirtyElapsed = 0f;
            try
            {
                if (transport.IsConnected)
                    await transport.SendPresenceAsync(levelId, displayName, isHost, true, Array.Empty<int>()).ConfigureAwait(false);
            }
            catch { }
            await transport.DisconnectAsync(isHost ? "Host disconnected" : "Client disconnected").ConfigureAwait(false);
            isHost = false; levelId = string.Empty; Revision = 0;
            clientSynchronized = true;
            syncRequestOutstanding = false;
            syncGeneration++;
            editorMissing = false;
            findEditorRetryElapsed = 0f;
            lastEditor = null; observedLevelData = null; observedLevelPath = string.Empty; observedSaveStateFrame = int.MinValue;
            ClearPresence();
            assetSync.Reset();
            logger.Log("[Collab] disconnected");
        }

        private static string AddRole(string url, string role) => AddQuery(url, "role", role);
        private static string AddQuery(string url, string key, string value) => url + (url.Contains("?") ? "&" : "?") + key + "=" + Uri.EscapeDataString(value ?? string.Empty);
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
                if (!ReferenceEquals(found, lastEditor))
                {
                    lastEditor = found;
                    editorMissing = false;
                    findEditorRetryElapsed = 0f;
                    ResetObservation(found);
                    ForcePresenceRefresh();
                    if (transport.IsConnected && !isHost)
                        RequireClientResync("editor instance changed");
                }
            }
            finally { findEditorTicks += Stopwatch.GetTimestamp() - started; }
        }

        private static int ReadSaveStateLastFrame(scnEditor editor) => editor == null || SaveStateLastFrameField == null ? int.MinValue : (int)SaveStateLastFrameField.GetValue(editor);
        private static string ReadLevelPath() => ADOBase.levelPath ?? string.Empty;
        private void ResetObservation(scnEditor editor)
        {
            observedSaveStateFrame = ReadSaveStateLastFrame(editor);
            observedLevelData = editor?.levelData;
            observedLevelPath = ReadLevelPath();
        }

        private void RequireClientResync(string reason)
        {
            if (isHost || !transport.IsConnected) return;

            clientSynchronized = false;
            dirty = false;
            dirtyElapsed = 0f;
            syncGeneration++;
            ClearRemoteSelections();
            logger.Warning("[CollabSafety] client entered sync barrier: " + reason);

            if (syncRequestOutstanding) return;
            syncRequestOutstanding = true;
            int generation = syncGeneration;
            _ = RequestCurrentStateAsync(generation, reason);
        }

        private async System.Threading.Tasks.Task RequestCurrentStateAsync(int generation, string reason)
        {
            try
            {
                await transport.SendSyncRequestAsync().ConfigureAwait(false);
                logger.Log($"[CollabSafety] sync-request sent; generation={generation}, reason={reason}");
            }
            catch (Exception ex)
            {
                if (syncGeneration == generation) syncRequestOutstanding = false;
                logger.Error("[CollabSafety] sync-request failed: " + ex);
            }
        }

        private async System.Threading.Tasks.Task CompleteClientSyncAsync(int generation, string synchronizedLevelId, long revision)
        {
            try
            {
                await transport.SendSyncReadyAsync(synchronizedLevelId, revision).ConfigureAwait(false);
                if (!transport.IsConnected || isHost || generation != syncGeneration || !string.Equals(levelId, synchronizedLevelId, StringComparison.Ordinal))
                    return;

                clientSynchronized = true;
                syncRequestOutstanding = false;
                dirty = false;
                dirtyElapsed = 0f;
                ResetObservation(lastEditor);
                ForcePresenceRefresh();
                logger.Log($"[CollabSafety] synchronized; level={synchronizedLevelId}, revision={revision}, generation={generation}");
            }
            catch (Exception ex)
            {
                if (generation == syncGeneration) syncRequestOutstanding = false;
                clientSynchronized = false;
                logger.Error("[CollabSafety] sync-ready failed; publishing remains blocked: " + ex);
            }
        }

        private void ObserveEditor(scnEditor editor)
        {
            if (!transport.IsConnected || IsApplyingRemote) return;

            if (!isHost && !clientSynchronized)
            {
                dirty = false;
                dirtyElapsed = 0f;
                ResetObservation(editor);
                return;
            }

            string currentLevelPath = ReadLevelPath();
            bool levelPathChanged = !string.Equals(observedLevelPath, currentLevelPath, StringComparison.OrdinalIgnoreCase);
            bool levelDataChanged = !ReferenceEquals(observedLevelData, editor.levelData);
            if (levelPathChanged || levelDataChanged)
            {
                string oldPath = observedLevelPath;
                observedLevelData = editor.levelData;
                observedLevelPath = currentLevelPath;
                observedSaveStateFrame = ReadSaveStateLastFrame(editor);
                dirty = false; dirtyElapsed = 0f;
                ClearRemoteSelections();
                if (isHost)
                {
                    logger.Log($"[Collab] detected host chart switch: '{oldPath}' -> '{currentLevelPath}'");
                    BeginNewLevel(editor, true);
                }
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
            if (editor == null) return;

            if (!ReferenceEquals(editor, lastEditor))
            {
                lastEditor = editor;
                editorMissing = false;
                findEditorRetryElapsed = 0f;
                ResetObservation(editor);
                ForcePresenceRefresh();
                if (transport.IsConnected && !isHost)
                    RequireClientResync("SaveState observed on a new editor instance");
            }
        }

        private void BeginNewLevel(scnEditor editor, bool publish)
        {
            levelId = Guid.NewGuid().ToString("N");
            Revision = 0;
            ResetObservation(editor);
            ClearRemoteSelections();
            ForcePresenceRefresh();
            logger.Log($"[Collab] host level changed; levelId={levelId}");
            if (publish)
            {
                PublishSnapshot(editor, true);
                _ = assetSync.PublishHostManifestAsync(levelId, editor.levelData, ReadLevelPath());
            }
        }

        private void PublishSnapshot(scnEditor editor, bool levelSwitch = false)
        {
            long started = Stopwatch.GetTimestamp();
            publishCalls++;
            try
            {
                if (!transport.IsConnected) return;
                if (!isHost && !clientSynchronized)
                {
                    logger.Warning("[CollabSafety] blocked local snapshot while client is waiting for authoritative state");
                    dirty = false;
                    dirtyElapsed = 0f;
                    return;
                }
                if (editor == null || editor.levelData == null)
                {
                    logger.Warning("[CollabSafety] blocked snapshot because editor/LevelData is unavailable");
                    return;
                }
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

                if (lastEditor == null)
                {
                    if (!editorMissing)
                    {
                        editorMissing = true;
                        if (!isHost) RequireClientResync("editor closed or unavailable");
                    }

                    findEditorRetryElapsed += dt;
                    if (findEditorRetryElapsed >= FindEditorRetrySeconds)
                    {
                        findEditorRetryElapsed = 0f;
                        TryFindEditor();
                    }
                    if (lastEditor == null) return;
                }
                else
                {
                    editorMissing = false;
                    findEditorRetryElapsed = 0f;
                }

                SnapshotMessage newest = null;
                while (transport.TryDequeue(out SnapshotMessage message)) newest = message;
                if (newest != null) ApplyRemoteSnapshot(lastEditor, newest);

                while (transport.TryDequeueAssetManifest(out AssetManifestMessage manifest))
                {
                    if (!isHost) assetSync.QueueRemoteManifest(manifest);
                }

                while (assetSync.TryDequeueCompleted(out string completedLevel))
                {
                    if (!isHost && string.Equals(completedLevel, levelId, StringComparison.Ordinal))
                        ReloadRemoteAssets(lastEditor);
                }

                while (transport.TryDequeuePresence(out PresenceMessage presence))
                    ApplyPresence(lastEditor, presence);

                UpdateLocalPresence(lastEditor, dt);
                CleanupPresence(dt);
                ObserveEditor(lastEditor);
                if (!dirty || IsApplyingRemote || (!isHost && !clientSynchronized)) return;
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

        private void ReloadRemoteAssets(scnEditor editor)
        {
            if (editor == null || editor.customLevel == null) return;
            try
            {
                AccessTools.Method(typeof(scnEditor), "UpdateSongAndLevelSettings")?.Invoke(editor, null);
                editor.customLevel.ReloadSong(false);
                editor.customLevel.ReloadAssets(true, false);
                AccessTools.Method(typeof(scnEditor), "UpdateDecorationObjects")?.Invoke(editor, null);
                logger.Log($"[CollabAssets] reloaded downloaded assets for level={levelId}");
            }
            catch (Exception ex)
            {
                logger.Error("[CollabAssets] asset reload failed: " + ex);
            }
        }

        private void UpdateLocalPresence(scnEditor editor, float dt)
        {
            var floors = new List<int>();
            if ((isHost || clientSynchronized) && editor.selectedFloors != null)
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
            try { await transport.SendPresenceAsync(levelId, displayName, isHost, false, floors).ConfigureAwait(false); }
            catch (Exception ex) { logger.Error("[Collab] presence send failed: " + ex.Message); }
        }

        private void ApplyPresence(scnEditor editor, PresenceMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.ClientId)) return;

            if (message.IsLeaving)
            {
                remotePresence.Remove(message.ClientId);
                presenceOverlay.Clear(message.ClientId);
                return;
            }

            string name = (message.DisplayName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name)) name = message.ClientId.Length > 6 ? message.ClientId.Substring(0, 6) : message.ClientId;
            remotePresence[message.ClientId] = new RemotePresenceState(name, message.IsHost, message.SelectedFloors, Time.realtimeSinceStartup);

            if (string.IsNullOrEmpty(levelId) || message.LevelId != levelId || message.SelectedFloors.Length == 0)
            {
                presenceOverlay.Clear(message.ClientId);
                return;
            }

            presenceOverlay.Show(editor, message.ClientId, name, message.SelectedFloors);
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

        private void ClearRemoteSelections()
        {
            presenceOverlay.ClearAll();
        }

        private void ClearPresence()
        {
            remotePresence.Clear();
            presenceOverlay.ClearAll();
            lastPresenceFloors.Clear();
            presenceHeartbeatElapsed = 0f;
            presenceCleanupElapsed = 0f;
        }

        private void FlushDiagnostics()
        {
            double tickMs = 1000.0 / Stopwatch.Frequency;
            long memory = GC.GetTotalMemory(false);
            logger.Log($"[CollabPerf] connected={transport.IsConnected} enabled={Main.Enabled} sync={(isHost ? "host" : (clientSynchronized ? "ready" : "blocked"))} window={diagnosticsElapsed:F1}s " +
                       $"update={updateCalls} calls/{updateTicks * tickMs:F2}ms " +
                       $"findEditor={findEditorCalls} calls/{findEditorTicks * tickMs:F2}ms " +
                       $"SaveState={saveStateCalls} publish={publishCalls}/{publishTicks * tickMs:F2}ms " +
                       $"apply={appliedSnapshots} peers={remotePresence.Count} managedMem={memory / (1024.0 * 1024.0):F1}MiB");
            diagnosticsElapsed = 0f;
            updateCalls = updateTicks = findEditorCalls = findEditorTicks = saveStateCalls = publishCalls = publishTicks = appliedSnapshots = 0;
        }

        private void ApplyRemoteSnapshot(scnEditor editor, SnapshotMessage snapshot)
        {
            bool waitingForAuthoritativeState = !isHost && !clientSynchronized;
            if (!snapshot.IsLevelSwitch && !waitingForAuthoritativeState && !string.IsNullOrEmpty(levelId) && snapshot.LevelId != levelId)
            {
                logger.Log($"[Collab] ignored stale snapshot for level={snapshot.LevelId}");
                return;
            }

            bool enteringRemoteLevel = !isHost && (waitingForAuthoritativeState || snapshot.IsLevelSwitch || string.IsNullOrEmpty(levelId) || !string.Equals(levelId, snapshot.LevelId, StringComparison.Ordinal));
            if (snapshot.IsLevelSwitch) ClearRemoteSelections();
            appliedSnapshots++;
            bool appliedSuccessfully = false;
            ApplyRemote(() =>
            {
                var dictionary = RuntimeJson.Deserialize(snapshot.LevelData) as Dictionary<string, object>;
                if (dictionary == null) throw new InvalidOperationException("Remote LevelData was not a JSON object.");
                var data = new LevelData(); data.Setup(); LoadResult loadResult; data.Decode(dictionary, out loadResult);

                if (enteringRemoteLevel)
                    editor.customLevel.levelPath = assetSync.PrepareRemoteLevel(snapshot.LevelId, snapshot.LevelData);

                editor.customLevel.levelData = data;
                editor.RemakePath(true, true);
                AccessTools.Method(typeof(scnEditor), "UpdateDecorationObjects")?.Invoke(editor, null);
                levelId = snapshot.LevelId;
                Revision = snapshot.IsLevelSwitch ? snapshot.Revision : Math.Max(Revision, snapshot.Revision);
                dirty = false; dirtyElapsed = 0f; ResetObservation(editor); ForcePresenceRefresh();

                if (!isHost && assetSync.IsLevelReady(levelId))
                    ReloadRemoteAssets(editor);

                appliedSuccessfully = true;
                logger.Log($"[Collab] applied {(snapshot.IsLevelSwitch ? "level switch" : "snapshot")}; level={levelId}, revision={snapshot.Revision}, from={snapshot.ClientId}, loadResult={loadResult}");
            });

            if (appliedSuccessfully && waitingForAuthoritativeState)
            {
                syncRequestOutstanding = false;
                int generation = syncGeneration;
                _ = CompleteClientSyncAsync(generation, snapshot.LevelId, snapshot.Revision);
            }
        }

        public void ApplyRemote(Action apply) { if (apply == null) throw new ArgumentNullException(nameof(apply)); IsApplyingRemote = true; try { apply(); } catch (Exception ex) { logger.Error($"[Collab] remote apply failed: {ex}"); } finally { IsApplyingRemote = false; } }
        public void Dispose() { presenceOverlay.Dispose(); assetSync.Dispose(); transport.Dispose(); }

        private sealed class RemotePresenceState
        {
            public string DisplayName { get; }
            public bool IsHost { get; }
            public int[] Floors { get; }
            public float LastSeen { get; }
            public RemotePresenceState(string displayName, bool isHost, int[] floors, float lastSeen)
            {
                DisplayName = displayName ?? string.Empty;
                IsHost = isHost;
                Floors = floors ?? Array.Empty<int>();
                LastSeen = lastSeen;
            }
        }
    }

    internal sealed class ParticipantInfo
    {
        public string ClientId { get; }
        public string DisplayName { get; }
        public bool IsHost { get; }
        public bool IsLocal { get; }

        public ParticipantInfo(string clientId, string displayName, bool isHost, bool isLocal)
        {
            ClientId = clientId ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName;
            IsHost = isHost;
            IsLocal = isLocal;
        }
    }
}
