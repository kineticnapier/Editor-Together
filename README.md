# EditorCollaboration

Experimental real-time collaboration for the ADOFAI editor.

## v0.0.1 goal

The first prototype intentionally uses full `LevelData` synchronization instead of fine-grained operations.

1. Detect the outermost data-changing `SaveStateScope`.
2. Serialize the current level after the edit finishes.
3. Send the snapshot to peers.
4. Apply remote snapshots without echoing them back.
5. Track a monotonically increasing revision number.

Fine-grained diffs, event IDs, conflict resolution, collaborative undo, cursor sharing, and ExtremeEditor integration are planned for later iterations.
