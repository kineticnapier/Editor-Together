using System;
using System.Reflection;
using HarmonyLib;

namespace EditorTogether.Patches
{
    /// <summary>
    /// Stock scnEditor mutations are wrapped in SaveStateScope rather than calling
    /// scnEditor.SaveState directly. Hook the scope constructor so OperationSync can
    /// capture the pre-edit LevelData before the body mutates it.
    /// </summary>
    [HarmonyPatch]
    internal static class SaveStateScopeOperationSyncPatch
    {
        private static ConstructorInfo targetConstructor;
        private static bool loggedActive;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            targetConstructor = FindTargetConstructor();
            if (targetConstructor != null) return true;
            Main.ModEntry?.Logger.Warning("[CollabOps] SaveStateScope constructor was not found; operation sync will rely on full-state fallback");
            return false;
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => targetConstructor;

        [HarmonyPrefix]
        private static void Prefix(object[] __args)
        {
            if (__args == null || __args.Length == 0) return;
            scnEditor editor = __args[0] as scnEditor;
            if (editor == null) return;

            bool dataHasChanged = true;
            if (__args.Length > 2 && __args[2] is bool value) dataHasChanged = value;
            if (!dataHasChanged) return;

            OperationSyncManager.CaptureBeforeMutation(editor, true);
            Main.Controller?.OnEditorStateSaved(editor);

            if (!loggedActive)
            {
                loggedActive = true;
                Main.ModEntry?.Logger.Log("[CollabOps] SaveStateScope hook active; pre-edit capture enabled");
            }
        }

        private static ConstructorInfo FindTargetConstructor()
        {
            Type scopeType = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length && scopeType == null; i++)
            {
                Assembly assembly = assemblies[i];
                try
                {
                    Type direct = assembly.GetType("SaveStateScope", false);
                    if (direct != null)
                    {
                        scopeType = direct;
                        break;
                    }

                    Type[] types = assembly.GetTypes();
                    for (int j = 0; j < types.Length; j++)
                    {
                        Type type = types[j];
                        if (type != null && string.Equals(type.Name, "SaveStateScope", StringComparison.Ordinal))
                        {
                            scopeType = type;
                            break;
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Type[] types = ex.Types;
                    if (types == null) continue;
                    for (int j = 0; j < types.Length; j++)
                    {
                        Type type = types[j];
                        if (type != null && string.Equals(type.Name, "SaveStateScope", StringComparison.Ordinal))
                        {
                            scopeType = type;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (scopeType == null) return null;
            ConstructorInfo[] constructors = scopeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < constructors.Length; i++)
            {
                ParameterInfo[] parameters = constructors[i].GetParameters();
                if (parameters.Length < 3) continue;
                if (parameters[0].ParameterType != typeof(scnEditor)) continue;
                if (parameters[2].ParameterType != typeof(bool)) continue;
                return constructors[i];
            }
            return null;
        }
    }
}
