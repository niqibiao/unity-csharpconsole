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
        [Serializable]
        private sealed class PropertyInfo
        {
            public string name = "";
            public string type = "";
            public string value = "";
        }

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
            public PropertyInfo[] properties = Array.Empty<PropertyInfo>();
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
                    var props = new List<PropertyInfo>();
                    var iter = so.GetIterator();
                    const int maxProperties = 200;

                    if (iter.NextVisible(true))
                    {
                        do
                        {
                            if (props.Count >= maxProperties) break;
                            props.Add(new PropertyInfo
                            {
                                name = iter.name,
                                type = iter.propertyType.ToString(),
                                value = SerializedPropertyToString(iter)
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

        [Serializable]
        private sealed class FieldPair
        {
            public string name = "";
            public string value = "";
        }

        [Serializable]
        private sealed class ModifyResult
        {
            public int gameObjectInstanceId;
            public string typeName = "";
            public string[] modifiedFields = Array.Empty<string>();
        }

        [CommandAction("component", "modify", editorOnly: true, summary: "Modify serialized fields of a component")]
        private static CommandResponse Modify(FieldPair[] fields, string typeName, string gameObjectPath = "", int gameObjectInstanceId = 0, int index = 0)
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

                        if (TrySetSerializedProperty(prop, field.value))
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

        // ── Helpers ──

        private static string SerializedPropertyToString(SerializedProperty prop)
        {
            return prop.propertyType switch
            {
                SerializedPropertyType.Integer => prop.intValue.ToString(),
                SerializedPropertyType.Boolean => prop.boolValue.ToString(),
                SerializedPropertyType.Float => prop.floatValue.ToString("R"),
                SerializedPropertyType.String => prop.stringValue ?? "",
                SerializedPropertyType.Color => $"({prop.colorValue.r},{prop.colorValue.g},{prop.colorValue.b},{prop.colorValue.a})",
                SerializedPropertyType.Vector2 => $"({prop.vector2Value.x},{prop.vector2Value.y})",
                SerializedPropertyType.Vector3 => $"({prop.vector3Value.x},{prop.vector3Value.y},{prop.vector3Value.z})",
                SerializedPropertyType.Vector4 => $"({prop.vector4Value.x},{prop.vector4Value.y},{prop.vector4Value.z},{prop.vector4Value.w})",
                SerializedPropertyType.Enum => prop.enumNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length
                    ? prop.enumNames[prop.enumValueIndex]
                    : prop.enumValueIndex.ToString(),
                SerializedPropertyType.ObjectReference => prop.objectReferenceValue != null
                    ? $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetInstanceID()})"
                    : "null",
                SerializedPropertyType.Rect => $"({prop.rectValue.x},{prop.rectValue.y},{prop.rectValue.width},{prop.rectValue.height})",
                SerializedPropertyType.Bounds => $"center({prop.boundsValue.center.x},{prop.boundsValue.center.y},{prop.boundsValue.center.z}) size({prop.boundsValue.size.x},{prop.boundsValue.size.y},{prop.boundsValue.size.z})",
                SerializedPropertyType.LayerMask => prop.intValue.ToString(),
                _ => $"<{prop.propertyType}>"
            };
        }

        private static bool TrySetSerializedProperty(SerializedProperty prop, string rawValue)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        if (int.TryParse(rawValue, out var intVal)) { prop.intValue = intVal; return true; }
                        break;
                    case SerializedPropertyType.Float:
                        if (float.TryParse(rawValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatVal)) { prop.floatValue = floatVal; return true; }
                        break;
                    case SerializedPropertyType.Boolean:
                        if (bool.TryParse(rawValue, out var boolVal)) { prop.boolValue = boolVal; return true; }
                        break;
                    case SerializedPropertyType.String:
                        prop.stringValue = rawValue;
                        return true;
                    case SerializedPropertyType.Enum:
                        if (int.TryParse(rawValue, out var enumIdx)) { prop.enumValueIndex = enumIdx; return true; }
                        if (prop.enumNames != null)
                        {
                            for (var ei = 0; ei < prop.enumNames.Length; ei++)
                            {
                                if (string.Equals(prop.enumNames[ei], rawValue, StringComparison.OrdinalIgnoreCase))
                                {
                                    prop.enumValueIndex = ei;
                                    return true;
                                }
                            }
                        }
                        break;
                    case SerializedPropertyType.Color:
                        var color = JsonUtility.FromJson<Color>(rawValue);
                        prop.colorValue = color;
                        return true;
                    case SerializedPropertyType.Vector2:
                        var v2 = JsonUtility.FromJson<Vector2>(rawValue);
                        prop.vector2Value = v2;
                        return true;
                    case SerializedPropertyType.Vector3:
                        var v3 = JsonUtility.FromJson<Vector3>(rawValue);
                        prop.vector3Value = v3;
                        return true;
                    case SerializedPropertyType.Vector4:
                        var v4 = JsonUtility.FromJson<Vector4>(rawValue);
                        prop.vector4Value = v4;
                        return true;
                    case SerializedPropertyType.ObjectReference:
                        if (int.TryParse(rawValue, out var objId))
                        {
                            prop.objectReferenceValue = EditorUtility.InstanceIDToObject(objId);
                            return true;
                        }
                        break;
                }
            }
            catch
            {
                // Ignore parse failures for individual fields
            }

            return false;
        }
#endif
    }
}
