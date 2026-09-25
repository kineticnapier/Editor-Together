using System;
using System.Collections.Generic;
using ADOFAI;
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
        public bool IsConnected => transport.IsConnected;
        public string ClientId => transport.ClientId;

        public CollaborationController(UnityModManager.ModEntry.ModLogger logger)
        {
            this.logger = logger;
            transport = new WebSocketTransport(logger);
        }

        public async System.Threading.Tasks.Task ConnectAsync(string url)
        {
            try
            {
                await transport.ConnectAsync(url).ConfigureAwait(false);
                TryFindEditor();
                if (lastEditor != null)
                {
                    logger.Log("[Collab] connected with an open editor; publishing initial host snapshot");
                    PublishSnapshot(lastEditor);
                }
                else
                {
                    logger.Log("[Collab] connected without an open editor; waiting for room snapshot or first edit");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"[Collab] connection failed: {ex.Message}");
            }
        }

        private void TryFindEditor()
        {
            if (lastEditor != null)
                return;

            try
            {
                lastEditor = UnityEngine.Object.FindFirstObjectByType<scnEditor>();
            }
            catch
            {
                lastEditor = UnityEngine.Object.FindObjectOfType<scnEditor>();
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
            logger.Log($"[Collab] snapshot published; revision={Revision}, bytes={encodedLevel.Length}, events={editor.events.Count}, decorations={editor.decorations.Count}");
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

        public void Update()
        {
            TryFindEditor();
            if (lastEditor == null)
                return;

            SnapshotMessage newest = null;
            while (transport.TryDequeue(out SnapshotMessage message))
                newest = message;

            if (newest != null)
                ApplyRemoteSnapshot(lastEditor, newest);
        }

        private void ApplyRemoteSnapshot(scnEditor editor, SnapshotMessage snapshot)
        {
            ApplyRemote(() =>
            {
                var dictionary = RuntimeJson.Deserialize(snapshot.LevelData) as Dictionary<string, object>;
                if (dictionary == null)
                    throw new InvalidOperationException("Remote LevelData was not a JSON object.");

                var levelData = new LevelData();
                levelData.Setup();
                LoadResult loadResult;
                levelData.Decode(dictionary, out loadResult);

                editor.customLevel.levelData = levelData;
                editor.RemakePath(true, true);
                AccessTools.Method(typeof(scnEditor), "UpdateDecorationObjects")?.Invoke(editor, null);

                Revision = Math.Max(Revision, snapshot.Revision);
                logger.Log($"[Collab] applied remote snapshot; revision={snapshot.Revision}, from={snapshot.ClientId}, loadResult={loadResult}");
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
