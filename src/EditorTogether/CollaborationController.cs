using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using UnityModManagerNet;

namespace EditorTogether
{
    internal sealed class CollaborationController : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
        private readonly WebSocketTransport transport;
        private static readonly System.Reflection.FieldInfo SaveStateLastFrameField = AccessTools.Field(typeof(scnEditor), "saveStateLastFrame");
        private scnEditor lastEditor;
        private LevelData observedLevelData;
        private bool dirty;
        private float dirtyElapsed;
        private int observedSaveStateFrame = int.MinValue;
        private const float DebounceDelay = 0.20f;
        private bool isHost;
        private string levelId = string.Empty;

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
            logger.Log("[Collab] joined room; waiting for host snapshot");
        }

        public async System.Threading.Tasks.Task DisconnectAsync()
        {
            dirty = false; dirtyElapsed = 0f;
            await transport.DisconnectAsync(isHost ? "Host disconnected" : "Client disconnected").ConfigureAwait(false);
            isHost = false; levelId = string.Empty; Revision = 0;
            logger.Log("[Collab] disconnected");
        }

        private static string AddRole(string url, string role) => url + (url.Contains("?") ? "&" : "?") + "role=" + role;
        private async System.Threading.Tasks.Task ConnectCoreAsync(string url) { try { await transport.ConnectAsync(url).ConfigureAwait(false); } catch (Exception ex) { logger.Error($"[Collab] connection failed: {ex.Message}"); throw; } }

        private void TryFindEditor()
        {
            scnEditor found;
            try { found = UnityEngine.Object.FindFirstObjectByType<scnEditor>(); }
            catch { found = UnityEngine.Object.FindObjectOfType<scnEditor>(); }
            if (found == null) return;
            if (!ReferenceEquals(found, lastEditor)) { lastEditor = found; ResetObservation(found); }
        }

        private static int ReadSaveStateLastFrame(scnEditor editor) => editor == null || SaveStateLastFrameField == null ? int.MinValue : (int)SaveStateLastFrameField.GetValue(editor);
        private void ResetObservation(scnEditor editor) { observedSaveStateFrame = ReadSaveStateLastFrame(editor); observedLevelData = editor?.levelData; }

        private void ObserveEditor(scnEditor editor)
        {
            if (!transport.IsConnected || IsApplyingRemote) return;

            // Opening another chart replaces the LevelData object. Only the host is allowed
            // to turn that into a room-wide level switch.
            if (!ReferenceEquals(observedLevelData, editor.levelData))
            {
                observedLevelData = editor.levelData;
                observedSaveStateFrame = ReadSaveStateLastFrame(editor);
                dirty = false; dirtyElapsed = 0f;
                if (isHost) BeginNewLevel(editor, true);
                return;
            }

            int frame = ReadSaveStateLastFrame(editor);
            if (frame == int.MinValue || frame == observedSaveStateFrame) return;
            observedSaveStateFrame = frame;
            dirty = true; dirtyElapsed = 0f;
        }

        public void OnEditorStateSaved(scnEditor editor) { if (editor != null) lastEditor = editor; }

        private void BeginNewLevel(scnEditor editor, bool publish)
        {
            levelId = Guid.NewGuid().ToString("N");
            Revision = 0;
            ResetObservation(editor);
            logger.Log($"[Collab] host level changed; levelId={levelId}");
            if (publish) PublishSnapshot(editor, true);
        }

        private void PublishSnapshot(scnEditor editor, bool levelSwitch = false)
        {
            if (!transport.IsConnected) return;
            if (string.IsNullOrEmpty(levelId)) levelId = Guid.NewGuid().ToString("N");
            string encodedLevel = editor.levelData.Encode();
            Revision++;
            logger.Log($"[Collab] {(levelSwitch ? "level switch" : "snapshot")} published; level={levelId}, revision={Revision}, bytes={encodedLevel.Length}");
            _ = SendSnapshotAsync(Revision, levelId, encodedLevel, levelSwitch);
        }

        private async System.Threading.Tasks.Task SendSnapshotAsync(long revision, string id, string encodedLevel, bool levelSwitch)
        {
            try { await transport.SendSnapshotAsync(revision, id, encodedLevel, levelSwitch).ConfigureAwait(false); }
            catch (Exception ex) { logger.Error($"[Collab] send failed: {ex.Message}"); }
        }

        public void Update(float deltaTime = 0f)
        {
            TryFindEditor();
            if (lastEditor == null) return;
            SnapshotMessage newest = null;
            while (transport.TryDequeue(out SnapshotMessage message)) newest = message;
            if (newest != null) ApplyRemoteSnapshot(lastEditor, newest);

            ObserveEditor(lastEditor);
            if (!dirty || !transport.IsConnected || IsApplyingRemote) return;
            dirtyElapsed += deltaTime > 0f ? deltaTime : UnityEngine.Time.unscaledDeltaTime;
            if (dirtyElapsed < DebounceDelay) return;
            dirty = false; dirtyElapsed = 0f;
            try { logger.Log("[Collab] editor mutation settled; publishing snapshot"); PublishSnapshot(lastEditor); }
            catch (Exception ex) { logger.Error("[Collab] live snapshot failed: " + ex.Message); }
        }

        private void ApplyRemoteSnapshot(scnEditor editor, SnapshotMessage snapshot)
        {
            // A packet from an old chart generation must never overwrite the current chart.
            if (!snapshot.IsLevelSwitch && !string.IsNullOrEmpty(levelId) && snapshot.LevelId != levelId)
            {
                logger.Log($"[Collab] ignored stale snapshot for level={snapshot.LevelId}");
                return;
            }

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
                dirty = false; dirtyElapsed = 0f; ResetObservation(editor);
                logger.Log($"[Collab] applied {(snapshot.IsLevelSwitch ? "level switch" : "snapshot")}; level={levelId}, revision={snapshot.Revision}, from={snapshot.ClientId}, loadResult={loadResult}");
            });
        }

        public void ApplyRemote(Action apply) { if (apply == null) throw new ArgumentNullException(nameof(apply)); IsApplyingRemote = true; try { apply(); } catch (Exception ex) { logger.Error($"[Collab] remote apply failed: {ex}"); } finally { IsApplyingRemote = false; } }
        public void Dispose() { transport.Dispose(); }
    }
}
