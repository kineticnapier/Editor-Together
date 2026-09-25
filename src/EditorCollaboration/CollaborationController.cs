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
        private scnEditor lastEditor;
        private string lastObservedLevel;
        private float pollElapsed;
        private float dirtyElapsed = -1f;
        private const float PollInterval = 0.10f;
        private const float DebounceDelay = 0.15f;

        public bool IsApplyingRemote { get; private set; }
        public long Revision { get; private set; }
        public bool IsConnected => transport.IsConnected;
        public string ClientId => transport.ClientId;

        public CollaborationController(UnityModManager.ModEntry.ModLogger logger)
        {
            this.logger = logger;
            transport = new WebSocketTransport(logger);
        }

        public async System.Threading.Tasks.Task CreateRoomAsync(string url)
        {
            await ConnectCoreAsync(url).ConfigureAwait(false);
            TryFindEditor();
            if (lastEditor == null)
                throw new InvalidOperationException("Open a chart in the editor before creating a room.");

            lastObservedLevel = lastEditor.levelData.Encode();
            logger.Log("[Collab] room created; publishing host snapshot");
            PublishSnapshot(lastEditor, lastObservedLevel);
        }

        public async System.Threading.Tasks.Task JoinRoomAsync(string url)
        {
            await ConnectCoreAsync(url).ConfigureAwait(false);
            TryFindEditor();
            lastObservedLevel = lastEditor != null ? lastEditor.levelData.Encode() : null;
            logger.Log("[Collab] joined room; waiting for host snapshot (local chart will NOT be published)");
        }

        private async System.Threading.Tasks.Task ConnectCoreAsync(string url)
        {
            try
            {
                await transport.ConnectAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error($"[Collab] connection failed: {ex.Message}");
                throw;
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

        // Kept for compatibility with the current Harmony patch. Polling below is
        // authoritative because editor mutations do not all cross the same save hook.
        public void OnEditorStateSaved(scnEditor editor)
        {
            if (editor != null)
                lastEditor = editor;
        }

        private void PublishSnapshot(scnEditor editor, string encodedLevel = null)
        {
            if (!transport.IsConnected)
                return;

            encodedLevel = encodedLevel ?? editor.levelData.Encode();
            lastObservedLevel = encodedLevel;
            Revision++;
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

        public void Update(float deltaTime = 0f)
        {
            TryFindEditor();
            if (lastEditor == null)
                return;

            SnapshotMessage newest = null;
            while (transport.TryDequeue(out SnapshotMessage message))
                newest = message;

            if (newest != null)
                ApplyRemoteSnapshot(lastEditor, newest);

            if (!transport.IsConnected || IsApplyingRemote)
                return;

            pollElapsed += deltaTime > 0f ? deltaTime : UnityEngine.Time.unscaledDeltaTime;
            if (pollElapsed >= PollInterval)
            {
                pollElapsed = 0f;
                try
                {
                    string current = lastEditor.levelData.Encode();
                    if (lastObservedLevel == null)
                    {
                        lastObservedLevel = current;
                    }
                    else if (!string.Equals(current, lastObservedLevel, StringComparison.Ordinal))
                    {
                        lastObservedLevel = current;
                        dirtyElapsed = 0f;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("[Collab] level polling failed: " + ex.Message);
                }
            }

            if (dirtyElapsed >= 0f)
            {
                dirtyElapsed += deltaTime > 0f ? deltaTime : UnityEngine.Time.unscaledDeltaTime;
                if (dirtyElapsed >= DebounceDelay)
                {
                    dirtyElapsed = -1f;
                    try
                    {
                        string stable = lastEditor.levelData.Encode();
                        lastObservedLevel = stable;
                        logger.Log("[Collab] live LevelData change detected");
                        PublishSnapshot(lastEditor, stable);
                    }
                    catch (Exception ex)
                    {
                        logger.Error("[Collab] live snapshot failed: " + ex.Message);
                    }
                }
            }
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

                lastObservedLevel = snapshot.LevelData;
                dirtyElapsed = -1f;
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
