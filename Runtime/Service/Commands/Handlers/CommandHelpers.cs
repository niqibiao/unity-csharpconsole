using System;
using System.Collections.Generic;
using System.Globalization;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    public static class CommandHelpers
    {
#if UNITY_EDITOR
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
                var obj = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
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

        internal static void ImportAssetIfUnderAssets(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath) &&
                (filePath.StartsWith("Assets/", StringComparison.Ordinal) ||
                 filePath.StartsWith("Assets\\", StringComparison.Ordinal)))
            {
                AssetDatabase.ImportAsset(filePath);
            }
        }

        [Serializable]
        internal sealed class FieldPair
        {
            [CommandField(NonEmpty = true)]
            public string name = "";
            public string value = "";
        }

        // ── Prefab asset helpers ──

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

        internal static GameObject ResolvePrefabGameObject(string assetPath, string gameObjectPath, out GameObject root, out string error)
        {
            root = LoadPrefabAsset(assetPath, out error);
            if (root == null) return null;

            return ResolvePrefabGameObject(root, assetPath, gameObjectPath, out error);
        }

        internal static GameObject ResolvePrefabGameObject(
            GameObject root,
            string assetPath,
            string gameObjectPath,
            out string error)
        {
            // Prefab locators carry serialized object identity; names and sibling order are mutable.
            error = null;
            if (root == null)
            {
                error = $"No prefab asset found at '{assetPath}'";
                return null;
            }

            var expectedGuid = GetPrefabAssetGuid(assetPath, out error);
            if (expectedGuid == null)
                return null;

            if (!TryGetPrefabSourceIdentity(
                    root,
                    assetPath,
                    expectedGuid,
                    out _,
                    out _,
                    out _,
                    out error))
            {
                return null;
            }

            if (gameObjectPath == "")
                return root;

            if (!TryParsePrefabLocator(gameObjectPath, out var locatorGuid, out var localId, out error))
                return null;
            if (!string.Equals(locatorGuid, expectedGuid, StringComparison.Ordinal))
            {
                error = $"Prefab GameObject locator belongs to asset GUID '{locatorGuid}', not '{expectedGuid}' for '{assetPath}'";
                return null;
            }

            GameObject match = null;
            var identities = new Dictionary<long, GameObject>();
            var pending = new Stack<Transform>();
            pending.Push(root.transform);
            while (pending.Count > 0)
            {
                var candidate = pending.Pop().gameObject;
                if (!TryGetPrefabSourceIdentity(
                        candidate,
                        assetPath,
                        expectedGuid,
                        out _,
                        out _,
                        out var candidateLocalId,
                        out error))
                {
                    return null;
                }

                if (identities.TryGetValue(candidateLocalId, out var existing) &&
                    existing != candidate)
                {
                    error = $"Prefab asset '{assetPath}' maps multiple GameObjects to local file ID {candidateLocalId}";
                    return null;
                }
                identities[candidateLocalId] = candidate;

                if (candidateLocalId == localId)
                {
                    if (match != null && match != candidate)
                    {
                        error = $"Prefab GameObject locator '{gameObjectPath}' is ambiguous in '{assetPath}'";
                        return null;
                    }
                    match = candidate;
                }

                var transform = candidate.transform;
                for (var i = transform.childCount - 1; i >= 0; i--)
                    pending.Push(transform.GetChild(i));
            }

            if (match == null)
            {
                error = $"No GameObject with local file ID {localId} exists in prefab '{assetPath}'";
                return null;
            }
            if (match == root)
            {
                error = "The prefab root must use an empty gameObjectPath";
                return null;
            }

            return match;
        }

        internal static string GetPrefabRelativePath(Transform node, Transform root, string assetPath)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var expectedGuid = GetPrefabAssetGuid(assetPath, out var error);
            if (expectedGuid == null)
                throw new InvalidOperationException(error);
            if (!TryGetPrefabSourceIdentity(
                    root.gameObject,
                    assetPath,
                    expectedGuid,
                    out _,
                    out _,
                    out var rootLocalId,
                    out error))
            {
                throw new InvalidOperationException(error);
            }
            if (!TryGetPrefabSourceIdentity(
                    node.gameObject,
                    assetPath,
                    expectedGuid,
                    out _,
                    out var guid,
                    out var localId,
                    out error))
            {
                throw new InvalidOperationException(error);
            }

            if (localId == rootLocalId)
            {
                if (node != root)
                    throw new InvalidOperationException($"Prefab asset '{assetPath}' maps a non-root GameObject to the root local file ID");
                return "";
            }

            var locator = $"gid:{guid}:{localId.ToString(CultureInfo.InvariantCulture)}";
            var resolved = ResolvePrefabGameObject(root.gameObject, assetPath, locator, out error);
            if (resolved == null ||
                !TryGetPrefabSourceIdentity(
                    resolved,
                    assetPath,
                    expectedGuid,
                    out _,
                    out var resolvedGuid,
                    out var resolvedLocalId,
                    out error) ||
                !string.Equals(resolvedGuid, guid, StringComparison.Ordinal) ||
                resolvedLocalId != localId)
            {
                throw new InvalidOperationException(error ?? $"Prefab locator '{locator}' did not resolve to its source GameObject");
            }

            return locator;
        }

        private static string GetPrefabAssetGuid(string assetPath, out string error)
        {
            var guid = (AssetDatabase.AssetPathToGUID(assetPath) ?? "").ToLowerInvariant();
            if (!IsCanonicalPrefabGuid(guid))
            {
                error = $"No canonical asset GUID exists for prefab '{assetPath}'";
                return null;
            }

            error = null;
            return guid;
        }

        private static bool TryGetPrefabSourceIdentity(
            GameObject candidate,
            string assetPath,
            string expectedGuid,
            out GameObject source,
            out string guid,
            out long localId,
            out string error)
        {
            source = null;
            guid = null;
            localId = 0;
            error = null;
            if (candidate == null)
            {
                error = $"Cannot map a missing GameObject to prefab '{assetPath}'";
                return false;
            }

            // The explicit target path keeps nested Prefab and Variant mapping on the requested asset.
            source = PrefabUtility.GetCorrespondingObjectFromSourceAtPath(candidate, assetPath);
            if (source == null && EditorUtility.IsPersistent(candidate) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out var candidateGuid, out long candidateLocalId) &&
                string.Equals((candidateGuid ?? "").ToLowerInvariant(), expectedGuid, StringComparison.Ordinal))
            {
                source = candidate;
                guid = expectedGuid;
                localId = candidateLocalId;
            }

            if (source == null || !EditorUtility.IsPersistent(source))
            {
                error = $"GameObject '{candidate.name}' has no persistent source in prefab '{assetPath}'";
                return false;
            }

            if (guid == null &&
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out guid, out localId))
            {
                error = $"Cannot read the persistent identity of GameObject '{candidate.name}' in prefab '{assetPath}'";
                return false;
            }

            guid = (guid ?? "").ToLowerInvariant();
            if (!IsCanonicalPrefabGuid(guid) ||
                !string.Equals(guid, expectedGuid, StringComparison.Ordinal))
            {
                error = $"GameObject '{candidate.name}' maps outside prefab asset '{assetPath}'";
                return false;
            }

            return true;
        }

        private static bool TryParsePrefabLocator(
            string locator,
            out string guid,
            out long localId,
            out string error)
        {
            guid = null;
            localId = 0;
            error = null;
            const int guidStart = 4;
            const int guidLength = 32;
            const int idSeparator = guidStart + guidLength;
            if (locator == null ||
                locator.Length <= idSeparator + 1 ||
                !locator.StartsWith("gid:", StringComparison.Ordinal) ||
                locator[idSeparator] != ':' ||
                locator.IndexOf(':', idSeparator + 1) >= 0)
            {
                error = $"Invalid prefab GameObject locator '{locator}': expected 'gid:<32 lowercase hex>:<canonical Int64>'";
                return false;
            }

            guid = locator.Substring(guidStart, guidLength);
            var localIdText = locator.Substring(idSeparator + 1);
            if (!IsCanonicalPrefabGuid(guid) ||
                !long.TryParse(
                    localIdText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out localId) ||
                !string.Equals(
                    localId.ToString(CultureInfo.InvariantCulture),
                    localIdText,
                    StringComparison.Ordinal))
            {
                error = $"Invalid prefab GameObject locator '{locator}': GUID and local file ID must be canonical";
                return false;
            }

            return true;
        }

        private static bool IsCanonicalPrefabGuid(string guid)
        {
            if (guid == null || guid.Length != 32)
                return false;

            for (var i = 0; i < guid.Length; i++)
            {
                var c = guid[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }

            return true;
        }

        // ── Shared SerializedProperty helpers ──

        [Serializable]
        internal sealed class PropertyInfo
        {
            public string name = "";
            public string type = "";
            public string value = "";
        }

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
#endif
    }
}
