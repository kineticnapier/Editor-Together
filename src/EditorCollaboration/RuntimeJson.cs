using System;
using System.Reflection;

namespace EditorCollaboration
{
    internal static class RuntimeJson
    {
        private static readonly MethodInfo SerializeMethod;
        private static readonly MethodInfo DeserializeMethod;

        static RuntimeJson()
        {
            Type jsonType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                jsonType = assembly.GetType("GDMiniJSON.Json", false);
                if (jsonType != null)
                    break;
            }

            if (jsonType == null)
                throw new TypeLoadException("Could not locate ADOFAI's GDMiniJSON.Json type in loaded assemblies.");

            SerializeMethod = jsonType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object) }, null);
            DeserializeMethod = jsonType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

            if (SerializeMethod == null || DeserializeMethod == null)
                throw new MissingMethodException("GDMiniJSON.Json Serialize/Deserialize methods were not found.");
        }

        public static string Serialize(object value)
        {
            return (string)SerializeMethod.Invoke(null, new[] { value });
        }

        public static object Deserialize(string value)
        {
            return DeserializeMethod.Invoke(null, new object[] { value });
        }
    }
}
