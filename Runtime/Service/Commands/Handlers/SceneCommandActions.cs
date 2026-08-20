using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    // Reading the loaded scene tree is plain UnityEngine work, so this reports
    // the live hierarchy of a player just as well as the editor's.
    internal static class SceneCommandActions
    {
        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(SceneCommandActions));
        }

        [Serializable]
        private sealed class HierarchyNode
        {
            public int instanceId;
            public string name = "";
            public bool activeSelf;
            public int childCount;
            public string[] components = Array.Empty<string>();
            public HierarchyNode[] children = Array.Empty<HierarchyNode>();
        }

        [Serializable]
        private sealed class HierarchyResult
        {
            public string sceneName = "";
            public string scenePath = "";
            public HierarchyNode[] roots = Array.Empty<HierarchyNode>();
            public HierarchyNode[] dontDestroyOnLoadRoots = Array.Empty<HierarchyNode>();
        }

        [CommandAction(
            "scene",
            "hierarchy",
            summary: "Get the full scene hierarchy tree",
            resultType: typeof(HierarchyResult))]
        private static CommandResponse Hierarchy(
            [CommandArgument(Minimum = -1)] int depth = -1,
            bool includeComponents = false)
        {
            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();
            var nodeCount = 0;
            const int maxNodes = 5000;

            var roots = new List<HierarchyNode>();
            foreach (var root in rootObjects)
            {
                if (nodeCount >= maxNodes) break;
                roots.Add(BuildNode(root.transform, depth, 0, includeComponents, ref nodeCount, maxNodes));
            }

            // Include DontDestroyOnLoad scene objects
            var ddolRoots = new List<HierarchyNode>();
            foreach (var ddolRoot in CommandHelpers.GetDontDestroyOnLoadRootObjects())
            {
                if (nodeCount >= maxNodes) break;
                ddolRoots.Add(BuildNode(ddolRoot.transform, depth, 0, includeComponents, ref nodeCount, maxNodes));
            }

            var result = new HierarchyResult
            {
                sceneName = scene.name,
                scenePath = scene.path ?? "",
                roots = roots.ToArray(),
                dontDestroyOnLoadRoots = ddolRoots.ToArray()
            };

            var ddolSuffix = ddolRoots.Count > 0 ? $" + {ddolRoots.Count} DontDestroyOnLoad root(s)" : "";
            return CommandResponseFactory.Ok($"Hierarchy of '{result.sceneName}' ({nodeCount} nodes{ddolSuffix})", JsonUtility.ToJson(result));
        }

        private static HierarchyNode BuildNode(Transform t, int maxDepth, int currentDepth, bool includeComponents, ref int nodeCount, int maxNodes)
        {
            nodeCount++;
            var go = t.gameObject;
            var node = new HierarchyNode
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
                var children = new List<HierarchyNode>(t.childCount);
                for (var i = 0; i < t.childCount && nodeCount < maxNodes; i++)
                {
                    children.Add(BuildNode(t.GetChild(i), maxDepth, currentDepth + 1, includeComponents, ref nodeCount, maxNodes));
                }

                node.children = children.ToArray();
            }

            return node;
        }
    }
}
