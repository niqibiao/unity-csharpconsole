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
    internal static class ScriptableObjectCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(ScriptableObjectCommandActions));
#endif
        }

#if UNITY_EDITOR
        private const int MAX_PROPERTIES = 200;

        // ── create ──

        [Serializable]
        private sealed class CreateResult
        {
            public string assetPath = "";
            public string typeName = "";
        }

        [CommandAction(
            "scriptableobject",
            "create",
            editorOnly: true,
            summary: "Create a ScriptableObject asset of a given type",
            resultType: typeof(CreateResult))]
        private static CommandResponse Create(
            [CommandArgument(NonEmpty = true)] string typeName,
            [CommandArgument(NonEmpty = true)] string savePath)
        {
            if (string.IsNullOrEmpty(typeName))
                return CommandResponseFactory.ValidationError("typeName is required for scriptableobject/create");
            if (string.IsNullOrEmpty(savePath))
                return CommandResponseFactory.ValidationError("savePath is required for scriptableobject/create");
            if (!savePath.StartsWith("Assets/", StringComparison.Ordinal) &&
                !savePath.StartsWith("Assets\\", StringComparison.Ordinal))
                return CommandResponseFactory.ValidationError("savePath must be under Assets/");
            if (!savePath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return CommandResponseFactory.ValidationError("savePath must end with '.asset'");

            return CommandHelpers.RunCommand<CreateResult>(
                () =>
                {
                    var type = CommandHelpers.ResolveType(typeName, out var typeError);
                    if (type == null) return (error: typeError, result: (CreateResult)null);

                    if (!typeof(ScriptableObject).IsAssignableFrom(type))
                        return (error: $"Type '{typeName}' is not a ScriptableObject", result: (CreateResult)null);
                    if (type.IsAbstract)
                        return (error: $"Type '{typeName}' is abstract and cannot be instantiated", result: (CreateResult)null);

                    var instance = ScriptableObject.CreateInstance(type);
                    if (instance == null)
                        return (error: $"Failed to create an instance of '{typeName}'", result: (CreateResult)null);

                    CommandHelpers.EnsureDirectoryExists(savePath);
                    AssetDatabase.CreateAsset(instance, savePath);

                    return (error: (string)null, result: new CreateResult
                    {
                        assetPath = savePath,
                        typeName = type.Name
                    });
                },
                r => $"Created ScriptableObject at '{r.assetPath}'"
            );
        }

        // ── get ──

        [Serializable]
        private sealed class GetResult
        {
            public string assetPath = "";
            public string typeName = "";
            public CommandHelpers.PropertyInfo[] properties = Array.Empty<CommandHelpers.PropertyInfo>();
        }

        [CommandAction(
            "scriptableobject",
            "get",
            editorOnly: true,
            summary: "Get serialized field data of a ScriptableObject asset",
            resultType: typeof(GetResult))]
        private static CommandResponse Get(
            [CommandArgument(NonEmpty = true)] string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for scriptableobject/get");

            return CommandHelpers.RunCommand<GetResult>(
                () =>
                {
                    var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
                    if (so == null)
                        return (error: $"No ScriptableObject asset found at '{assetPath}'", result: (GetResult)null);

                    var serialized = new SerializedObject(so);
                    var props = new List<CommandHelpers.PropertyInfo>();
                    var iter = serialized.GetIterator();

                    if (iter.NextVisible(true))
                    {
                        do
                        {
                            if (props.Count >= MAX_PROPERTIES) break;
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
                        assetPath = assetPath,
                        typeName = so.GetType().Name,
                        properties = props.ToArray()
                    });
                },
                r => $"Got {r.typeName} ({r.properties.Length} properties)"
            );
        }

        // ── modify ──

        [Serializable]
        private sealed class ModifyResult
        {
            public string assetPath = "";
            public string typeName = "";
            public string[] modifiedFields = Array.Empty<string>();
        }

        [CommandAction(
            "scriptableobject",
            "modify",
            editorOnly: true,
            summary: "Modify serialized fields of a ScriptableObject asset",
            resultType: typeof(ModifyResult))]
        private static CommandResponse Modify(
            [CommandArgument(NonEmpty = true)] string assetPath,
            [CommandArgument(NonEmpty = true)] CommandHelpers.FieldPair[] fields)
        {
            if (string.IsNullOrEmpty(assetPath))
                return CommandResponseFactory.ValidationError("assetPath is required for scriptableobject/modify");
            if (fields == null || fields.Length == 0)
                return CommandResponseFactory.ValidationError("fields array is required for scriptableobject/modify");

            return CommandHelpers.RunCommand<ModifyResult>(
                () =>
                {
                    var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
                    if (so == null)
                        return (error: $"No ScriptableObject asset found at '{assetPath}'", result: (ModifyResult)null);

                    var serialized = new SerializedObject(so);
                    var modifiedFields = new List<string>();

                    foreach (var field in fields)
                    {
                        if (string.IsNullOrEmpty(field.name)) continue;
                        var prop = serialized.FindProperty(field.name);
                        if (prop == null) continue;

                        if (CommandHelpers.TrySetSerializedProperty(prop, field.value))
                        {
                            modifiedFields.Add(field.name);
                        }
                    }

                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(so);
                    AssetDatabase.SaveAssetIfDirty(so);

                    return (error: (string)null, result: new ModifyResult
                    {
                        assetPath = assetPath,
                        typeName = so.GetType().Name,
                        modifiedFields = modifiedFields.ToArray()
                    });
                },
                r => $"Modified {r.modifiedFields.Length} field(s) on {r.typeName}"
            );
        }
#endif
    }
}
