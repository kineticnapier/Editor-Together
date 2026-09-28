using System;
using System.Collections.Generic;
using UnityEngine;

namespace EditorTogether
{
    /// <summary>
    /// Lightweight world-space visualization for remote floor selections.
    ///
    /// Single selections get one strong outline. Multi-selections keep strong outlines
    /// only on the endpoints and use small sampled markers for the interior so a large
    /// selection does not turn the whole chart into a wall of boxes. Name labels and
    /// outline radii are offset deterministically per client so overlapping selections
    /// remain distinguishable.
    /// </summary>
    internal sealed class RemotePresenceOverlay : IDisposable
    {
        private const int MaxInteriorMarkers = 24;
        private readonly Dictionary<string, List<GameObject>> objects = new Dictionary<string, List<GameObject>>();
        private Material material;

        public void Show(scnEditor editor, string clientId, string displayName, IReadOnlyList<int> floorIds)
        {
            Clear(clientId);
            if (editor == null || editor.floors == null || floorIds == null || floorIds.Count == 0) return;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) return;
            if (material == null)
                material = new Material(shader) { hideFlags = HideFlags.DontSave };

            List<scrFloor> selected = CollectFloors(editor, floorIds);
            if (selected.Count == 0) return;

            Color color = ColorForClient(clientId);
            int slot = SlotForClient(clientId);
            var list = new List<GameObject>(Math.Min(selected.Count, MaxInteriorMarkers + 2) + 2);

            if (selected.Count == 1)
            {
                CreateOutline(selected[0].transform, clientId, color, slot, list);
            }
            else
            {
                // Endpoint outlines communicate the selected range without drawing an
                // expensive/full-strength box around every floor in a large selection.
                CreateOutline(selected[0].transform, clientId, color, slot, list);
                CreateOutline(selected[selected.Count - 1].transform, clientId, color, slot, list);
                CreateInteriorMarkers(selected, clientId, color, slot, list);
            }

            CreateLabel(selected[0].transform, clientId, displayName, selected.Count, color, slot, list);
            objects[clientId ?? string.Empty] = list;
        }

        private static List<scrFloor> CollectFloors(scnEditor editor, IReadOnlyList<int> floorIds)
        {
            var ids = new List<int>(floorIds.Count);
            for (int i = 0; i < floorIds.Count; i++)
            {
                int id = floorIds[i];
                if (id < 0 || id >= editor.floors.Count) continue;
                if (!ids.Contains(id)) ids.Add(id);
            }
            ids.Sort();

            var floors = new List<scrFloor>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                scrFloor floor = editor.floors[ids[i]];
                if (floor != null) floors.Add(floor);
            }
            return floors;
        }

        private void CreateOutline(Transform parent, string clientId, Color color, int slot, List<GameObject> list)
        {
            var go = new GameObject("EditorTogether Remote Selection " + ShortId(clientId));
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.20f - slot * 0.004f);

            var line = go.AddComponent<LineRenderer>();
            ConfigureLine(line, color, 0.078f, 32000 + slot);
            line.positionCount = 5;
            line.numCornerVertices = 2;

            // Concentric radii let two users selecting the same floor remain visible.
            float r = 0.585f + (slot % 3) * 0.028f;
            line.SetPosition(0, new Vector3(-r, -r, 0f));
            line.SetPosition(1, new Vector3(-r, r, 0f));
            line.SetPosition(2, new Vector3(r, r, 0f));
            line.SetPosition(3, new Vector3(r, -r, 0f));
            line.SetPosition(4, new Vector3(-r, -r, 0f));
            list.Add(go);
        }

        private void CreateInteriorMarkers(List<scrFloor> selected, string clientId, Color color, int slot, List<GameObject> list)
        {
            int interiorCount = selected.Count - 2;
            if (interiorCount <= 0) return;

            int step = Math.Max(1, (int)Math.Ceiling(interiorCount / (double)MaxInteriorMarkers));
            Color markerColor = color;
            markerColor.a = 0.72f;

            for (int i = 1; i < selected.Count - 1; i += step)
            {
                Transform parent = selected[i].transform;
                var go = new GameObject("EditorTogether Remote Selection Marker " + ShortId(clientId));
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(0f, 0f, -0.205f - slot * 0.004f);

                var line = go.AddComponent<LineRenderer>();
                ConfigureLine(line, markerColor, 0.052f, 31990 + slot);
                line.positionCount = 5;
                line.numCornerVertices = 1;

                float x = 0.48f - (slot % 3) * 0.045f;
                float y = 0.49f;
                const float d = 0.095f;
                line.SetPosition(0, new Vector3(x, y + d, 0f));
                line.SetPosition(1, new Vector3(x + d, y, 0f));
                line.SetPosition(2, new Vector3(x, y - d, 0f));
                line.SetPosition(3, new Vector3(x - d, y, 0f));
                line.SetPosition(4, new Vector3(x, y + d, 0f));
                list.Add(go);
            }
        }

        private void ConfigureLine(LineRenderer line, Color color, float width, int sortingOrder)
        {
            line.useWorldSpace = false;
            line.material = material;
            line.startColor = color;
            line.endColor = color;
            line.startWidth = width;
            line.endWidth = width;
            line.sortingOrder = sortingOrder;
        }

        private static void CreateLabel(Transform parent, string clientId, string displayName, int selectedCount, Color color, int slot, List<GameObject> list)
        {
            string name = string.IsNullOrWhiteSpace(displayName) ? ShortId(clientId) : displayName.Trim();
            if (name.Length > 22) name = name.Substring(0, 21) + "…";
            string text = selectedCount > 1 ? name + "  ×" + selectedCount : name;

            Vector3 offset = LabelOffset(slot);

            var shadowGo = new GameObject("EditorTogether Remote Name Shadow " + ShortId(clientId));
            shadowGo.hideFlags = HideFlags.DontSave;
            shadowGo.transform.SetParent(parent, false);
            shadowGo.transform.localPosition = offset + new Vector3(0.022f, -0.022f, -0.01f);
            var shadow = shadowGo.AddComponent<TextMesh>();
            ConfigureText(shadow, text, new Color(0f, 0f, 0f, 0.88f), 32010 + slot);
            list.Add(shadowGo);

            var labelGo = new GameObject("EditorTogether Remote Name " + ShortId(clientId));
            labelGo.hideFlags = HideFlags.DontSave;
            labelGo.transform.SetParent(parent, false);
            labelGo.transform.localPosition = offset;
            var label = labelGo.AddComponent<TextMesh>();
            ConfigureText(label, text, color, 32020 + slot);
            list.Add(labelGo);
        }

        private static Vector3 LabelOffset(int slot)
        {
            // Six deterministic lanes are enough for normal collaboration sizes and avoid
            // labels sitting directly on top of each other when users share a floor.
            switch (slot % 6)
            {
                case 0: return new Vector3(0f, 0.78f, -0.32f);
                case 1: return new Vector3(-0.48f, 0.78f, -0.32f);
                case 2: return new Vector3(0.48f, 0.78f, -0.32f);
                case 3: return new Vector3(-0.32f, 1.00f, -0.32f);
                case 4: return new Vector3(0.32f, 1.00f, -0.32f);
                default: return new Vector3(0f, 1.20f, -0.32f);
            }
        }

        private static void ConfigureText(TextMesh textMesh, string text, Color color, int sortingOrder)
        {
            textMesh.text = text;
            textMesh.anchor = TextAnchor.LowerCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.fontSize = 36;
            textMesh.characterSize = 0.082f;
            textMesh.fontStyle = FontStyle.Bold;
            textMesh.richText = false;
            textMesh.color = color;
            var renderer = textMesh.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sortingOrder = sortingOrder;
        }

        public void Clear(string clientId)
        {
            string key = clientId ?? string.Empty;
            if (!objects.TryGetValue(key, out List<GameObject> list)) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) UnityEngine.Object.Destroy(list[i]);
            objects.Remove(key);
        }

        public void ClearAll()
        {
            foreach (string key in new List<string>(objects.Keys)) Clear(key);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "peer";
            return id.Length <= 6 ? id : id.Substring(0, 6);
        }

        private static int SlotForClient(string id)
        {
            unchecked
            {
                uint hash = 2166136261;
                string value = id ?? string.Empty;
                for (int i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * 16777619;
                return (int)(hash % 6);
            }
        }

        internal static Color ColorForClient(string id)
        {
            unchecked
            {
                uint hash = 2166136261;
                string value = id ?? string.Empty;
                for (int i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * 16777619;
                float hue = (hash % 1000) / 1000f;
                Color color = Color.HSVToRGB(hue, 0.72f, 1f);
                color.a = 0.95f;
                return color;
            }
        }

        public void Dispose()
        {
            ClearAll();
            if (material != null) UnityEngine.Object.Destroy(material);
            material = null;
        }
    }
}
