using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityModManagerNet;

namespace EditorCollaboration
{
    internal sealed class CollaborationController : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
        private readonly WebSocketTransport transport;
        private bool rootChangesData;
        private scnEditor lastEditor;

        public bool IsApplyingRemote { get; private set; }
        public long Revision { get; private set; }

        public CollaborationController(UnityModManager.ModEntry.ModLogger logger)
        {
            this.logger = logger;
            transport = new WebSocketTransport(logger);

            string url = Environment.GetEnvironmentVariable("EDITORCOLLAB_URL");
            if (string.IsNullOrWhiteSpace(url))
                url = "ws://127.0.0.1:38241/ws?room=default";

            _ = ConnectAsync(url);
        }

        private async System.Threading.Tasks.Task ConnectAsync(string url)
        {
            try
            {
                await transport.ConnectAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error($"[Collab] connection failed: {ex.Message}");
            }
        }

        public void OnScopeEntering(scnEditor editor, bool dataHasChanged, bool skipSaving)
        {
            if (editor == null || IsApplyingRemote)
                return;

            lastEditor = editor;
            if (editor.changingState != 0)
                return;

            rootChangesData = dataHasChanged && !skipSaving;
            if (rootChangesData)
                logger.Log("[Collab] root edit started");
        }

        public void OnScopeDisposed(scnEditor editor)
        {
            if (editor == null || IsApplyingRemote)
                return;

            lastEditor = editor;
            if (editor.changingState != 0)
                return;

            bool shouldPublish = rootChangesData;
            rootChangesData = false;
            if (!shouldPublish)
                return;

            PublishSnapshot(editor);
        }

        private void PublishSnapshot(scnEditor editor)
        {
            Revision++;
            string encodedLevel = editor.levelData.Encode();
            logger.Log($"[Collab] root edit committed; revision={Revision}, bytes={encodedLevel.Length}, events={editor.events.Count}, decorations={editor.decorations.Count}");
            _ = SendSnapshotAsync(Revision, encodedLevel);
        }

        private async System.Threading.Tasks.Task SendSnapshotAsync(long revision, string encodedLevel)
        {
            try
            {
                await transport.SendSnapshotAsync(revision, encodedLevel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error($"[Collab] send failed: {ex.Message}");
            }
        }

        // Called by UMM on Unity's main thread. Network callbacks only enqueue data.
        public void Update()
        {
            if (lastEditor == null)
                return;

            // Full-snapshot prototype: collapse a burst to the newest received snapshot.
            SnapshotMessage newest = null;
            while (transport.TryDequeue(out SnapshotMessage message))
            {
                if (message.Revision >= Revision)
                    newest = message;
            }

            if (newest == null)
                return;

            ApplyRemoteSnapshot(lastEditor, newest);
        }

        private void ApplyRemoteSnapshot(scnEditor editor, SnapshotMessage snapshot)
        {
            ApplyRemote(() =>
            {
                var dictionary = Json.Deserialize(snapshot.LevelData) as Dictionary<string, object>;
                if (dictionary == null)
                    throw new InvalidOperationException("Remote LevelData was not a JSON object.");

                var levelData = new LevelData();
                levelData.Setup();
                LoadResult loadResult;
                levelData.Decode(dictionary, out loadResult);

                editor.customLevel.levelData = levelData;
                editor.RemakePath(true, true);

                // Undo/redo uses the same levelData replacement followed by RemakePath and
                // decoration refresh. The latter is private in scnEditor, so invoke it here.
                AccessTools.Method(typeof(scnEditor), "UpdateDecorationObjects")?.Invoke(editor, null);

                Revision = Math.Max(Revision, snapshot.Revision);
                logger.Log($"[Collab] applied remote snapshot; revision={Revision}, from={snapshot.ClientId}, loadResult={loadResult}");
            });
        }

        public void ApplyRemote(Action apply)
        {
            if (apply == null)
                throw new ArgumentNullException(nameof(apply));

            IsApplyingRemote = true;
            try
            {
                apply();
            }
            catch (Exception ex)
            {
                logger.Error($"[Collab] remote apply failed: {ex}");
            }
            finally
            {
                IsApplyingRemote = false;
            }
        }

        public void Dispose()
        {
            transport.Dispose();
        }
    }
}
