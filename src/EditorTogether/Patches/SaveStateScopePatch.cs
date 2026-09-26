using HarmonyLib;

namespace EditorTogether.Patches
{
    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveState))]
    internal static class SaveStateScopePatch
    {
        [HarmonyPostfix]
        private static void Postfix(scnEditor __instance, bool clearRedo = true, bool dataHasChanged = true)
        {
            if (__instance == null || !dataHasChanged) return;
            Main.Controller?.OnEditorStateSaved(__instance);
        }
    }
}
