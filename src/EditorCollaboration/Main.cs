using HarmonyLib;
using UnityModManagerNet;

namespace EditorCollaboration
{
    public static class Main
    {
        internal static UnityModManager.ModEntry ModEntry;
        internal static Harmony Harmony;
        internal static CollaborationController Controller;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Controller = new CollaborationController(modEntry.Logger);
            Harmony = new Harmony(modEntry.Info.Id);
            Harmony.PatchAll(typeof(Main).Assembly);

            modEntry.OnUpdate = OnUpdate;
            modEntry.OnUnload = Unload;
            modEntry.Logger.Log("EditorCollaboration v0.0.1 prototype loaded.");
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            Controller?.Update();
        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            Controller?.Dispose();
            Harmony?.UnpatchAll(modEntry.Info.Id);
            return true;
        }
    }
}
