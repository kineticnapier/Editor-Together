# EditorCollaboration

Experimental real-time collaboration for the ADOFAI editor.

## v0.0.2 prototype

The prototype intentionally synchronizes the complete `LevelData` after each outermost data-changing `SaveStateScope`. Fine-grained operations come later.

Current flow:

1. ADOFAI finishes a root edit.
2. The mod calls `LevelData.Encode()`.
3. The encoded snapshot is sent over WebSocket.
4. The relay broadcasts it to clients in the same room.
5. Receivers queue the snapshot from the network thread.
6. UMM `OnUpdate` applies it on Unity's main thread with `LevelData.Decode()` + `RemakePath()`.
7. `IsApplyingRemote` prevents received edits from being sent back.

## Relay

The repository contains a minimal .NET 8 relay in `src/EditorCollaboration.Server`.

```text
dotnet run --project src/EditorCollaboration.Server
```

It listens on port `38241`. The mod defaults to:

```text
ws://127.0.0.1:38241/ws?room=default
```

Set the `EDITORCOLLAB_URL` environment variable before launching ADOFAI to use another relay or room. For example, on another PC in the same LAN, point it at the relay PC's LAN address.

## First two-PC test

Both clients should load the same starting level. Start the relay, launch ADOFAI with the mod on both PCs, and confirm both logs contain `[Collab] connected`.

Then edit only one client at a time for this prototype. Test BPM/property changes, floor insertion/deletion, event insertion/deletion, and decorations. The receiver should log `[Collab] applied remote snapshot`.

Known v0.0.2 limitations:

- revision values are currently per-client diagnostic counters only;
- simultaneous edits can diverge and are intentionally not supported yet;
- reconnect does not fetch the latest room snapshot;
- collaborative Undo/Redo semantics are not implemented;
- no in-editor connection UI yet;
- full snapshots are bandwidth-heavy by design.

Fine-grained diffs, stable event IDs, server-authoritative revisions, conflict handling, collaborative undo, cursor sharing, reconnect state, and ExtremeEditor integration are planned for later iterations.
