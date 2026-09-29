using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EditorTogether.Patches
{
    /// <summary>
    /// Stock floor creation is one of the simplest mutations to verify end-to-end.
    /// Capture its pre-edit state at the actual editor method boundary instead of relying
    /// on SaveStateScope/SaveState timing, which can vary across stock editor paths.
    ///
    /// This is deliberately narrow: once the operation pipeline is proven with floor
    /// creation, the same boundary strategy can be expanded to the remaining editor
    /// mutation families.
    /// </summary>
    [HarmonyPatch]
    internal static class StockFloorOperationBoundaryPatch
    {
        private static bool loggedActive;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo charCreate = AccessTools.Method(typeof(scnEditor), "CreateFloor", new[] { typeof(char), typeof(bool), typeof(bool) });
            MethodInfo floatCreate = AccessTools.Method(typeof(scnEditor), "CreateFloor", new[] { typeof(float), typeof(bool), typeof(bool) });

            if (charCreate != null) yield return charCreate;
            if (floatCreate != null) yield return floatCreate;
        }

        [HarmonyPrefix]
        private static void Prefix(scnEditor __instance)
        {
            if (__instance == null) return;

            OperationSyncManager.CaptureBeforeMutation(__instance, true);

            if (!loggedActive)
            {
                loggedActive = true;
                Main.ModEntry?.Logger.Log("[CollabOps] stock CreateFloor boundary hook active; floor additions will use pre-edit capture");
            }
        }
    }
}
