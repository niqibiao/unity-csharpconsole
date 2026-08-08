using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class TransformCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(TransformCommandActions));
#endif
        }

#if UNITY_EDITOR
        [Serializable]
        private sealed class SetResult
        {
            public int instanceId;
            public string path = "";
            public Vector3 localPosition;
            public Vector3 localEulerAngles;
            public Vector3 localScale;
        }

        [CommandAction(
            "transform",
            "set",
            editorOnly: true,
            summary: "Set a GameObject's transform values",
            resultType: typeof(SetResult))]
        [CommandRule(CommandRuleKind.ExactlyOneOf, "path", "instanceId")]
        [CommandRule(
            CommandRuleKind.AtLeastOneMutation,
            "position",
            "rotation",
            "scale")]
        private static CommandResponse Set(
            string path = "",
            int instanceId = 0,
            Vector3? position = null,
            Vector3? rotation = null,
            Vector3? scale = null,
            bool local = true)
        {
            return CommandHelpers.RunCommand<SetResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
                    if (go == null) return (error, result: (SetResult)null);

                    var t = go.transform;

                    Undo.RecordObject(t, "Set Transform");

                    if (position.HasValue)
                    {
                        if (local) t.localPosition = position.Value;
                        else t.position = position.Value;
                    }

                    if (rotation.HasValue)
                    {
                        if (local) t.localEulerAngles = rotation.Value;
                        else t.eulerAngles = rotation.Value;
                    }

                    if (scale.HasValue)
                    {
                        t.localScale = scale.Value;
                    }

                    return (error: (string)null, result: new SetResult
                    {
                        instanceId = go.GetInstanceID(),
                        path = CommandHelpers.GetHierarchyPath(t),
                        localPosition = t.localPosition,
                        localEulerAngles = t.localEulerAngles,
                        localScale = t.localScale
                    });
                },
                r => $"Set transform for '{r.path}'"
            );
        }
#endif
    }
}
