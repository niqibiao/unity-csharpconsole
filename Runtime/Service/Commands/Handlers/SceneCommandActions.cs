using System;
using System.Collections.Generic;
using System.Text;
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
            public int nodeCount;
            public bool truncated;
            public string[] truncationReasons = Array.Empty<string>();
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
            var traversal = new HierarchyTraversal<HierarchyNode>(depth,
                t => CreateNode(t, includeComponents), (node, children) => node.children = children);
            var roots = traversal.Build(rootObjects);
            var ddolRoots = traversal.Build(CommandHelpers.GetDontDestroyOnLoadRootObjects());

            var result = new HierarchyResult
            {
                sceneName = scene.name,
                scenePath = scene.path ?? "",
                roots = roots,
                dontDestroyOnLoadRoots = ddolRoots,
                nodeCount = traversal.NodeCount,
                truncated = traversal.Truncated,
                truncationReasons = traversal.TruncationReasons
            };

            var ddolSuffix = ddolRoots.Length > 0 ? $" + {ddolRoots.Length} DontDestroyOnLoad root(s)" : "";
            return CommandResponseFactory.Ok($"Hierarchy of '{result.sceneName}' ({result.nodeCount} nodes{ddolSuffix})", Serialize(result));
        }

        private static HierarchyNode CreateNode(Transform t, bool includeComponents)
        {
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
                    if (c != null) names.Add(c.GetType().FullName);
                }

                node.components = names.ToArray();
            }

            return node;
        }

        private static string Serialize(HierarchyResult result)
        {
            var json = new StringBuilder();
            json.Append("{\"sceneName\":").Append(CommandContractValueEncoder.Quote(result.sceneName));
            json.Append(",\"scenePath\":").Append(CommandContractValueEncoder.Quote(result.scenePath));
            json.Append(",\"nodeCount\":").Append(result.nodeCount);
            json.Append(",\"truncated\":").Append(result.truncated ? "true" : "false");
            json.Append(",\"truncationReasons\":").Append(CommandContractValueEncoder.Encode(result.truncationReasons));
            json.Append(",\"roots\":");
            HierarchyTraversal<HierarchyNode>.WriteNodes(json, result.roots, WriteNodeFields, node => node.children);
            json.Append(",\"dontDestroyOnLoadRoots\":");
            HierarchyTraversal<HierarchyNode>.WriteNodes(json, result.dontDestroyOnLoadRoots, WriteNodeFields, node => node.children);
            return json.Append('}').ToString();
        }

        private static void WriteNodeFields(StringBuilder json, HierarchyNode node)
        {
            json.Append("\"instanceId\":").Append(node.instanceId);
            json.Append(",\"name\":").Append(CommandContractValueEncoder.Quote(node.name));
            json.Append(",\"activeSelf\":").Append(node.activeSelf ? "true" : "false");
            json.Append(",\"childCount\":").Append(node.childCount);
            json.Append(",\"components\":").Append(CommandContractValueEncoder.Encode(node.components));
        }
    }
}
