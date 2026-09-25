using System;
using UnityModManagerNet;

namespace EditorCollaboration
{
    internal sealed class CollaborationController : IDisposable
    {
        private readonly UnityModManager.ModEntry.ModLogger logger;
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

            // Harmony constructor prefixes run before SaveStateScope increments
            // scnEditor.changingState. Therefore 0 means this is the outermost scope.
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

            // Harmony Dispose postfix runs after SaveStateScope decrements
            // changingState, so only the outermost scope reaches zero here.
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

            // TODO(v0.0.1): hand encodedLevel to the transport layer.
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
