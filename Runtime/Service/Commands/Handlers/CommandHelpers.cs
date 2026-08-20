using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    // Most helpers here are plain UnityEngine work and are shared by the editor
    // and the player. Only the members that reach into UnityEditor are gated.
    public static class CommandHelpers
    {
        // Caller is already on the main thread via the framework's runOnMainThread default.
        internal static CommandResponse RunCommand<TResult>(
            Func<(string error, TResult result)> execute,
            Func<TResult, string> summarize)
            where TResult : class
        {
            var result = execute();

            if (result.error != null)
                return CommandResponseFactory.ValidationError(result.error);

            return CommandResponseFactory.Ok(summarize(result.result), JsonUtility.ToJson(result.result));
        }

        public static GameObject ResolveGameObject(string path, int instanceId, out string error)
        {
            error = null;

            if (instanceId != 0)
            {
#if UNITY_EDITOR
                var obj = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
#else
                var obj = Resources.InstanceIDToObject(instanceId) as GameObject;
#endif
                if (obj == null)
                {
                    error = $"No GameObject found with instanceId {instanceId}";
                }

                return obj;
            }

            if (!string.IsNullOrEmpty(path))
            {
                var obj = FindByPath(path);
                if (obj == null)
                {
                    error = $"No GameObject found at path '{path}'";
                }

                return obj;
            }

            error = "Either path or instanceId must be provided";
            return null;
        }

        public static GameObject FindByPath(string path)
        {
            var segments = path.TrimStart('/').Split('/');
            GameObject current = null;
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    if (root.name == segments[0]) { current = root; break; }
                }
                if (current != null) break;
            }

            // Also search DontDestroyOnLoad scene
            if (current == null)
            {
                foreach (var root in GetDontDestroyOnLoadRootObjects())
                {
                    if (root.name == segments[0]) { current = root; break; }
                }
            }

            if (current == null) return null;
            for (var i = 1; i < segments.Length; i++)
            {
                var t = current.transform;
                GameObject child = null;
                for (var j = 0; j < t.childCount; j++)
                {
                    var c = t.GetChild(j);
                    if (c.name == segments[i]) { child = c.gameObject; break; }
                }

                if (child == null) return null;
                current = child;
            }

            return current;
        }

        /// <summary>
        /// Gets root GameObjects from the DontDestroyOnLoad scene.
        /// Only available in play mode; returns empty array in edit mode.
        /// </summary>
        internal static GameObject[] GetDontDestroyOnLoadRootObjects()
        {
            if (!Application.isPlaying) return Array.Empty<GameObject>();

            // The DontDestroyOnLoad scene is not enumerable through SceneManager.
            // Probe it by temporarily moving a hidden object there.
            var probe = new GameObject("__ddol_probe__") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(probe);
            var scene = probe.scene;
            UnityEngine.Object.DestroyImmediate(probe);

            if (!scene.IsValid()) return Array.Empty<GameObject>();
            return scene.GetRootGameObjects();
        }

        private static readonly Dictionary<string, Type> s_TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        public static Type ResolveType(string typeName, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(typeName))
            {
                error = "typeName is required";
                return null;
            }

            if (s_TypeCache.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            var type = Type.GetType(typeName);
            if (type != null)
            {
                s_TypeCache[typeName] = type;
                return type;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                type = assembly.GetType(typeName);
                if (type != null)
                {
                    s_TypeCache[typeName] = type;
                    return type;
                }
            }

            string[] prefixes = { "UnityEngine.", "UnityEditor.", "UnityEngine.UI.", "UnityEngine.EventSystems.", "TMPro." };

            foreach (var prefix in prefixes)
            {
                var fullName = prefix + typeName;
                type = Type.GetType(fullName);
                if (type != null)
                {
                    s_TypeCache[typeName] = type;
                    return type;
                }

                foreach (var assembly in assemblies)
                {
                    type = assembly.GetType(fullName);
                    if (type != null)
                    {
                        s_TypeCache[typeName] = type;
                        return type;
                    }
                }
            }

            error = $"Type '{typeName}' could not be resolved";
            return null;
        }

        public static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return "";
            }

            var sb = new System.Text.StringBuilder(transform.name);
            var current = transform.parent;
            while (current != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, current.name);
                current = current.parent;
            }

            return sb.ToString();
        }

        internal static void EnsureDirectoryExists(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            var directory = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
        }

#if UNITY_EDITOR
        internal static void ImportAssetIfUnderAssets(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath) &&
                (filePath.StartsWith("Assets/", StringComparison.Ordinal) ||
                 filePath.StartsWith("Assets\\", StringComparison.Ordinal)))
            {
                AssetDatabase.ImportAsset(filePath);
            }
        }
#endif

        [Serializable]
        internal sealed class FieldPair
        {
            [CommandField(NonEmpty = true)]
            public string name = "";
            public string value = "";
        }

        // ── Prefab asset helpers ──

#if UNITY_EDITOR
        internal static GameObject LoadPrefabAsset(string assetPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(assetPath))
            {
                error = "assetPath is required";
                return null;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                error = $"No prefab asset found at '{assetPath}'";
            }

            return prefab;
        }
#endif

        // ── Shared SerializedProperty helpers ──

        [Serializable]
        internal sealed class PropertyInfo
        {
            public string name = "";
            public string type = "";
            public string value = "";
        }

        // ── Runtime field reflection ──
        //
        // A player has no SerializedObject, so component state is read and
        // written through reflection instead. This is restricted to components
        // the project itself declares: their serialized state really is their
        // fields, so reflection and SerializedObject agree on what exists. The
        // built-in types keep their state in native-backed properties, whose
        // getters can also have side effects (reading Renderer.material
        // instantiates a copy), so they are refused rather than guessed at.
        internal static bool IsProjectDeclaredComponent(Component component)
        {
            return component is MonoBehaviour;
        }

        // Mirrors Unity's serialization rule: public fields unless opted out,
        // and non-public fields that opted in with [SerializeField].
        internal static System.Reflection.FieldInfo[] GetSerializableFields(Type componentType)
        {
            var fields = new List<System.Reflection.FieldInfo>();
            for (var t = componentType; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                var declared = t.GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly);

                foreach (var field in declared)
                {
                    if (field.IsNotSerialized)
                    {
                        continue;
                    }

                    var optedIn = Attribute.IsDefined(field, typeof(SerializeField));
                    if (field.IsPublic || optedIn)
                    {
                        fields.Add(field);
                    }
                }
            }

            return fields.ToArray();
        }

        internal static string FieldValueToString(object value)
        {
            switch (value)
            {
                case null:
                    return "";
                case float f:
                    return f.ToString("R");
                case double d:
                    return d.ToString("R");
                case Vector2 v2:
                    return $"{v2.x},{v2.y}";
                case Vector3 v3:
                    return $"{v3.x},{v3.y},{v3.z}";
                case Vector4 v4:
                    return $"{v4.x},{v4.y},{v4.z},{v4.w}";
                case Quaternion q:
                    return $"{q.x},{q.y},{q.z},{q.w}";
                case Color c:
                    return $"{c.r},{c.g},{c.b},{c.a}";
                case UnityEngine.Object o:
                    return o == null ? "" : o.name;
                default:
                    return value.ToString();
            }
        }

        internal static bool TrySetFieldValue(System.Reflection.FieldInfo field, object target, string rawValue)
        {
            try
            {
                var type = field.FieldType;

                if (type == typeof(string))
                {
                    field.SetValue(target, rawValue ?? "");
                    return true;
                }

                if (type.IsEnum)
                {
                    field.SetValue(target, Enum.Parse(type, rawValue, true));
                    return true;
                }

                if (type == typeof(bool))
                {
                    field.SetValue(target, bool.Parse(rawValue));
                    return true;
                }

                if (type == typeof(Vector2) || type == typeof(Vector3) ||
                    type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color))
                {
                    if (!TryParseFloats(rawValue, out var parts))
                    {
                        return false;
                    }

                    if (type == typeof(Vector2) && parts.Length >= 2)
                    {
                        field.SetValue(target, new Vector2(parts[0], parts[1]));
                        return true;
                    }

                    if (type == typeof(Vector3) && parts.Length >= 3)
                    {
                        field.SetValue(target, new Vector3(parts[0], parts[1], parts[2]));
                        return true;
                    }

                    if (type == typeof(Vector4) && parts.Length >= 4)
                    {
                        field.SetValue(target, new Vector4(parts[0], parts[1], parts[2], parts[3]));
                        return true;
                    }

                    if (type == typeof(Quaternion) && parts.Length >= 4)
                    {
                        field.SetValue(target, new Quaternion(parts[0], parts[1], parts[2], parts[3]));
                        return true;
                    }

                    if (type == typeof(Color) && parts.Length >= 4)
                    {
                        field.SetValue(target, new Color(parts[0], parts[1], parts[2], parts[3]));
                        return true;
                    }

                    return false;
                }

                // Numeric types share Convert's parsing; anything left over
                // (object references, collections) is refused rather than
                // half-applied.
                if (type.IsPrimitive || type == typeof(decimal))
                {
                    field.SetValue(target, Convert.ChangeType(rawValue, type, System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                }

                return false;
            }
            catch
            {
                // A single unparsable field is reported as not modified rather
                // than failing the whole command.
                return false;
            }
        }

        private static bool TryParseFloats(string raw, out float[] values)
        {
            values = Array.Empty<float>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var pieces = raw.Split(',');
            var parsed = new float[pieces.Length];
            for (var i = 0; i < pieces.Length; i++)
            {
                if (!float.TryParse(pieces[i].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed[i]))
                {
                    return false;
                }
            }

            values = parsed;
            return true;
        }

#if UNITY_EDITOR
        internal static string SerializedPropertyToString(SerializedProperty prop)
        {
            return prop.propertyType switch
            {
                SerializedPropertyType.Integer => prop.intValue.ToString(),
                SerializedPropertyType.Boolean => prop.boolValue.ToString(),
                SerializedPropertyType.Float => prop.floatValue.ToString("R"),
                SerializedPropertyType.String => prop.stringValue ?? "",
                SerializedPropertyType.Color => $"({prop.colorValue.r},{prop.colorValue.g},{prop.colorValue.b},{prop.colorValue.a})",
                SerializedPropertyType.Vector2 => $"({prop.vector2Value.x},{prop.vector2Value.y})",
                SerializedPropertyType.Vector3 => $"({prop.vector3Value.x},{prop.vector3Value.y},{prop.vector3Value.z})",
                SerializedPropertyType.Vector4 => $"({prop.vector4Value.x},{prop.vector4Value.y},{prop.vector4Value.z},{prop.vector4Value.w})",
                SerializedPropertyType.Enum => prop.enumNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length
                    ? prop.enumNames[prop.enumValueIndex]
                    : prop.enumValueIndex.ToString(),
                SerializedPropertyType.ObjectReference => prop.objectReferenceValue != null
                    ? $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetInstanceID()})"
                    : "null",
                SerializedPropertyType.Rect => $"({prop.rectValue.x},{prop.rectValue.y},{prop.rectValue.width},{prop.rectValue.height})",
                SerializedPropertyType.Bounds => $"center({prop.boundsValue.center.x},{prop.boundsValue.center.y},{prop.boundsValue.center.z}) size({prop.boundsValue.size.x},{prop.boundsValue.size.y},{prop.boundsValue.size.z})",
                SerializedPropertyType.LayerMask => prop.intValue.ToString(),
                _ => $"<{prop.propertyType}>"
            };
        }

        internal static bool TrySetSerializedProperty(SerializedProperty prop, string rawValue)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        if (int.TryParse(rawValue, out var intVal)) { prop.intValue = intVal; return true; }
                        break;
                    case SerializedPropertyType.Float:
                        if (float.TryParse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatVal)) { prop.floatValue = floatVal; return true; }
                        break;
                    case SerializedPropertyType.Boolean:
                        if (bool.TryParse(rawValue, out var boolVal)) { prop.boolValue = boolVal; return true; }
                        break;
                    case SerializedPropertyType.String:
                        prop.stringValue = rawValue;
                        return true;
                    case SerializedPropertyType.Enum:
                        if (int.TryParse(rawValue, out var enumIdx)) { prop.enumValueIndex = enumIdx; return true; }
                        if (prop.enumNames != null)
                        {
                            for (var ei = 0; ei < prop.enumNames.Length; ei++)
                            {
                                if (string.Equals(prop.enumNames[ei], rawValue, StringComparison.OrdinalIgnoreCase))
                                {
                                    prop.enumValueIndex = ei;
                                    return true;
                                }
                            }
                        }
                        break;
                    case SerializedPropertyType.Color:
                        var color = JsonUtility.FromJson<Color>(rawValue);
                        prop.colorValue = color;
                        return true;
                    case SerializedPropertyType.Vector2:
                        var v2 = JsonUtility.FromJson<Vector2>(rawValue);
                        prop.vector2Value = v2;
                        return true;
                    case SerializedPropertyType.Vector3:
                        var v3 = JsonUtility.FromJson<Vector3>(rawValue);
                        prop.vector3Value = v3;
                        return true;
                    case SerializedPropertyType.Vector4:
                        var v4 = JsonUtility.FromJson<Vector4>(rawValue);
                        prop.vector4Value = v4;
                        return true;
                    case SerializedPropertyType.ObjectReference:
                        if (int.TryParse(rawValue, out var objId))
                        {
                            prop.objectReferenceValue = EditorUtility.InstanceIDToObject(objId);
                            return true;
                        }
                        break;
                }
            }
            catch
            {
                // Ignore parse failures for individual fields
            }

            return false;
        }
#endif

        internal static byte[] CaptureCamera(Camera cam, int w, int h)
        {
            var rt = new RenderTexture(w, h, 24);
            Texture2D tex = null;
            var prevRT = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevRT;
                prevRT = null;

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                if (prevRT != null) cam.targetTexture = prevRT;
                RenderTexture.active = prevActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
