using System;
using System.Collections.Generic;
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
            if (!savePath.StartsWith("Assets/", StringComparison.Ordinal) &&
                !savePath.StartsWith("Assets\\", StringComparison.Ordinal))
                return CommandResponseFactory.ValidationError("savePath must be under Assets/");

            return CommandHelpers.RunCommand<CreateResult>(
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

            return CommandHelpers.RunCommand<InstantiateResult>(
                () =>
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null)
                        return (error: $"Prefab not found at '{assetPath}'", result: (InstantiateResult)null);

                    Transform parentTransform = null;
                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        var parent = CommandHelpers.FindByPath(parentPath);
                        if (parent == null)
                            return (error: $"No GameObject found at parent path '{parentPath}'", result: (InstantiateResult)null);
                        parentTransform = parent.transform;
                    }

                    var instance = parentTransform != null
                        ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, parentTransform)
                        : (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    if (instance == null)
                        return (error: "Failed to instantiate prefab", result: (InstantiateResult)null);

                    if (position.HasValue)
                        instance.transform.position = position.Value;

                    Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");

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
            return CommandHelpers.RunCommand<UnpackResult>(
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

        // ══════════════════════════════════════════════════════
        //  Prefab asset-level commands
        // ══════════════════════════════════════════════════════

        // ── asset_hierarchy ──

        [Serializable]
        private sealed class AssetHierarchyNode
        {
            public int instanceId;
            public string name = "";
            public bool activeSelf;
            public int childCount;
            public string[] components = Array.Empty<string>();
            public AssetHierarchyNode[] children = Array.Empty<AssetHierarchyNode>();
        }

        [Serializable]
        private sealed class AssetHierarchyResult
        {
            public string assetPath = "";
            public string rootName = "";
            public AssetHierarchyNode root;
        }

        [CommandAction("prefab", "asset_hierarchy", editorOnly: true, summary: "Get the hierarchy tree of a prefab asset")]
        private static CommandResponse AssetHierarchy(string assetPath, int depth = -1, bool includeComponents = false)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_hierarchy");

            return CommandHelpers.RunCommand<AssetHierarchyResult>(
                () =>
                {
                    var root = CommandHelpers.LoadPrefabAsset(assetPath, out var error);
                    if (root == null) return (error, result: (AssetHierarchyResult)null);

                    var nodeCount = 0;
                    const int maxNodes = 5000;

                    return (error: (string)null, result: new AssetHierarchyResult
                    {
                        assetPath = assetPath,
                        rootName = root.name,
                        root = BuildAssetHierarchyNode(root.transform, depth, 0, includeComponents, ref nodeCount, maxNodes)
                    });
                },
                r => $"Prefab '{r.rootName}' hierarchy"
            );
        }

        private static AssetHierarchyNode BuildAssetHierarchyNode(Transform t, int maxDepth, int currentDepth, bool includeComponents, ref int nodeCount, int maxNodes)
        {
            nodeCount++;
            var go = t.gameObject;
            var node = new AssetHierarchyNode
            {
                instanceId = go.GetInstanceID(),
                name = go.name,
                activeSelf = go.activeSelf,
                childCount = t.childCount
            };

            if (includeComponents)
            {
                var comps = go.GetComponents<Component>();
                var names = new List<string>(comps.Length);
                foreach (var c in comps)
                {
                    if (c != null) names.Add(c.GetType().Name);
                }
                node.components = names.ToArray();
            }

            if ((maxDepth < 0 || currentDepth < maxDepth) && t.childCount > 0)
            {
                var children = new List<AssetHierarchyNode>(t.childCount);
                for (var i = 0; i < t.childCount && nodeCount < maxNodes; i++)
                {
                    children.Add(BuildAssetHierarchyNode(t.GetChild(i), maxDepth, currentDepth + 1, includeComponents, ref nodeCount, maxNodes));
                }
                node.children = children.ToArray();
            }

            return node;
        }

        // ── asset_get ──

        [Serializable]
        private sealed class AssetTransformInfo
        {
            public Vector3 localPosition;
            public Vector3 localRotation;
            public Vector3 localScale;
        }

        [Serializable]
        private sealed class AssetComponentBrief
        {
            public string typeName = "";
            public int instanceId;
            public bool enabled;
        }

        [Serializable]
        private sealed class AssetGetResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public int instanceId;
            public string name = "";
            public string tag = "";
            public int layer;
            public bool activeSelf;
            public AssetTransformInfo transform = new();
            public AssetComponentBrief[] components = Array.Empty<AssetComponentBrief>();
        }

        [CommandAction("prefab", "asset_get", editorOnly: true, summary: "Get detailed info about a GameObject in a prefab asset")]
        private static CommandResponse AssetGet(string assetPath, string gameObjectPath = "")
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_get");

            return CommandHelpers.RunCommand<AssetGetResult>(
                () =>
                {
                    var go = CommandHelpers.ResolvePrefabGameObject(assetPath, gameObjectPath, out var root, out var error);
                    if (go == null) return (error, result: (AssetGetResult)null);

                    var t = go.transform;
                    var comps = go.GetComponents<Component>();
                    var compInfos = new List<AssetComponentBrief>();
                    foreach (var comp in comps)
                    {
                        if (comp == null) continue;
                        compInfos.Add(new AssetComponentBrief
                        {
                            typeName = comp.GetType().Name,
                            instanceId = comp.GetInstanceID(),
                            enabled = comp is Behaviour b ? b.enabled : true
                        });
                    }

                    return (error: (string)null, result: new AssetGetResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = CommandHelpers.GetPrefabRelativePath(t, root.transform),
                        instanceId = go.GetInstanceID(),
                        name = go.name,
                        tag = go.tag,
                        layer = go.layer,
                        activeSelf = go.activeSelf,
                        transform = new AssetTransformInfo
                        {
                            localPosition = t.localPosition,
                            localRotation = t.localEulerAngles,
                            localScale = t.localScale,
                        },
                        components = compInfos.ToArray()
                    });
                },
                r => $"Got '{r.name}' in prefab"
            );
        }

        // ── asset_get_component ──

        [Serializable]
        private sealed class AssetGetComponentResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public string typeName = "";
            public int componentInstanceId;
            public CommandHelpers.PropertyInfo[] properties = Array.Empty<CommandHelpers.PropertyInfo>();
        }

        [CommandAction("prefab", "asset_get_component", editorOnly: true, summary: "Get serialized properties of a component in a prefab asset")]
        private static CommandResponse AssetGetComponent(string assetPath, string typeName, string gameObjectPath = "", int index = 0)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_get_component");
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for prefab/asset_get_component");

            return CommandHelpers.RunCommand<AssetGetComponentResult>(
                () =>
                {
                    var go = CommandHelpers.ResolvePrefabGameObject(assetPath, gameObjectPath, out var root, out var error);
                    if (go == null) return (error, result: (AssetGetComponentResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (AssetGetComponentResult)null);

                    var comps = go.GetComponents(type);
                    if (comps.Length == 0)
                        return (error: $"No component of type '{typeName}' found on '{go.name}'", result: (AssetGetComponentResult)null);
                    if (index < 0 || index >= comps.Length)
                        return (error: $"Component index {index} is out of range (0..{comps.Length - 1})", result: (AssetGetComponentResult)null);

                    var comp = comps[index];
                    var so = new SerializedObject(comp);
                    var props = new List<CommandHelpers.PropertyInfo>();
                    var iter = so.GetIterator();
                    const int maxProperties = 200;

                    if (iter.NextVisible(true))
                    {
                        do
                        {
                            if (props.Count >= maxProperties) break;
                            props.Add(new CommandHelpers.PropertyInfo
                            {
                                name = iter.name,
                                type = iter.propertyType.ToString(),
                                value = CommandHelpers.SerializedPropertyToString(iter)
                            });
                        } while (iter.NextVisible(false));
                    }

                    return (error: (string)null, result: new AssetGetComponentResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = CommandHelpers.GetPrefabRelativePath(go.transform, root.transform),
                        typeName = type.Name,
                        componentInstanceId = comp.GetInstanceID(),
                        properties = props.ToArray()
                    });
                },
                r => $"Got {r.typeName} ({r.properties.Length} properties)"
            );
        }

        // ── asset_modify_component ──

        [Serializable]
        private sealed class AssetModifyComponentResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public string typeName = "";
            public string[] modifiedFields = Array.Empty<string>();
        }

        [CommandAction("prefab", "asset_modify_component", editorOnly: true, summary: "Modify serialized fields of a component in a prefab asset")]
        private static CommandResponse AssetModifyComponent(CommandHelpers.FieldPair[] fields, string assetPath, string typeName, string gameObjectPath = "", int index = 0)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_modify_component");
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for prefab/asset_modify_component");
            if (fields == null || fields.Length == 0)
                return CommandResponseFactory.ValidationError("fields array is required for prefab/asset_modify_component");

            return CommandHelpers.RunCommand<AssetModifyComponentResult>(
                () =>
                {
                    var go = CommandHelpers.ResolvePrefabGameObject(assetPath, gameObjectPath, out var root, out var error);
                    if (go == null) return (error, result: (AssetModifyComponentResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (AssetModifyComponentResult)null);

                    var comps = go.GetComponents(type);
                    if (comps.Length == 0)
                        return (error: $"No component of type '{typeName}' found on '{go.name}'", result: (AssetModifyComponentResult)null);
                    if (index < 0 || index >= comps.Length)
                        return (error: $"Component index {index} is out of range (0..{comps.Length - 1})", result: (AssetModifyComponentResult)null);

                    var comp = comps[index];
                    var so = new SerializedObject(comp);
                    var modifiedFields = new List<string>();

                    foreach (var field in fields)
                    {
                        if (string.IsNullOrEmpty(field.name)) continue;
                        var prop = so.FindProperty(field.name);
                        if (prop == null) continue;
                        if (CommandHelpers.TrySetSerializedProperty(prop, field.value))
                        {
                            modifiedFields.Add(field.name);
                        }
                    }

                    so.ApplyModifiedProperties();
                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetModifyComponentResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = CommandHelpers.GetPrefabRelativePath(go.transform, root.transform),
                        typeName = type.Name,
                        modifiedFields = modifiedFields.ToArray()
                    });
                },
                r => $"Modified {r.modifiedFields.Length} field(s) on {r.typeName}"
            );
        }

        // ── asset_add_component ──

        [Serializable]
        private sealed class AssetAddComponentResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public string typeName = "";
            public int componentInstanceId;
        }

        [CommandAction("prefab", "asset_add_component", editorOnly: true, summary: "Add a component to a GameObject in a prefab asset")]
        private static CommandResponse AssetAddComponent(string assetPath, string typeName, string gameObjectPath = "")
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_add_component");
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for prefab/asset_add_component");

            return CommandHelpers.RunCommand<AssetAddComponentResult>(
                () =>
                {
                    var go = CommandHelpers.ResolvePrefabGameObject(assetPath, gameObjectPath, out var root, out var error);
                    if (go == null) return (error, result: (AssetAddComponentResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (AssetAddComponentResult)null);

                    var comp = ObjectFactory.AddComponent(go, type);
                    if (comp == null)
                        return (error: $"Failed to add component '{typeName}' to '{go.name}'", result: (AssetAddComponentResult)null);

                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetAddComponentResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = CommandHelpers.GetPrefabRelativePath(go.transform, root.transform),
                        typeName = type.Name,
                        componentInstanceId = comp.GetInstanceID()
                    });
                },
                r => $"Added {r.typeName} to prefab"
            );
        }

        // ── asset_remove_component ──

        [Serializable]
        private sealed class AssetRemoveComponentResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public string typeName = "";
            public bool removed;
        }

        [CommandAction("prefab", "asset_remove_component", editorOnly: true, summary: "Remove a component from a GameObject in a prefab asset")]
        private static CommandResponse AssetRemoveComponent(string assetPath, string typeName, string gameObjectPath = "", int index = 0)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_remove_component");
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for prefab/asset_remove_component");

            return CommandHelpers.RunCommand<AssetRemoveComponentResult>(
                () =>
                {
                    var go = CommandHelpers.ResolvePrefabGameObject(assetPath, gameObjectPath, out var root, out var error);
                    if (go == null) return (error, result: (AssetRemoveComponentResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (AssetRemoveComponentResult)null);

                    var comps = go.GetComponents(type);
                    if (comps.Length == 0)
                        return (error: $"No component of type '{typeName}' found on '{go.name}'", result: (AssetRemoveComponentResult)null);
                    if (index < 0 || index >= comps.Length)
                        return (error: $"Component index {index} is out of range (0..{comps.Length - 1})", result: (AssetRemoveComponentResult)null);

                    UnityEngine.Object.DestroyImmediate(comps[index], true);
                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetRemoveComponentResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = CommandHelpers.GetPrefabRelativePath(go.transform, root.transform),
                        typeName = type.Name,
                        removed = true
                    });
                },
                r => $"Removed {r.typeName} from prefab"
            );
        }

        // ── asset_modify_gameobject ──

        [Serializable]
        private sealed class AssetModifyGameObjectResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public string name = "";
        }

        [CommandAction("prefab", "asset_modify_gameobject", editorOnly: true, summary: "Modify a GameObject's properties in a prefab asset")]
        private static CommandResponse AssetModifyGameObject(
            string assetPath,
            string gameObjectPath = "",
            string name = "",
            string tag = "",
            int layer = -1,
            int active = -1,
            int isStatic = -1)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_modify_gameobject");

            return CommandHelpers.RunCommand<AssetModifyGameObjectResult>(
                () =>
                {
                    var go = CommandHelpers.ResolvePrefabGameObject(assetPath, gameObjectPath, out var root, out var error);
                    if (go == null) return (error, result: (AssetModifyGameObjectResult)null);

                    if (!string.IsNullOrEmpty(name)) go.name = name;
                    if (!string.IsNullOrEmpty(tag)) go.tag = tag;
                    if (layer >= 0) go.layer = layer;
                    if (active >= 0) go.SetActive(active != 0);
                    if (isStatic >= 0) go.isStatic = isStatic != 0;

                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetModifyGameObjectResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = CommandHelpers.GetPrefabRelativePath(go.transform, root.transform),
                        name = go.name
                    });
                },
                r => $"Modified '{r.name}' in prefab"
            );
        }

        // ── asset_add_gameobject ──

        [Serializable]
        private sealed class AssetAddGameObjectResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public string name = "";
        }

        [CommandAction("prefab", "asset_add_gameobject", editorOnly: true, summary: "Add a child GameObject to a prefab asset")]
        private static CommandResponse AssetAddGameObject(string assetPath, string parentPath = "", string name = "")
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_add_gameobject");

            return CommandHelpers.RunCommand<AssetAddGameObjectResult>(
                () =>
                {
                    var parent = CommandHelpers.ResolvePrefabGameObject(assetPath, parentPath, out var root, out var error);
                    if (parent == null) return (error, result: (AssetAddGameObjectResult)null);

                    var child = new GameObject(string.IsNullOrEmpty(name) ? "GameObject" : name);
                    child.transform.SetParent(parent.transform, false);

                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetAddGameObjectResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = CommandHelpers.GetPrefabRelativePath(child.transform, root.transform),
                        name = child.name
                    });
                },
                r => $"Added '{r.name}' to prefab"
            );
        }

        // ── asset_remove_gameobject ──

        [Serializable]
        private sealed class AssetRemoveGameObjectResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public bool removed;
        }

        [CommandAction("prefab", "asset_remove_gameobject", editorOnly: true, summary: "Remove a child GameObject from a prefab asset")]
        private static CommandResponse AssetRemoveGameObject(string assetPath, string gameObjectPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_remove_gameobject");
            if (string.IsNullOrEmpty(gameObjectPath))
                return CommandResponseFactory.ValidationError("gameObjectPath is required for prefab/asset_remove_gameobject (cannot remove root)");

            return CommandHelpers.RunCommand<AssetRemoveGameObjectResult>(
                () =>
                {
                    var go = CommandHelpers.ResolvePrefabGameObject(assetPath, gameObjectPath, out var root, out var error);
                    if (go == null) return (error, result: (AssetRemoveGameObjectResult)null);

                    if (go == root)
                        return (error: "Cannot remove the root GameObject of a prefab asset", result: (AssetRemoveGameObjectResult)null);

                    var path = CommandHelpers.GetPrefabRelativePath(go.transform, root.transform);
                    UnityEngine.Object.DestroyImmediate(go, true);
                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetRemoveGameObjectResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = path,
                        removed = true
                    });
                },
                r => $"Removed '{r.gameObjectPath}' from prefab"
            );
        }
#endif
    }
}
