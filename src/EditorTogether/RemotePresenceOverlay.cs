using System;
using System.Collections.Generic;
using UnityEngine;

namespace EditorTogether
{
    internal sealed class RemotePresenceOverlay : IDisposable
    {
        private readonly Dictionary<string, List<GameObject>> objects = new Dictionary<string, List<GameObject>>();
        private Material material;

        public void Show(scnEditor editor, string clientId, IReadOnlyList<int> floorIds)
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
            var list = new List<GameObject>(floorIds.Count);
            for (int i = 0; i < floorIds.Count; i++)
            {
                int floorId = floorIds[i];
                if (floorId < 0 || floorId >= editor.floors.Count) continue;
                scrFloor floor = editor.floors[floorId];
                if (floor == null) continue;

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
            objects[clientId ?? string.Empty] = list;
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
