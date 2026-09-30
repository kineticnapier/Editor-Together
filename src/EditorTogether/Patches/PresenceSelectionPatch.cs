using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ADOFAI;
using HarmonyLib;

namespace EditorTogether.Patches
{
    /// <summary>
    /// Extends Presence v1 without changing the server protocol contract: presence payloads
    /// now optionally carry selected decoration indices in addition to selected floor IDs.
    /// Old peers simply omit/ignore the extra field.
    /// </summary>
    [HarmonyPatch]
    internal static class LocalDecorationPresencePatch
    {
        private const float PresenceHeartbeatSeconds = 2f;
        private static readonly FieldInfo TransportField = AccessTools.Field(typeof(CollaborationController), "transport");
        private static readonly FieldInfo LastPresenceFloorsField = AccessTools.Field(typeof(CollaborationController), "lastPresenceFloors");
        private static readonly FieldInfo PresenceHeartbeatElapsedField = AccessTools.Field(typeof(CollaborationController), "presenceHeartbeatElapsed");
        private static readonly ConditionalWeakTable<CollaborationController, PresenceState> States = new ConditionalWeakTable<CollaborationController, PresenceState>();

        private sealed class PresenceState
        {
            public readonly List<int> Decorations = new List<int>();
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "UpdateLocalPresence");

        [HarmonyPrefix]
        private static bool Prefix(CollaborationController __instance, scnEditor editor, float dt)
        {
            if (__instance == null || editor == null) return false;
            WebSocketTransport transport = TransportField?.GetValue(__instance) as WebSocketTransport;
            if (transport == null || !transport.IsConnected) return false;

            var floors = new List<int>();
            var decorations = new List<int>();
            if (__instance.IsSynchronized)
            {
                if (editor.selectedFloors != null)
                {
                    for (int i = 0; i < editor.selectedFloors.Count; i++)
                    {
                        scrFloor floor = editor.selectedFloors[i];
                        if (floor != null && !floors.Contains(floor.seqID)) floors.Add(floor.seqID);
                    }
                }

                if (editor.selectedDecorations != null && editor.decorations != null)
                {
                    for (int i = 0; i < editor.selectedDecorations.Count; i++)
                    {
                        LevelEvent decoration = editor.selectedDecorations[i];
                        if (decoration == null) continue;
                        int index = editor.decorations.IndexOf(decoration);
                        if (index >= 0 && !decorations.Contains(index)) decorations.Add(index);
                    }
                }
            }

            floors.Sort();
            decorations.Sort();

            var lastFloors = LastPresenceFloorsField?.GetValue(__instance) as List<int>;
            if (lastFloors == null) return true; // Compatibility fallback to stock sender.
            PresenceState state = States.GetValue(__instance, _ => new PresenceState());

            float elapsed = 0f;
            try { elapsed = PresenceHeartbeatElapsedField == null ? 0f : (float)PresenceHeartbeatElapsedField.GetValue(__instance); }
            catch { }
            elapsed += Math.Max(0f, dt);

            bool changed = !SequenceEqual(floors, lastFloors) || !SequenceEqual(decorations, state.Decorations);
            if (!changed && elapsed < PresenceHeartbeatSeconds)
            {
                try { PresenceHeartbeatElapsedField?.SetValue(__instance, elapsed); } catch { }
                return false;
            }

            lastFloors.Clear();
            lastFloors.AddRange(floors);
            state.Decorations.Clear();
            state.Decorations.AddRange(decorations);
            try { PresenceHeartbeatElapsedField?.SetValue(__instance, 0f); } catch { }

            Task send = transport.SendPresenceAsync(
                __instance.LevelId,
                __instance.DisplayName,
                __instance.IsHost,
                false,
                floors,
                decorations);
            _ = send.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Main.ModEntry?.Logger.Error("[Collab] presence send failed: " + t.Exception.GetBaseException().Message);
            }, TaskScheduler.Default);
            return false;
        }

        private static bool SequenceEqual(IReadOnlyList<int> left, IReadOnlyList<int> right)
        {
            if (left == null || right == null) return ReferenceEquals(left, right);
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i] != right[i]) return false;
            return true;
        }
    }

    [HarmonyPatch]
    internal static class RemoteDecorationPresencePatch
    {
        private static readonly FieldInfo OverlayField = AccessTools.Field(typeof(CollaborationController), "presenceOverlay");

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(CollaborationController), "ApplyPresence");

        [HarmonyPostfix]
        private static void Postfix(CollaborationController __instance, scnEditor editor, PresenceMessage message)
        {
            if (__instance == null || editor == null || message == null || message.IsLeaving) return;
            if (string.IsNullOrEmpty(__instance.LevelId) || !string.Equals(message.LevelId, __instance.LevelId, StringComparison.Ordinal)) return;
            if (message.SelectedDecorations == null || message.SelectedDecorations.Length == 0) return;

            RemotePresenceOverlay overlay = OverlayField?.GetValue(__instance) as RemotePresenceOverlay;
            if (overlay == null) return;

            string name = (message.DisplayName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                name = message.ClientId.Length > 6 ? message.ClientId.Substring(0, 6) : message.ClientId;

            overlay.ShowDecorations(
                editor,
                message.ClientId,
                name,
                message.SelectedDecorations,
                message.SelectedFloors == null || message.SelectedFloors.Length == 0);
        }
    }
}
