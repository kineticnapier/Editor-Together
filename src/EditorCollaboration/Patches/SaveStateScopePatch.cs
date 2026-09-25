using System;
using System.Reflection;
using HarmonyLib;

namespace EditorCollaboration.Patches
{
    [HarmonyPatch]
    internal static class SaveStateScopePatch
    {
        private static Type ScopeType => AccessTools.TypeByName("SaveStateScope");

        [HarmonyTargetMethods]
        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            Type type = ScopeType;
            if (type == null)
                yield break;

            ConstructorInfo ctor = AccessTools.Constructor(type, new[]
            {
                typeof(scnEditor), typeof(bool), typeof(bool), typeof(bool)
            });
            if (ctor != null)
                yield return ctor;

            MethodInfo dispose = AccessTools.Method(type, "Dispose");
            if (dispose != null)
                yield return dispose;
        }

        [HarmonyPrefix]
        private static void Prefix(MethodBase __originalMethod, object[] __args)
        {
            if (__originalMethod is ConstructorInfo)
            {
                if (__args.Length < 4)
                    return;

                Main.Controller?.OnScopeEntering(
                    (scnEditor)__args[0],
                    (bool)__args[2],
                    (bool)__args[3]);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(MethodBase __originalMethod, object __instance)
        {
            if (__originalMethod is ConstructorInfo)
                return;

            scnEditor editor = null;
            if (__instance != null)
            {
                FieldInfo field = AccessTools.Field(__instance.GetType(), "editor");
                editor = field?.GetValue(__instance) as scnEditor;
            }

            Main.Controller?.OnScopeDisposed(editor);
        }
    }
}
