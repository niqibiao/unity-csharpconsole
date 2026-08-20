using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    // The editor reads and writes components through SerializedObject, which
    // reports Unity's serialized names such as m_LocalPosition. A player has no
    // SerializedObject, so it reflects over fields instead, and that only agrees
    // with the editor for components the project declares -- see
    // CommandHelpers.IsProjectDeclaredComponent. Built-in components are refused
    // there rather than reported in a second, incompatible shape.
    internal static class ComponentCommandActions
    {
        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(ComponentCommandActions));
        }

        // PropertyInfo is shared via CommandHelpers.PropertyInfo

        // ── add ──

        [Serializable]
        private sealed class AddResult
        {
            public int gameObjectInstanceId;
            public string typeName = "";
            public int componentInstanceId;
        }

        [CommandAction(
            "component",
            "add",
            summary: "Add a component to a GameObject",
            resultType: typeof(AddResult))]
        [CommandRule(
            CommandRuleKind.ExactlyOneOf,
            "gameObjectPath",
            "gameObjectInstanceId")]
        private static CommandResponse Add(
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "",
            int gameObjectInstanceId = 0)
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

#if UNITY_EDITOR
                    var comp = ObjectFactory.AddComponent(go, type);
#else
                    var comp = go.AddComponent(type);
#endif
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

        [CommandAction(
            "component",
            "remove",
            summary: "Remove a component from a GameObject",
            resultType: typeof(RemoveResult))]
        [CommandRule(
            CommandRuleKind.ExactlyOneOf,
            "gameObjectPath",
            "gameObjectInstanceId")]
        private static CommandResponse Remove(
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "",
            int gameObjectInstanceId = 0,
            [CommandArgument(Minimum = 0)] int index = 0)
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

#if UNITY_EDITOR
                    Undo.DestroyObjectImmediate(comps[index]);
#else
                    UnityEngine.Object.DestroyImmediate(comps[index]);
#endif

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

        [CommandAction(
            "component",
            "get",
            summary: "Get serialized field data of a component",
            resultType: typeof(GetResult))]
        [CommandRule(
            CommandRuleKind.ExactlyOneOf,
            "gameObjectPath",
            "gameObjectInstanceId")]
        private static CommandResponse Get(
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "",
            int gameObjectInstanceId = 0,
            [CommandArgument(Minimum = 0)] int index = 0)
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
                    var props = new List<CommandHelpers.PropertyInfo>();
                    const int maxProperties = 200;

#if UNITY_EDITOR
                    var so = new SerializedObject(comp);
                    var iter = so.GetIterator();

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
#else
                    if (!CommandHelpers.IsProjectDeclaredComponent(comp))
                    {
                        return (
                            error: $"'{type.Name}' is a built-in component; a player can only report fields of components the project declares",
                            result: (GetResult)null);
                    }

                    foreach (var field in CommandHelpers.GetSerializableFields(comp.GetType()))
                    {
                        if (props.Count >= maxProperties) break;
                        props.Add(new CommandHelpers.PropertyInfo
                        {
                            name = field.Name,
                            type = field.FieldType.Name,
                            value = CommandHelpers.FieldValueToString(field.GetValue(comp))
                        });
                    }
#endif

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

        [CommandAction(
            "component",
            "modify",
            summary: "Modify serialized fields of a component",
            resultType: typeof(ModifyResult))]
        [CommandRule(
            CommandRuleKind.ExactlyOneOf,
            "gameObjectPath",
            "gameObjectInstanceId")]
        private static CommandResponse Modify(
            [CommandArgument(NonEmpty = true)] CommandHelpers.FieldPair[] fields,
            [CommandArgument(NonEmpty = true)] string typeName,
            string gameObjectPath = "",
            int gameObjectInstanceId = 0,
            [CommandArgument(Minimum = 0)] int index = 0)
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
                    var modifiedFields = new List<string>();

#if UNITY_EDITOR
                    var so = new SerializedObject(comp);

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
#else
                    if (!CommandHelpers.IsProjectDeclaredComponent(comp))
                    {
                        return (
                            error: $"'{type.Name}' is a built-in component; a player can only modify fields of components the project declares",
                            result: (ModifyResult)null);
                    }

                    var reflected = CommandHelpers.GetSerializableFields(comp.GetType());
                    foreach (var field in fields)
                    {
                        if (string.IsNullOrEmpty(field.name)) continue;

                        foreach (var target in reflected)
                        {
                            if (!string.Equals(target.Name, field.name, StringComparison.Ordinal)) continue;

                            if (CommandHelpers.TrySetFieldValue(target, comp, field.value))
                            {
                                modifiedFields.Add(field.name);
                            }

                            break;
                        }
                    }
#endif

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
    }
}
