using System;
using System.Collections.Generic;
using ADOFAI;
using UnityEngine;

namespace EditorTogether
{
    /// <summary>
    /// Lightweight world-space visualization for remote selections.
    ///
    /// Single floor selections get one strong outline. Multi-selections keep strong
    /// outlines only on the endpoints and use small sampled markers for the interior so
    /// a large selection does not turn the whole chart into a wall of boxes. Decoration
    /// selections use their rendered bounds when available. Name labels and outline radii
    /// are offset deterministically per client so overlapping selections remain visible.
    /// </summary>
    internal sealed class RemotePresenceOverlay : IDisposable
    {
        private const int MaxInteriorMarkers = 24;
        private readonly Dictionary<string, List<GameObject>> objects = new Dictionary<string, List<GameObject>>();
        private readonly Dictionary<string, List<GameObject>> decorationObjects = new Dictionary<string, List<GameObject>>();
        private Material material;

        public void Show(scnEditor editor, string clientId, string displayName, IReadOnlyList<int> floorIds)
        {
            Clear(clientId);
            if (editor == null || editor.floors == null || floorIds == null || floorIds.Count == 0) return;
            if (!EnsureMaterial()) return;

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
                CreateOutline(selected[0].transform, clientId, color, slot, list);
                CreateOutline(selected[selected.Count - 1].transform, clientId, color, slot, list);
                CreateInteriorMarkers(selected, clientId, color, slot, list);
            }

            CreateLabel(selected[0].transform, clientId, displayName, selected.Count, 0, color, slot, list);
            objects[clientId ?? string.Empty] = list;
        }

        public void ShowDecorations(scnEditor editor, string clientId, string displayName, IReadOnlyList<int> decorationIds, bool showLabel)
        {
            ClearDecorations(clientId);
            if (editor == null || editor.decorations == null || decorationIds == null || decorationIds.Count == 0) return;
            if (!EnsureMaterial()) return;

            Color color = ColorForClient(clientId);
            int slot = SlotForClient(clientId);
            var list = new List<GameObject>(decorationIds.Count + 2);
            Transform first = null;
            int validCount = 0;

            var seen = new HashSet<int>();
            for (int i = 0; i < decorationIds.Count; i++)
            {
                int index = decorationIds[i];
                if (!seen.Add(index) || index < 0 || index >= editor.decorations.Count) continue;
                LevelEvent evnt = editor.decorations[index];
                if (evnt == null) continue;

                scrDecoration decoration;
                try { decoration = scrDecorationManager.GetDecoration(evnt); }
                catch { decoration = null; }
                if (decoration == null) continue;

                if (first == null) first = decoration.transform;
                validCount++;
                CreateDecorationOutline(decoration, clientId, color, slot, list);
            }

            if (validCount == 0) return;
            if (showLabel && first != null)
                CreateLabel(first, clientId, displayName, 0, validCount, color, slot, list);
            decorationObjects[clientId ?? string.Empty] = list;
        }

        private bool EnsureMaterial()
        {
            if (material != null) return true;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) return false;
            material = new Material(shader) { hideFlags = HideFlags.DontSave };
            return true;
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
            ConfigureLine(line, color, 0.078f, 32000 + slot, false);
            line.positionCount = 5;
            line.numCornerVertices = 2;

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
                ConfigureLine(line, markerColor, 0.052f, 31990 + slot, false);
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

        private void CreateDecorationOutline(scrDecoration decoration, string clientId, Color color, int slot, List<GameObject> list)
        {
            Vector3 center = decoration.transform.position;
            float halfX = 0.42f;
            float halfY = 0.42f;

            try
            {
                Renderer[] renderers = decoration.GetComponentsInChildren<Renderer>(true);
                bool found = false;
                Bounds bounds = default(Bounds);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || renderer is LineRenderer) continue;
                    if (!found) { bounds = renderer.bounds; found = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }

                if (found && IsFinite(bounds.center.x) && IsFinite(bounds.center.y))
                {
                    center = bounds.center;
                    halfX = Mathf.Clamp(bounds.extents.x + 0.08f, 0.25f, 4f);
                    halfY = Mathf.Clamp(bounds.extents.y + 0.08f, 0.25f, 4f);
                }
            }
            catch { }

            // Slight expansion gives overlapping users separate concentric outlines.
            float expand = (slot % 3) * 0.035f;
            halfX += expand;
            halfY += expand;

            var go = new GameObject("EditorTogether Remote Decoration Selection " + ShortId(clientId));
            go.hideFlags = HideFlags.DontSave;
            var line = go.AddComponent<LineRenderer>();
            ConfigureLine(line, color, 0.065f, 32000 + slot, true);
            line.positionCount = 5;
            line.numCornerVertices = 2;
            float z = center.z - 0.02f - slot * 0.002f;
            line.SetPosition(0, new Vector3(center.x - halfX, center.y - halfY, z));
            line.SetPosition(1, new Vector3(center.x - halfX, center.y + halfY, z));
            line.SetPosition(2, new Vector3(center.x + halfX, center.y + halfY, z));
            line.SetPosition(3, new Vector3(center.x + halfX, center.y - halfY, z));
            line.SetPosition(4, new Vector3(center.x - halfX, center.y - halfY, z));
            list.Add(go);
        }

        private void ConfigureLine(LineRenderer line, Color color, float width, int sortingOrder, bool worldSpace)
        {
            line.useWorldSpace = worldSpace;
            line.material = material;
            line.startColor = color;
            line.endColor = color;
            line.startWidth = width;
            line.endWidth = width;
            line.sortingOrder = sortingOrder;
        }

        private static void CreateLabel(Transform parent, string clientId, string displayName, int floorCount, int decorationCount, Color color, int slot, List<GameObject> list)
        {
            string name = string.IsNullOrWhiteSpace(displayName) ? ShortId(clientId) : displayName.Trim();
            if (name.Length > 22) name = name.Substring(0, 21) + "…";

            string suffix = string.Empty;
            if (floorCount > 1) suffix += "  F" + floorCount;
            if (decorationCount > 0) suffix += "  D" + decorationCount;
            string text = name + suffix;
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
            ClearObjects(objects, clientId);
            ClearObjects(decorationObjects, clientId);
        }

        private void ClearDecorations(string clientId)
        {
            ClearObjects(decorationObjects, clientId);
        }

        private static void ClearObjects(Dictionary<string, List<GameObject>> source, string clientId)
        {
            string key = clientId ?? string.Empty;
            if (!source.TryGetValue(key, out List<GameObject> list)) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) UnityEngine.Object.Destroy(list[i]);
            source.Remove(key);
        }

        public void ClearAll()
        {
            foreach (string key in new List<string>(objects.Keys)) ClearObjects(objects, key);
            foreach (string key in new List<string>(decorationObjects.Keys)) ClearObjects(decorationObjects, key);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
