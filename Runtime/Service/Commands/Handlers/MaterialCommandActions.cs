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
    internal static class MaterialCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(MaterialCommandActions));
#endif
        }

#if UNITY_EDITOR
        [Serializable]
        private sealed class MaterialProperty
        {
            public string name = "";
            public string type = "";
            public string value = "";
        }

        // ── create ──

        [Serializable]
        private sealed class CreateResult
        {
            public string assetPath = "";
            public string shaderName = "";
        }

        [CommandAction("material", "create", editorOnly: true, summary: "Create a new material asset")]
        private static CommandResponse Create(string savePath, string shaderName = "")
        {
            if (string.IsNullOrEmpty(savePath))
                return CommandResponseFactory.ValidationError("savePath is required for material/create");

            return CommandHelpers.MainThreadCommand<CreateResult>(
                () =>
                {
                    var resolvedShaderName = string.IsNullOrEmpty(shaderName) ? "Standard" : shaderName;
                    var shader = Shader.Find(resolvedShaderName);

                    // Fallback to URP Lit if Standard not found
                    if (shader == null && resolvedShaderName == "Standard")
                    {
                        shader = Shader.Find("Universal Render Pipeline/Lit");
                        if (shader != null) resolvedShaderName = "Universal Render Pipeline/Lit";
                    }

                    if (shader == null)
                        return (error: $"Shader '{resolvedShaderName}' not found", result: (CreateResult)null);

                    CommandHelpers.EnsureDirectoryExists(savePath);
                    var mat = new Material(shader);
                    AssetDatabase.CreateAsset(mat, savePath);

                    return (error: (string)null, result: new CreateResult
                    {
                        assetPath = savePath,
                        shaderName = resolvedShaderName
                    });
                },
                r => $"Created material at '{r.assetPath}'"
            );
        }

        // ── get ──

        [Serializable]
        private sealed class GetResult
        {
            public string assetPath = "";
            public string shaderName = "";
            public MaterialProperty[] properties = Array.Empty<MaterialProperty>();
        }

        [CommandAction("material", "get", editorOnly: true, summary: "Get material properties")]
        private static CommandResponse Get(string assetPath = "", string gameObjectPath = "")
        {
            return CommandHelpers.MainThreadCommand<GetResult>(
                () =>
                {
                    Material mat = null;
                    var resolvedPath = "";

                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                        resolvedPath = assetPath;
                    }
                    else if (!string.IsNullOrEmpty(gameObjectPath))
                    {
                        var go = GameObject.Find(gameObjectPath);
                        if (go == null)
                            return (error: $"No GameObject found at path '{gameObjectPath}'", result: (GetResult)null);

                        var renderer = go.GetComponent<Renderer>();
                        if (renderer == null)
                            return (error: $"No Renderer found on '{go.name}'", result: (GetResult)null);

                        mat = renderer.sharedMaterial;
                        resolvedPath = mat != null ? AssetDatabase.GetAssetPath(mat) ?? "" : "";
                    }
                    else
                    {
                        return (error: "Either assetPath or gameObjectPath is required for material/get", result: (GetResult)null);
                    }

                    if (mat == null)
                        return (error: "Material not found", result: (GetResult)null);

                    var shader = mat.shader;
                    var propCount = ShaderUtil.GetPropertyCount(shader);
                    var props = new List<MaterialProperty>(propCount);

                    for (var i = 0; i < propCount; i++)
                    {
                        var propName = ShaderUtil.GetPropertyName(shader, i);
                        var propType = ShaderUtil.GetPropertyType(shader, i);
                        var propValue = GetMaterialPropertyValue(mat, propName, propType);

                        props.Add(new MaterialProperty
                        {
                            name = propName,
                            type = propType.ToString(),
                            value = propValue
                        });
                    }

                    return (error: (string)null, result: new GetResult
                    {
                        assetPath = resolvedPath,
                        shaderName = shader.name,
                        properties = props.ToArray()
                    });
                },
                r => $"Material '{r.shaderName}' ({r.properties.Length} properties)"
            );
        }

        private static string GetMaterialPropertyValue(Material mat, string propName, ShaderUtil.ShaderPropertyType propType)
        {
            switch (propType)
            {
                case ShaderUtil.ShaderPropertyType.Color:
                    var c = mat.GetColor(propName);
                    return $"({c.r},{c.g},{c.b},{c.a})";
                case ShaderUtil.ShaderPropertyType.Vector:
                    var v = mat.GetVector(propName);
                    return $"({v.x},{v.y},{v.z},{v.w})";
                case ShaderUtil.ShaderPropertyType.Float:
                case ShaderUtil.ShaderPropertyType.Range:
                    return mat.GetFloat(propName).ToString("R");
                case ShaderUtil.ShaderPropertyType.TexEnv:
                    var tex = mat.GetTexture(propName);
                    return tex != null ? $"{tex.name} ({AssetDatabase.GetAssetPath(tex)})" : "null";
                default:
                    return "<unknown>";
            }
        }

        // ── assign ──

        [Serializable]
        private sealed class AssignResult
        {
            public string gameObjectPath = "";
            public string materialPath = "";
            public int index;
        }

        [CommandAction("material", "assign", editorOnly: true, summary: "Assign a material to a Renderer component")]
        private static CommandResponse Assign(string materialPath, string gameObjectPath = "", int gameObjectInstanceId = 0, int index = 0)
        {
            if (string.IsNullOrEmpty(materialPath))
                return CommandResponseFactory.ValidationError("materialPath is required for material/assign");

            return CommandHelpers.MainThreadCommand<AssignResult>(
                () =>
                {
                    var go = CommandHelpers.ResolveGameObject(gameObjectPath, gameObjectInstanceId, out var error);
                    if (go == null) return (error, result: (AssignResult)null);

                    var renderer = go.GetComponent<Renderer>();
                    if (renderer == null)
                        return (error: $"No Renderer found on '{go.name}'", result: (AssignResult)null);

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if (mat == null)
                        return (error: $"Material not found at '{materialPath}'", result: (AssignResult)null);

                    Undo.RecordObject(renderer, "Assign Material");
                    var materials = renderer.sharedMaterials;
                    if (materials.Length == 0)
                        return (error: $"Renderer on '{go.name}' has no material slots", result: (AssignResult)null);

                    var idx = Math.Max(0, Math.Min(index, materials.Length - 1));
                    materials[idx] = mat;
                    renderer.sharedMaterials = materials;

                    return (error: (string)null, result: new AssignResult
                    {
                        gameObjectPath = CommandHelpers.GetHierarchyPath(go.transform),
                        materialPath = materialPath,
                        index = idx
                    });
                },
                r => $"Assigned material to slot {r.index}"
            );
        }
#endif
    }
}
