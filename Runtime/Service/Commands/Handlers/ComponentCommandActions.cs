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
    internal static class ComponentCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(ComponentCommandActions));
#endif
        }

#if UNITY_EDITOR
        // PropertyInfo is shared via CommandHelpers.PropertyInfo

        // ── add ──

        [Serializable]
        private sealed class AddResult
        {
            public int gameObjectInstanceId;
            public string typeName = "";
            public int componentInstanceId;
        }

        [CommandAction("component", "add", editorOnly: true, summary: "Add a component to a GameObject")]
        private static CommandResponse Add(string typeName, string gameObjectPath = "", int gameObjectInstanceId = 0)
        {
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for component/add");

            return CommandHelpers.RunCommand<AddResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(gameObjectPath, gameObjectInstanceId, out var error);
                    if (go == null) return (error, result: (AddResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (AddResult)null);

                    var comp = ObjectFactory.AddComponent(go, type);
                    if (comp == null)
                        return (error: $"Failed to add component '{typeName}' to '{go.name}'", result: (AddResult)null);

                    return (error: (string)null, result: new AddResult
                    {
                        gameObjectInstanceId = go.GetInstanceID(),
                        typeName = type.Name,
                        componentInstanceId = comp.GetInstanceID()
                    });
                },
                r => $"Added {r.typeName}"
            );
        }

        // ── remove ──

        [Serializable]
        private sealed class RemoveResult
        {
            public int gameObjectInstanceId;
            public string typeName = "";
            public bool removed;
        }

        [CommandAction("component", "remove", editorOnly: true, summary: "Remove a component from a GameObject")]
        private static CommandResponse Remove(string typeName, string gameObjectPath = "", int gameObjectInstanceId = 0, int index = 0)
        {
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for component/remove");

            return CommandHelpers.RunCommand<RemoveResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(gameObjectPath, gameObjectInstanceId, out var error);
                    if (go == null) return (error, result: (RemoveResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (RemoveResult)null);

                    var comps = go.GetComponents(type);
                    if (comps.Length == 0)
                        return (error: $"No component of type '{typeName}' found on '{go.name}'", result: (RemoveResult)null);

                    if (index < 0 || index >= comps.Length)
                        return (error: $"Component index {index} is out of range (0..{comps.Length - 1}) for type '{typeName}' on '{go.name}'", result: (RemoveResult)null);

                    Undo.DestroyObjectImmediate(comps[index]);

                    return (error: (string)null, result: new RemoveResult
                    {
                        gameObjectInstanceId = go.GetInstanceID(),
                        typeName = type.Name,
                        removed = true
                    });
                },
                r => $"Removed {r.typeName}"
            );
        }

        // ── get ──

        [Serializable]
        private sealed class GetResult
        {
            public int gameObjectInstanceId;
            public string typeName = "";
            public int componentInstanceId;
            public CommandHelpers.PropertyInfo[] properties = Array.Empty<CommandHelpers.PropertyInfo>();
        }

        [CommandAction("component", "get", editorOnly: true, summary: "Get serialized field data of a component")]
        private static CommandResponse Get(string typeName, string gameObjectPath = "", int gameObjectInstanceId = 0, int index = 0)
        {
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for component/get");

            return CommandHelpers.RunCommand<GetResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(gameObjectPath, gameObjectInstanceId, out var error);
                    if (go == null) return (error, result: (GetResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (GetResult)null);

                    var comps = go.GetComponents(type);
                    if (comps.Length == 0)
                        return (error: $"No component of type '{typeName}' found on '{go.name}'", result: (GetResult)null);

                    if (index < 0 || index >= comps.Length)
                        return (error: $"Component index {index} is out of range (0..{comps.Length - 1}) for type '{typeName}' on '{go.name}'", result: (GetResult)null);

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

                    return (error: (string)null, result: new GetResult
                    {
                        gameObjectInstanceId = go.GetInstanceID(),
                        typeName = type.Name,
                        componentInstanceId = comp.GetInstanceID(),
                        properties = props.ToArray()
                    });
                },
                r => $"Got {r.typeName} ({r.properties.Length} properties)"
            );
        }

        // ── modify ──

        // FieldPair is shared via CommandHelpers.FieldPair

        [Serializable]
        private sealed class ModifyResult
        {
            public int gameObjectInstanceId;
            public string typeName = "";
            public string[] modifiedFields = Array.Empty<string>();
        }

        [CommandAction("component", "modify", editorOnly: true, summary: "Modify serialized fields of a component")]
        private static CommandResponse Modify(CommandHelpers.FieldPair[] fields, string typeName, string gameObjectPath = "", int gameObjectInstanceId = 0, int index = 0)
        {
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for component/modify");

            if (fields == null || fields.Length == 0)
                return CommandResponseFactory.ValidationError("fields array is required for component/modify");

            return CommandHelpers.RunCommand<ModifyResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(gameObjectPath, gameObjectInstanceId, out var error);
                    if (go == null) return (error, result: (ModifyResult)null);

                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (ModifyResult)null);

                    var comps = go.GetComponents(type);
                    if (comps.Length == 0)
                        return (error: $"No component of type '{typeName}' found on '{go.name}'", result: (ModifyResult)null);

                    if (index < 0 || index >= comps.Length)
                        return (error: $"Component index {index} is out of range (0..{comps.Length - 1}) for type '{typeName}' on '{go.name}'", result: (ModifyResult)null);

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

                    return (error: (string)null, result: new ModifyResult
                    {
                        gameObjectInstanceId = go.GetInstanceID(),
                        typeName = type.Name,
                        modifiedFields = modifiedFields.ToArray()
                    });
                },
                r => $"Modified {r.modifiedFields.Length} field(s) on {r.typeName}"
            );
        }

        // SerializedProperty helpers moved to CommandHelpers.SerializedPropertyToString / TrySetSerializedProperty
#endif
    }
}
