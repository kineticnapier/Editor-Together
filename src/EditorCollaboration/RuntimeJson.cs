using System;
using System.Reflection;

namespace EditorTogether
{
    internal static class RuntimeJson
    {
        private static readonly Type SerializerType;
        private static readonly MethodInfo SerializeMethod;
        private static readonly MethodInfo DeserializeMethod;

        static RuntimeJson()
        {
            SerializerType = Type.GetType("GDMiniJSON.Json, Assembly-CSharp", false);
            if (SerializerType == null) throw new TypeLoadException("GDMiniJSON.Json was not found in Assembly-CSharp.");
            SerializeMethod = SerializerType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object) }, null);
            DeserializeMethod = SerializerType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (SerializeMethod == null || DeserializeMethod == null) throw new MissingMethodException("GDMiniJSON.Json Serialize/Deserialize methods were not found.");
        }

        public static string Serialize(object value) => (string)SerializeMethod.Invoke(null, new[] { value });
        public static object Deserialize(string value) => DeserializeMethod.Invoke(null, new object[] { value });
    }
}
