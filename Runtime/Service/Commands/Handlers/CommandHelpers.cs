using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class CommandHelpers
    {
#if UNITY_EDITOR
        internal static CommandResponse MainThreadCommand<TResult>(
            Func<(string error, TResult result)> execute,
            Func<TResult, string> summarize)
            where TResult : class
        {
            var result = MainThreadRequestRunner.RunOnMainThread(execute);

            if (result.error != null)
                return CommandResponseFactory.ValidationError(result.error);

            return CommandResponseFactory.Ok(summarize(result.result), JsonUtility.ToJson(result.result));
        }

        internal static GameObject ResolveGameObject(string path, int instanceId, out string error)
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

        internal static GameObject FindByPath(string path)
        {
            var segments = path.TrimStart('/').Split('/');
            if (segments.Length == 0) return null;

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

        private static readonly Dictionary<string, Type> s_TypeCache = new(StringComparer.Ordinal);

        internal static Type ResolveType(string typeName, out string error)
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

        internal static string GetHierarchyPath(Transform transform)
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
