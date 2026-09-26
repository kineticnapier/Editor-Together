using System;
using System.Collections.Generic;
using UnityEngine;

namespace EditorTogether
{
    internal sealed class RemotePresenceOverlay : IDisposable
    {
        private readonly Dictionary<string, List<GameObject>> objects = new Dictionary<string, List<GameObject>>();
        private Material material;

        public void Show(scnEditor editor, string clientId, string displayName, IReadOnlyList<int> floorIds)
        {
            Clear(clientId);
            if (editor == null || editor.floors == null || floorIds == null || floorIds.Count == 0) return;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) return;
            if (material == null)
            {
                material = new Material(shader) { hideFlags = HideFlags.DontSave };
            }

            Color color = ColorForClient(clientId);
            var list = new List<GameObject>(floorIds.Count + 2);
            scrFloor labelFloor = null;
            for (int i = 0; i < floorIds.Count; i++)
            {
                int floorId = floorIds[i];
                if (floorId < 0 || floorId >= editor.floors.Count) continue;
                scrFloor floor = editor.floors[floorId];
                if (floor == null) continue;
                if (labelFloor == null) labelFloor = floor;

                var go = new GameObject("EditorTogether Remote Selection " + ShortId(clientId));
                go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(floor.transform, false);
                go.transform.localPosition = new Vector3(0f, 0f, -0.2f);

                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.material = material;
                line.startColor = color;
                line.endColor = color;
                line.startWidth = 0.075f;
                line.endWidth = 0.075f;
                line.positionCount = 5;
                line.numCornerVertices = 2;
                line.sortingOrder = 32000;
                const float r = 0.59f;
                line.SetPosition(0, new Vector3(-r, -r, 0f));
                line.SetPosition(1, new Vector3(-r, r, 0f));
                line.SetPosition(2, new Vector3(r, r, 0f));
                line.SetPosition(3, new Vector3(r, -r, 0f));
                line.SetPosition(4, new Vector3(-r, -r, 0f));
                list.Add(go);
            }

            if (labelFloor != null)
                CreateLabel(labelFloor.transform, clientId, displayName, color, list);

            objects[clientId ?? string.Empty] = list;
        }

        private static void CreateLabel(Transform parent, string clientId, string displayName, Color color, List<GameObject> list)
        {
            string text = string.IsNullOrWhiteSpace(displayName) ? ShortId(clientId) : displayName.Trim();
            if (text.Length > 32) text = text.Substring(0, 32);

            // A small dark shadow keeps the label readable over bright decorations.
            var shadowGo = new GameObject("EditorTogether Remote Name Shadow " + ShortId(clientId));
            shadowGo.hideFlags = HideFlags.DontSave;
            shadowGo.transform.SetParent(parent, false);
            shadowGo.transform.localPosition = new Vector3(0.025f, 0.745f, -0.31f);
            var shadow = shadowGo.AddComponent<TextMesh>();
            ConfigureText(shadow, text, new Color(0f, 0f, 0f, 0.9f), 32000);
            list.Add(shadowGo);

            var labelGo = new GameObject("EditorTogether Remote Name " + ShortId(clientId));
            labelGo.hideFlags = HideFlags.DontSave;
            labelGo.transform.SetParent(parent, false);
            labelGo.transform.localPosition = new Vector3(0f, 0.77f, -0.32f);
            var label = labelGo.AddComponent<TextMesh>();
            ConfigureText(label, text, color, 32001);
            list.Add(labelGo);
        }

        private static void ConfigureText(TextMesh textMesh, string text, Color color, int sortingOrder)
        {
            textMesh.text = text;
            textMesh.anchor = TextAnchor.LowerCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.fontSize = 42;
            textMesh.characterSize = 0.095f;
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

        private static Color ColorForClient(string id)
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
