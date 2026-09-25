using System;
using UnityModManagerNet;

namespace EditorCollaboration
{
    internal sealed class CollaborationController : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
        private int rootDepth;
        private bool rootChangesData;

        public bool IsApplyingRemote { get; private set; }
        public long Revision { get; private set; }

        public CollaborationController(UnityModManager.ModEntry.ModLogger logger)
        {
            this.logger = logger;
        }

        public void OnScopeEntering(scnEditor editor, bool dataHasChanged, bool skipSaving)
        {
            if (editor == null || IsApplyingRemote)
                return;

            // changingState is still the pre-constructor value here.
            if (editor.changingState == 0)
            {
                rootDepth = 1;
                rootChangesData = dataHasChanged && !skipSaving;
                if (rootChangesData)
                    logger.Log("[Collab] root edit started");
                return;
            }

            if (rootDepth > 0)
                rootDepth++;
        }

        public void OnScopeDisposed(scnEditor editor)
        {
            if (editor == null || IsApplyingRemote || rootDepth == 0)
                return;

            rootDepth--;
            if (rootDepth != 0)
                return;

            bool shouldPublish = rootChangesData && editor.changingState == 0;
            rootChangesData = false;
            if (!shouldPublish)
                return;

            PublishSnapshot(editor);
        }

        private void PublishSnapshot(scnEditor editor)
        {
            Revision++;

            // v0.0.1 transport is intentionally not wired yet. This establishes the
            // correct editor transaction boundary before networking is introduced.
            LevelData snapshot = editor.levelData.Copy();
            logger.Log($"[Collab] root edit committed; revision={Revision}, events={snapshot.levelEvents.Count}, decorations={snapshot.decorations.Count}");
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
            finally
            {
                IsApplyingRemote = false;
            }
        }

        public void Dispose()
        {
        }
    }
}
