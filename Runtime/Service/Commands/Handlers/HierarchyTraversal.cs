using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    // Scene and prefab hierarchies share traversal limits and JSON tree framing.
    // Explicit stacks avoid both JsonUtility's depth limit and recursive calls.
    internal sealed class HierarchyTraversal<T>
    {
        private const int MaxNodes = 5000;
        // Keep the nested response within ordinary JSON client recursion limits.
        private const int MaxDepth = 128;
        private readonly int _maxDepth;
        private readonly Func<Transform, T> _create;
        private readonly Action<T, T[]> _setChildren;
        private bool _depthLimited;
        private bool _nodeLimited;
        private bool _safetyDepthLimited;

        internal int NodeCount { get; private set; }
        internal bool Truncated => _depthLimited || _nodeLimited || _safetyDepthLimited;
        internal string[] TruncationReasons
        {
            get
            {
                var reasons = new List<string>(3);
                if (_depthLimited) reasons.Add("requested_depth");
                if (_safetyDepthLimited) reasons.Add("depth_limit");
                if (_nodeLimited) reasons.Add("node_limit");
                return reasons.ToArray();
            }
        }

        internal HierarchyTraversal(int maxDepth, Func<Transform, T> create, Action<T, T[]> setChildren)
        {
            _maxDepth = maxDepth;
            _create = create;
            _setChildren = setChildren;
        }

        internal T[] Build(GameObject[] roots)
        {
            var result = new List<T>();
            foreach (var root in roots)
            {
                if (NodeCount >= MaxNodes)
                {
                    _nodeLimited = true;
                    break;
                }

                var node = _create(root.transform);
                NodeCount++;
                result.Add(node);
                var stack = new Stack<(Transform source, T node, int depth, List<T> children)>();
                stack.Push((root.transform, node, 0, new List<T>()));
                while (stack.Count > 0)
                {
                    var frame = stack.Peek();
                    if (frame.children.Count < frame.source.childCount)
                    {
                        if (_maxDepth >= 0 && frame.depth >= _maxDepth)
                            _depthLimited = true;
                        else if (frame.depth >= MaxDepth)
                            _safetyDepthLimited = true;
                        else if (NodeCount >= MaxNodes)
                            _nodeLimited = true;
                        else
                        {
                            var child = frame.source.GetChild(frame.children.Count);
                            var childNode = _create(child);
                            NodeCount++;
                            frame.children.Add(childNode);
                            stack.Push((child, childNode, frame.depth + 1, new List<T>()));
                            continue;
                        }
                    }

                    _setChildren(frame.node, frame.children.ToArray());
                    stack.Pop();
                }
            }
            return result.ToArray();
        }

        internal static void WriteNodes(StringBuilder json, T[] nodes,
            Action<StringBuilder, T> writeFields, Func<T, T[]> children)
        {
            var stack = new Stack<(T[] nodes, int index)>();
            stack.Push((nodes, 0));
            json.Append('[');
            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame.index == frame.nodes.Length)
                {
                    json.Append(']');
                    if (stack.Count > 0) json.Append('}');
                    continue;
                }
                if (frame.index > 0) json.Append(',');
                var node = frame.nodes[frame.index];
                json.Append('{');
                writeFields(json, node);
                json.Append(",\"children\":[");
                stack.Push((frame.nodes, frame.index + 1));
                stack.Push((children(node), 0));
            }
        }
    }
}
