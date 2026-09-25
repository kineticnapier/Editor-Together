using HarmonyLib;

namespace EditorCollaboration.Patches
{
    // SaveState is the stable mutation boundary in the stock editor. SaveStateScope
    // is an implementation detail and its runtime type/constructor shape can move
    // between ADOFAI builds, while mutating editor operations ultimately save a
    // full LevelData snapshot through scnEditor.SaveState.
    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveState))]
    internal static class SaveStateScopePatch
    {
        [HarmonyPostfix]
        private static void Postfix(scnEditor __instance, bool clearRedo = true, bool dataHasChanged = true)
        {
            if (__instance == null || !dataHasChanged)
                return;

            Main.Controller?.OnEditorStateSaved(__instance);
        }
    }
}
