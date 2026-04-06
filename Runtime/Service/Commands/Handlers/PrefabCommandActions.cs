using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class PrefabCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(PrefabCommandActions));
#endif
        }

#if UNITY_EDITOR
        // ── create ──

        [Serializable]
        private sealed class CreateResult
        {
            public string assetPath = "";
            public int instanceId;
            public string name = "";
        }

        [CommandAction("prefab", "create", editorOnly: true, summary: "Create a prefab asset from a scene GameObject")]
        private static CommandResponse Create(
            string savePath,
            string gameObjectPath = "",
            int gameObjectInstanceId = 0)
        {
            if (string.IsNullOrEmpty(savePath))
                return CommandResponseFactory.ValidationError("savePath is required for prefab/create");

            return CommandHelpers.MainThreadCommand<CreateResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(gameObjectPath, gameObjectInstanceId, out var error);
                    if (go == null) return (error, result: (CreateResult)null);

                    CommandHelpers.EnsureDirectoryExists(savePath);
                    var prefab = PrefabUtility.SaveAsPrefabAsset(go, savePath);

                    if (prefab == null)
                        return (error: $"Failed to create prefab at '{savePath}'", result: (CreateResult)null);

                    return (error: (string)null, result: new CreateResult
                    {
                        assetPath = savePath,
                        instanceId = prefab.GetInstanceID(),
                        name = prefab.name
                    });
                },
                r => $"Created prefab '{r.name}'"
            );
        }

        // ── instantiate ──

        [Serializable]
        private sealed class InstantiateResult
        {
            public int instanceId;
            public string name = "";
            public string path = "";
        }

        [CommandAction("prefab", "instantiate", editorOnly: true, summary: "Instantiate a prefab into the active scene")]
        private static CommandResponse Instantiate(string assetPath, string parentPath = "", Vector3? position = null)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/instantiate");

            return CommandHelpers.MainThreadCommand<InstantiateResult>(
                () =>
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null)
                        return (error: $"Prefab not found at '{assetPath}'", result: (InstantiateResult)null);

                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    if (instance == null)
                        return (error: "Failed to instantiate prefab", result: (InstantiateResult)null);

                    Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");

                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        var parent = GameObject.Find(parentPath);
                        if (parent != null) instance.transform.SetParent(parent.transform, false);
                    }

                    if (position.HasValue)
                        instance.transform.position = position.Value;

                    return (error: (string)null, result: new InstantiateResult
                    {
                        instanceId = instance.GetInstanceID(),
                        name = instance.name,
                        path = CommandHelpers.GetHierarchyPath(instance.transform)
                    });
                },
                r => $"Instantiated '{r.name}'"
            );
        }

        // ── unpack ──

        [Serializable]
        private sealed class UnpackResult
        {
            public int instanceId;
            public string name = "";
            public bool unpacked;
        }

        [CommandAction("prefab", "unpack", editorOnly: true, summary: "Unpack a prefab instance")]
        private static CommandResponse Unpack(string gameObjectPath = "", int gameObjectInstanceId = 0, bool full = false)
        {
            return CommandHelpers.MainThreadCommand<UnpackResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(gameObjectPath, gameObjectInstanceId, out var error);
                    if (go == null) return (error, result: (UnpackResult)null);

                    if (!PrefabUtility.IsPartOfPrefabInstance(go))
                        return (error: $"'{go.name}' is not a prefab instance", result: (UnpackResult)null);

                    var mode = full ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
                    PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.UserAction);

                    return (error: (string)null, result: new UnpackResult
                    {
                        instanceId = go.GetInstanceID(),
                        name = go.name,
                        unpacked = true
                    });
                },
                r => $"Unpacked '{r.name}'"
            );
        }
#endif
    }
}
