using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
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

        [CommandAction(
            "prefab",
            "create",
            editorOnly: true,
            summary: "Create a prefab asset from a scene GameObject",
            resultType: typeof(CreateResult))]
        [CommandRule(CommandRuleKind.ExactlyOneOf, "gameObjectPath", "gameObjectInstanceId")]
        private static CommandResponse Create(
            [CommandArgument(NonEmpty = true)] string savePath,
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

        [CommandAction(
            "prefab",
            "instantiate",
            editorOnly: true,
            summary: "Instantiate a prefab into the active scene",
            resultType: typeof(InstantiateResult))]
        private static CommandResponse Instantiate(
            [CommandArgument(NonEmpty = true)] string assetPath,
            string parentPath = "",
            Vector3? position = null)
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

        [CommandAction(
            "prefab",
            "unpack",
            editorOnly: true,
            summary: "Unpack a prefab instance",
            resultType: typeof(UnpackResult))]
        [CommandRule(CommandRuleKind.ExactlyOneOf, "gameObjectPath", "gameObjectInstanceId")]
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

        private static GameObject InstantiatePrefabForAssetMutation(
            string assetPath,
            out Scene previewScene,
            out GameObject assetRoot,
            out string error)
        {
            previewScene = default;
            assetRoot = CommandHelpers.LoadPrefabAsset(assetPath, out error);
            if (assetRoot == null)
                return null;

            if (!EditorUtility.IsPersistent(assetRoot) ||
                !PrefabUtility.IsPartOfPrefabAsset(assetRoot))
            {
                error = $"'{assetPath}' is not a persistent prefab asset";
                assetRoot = null;
                return null;
            }

            if (PrefabUtility.IsPartOfImmutablePrefab(assetRoot))
            {
                error = $"Prefab asset '{assetPath}' is immutable";
                assetRoot = null;
                return null;
            }

            previewScene = EditorSceneManager.NewPreviewScene();
            GameObject instanceRoot;
            try
            {
                instanceRoot = PrefabUtility.InstantiatePrefab(assetRoot, previewScene) as GameObject;
            }
            catch (Exception exception)
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
                previewScene = default;
                error = $"Failed to create an isolated prefab instance for '{assetPath}': {exception.Message}";
                assetRoot = null;
                return null;
            }

            if (instanceRoot == null ||
                EditorUtility.IsPersistent(instanceRoot) ||
                !PrefabUtility.IsPartOfPrefabInstance(instanceRoot))
            {
                if (previewScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                    previewScene = default;
                }

                error = $"Failed to create an isolated prefab instance for '{assetPath}'";
                assetRoot = null;
                return null;
            }

            return instanceRoot;
        }

        private static bool TryResolvePrefabMutationPair(
            PrefabLocatorIndex assetIndex,
            PrefabLocatorIndex instanceIndex,
            string gameObjectPath,
            out GameObject assetGameObject,
            out GameObject instanceGameObject,
            out string error)
        {
            assetGameObject = assetIndex.Resolve(gameObjectPath, out error);
            instanceGameObject = null;
            if (assetGameObject == null)
                return false;

            instanceGameObject = instanceIndex.Resolve(gameObjectPath, out error);
            if (instanceGameObject == null)
                return false;

            if (!assetIndex.TryGetLocalId(assetGameObject, out var assetLocalId) ||
                !instanceIndex.TryGetLocalId(instanceGameObject, out var instanceLocalId) ||
                assetLocalId != instanceLocalId)
            {
                error = $"GameObject locator does not map to the same serialized object in '{assetIndex.AssetPath}'";
                return false;
            }

            if (!EditorUtility.IsPersistent(assetGameObject) ||
                EditorUtility.IsPersistent(instanceGameObject))
            {
                error = "Prefab mutation requires a persistent asset object and its non-persistent instance";
                return false;
            }

            return true;
        }

        private static PrefabLocatorIndex LoadPrefabLocatorIndex(
            string assetPath,
            out GameObject root,
            out string error)
        {
            root = CommandHelpers.LoadPrefabAsset(assetPath, out error);
            return root == null
                ? null
                : PrefabLocatorIndex.Create(root, assetPath, out error);
        }

        private static PrefabLocatorIndex ReloadPrefabLocatorIndex(
            string assetPath,
            out GameObject root,
            out string error)
        {
            root = null;
            error = null;
            try
            {
                AssetDatabase.ImportAsset(
                    assetPath,
                    ImportAssetOptions.ForceUpdate |
                    ImportAssetOptions.ForceSynchronousImport);
                return LoadPrefabLocatorIndex(assetPath, out root, out error);
            }
            catch (Exception exception)
            {
                error = $"Failed to reload prefab '{assetPath}': {exception.Message}";
                root = null;
                return null;
            }
        }

        private static bool IdSequenceEquals(long[] left, long[] right)
        {
            left ??= Array.Empty<long>();
            right ??= Array.Empty<long>();
            if (left.Length != right.Length)
                return false;

            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }

            return true;
        }

        private static long[] RemoveId(long[] source, long removedId)
        {
            source ??= Array.Empty<long>();
            var result = new List<long>(Math.Max(0, source.Length - 1));
            foreach (var localId in source)
            {
                if (localId != removedId)
                    result.Add(localId);
            }

            return result.ToArray();
        }

        private static string ApplyFailureSuffix(Exception applyException)
        {
            return applyException == null
                ? ""
                : $" Unity reported: {applyException.Message}";
        }

        private static CommandResponse PrefabStateUncertain(
            string operation,
            string assetPath,
            string detail,
            Exception applyException)
        {
            return CommandResponseFactory.SystemError(
                $"Prefab asset state uncertain; do not retry. "
                + $"{operation} on '{assetPath}' could not be reconciled: {detail}"
                + ApplyFailureSuffix(applyException));
        }

        private static void ClosePrefabMutationScene(Scene previewScene)
        {
            if (!previewScene.IsValid())
                return;

            try
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
            catch (Exception exception)
            {
                // Cleanup failure must not replace a reconciled mutation result:
                // retrying an already committed add/remove would be destructive.
                Debug.LogWarning(
                    "[CSharpConsole] Failed to close prefab mutation preview "
                    + $"scene: {exception.Message}");
            }
        }

        // ── asset_hierarchy ──

        [Serializable]
        private sealed class AssetHierarchyNode
        {
            public string gameObjectPath = "";
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

        [CommandAction(
            "prefab",
            "asset_hierarchy",
            editorOnly: true,
            summary: "Get the hierarchy tree of a prefab asset",
            resultType: typeof(AssetHierarchyResult))]
        private static CommandResponse AssetHierarchy(
            [CommandArgument(NonEmpty = true)] string assetPath,
            [CommandArgument(Minimum = -1)] int depth = -1,
            bool includeComponents = false)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_hierarchy");

            return CommandHelpers.RunCommand<AssetHierarchyResult>(
                () =>
                {
                    var locatorIndex = LoadPrefabLocatorIndex(
                        assetPath,
                        out var root,
                        out var error);
                    if (locatorIndex == null)
                        return (error, result: (AssetHierarchyResult)null);

                    var nodeCount = 0;
                    const int maxNodes = 5000;

                    return (error: (string)null, result: new AssetHierarchyResult
                    {
                        assetPath = assetPath,
                        rootName = root.name,
                        root = BuildAssetHierarchyNode(
                            root.transform,
                            locatorIndex,
                            depth,
                            0,
                            includeComponents,
                            ref nodeCount,
                            maxNodes)
                    });
                },
                r => $"Prefab '{r.rootName}' hierarchy"
            );
        }

        private static AssetHierarchyNode BuildAssetHierarchyNode(
            Transform t,
            PrefabLocatorIndex locatorIndex,
            int maxDepth,
            int currentDepth,
            bool includeComponents,
            ref int nodeCount,
            int maxNodes)
        {
            nodeCount++;
            var go = t.gameObject;
            var locator = locatorIndex.GetLocator(go, out var locatorError);
            if (locator == null)
                throw new InvalidOperationException(locatorError);

            var node = new AssetHierarchyNode
            {
                gameObjectPath = locator,
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
                    children.Add(BuildAssetHierarchyNode(
                        t.GetChild(i),
                        locatorIndex,
                        maxDepth,
                        currentDepth + 1,
                        includeComponents,
                        ref nodeCount,
                        maxNodes));
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
            public Vector3 localEulerAngles;
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
            public bool isStatic;
            public AssetTransformInfo transform = new AssetTransformInfo();
            public AssetComponentBrief[] components = Array.Empty<AssetComponentBrief>();
        }

        [CommandAction(
            "prefab",
            "asset_get",
            editorOnly: true,
            summary: "Get detailed info about a GameObject in a prefab asset",
            resultType: typeof(AssetGetResult))]
        private static CommandResponse AssetGet(
            [CommandArgument(NonEmpty = true)] string assetPath,
            string gameObjectPath = "")
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_get");

            return CommandHelpers.RunCommand<AssetGetResult>(
                () =>
                {
                    var locatorIndex = LoadPrefabLocatorIndex(
                        assetPath,
                        out _,
                        out var error);
                    if (locatorIndex == null)
                        return (error, result: (AssetGetResult)null);
                    var go = locatorIndex.Resolve(gameObjectPath, out error);
                    if (go == null) return (error, result: (AssetGetResult)null);
                    var locator = locatorIndex.GetLocator(go, out error);
                    if (locator == null)
                        return (error, result: (AssetGetResult)null);

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
                        gameObjectPath = locator,
                        instanceId = go.GetInstanceID(),
                        name = go.name,
                        tag = go.tag,
                        layer = go.layer,
                        activeSelf = go.activeSelf,
                        isStatic = go.isStatic,
                        transform = new AssetTransformInfo
                        {
                            localPosition = t.localPosition,
                            localEulerAngles = t.localEulerAngles,
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

        [CommandAction(
            "prefab",
            "asset_get_component",
            editorOnly: true,
            summary: "Get serialized properties of a component in a prefab asset",
            resultType: typeof(AssetGetComponentResult))]
        private static CommandResponse AssetGetComponent(
            [CommandArgument(NonEmpty = true)] string assetPath,
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "",
            [CommandArgument(Minimum = 0)] int index = 0)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_get_component");
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for prefab/asset_get_component");

            return CommandHelpers.RunCommand<AssetGetComponentResult>(
                () =>
                {
                    var locatorIndex = LoadPrefabLocatorIndex(
                        assetPath,
                        out _,
                        out var error);
                    if (locatorIndex == null)
                        return (error, result: (AssetGetComponentResult)null);
                    var go = locatorIndex.Resolve(gameObjectPath, out error);
                    if (go == null) return (error, result: (AssetGetComponentResult)null);
                    var locator = locatorIndex.GetLocator(go, out error);
                    if (locator == null)
                        return (error, result: (AssetGetComponentResult)null);

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
                        gameObjectPath = locator,
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

        [CommandAction(
            "prefab",
            "asset_modify_component",
            editorOnly: true,
            summary: "Modify serialized fields of a component in a prefab asset",
            resultType: typeof(AssetModifyComponentResult))]
        private static CommandResponse AssetModifyComponent(
            [CommandArgument(NonEmpty = true)] CommandHelpers.FieldPair[] fields,
            [CommandArgument(NonEmpty = true)] string assetPath,
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "",
            [CommandArgument(Minimum = 0)] int index = 0)
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
                    var locatorIndex = LoadPrefabLocatorIndex(
                        assetPath,
                        out var root,
                        out var error);
                    if (locatorIndex == null)
                        return (error, result: (AssetModifyComponentResult)null);
                    var go = locatorIndex.Resolve(gameObjectPath, out error);
                    if (go == null) return (error, result: (AssetModifyComponentResult)null);
                    var locator = locatorIndex.GetLocator(go, out error);
                    if (locator == null)
                        return (error, result: (AssetModifyComponentResult)null);

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
                        gameObjectPath = locator,
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

        [CommandAction(
            "prefab",
            "asset_add_component",
            editorOnly: true,
            summary: "Add a component to a GameObject in a prefab asset",
            resultType: typeof(AssetAddComponentResult))]
        private static CommandResponse AssetAddComponent(
            [CommandArgument(NonEmpty = true)] string assetPath,
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "")
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_add_component");
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for prefab/asset_add_component");

            return CommandHelpers.RunCommand<AssetAddComponentResult>(
                () =>
                {
                    var locatorIndex = LoadPrefabLocatorIndex(
                        assetPath,
                        out var root,
                        out var error);
                    if (locatorIndex == null)
                        return (error, result: (AssetAddComponentResult)null);
                    var go = locatorIndex.Resolve(gameObjectPath, out error);
                    if (go == null) return (error, result: (AssetAddComponentResult)null);
                    var locator = locatorIndex.GetLocator(go, out error);
                    if (locator == null)
                        return (error, result: (AssetAddComponentResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (AssetAddComponentResult)null);

                    var comp = ObjectFactory.AddComponent(go, type);
                    if (comp == null)
                        return (error: $"Failed to add component '{typeName}' to '{go.name}'", result: (AssetAddComponentResult)null);

                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetAddComponentResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = locator,
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

        [CommandAction(
            "prefab",
            "asset_remove_component",
            editorOnly: true,
            summary: "Remove a component from a GameObject in a prefab asset",
            resultType: typeof(AssetRemoveComponentResult))]
        private static CommandResponse AssetRemoveComponent(
            [CommandArgument(NonEmpty = true)] string assetPath,
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "",
            [CommandArgument(Minimum = 0)] int index = 0)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_remove_component");
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for prefab/asset_remove_component");

            return CommandHelpers.RunCommand<AssetRemoveComponentResult>(
                () =>
                {
                    var locatorIndex = LoadPrefabLocatorIndex(
                        assetPath,
                        out var root,
                        out var error);
                    if (locatorIndex == null)
                        return (error, result: (AssetRemoveComponentResult)null);
                    var go = locatorIndex.Resolve(gameObjectPath, out error);
                    if (go == null) return (error, result: (AssetRemoveComponentResult)null);
                    var locator = locatorIndex.GetLocator(go, out error);
                    if (locator == null)
                        return (error, result: (AssetRemoveComponentResult)null);

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
                        gameObjectPath = locator,
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

        [CommandAction(
            "prefab",
            "asset_modify_gameobject",
            editorOnly: true,
            summary: "Modify a GameObject's properties in a prefab asset",
            resultType: typeof(AssetModifyGameObjectResult))]
        [CommandRule(
            CommandRuleKind.AtLeastOneMutation,
            "name",
            "tag",
            "layer",
            "active",
            "isStatic")]
        private static CommandResponse AssetModifyGameObject(
            [CommandArgument(NonEmpty = true)] string assetPath,
            string gameObjectPath = "",
            string name = "",
            string tag = "",
            [CommandArgument(Minimum = 0, Maximum = 31)] int? layer = null,
            bool? active = null,
            bool? isStatic = null)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_modify_gameobject");

            return CommandHelpers.RunCommand<AssetModifyGameObjectResult>(
                () =>
                {
                    var locatorIndex = LoadPrefabLocatorIndex(
                        assetPath,
                        out var root,
                        out var error);
                    if (locatorIndex == null)
                        return (error, result: (AssetModifyGameObjectResult)null);
                    var go = locatorIndex.Resolve(gameObjectPath, out error);
                    if (go == null) return (error, result: (AssetModifyGameObjectResult)null);
                    var locator = locatorIndex.GetLocator(go, out error);
                    if (locator == null)
                        return (error, result: (AssetModifyGameObjectResult)null);

                    if (!string.IsNullOrEmpty(name)) go.name = name;
                    if (!string.IsNullOrEmpty(tag)) go.tag = tag;
                    if (layer.HasValue) go.layer = layer.Value;
                    if (active.HasValue) go.SetActive(active.Value);
                    if (isStatic.HasValue) go.isStatic = isStatic.Value;

                    PrefabUtility.SavePrefabAsset(root);

                    return (error: (string)null, result: new AssetModifyGameObjectResult
                    {
                        assetPath = assetPath,
                        gameObjectPath = locator,
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

        [CommandAction(
            "prefab",
            "asset_add_gameobject",
            editorOnly: true,
            summary: "Add a child GameObject to a prefab asset",
            resultType: typeof(AssetAddGameObjectResult))]
        private static CommandResponse AssetAddGameObject(
            [CommandArgument(NonEmpty = true)] string assetPath,
            string parentPath = "",
            string name = "")
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_add_gameobject");

            var instanceRoot = InstantiatePrefabForAssetMutation(
                assetPath,
                out var previewScene,
                out var assetRoot,
                out var error);
            if (instanceRoot == null)
                return CommandResponseFactory.ValidationError(error);

            try
            {
                var assetIndex = PrefabLocatorIndex.Create(
                    assetRoot,
                    assetPath,
                    out error);
                if (assetIndex == null)
                    return CommandResponseFactory.ValidationError(error);

                var instanceIndex = PrefabLocatorIndex.Create(
                    instanceRoot,
                    assetPath,
                    out error);
                if (instanceIndex == null)
                    return CommandResponseFactory.ValidationError(error);

                if (!TryResolvePrefabMutationPair(
                    assetIndex,
                    instanceIndex,
                    parentPath,
                    out var assetParent,
                    out var instanceParent,
                    out error))
                {
                    return CommandResponseFactory.ValidationError(error);
                }

                if (!assetIndex.TryGetLocalId(assetParent, out var parentLocalId))
                {
                    return CommandResponseFactory.ValidationError(
                        $"Cannot read the parent identity in prefab '{assetPath}'");
                }

                var beforeLocalIds = assetIndex.CopyLocalIds();
                var beforeSiblingIds = assetIndex.GetDirectChildIds(
                    assetParent,
                    out error);
                if (beforeSiblingIds == null)
                    return CommandResponseFactory.ValidationError(error);

                var expectedName = string.IsNullOrEmpty(name)
                    ? "GameObject"
                    : name;
                var child = new GameObject(expectedName);
                child.transform.SetParent(instanceParent.transform, false);
                if (EditorUtility.IsPersistent(child) ||
                    !PrefabUtility.IsAddedGameObjectOverride(child))
                {
                    return CommandResponseFactory.ValidationError(
                        "Failed to create an added GameObject override on the isolated prefab instance");
                }

                Exception applyException = null;
                try
                {
                    PrefabUtility.ApplyAddedGameObject(
                        child,
                        assetPath,
                        InteractionMode.AutomatedAction);
                }
                catch (Exception exception)
                {
                    applyException = exception;
                }

                try
                {
                    var afterIndex = ReloadPrefabLocatorIndex(
                        assetPath,
                        out _,
                        out error);
                    if (afterIndex == null)
                    {
                        return PrefabStateUncertain(
                            "Add GameObject",
                            assetPath,
                            error,
                            applyException);
                    }

                    var afterLocalIds = afterIndex.CopyLocalIds();
                    var addedLocalIds = new HashSet<long>(afterLocalIds);
                    addedLocalIds.ExceptWith(beforeLocalIds);
                    var missingLocalIds = new HashSet<long>(beforeLocalIds);
                    missingLocalIds.ExceptWith(afterLocalIds);

                    var parentExists = afterIndex.TryGetGameObject(
                        parentLocalId,
                        out var afterParent);
                    var afterSiblingIds = parentExists
                        ? afterIndex.GetDirectChildIds(afterParent, out error)
                        : null;
                    if (parentExists && afterSiblingIds == null)
                    {
                        return PrefabStateUncertain(
                            "Add GameObject",
                            assetPath,
                            error,
                            applyException);
                    }

                    GameObject addedGameObject = null;
                    long addedLocalId = 0;
                    foreach (var localId in addedLocalIds)
                    {
                        addedLocalId = localId;
                        afterIndex.TryGetGameObject(localId, out addedGameObject);
                    }

                    var addedUnderExpectedParent = false;
                    if (addedGameObject != null &&
                        addedGameObject.transform.parent != null &&
                        afterIndex.TryGetLocalId(
                            addedGameObject.transform.parent.gameObject,
                            out var addedParentLocalId))
                    {
                        addedUnderExpectedParent =
                            addedParentLocalId == parentLocalId;
                    }

                    var siblingsWithoutAdded = addedLocalIds.Count == 1 &&
                        afterSiblingIds != null
                            ? RemoveId(afterSiblingIds, addedLocalId)
                            : null;
                    var confirmedSuccess =
                        addedLocalIds.Count == 1 &&
                        missingLocalIds.Count == 0 &&
                        parentExists &&
                        addedGameObject != null &&
                        addedUnderExpectedParent &&
                        string.Equals(
                            addedGameObject.name,
                            expectedName,
                            StringComparison.Ordinal) &&
                        IdSequenceEquals(
                            siblingsWithoutAdded,
                            beforeSiblingIds);

                    if (confirmedSuccess)
                    {
                        var persistedLocator = afterIndex.GetLocator(
                            addedGameObject,
                            out error);
                        if (persistedLocator == null)
                        {
                            return PrefabStateUncertain(
                                "Add GameObject",
                                assetPath,
                                error,
                                applyException);
                        }

                        var result = new AssetAddGameObjectResult
                        {
                            assetPath = assetPath,
                            gameObjectPath = persistedLocator,
                            name = addedGameObject.name
                        };
                        return CommandResponseFactory.Ok(
                            $"Added '{result.name}' to prefab",
                            JsonUtility.ToJson(result));
                    }

                    var confirmedNoChange =
                        afterLocalIds.SetEquals(beforeLocalIds) &&
                        parentExists &&
                        IdSequenceEquals(afterSiblingIds, beforeSiblingIds);
                    if (confirmedNoChange)
                    {
                        return CommandResponseFactory.ValidationError(
                            $"Failed to add '{expectedName}' to prefab '{assetPath}'; "
                            + "the prefab asset remained unchanged."
                            + ApplyFailureSuffix(applyException));
                    }

                    return PrefabStateUncertain(
                        "Add GameObject",
                        assetPath,
                        $"expected exactly one new child under parent local ID {parentLocalId}, "
                        + $"but observed {addedLocalIds.Count} added and "
                        + $"{missingLocalIds.Count} missing GameObject ID(s)",
                        applyException);
                }
                catch (Exception exception)
                {
                    return PrefabStateUncertain(
                        "Add GameObject",
                        assetPath,
                        $"reconciliation failed: {exception.Message}",
                        applyException);
                }
            }
            finally
            {
                ClosePrefabMutationScene(previewScene);
            }
        }

        // ── asset_remove_gameobject ──

        [Serializable]
        private sealed class AssetRemoveGameObjectResult
        {
            public string assetPath = "";
            public string gameObjectPath = "";
            public bool removed;
        }

        [CommandAction(
            "prefab",
            "asset_remove_gameobject",
            editorOnly: true,
            summary: "Remove a child GameObject from a prefab asset",
            resultType: typeof(AssetRemoveGameObjectResult))]
        private static CommandResponse AssetRemoveGameObject(
            [CommandArgument(NonEmpty = true)] string assetPath,
            [CommandArgument(NonEmpty = true)] string gameObjectPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for prefab/asset_remove_gameobject");
            if (string.IsNullOrEmpty(gameObjectPath))
                return CommandResponseFactory.ValidationError("gameObjectPath is required for prefab/asset_remove_gameobject (cannot remove root)");

            var instanceRoot = InstantiatePrefabForAssetMutation(
                assetPath,
                out var previewScene,
                out var assetRoot,
                out var error);
            if (instanceRoot == null)
                return CommandResponseFactory.ValidationError(error);

            try
            {
                var assetIndex = PrefabLocatorIndex.Create(
                    assetRoot,
                    assetPath,
                    out error);
                if (assetIndex == null)
                    return CommandResponseFactory.ValidationError(error);

                var instanceIndex = PrefabLocatorIndex.Create(
                    instanceRoot,
                    assetPath,
                    out error);
                if (instanceIndex == null)
                    return CommandResponseFactory.ValidationError(error);

                if (!TryResolvePrefabMutationPair(
                    assetIndex,
                    instanceIndex,
                    gameObjectPath,
                    out var assetGameObject,
                    out var instanceGameObject,
                    out error))
                {
                    return CommandResponseFactory.ValidationError(error);
                }

                if (assetGameObject == assetRoot)
                {
                    return CommandResponseFactory.ValidationError(
                        "Cannot remove the root GameObject of a prefab asset");
                }

                var instanceParent = instanceGameObject.transform.parent;
                var assetParent = assetGameObject.transform.parent;
                if (instanceParent == null || assetParent == null)
                {
                    return CommandResponseFactory.ValidationError(
                        "Cannot remove the root GameObject of a prefab asset");
                }

                if (!assetIndex.TryGetLocalId(
                        assetGameObject,
                        out var targetLocalId) ||
                    !assetIndex.TryGetLocalId(
                        assetParent.gameObject,
                        out var parentLocalId))
                {
                    return CommandResponseFactory.ValidationError(
                        $"Cannot read removal identities in prefab '{assetPath}'");
                }

                var originalLocator = assetIndex.GetLocator(
                    assetGameObject,
                    out error);
                if (originalLocator == null)
                    return CommandResponseFactory.ValidationError(error);

                var beforeLocalIds = assetIndex.CopyLocalIds();
                var removedSubtreeIds = assetIndex.GetSubtreeIds(
                    assetGameObject,
                    out error);
                if (removedSubtreeIds == null)
                    return CommandResponseFactory.ValidationError(error);

                var beforeSiblingIds = assetIndex.GetDirectChildIds(
                    assetParent.gameObject,
                    out error);
                if (beforeSiblingIds == null)
                    return CommandResponseFactory.ValidationError(error);

                var expectedLocalIds = new HashSet<long>(beforeLocalIds);
                expectedLocalIds.ExceptWith(removedSubtreeIds);
                var expectedSiblingIds = RemoveId(
                    beforeSiblingIds,
                    targetLocalId);

                UnityEngine.Object.DestroyImmediate(instanceGameObject);

                Exception applyException = null;
                try
                {
                    PrefabUtility.ApplyRemovedGameObject(
                        instanceParent.gameObject,
                        assetGameObject,
                        InteractionMode.AutomatedAction);
                }
                catch (Exception exception)
                {
                    applyException = exception;
                }

                try
                {
                    var afterIndex = ReloadPrefabLocatorIndex(
                        assetPath,
                        out _,
                        out error);
                    if (afterIndex == null)
                    {
                        return PrefabStateUncertain(
                            "Remove GameObject",
                            assetPath,
                            error,
                            applyException);
                    }

                    var afterLocalIds = afterIndex.CopyLocalIds();
                    var targetExists = afterIndex.TryGetGameObject(
                        targetLocalId,
                        out _);
                    var parentExists = afterIndex.TryGetGameObject(
                        parentLocalId,
                        out var afterParent);
                    var afterSiblingIds = parentExists
                        ? afterIndex.GetDirectChildIds(afterParent, out error)
                        : null;
                    if (parentExists && afterSiblingIds == null)
                    {
                        return PrefabStateUncertain(
                            "Remove GameObject",
                            assetPath,
                            error,
                            applyException);
                    }

                    var confirmedSuccess =
                        !targetExists &&
                        parentExists &&
                        afterLocalIds.SetEquals(expectedLocalIds) &&
                        IdSequenceEquals(
                            afterSiblingIds,
                            expectedSiblingIds);
                    if (confirmedSuccess)
                    {
                        var result = new AssetRemoveGameObjectResult
                        {
                            assetPath = assetPath,
                            gameObjectPath = originalLocator,
                            removed = true
                        };
                        return CommandResponseFactory.Ok(
                            $"Removed '{result.gameObjectPath}' from prefab",
                            JsonUtility.ToJson(result));
                    }

                    var confirmedNoChange =
                        targetExists &&
                        afterLocalIds.SetEquals(beforeLocalIds) &&
                        parentExists &&
                        IdSequenceEquals(
                            afterSiblingIds,
                            beforeSiblingIds);
                    if (confirmedNoChange)
                    {
                        return CommandResponseFactory.ValidationError(
                            $"Failed to remove '{originalLocator}' from prefab '{assetPath}'; "
                            + "the prefab asset remained unchanged."
                            + ApplyFailureSuffix(applyException));
                    }

                    var unexpectedIds = new HashSet<long>(afterLocalIds);
                    unexpectedIds.ExceptWith(expectedLocalIds);
                    var missingUnrelatedIds = new HashSet<long>(expectedLocalIds);
                    missingUnrelatedIds.ExceptWith(afterLocalIds);
                    return PrefabStateUncertain(
                        "Remove GameObject",
                        assetPath,
                        $"target local ID {targetLocalId} present={targetExists}, "
                        + $"parent local ID {parentLocalId} present={parentExists}, "
                        + $"{unexpectedIds.Count} unexpected and "
                        + $"{missingUnrelatedIds.Count} unrelated missing GameObject ID(s)",
                        applyException);
                }
                catch (Exception exception)
                {
                    return PrefabStateUncertain(
                        "Remove GameObject",
                        assetPath,
                        $"reconciliation failed: {exception.Message}",
                        applyException);
                }
            }
            finally
            {
                ClosePrefabMutationScene(previewScene);
            }
        }
#endif
    }
}
