using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class GameObjectCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(GameObjectCommandActions));
#endif
        }

#if UNITY_EDITOR
        [Serializable]
        private sealed class GameObjectInfo
        {
            public int instanceId;
            public string name = "";
            public string path = "";
            public string tag = "";
            public string layer = "";
            public bool activeSelf;
        }

        [Serializable]
        private sealed class TransformInfo
        {
            public Vector3 localPosition;
            public Vector3 localRotation;
            public Vector3 localScale;
            public Vector3 position;
            public Vector3 rotation;
        }

        [Serializable]
        private sealed class ComponentInfo
        {
            public string typeName = "";
            public int instanceId;
            public bool enabled;
        }

        // ── find ──

        [Serializable]
        private sealed class FindResult
        {
            public GameObjectInfo[] gameObjects = Array.Empty<GameObjectInfo>();
        }

        [CommandAction("gameobject", "find", editorOnly: true, summary: "Find GameObjects by name, tag, or component type")]
        private static CommandResponse Find(string name = "", string tag = "", string componentType = "")
        {
            return CommandHelpers.RunCommand<FindResult>(
                () =>
                {
                    Type componentFilter = null;
                    if (!string.IsNullOrEmpty(componentType))
                    {
                        componentFilter = CommandHelpers.ResolveType(componentType, out var typeError);
                        if (componentFilter == null)
                            return (error: typeError, result: (FindResult)null);
                    }

                    const int maxResults = 500;
                    var collected = new List<GameObjectInfo>();

                    for (var s = 0; s < SceneManager.sceneCount && collected.Count < maxResults; s++)
                    {
                        var scene = SceneManager.GetSceneAt(s);
                        if (!scene.IsValid() || !scene.isLoaded) continue;
                        var roots = scene.GetRootGameObjects();
                        foreach (var root in roots)
                        {
                            CollectMatching(root.transform, name, tag, componentFilter, collected, maxResults);
                            if (collected.Count >= maxResults) break;
                        }
                    }

                    return (error: (string)null, result: new FindResult { gameObjects = collected.ToArray() });
                },
                r => $"Found {r.gameObjects.Length} GameObject(s)"
            );
        }

        private static void CollectMatching(Transform current, string nameFilter, string tagFilter, Type componentFilter, List<GameObjectInfo> collected, int max)
        {
            if (collected.Count >= max)
            {
                return;
            }

            var go = current.gameObject;
            var match = !(!string.IsNullOrEmpty(nameFilter) && go.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0);

            if (match && !string.IsNullOrEmpty(tagFilter))
            {
                try { if (!go.CompareTag(tagFilter)) match = false; }
                catch { match = false; }
            }

            if (match && componentFilter != null && go.GetComponent(componentFilter) == null)
            {
                match = false;
            }

            if (match)
            {
                collected.Add(new GameObjectInfo
                {
                    instanceId = go.GetInstanceID(),
                    name = go.name,
                    path = CommandHelpers.GetHierarchyPath(current),
                    tag = go.tag,
                    layer = LayerMask.LayerToName(go.layer),
                    activeSelf = go.activeSelf
                });
            }

            for (var i = 0; i < current.childCount && collected.Count < max; i++)
            {
                CollectMatching(current.GetChild(i), nameFilter, tagFilter, componentFilter, collected, max);
            }
        }

        // ── create ──

        [Serializable]
        private sealed class CreateResult
        {
            public int instanceId;
            public string name = "";
            public string path = "";
        }

        [CommandAction("gameobject", "create", editorOnly: true, summary: "Create a new GameObject (empty or primitive)")]
        private static CommandResponse Create(string name = "", string primitiveType = "", string parentPath = "")
        {
            return CommandHelpers.RunCommand<CreateResult>(
                () =>
                {
                    GameObject go;
                    if (!string.IsNullOrEmpty(primitiveType))
                    {
                        if (!Enum.TryParse<PrimitiveType>(primitiveType, true, out var pt))
                            return (error: $"Invalid primitiveType '{primitiveType}'. Valid: Cube, Sphere, Capsule, Cylinder, Plane, Quad", result: (CreateResult)null);

                        go = GameObject.CreatePrimitive(pt);
                        if (!string.IsNullOrEmpty(name)) go.name = name;
                    }
                    else
                    {
                        go = new GameObject(string.IsNullOrEmpty(name) ? "GameObject" : name);
                    }

                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        var parent = CommandHelpers.FindByPath(parentPath);
                        if (parent == null)
                        {
                            UnityEngine.Object.DestroyImmediate(go);
                            return (error: $"Parent not found at path '{parentPath}'", result: (CreateResult)null);
                        }

                        go.transform.SetParent(parent.transform, false);
                    }

                    Undo.RegisterCreatedObjectUndo(go, "Create GameObject");

                    return (error: (string)null, result: new CreateResult
                    {
                        instanceId = go.GetInstanceID(),
                        name = go.name,
                        path = CommandHelpers.GetHierarchyPath(go.transform)
                    });
                },
                r => $"Created '{r.name}'"
            );
        }

        // ── destroy ──

        [Serializable]
        private sealed class DestroyResult
        {
            public int instanceId;
            public string name = "";
            public bool destroyed;
        }

        [CommandAction("gameobject", "destroy", editorOnly: true, summary: "Destroy a GameObject")]
        private static CommandResponse Destroy(string path = "", int instanceId = 0)
        {
            return CommandHelpers.RunCommand<DestroyResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
                    if (go == null) return (error, result: (DestroyResult)null);

                    var r = new DestroyResult
                    {
                        instanceId = go.GetInstanceID(),
                        name = go.name,
                        destroyed = true
                    };
                    Undo.DestroyObjectImmediate(go);
                    return (error: (string)null, result: r);
                },
                r => $"Destroyed '{r.name}'"
            );
        }

        // ── get ──

        [Serializable]
        private sealed class GetResult
        {
            public int instanceId;
            public string name = "";
            public string path = "";
            public string tag = "";
            public int layer;
            public bool activeSelf;
            public bool activeInHierarchy;
            public TransformInfo transform = new();
            public ComponentInfo[] components = Array.Empty<ComponentInfo>();
        }

        [CommandAction("gameobject", "get", editorOnly: true, summary: "Get detailed info about a GameObject")]
        private static CommandResponse Get(string path = "", int instanceId = 0)
        {
            return CommandHelpers.RunCommand<GetResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
                    if (go == null) return (error, result: (GetResult)null);

                    var t = go.transform;
                    var transformInfo = new TransformInfo
                    {
                        localPosition = t.localPosition,
                        localRotation = t.localEulerAngles,
                        localScale = t.localScale,
                        position = t.position,
                        rotation = t.eulerAngles
                    };

                    var comps = go.GetComponents<Component>();
                    var compInfos = new List<ComponentInfo>();
                    foreach (var comp in comps)
                    {
                        if (comp == null) continue;
                        compInfos.Add(new ComponentInfo
                        {
                            typeName = comp.GetType().Name,
                            instanceId = comp.GetInstanceID(),
                            enabled = comp is Behaviour b ? b.enabled : true
                        });
                    }

                    return (error: (string)null, result: new GetResult
                    {
                        instanceId = go.GetInstanceID(),
                        name = go.name,
                        path = CommandHelpers.GetHierarchyPath(t),
                        tag = go.tag,
                        layer = go.layer,
                        activeSelf = go.activeSelf,
                        activeInHierarchy = go.activeInHierarchy,
                        transform = transformInfo,
                        components = compInfos.ToArray()
                    });
                },
                r => $"Got '{r.name}'"
            );
        }

        // ── modify ──

        [Serializable]
        private sealed class ModifyResult
        {
            public int instanceId;
            public string name = "";
            public string path = "";
        }

        [CommandAction("gameobject", "modify", editorOnly: true, summary: "Modify a GameObject's basic properties")]
        private static CommandResponse Modify(
            string path = "",
            int instanceId = 0,
            string name = "",
            string tag = "",
            int layer = -1,
            int active = -1,
            int isStatic = -1)
        {
            return CommandHelpers.RunCommand<ModifyResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
                    if (go == null) return (error, result: (ModifyResult)null);

                    Undo.RecordObject(go, "Modify GameObject");

                    if (!string.IsNullOrEmpty(name)) go.name = name;
                    if (!string.IsNullOrEmpty(tag)) go.tag = tag;
                    if (layer >= 0) go.layer = layer;
                    if (active >= 0) go.SetActive(active != 0);
                    if (isStatic >= 0) go.isStatic = isStatic != 0;

                    return (error: (string)null, result: new ModifyResult
                    {
                        instanceId = go.GetInstanceID(),
                        name = go.name,
                        path = CommandHelpers.GetHierarchyPath(go.transform)
                    });
                },
                r => $"Modified '{r.name}'"
            );
        }

        // ── set-parent ──

        [Serializable]
        private sealed class SetParentResult
        {
            public int instanceId;
            public string name = "";
            public string newPath = "";
        }

        [CommandAction("gameobject", "set_parent", editorOnly: true, summary: "Change a GameObject's parent")]
        private static CommandResponse SetParent(
            string path = "",
            int instanceId = 0,
            string parentPath = "",
            int parentInstanceId = 0,
            bool worldPositionStays = true)
        {
            return CommandHelpers.RunCommand<SetParentResult>(
                () =>
                {
                    var child = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
                    if (child == null) return (error, result: (SetParentResult)null);

                    Transform parentTransform = null;
                    if (parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
                    {
                        var parent = CommandHelpers.ResolveGameObject(parentPath, parentInstanceId, out var parentError);
                        if (parent == null) return (error: parentError, result: (SetParentResult)null);

                        parentTransform = parent.transform;
                    }

                    if (worldPositionStays)
                    {
                        Undo.SetTransformParent(child.transform, parentTransform, "Set Parent");
                    }
                    else
                    {
                        // Undo.SetTransformParent always uses worldPositionStays=true internally.
                        // Use RecordObject + SetParent(false) so that both undo and redo correctly
                        // preserve the local transform instead of restoring the world-space result.
                        Undo.RecordObject(child.transform, "Set Parent");
                        child.transform.SetParent(parentTransform, false);
                    }

                    return (error: (string)null, result: new SetParentResult
                    {
                        instanceId = child.GetInstanceID(),
                        name = child.name,
                        newPath = CommandHelpers.GetHierarchyPath(child.transform)
                    });
                },
                r => $"Set parent for '{r.name}'"
            );
        }

        // ── duplicate ──

        [Serializable]
        private sealed class DuplicateResult
        {
            public int instanceId;
            public string name = "";
            public string path = "";
        }

        [CommandAction("gameobject", "duplicate", editorOnly: true, summary: "Duplicate a GameObject")]
        private static CommandResponse Duplicate(string path = "", int instanceId = 0, string newName = "")
        {
            return CommandHelpers.RunCommand<DuplicateResult>(
                () =>
                {
                    var source = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
                    if (source == null) return (error, result: (DuplicateResult)null);

                    var copy = UnityEngine.Object.Instantiate(source, source.transform.parent);
                    Undo.RegisterCreatedObjectUndo(copy, "Duplicate GameObject");
                    copy.name = !string.IsNullOrEmpty(newName) ? newName : source.name;

                    return (error: (string)null, result: new DuplicateResult
                    {
                        instanceId = copy.GetInstanceID(),
                        name = copy.name,
                        path = CommandHelpers.GetHierarchyPath(copy.transform)
                    });
                },
                r => $"Duplicated as '{r.name}'"
            );
        }

#endif
    }
}
