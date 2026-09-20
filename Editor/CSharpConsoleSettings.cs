using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Editor.Compiler;

namespace Zh1Zh1.CSharpConsole.Editor
{
    [Serializable]
    internal sealed class CSharpConsoleSettings
    {
        public bool exportCompileSetAfterBuild = true;
        public bool overrideExportCompileSetPath;
        public string exportCompileSetPath = "";

        internal static string FilePath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            "ProjectSettings/CSharpConsoleSettings.json");

        internal static CSharpConsoleSettings Load()
        {
            var settings = new CSharpConsoleSettings();
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                JsonUtility.FromJsonOverwrite(json, settings);
                // Preserve custom paths saved before the Override checkbox existed.
                // A second pass lets an explicitly saved false take precedence.
                settings.overrideExportCompileSetPath = !string.IsNullOrWhiteSpace(settings.exportCompileSetPath);
                JsonUtility.FromJsonOverwrite(json, settings);
            }
            return settings;
        }

        internal void Save()
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true) + Environment.NewLine);
        }

        internal string ResolveExportPath()
        {
            if (!overrideExportCompileSetPath)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(exportCompileSetPath))
            {
                throw new ArgumentException("Choose an Export ZIP Path or turn off Override to use the default.");
            }

            var path = exportCompileSetPath.Trim();
            if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Export ZIP Path must include a file name ending in .zip.");
            }
            return Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(Directory.GetParent(Application.dataPath).FullName, path));
        }

        internal static string GetDefaultExportPathPreview()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var playerOutput = EditorUserBuildSettings.GetBuildLocation(target);
            var path = CompileSetExporter.GetDefaultExportPath(playerOutput);
            return path == null
                ? "<Player output directory>/" + CompileSetExporter.FileName
                : Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, path));
        }

        [SettingsProvider]
        internal static SettingsProvider CreateSettingsProvider()
        {
            return new SettingsProvider("Project/C# Console", SettingsScope.Project)
            {
                keywords = new HashSet<string> { "CSharpConsole", "Compile", "Export", "Build", "ZIP", "Path" },
                guiHandler = _ =>
                {
                    try
                    {
                        var settings = Load();
                        var defaultPath = GetDefaultExportPathPreview();
                        EditorGUI.BeginChangeCheck();
                        settings.exportCompileSetAfterBuild = EditorGUILayout.ToggleLeft(
                            new GUIContent("Export Compile Set After Build",
                                "Export the Player's compile set after building."),
                            settings.exportCompileSetAfterBuild);
                        using (new EditorGUI.DisabledScope(!settings.exportCompileSetAfterBuild))
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PrefixLabel("Export ZIP Path");
                            var wasOverridden = settings.overrideExportCompileSetPath;
                            settings.overrideExportCompileSetPath = EditorGUILayout.ToggleLeft(
                                "Override", settings.overrideExportCompileSetPath, GUILayout.Width(80));
                            if (!wasOverridden && settings.overrideExportCompileSetPath
                                && string.IsNullOrWhiteSpace(settings.exportCompileSetPath)
                                && Path.IsPathRooted(defaultPath))
                            {
                                // Seed from the real default, never from the unresolved preview placeholder.
                                settings.exportCompileSetPath = defaultPath;
                            }
                            using (new EditorGUI.DisabledScope(!settings.overrideExportCompileSetPath))
                            {
                                var path = EditorGUILayout.TextField(settings.overrideExportCompileSetPath
                                    ? settings.exportCompileSetPath : defaultPath);
                                if (settings.overrideExportCompileSetPath)
                                {
                                    settings.exportCompileSetPath = path;
                                }
                                if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                                {
                                    var selected = EditorUtility.SaveFilePanel("Export Compile Set",
                                        Directory.GetParent(Application.dataPath).FullName,
                                        "CSharpConsoleCompileSet", "zip");
                                    if (!string.IsNullOrEmpty(selected))
                                    {
                                        settings.exportCompileSetPath = selected;
                                        GUI.changed = true;
                                    }
                                }
                            }
                        }
                        if (EditorGUI.EndChangeCheck())
                        {
                            settings.Save();
                        }
                        EditorGUILayout.HelpBox(
                            "Export the Player's assemblies and defines for runtime REPL alignment. " +
                            "Turn off Override to export beside the Player. The default path follows the build output; " +
                            "the preview uses the current target's saved build location when available. " +
                            "Relative paths start at the project root; missing folders are created. " +
                            "Disabling export leaves existing compile sets in place.", MessageType.Info);
                        if (settings.exportCompileSetAfterBuild)
                        {
                            try
                            {
                                settings.ResolveExportPath();
                            }
                            catch (Exception e)
                            {
                                EditorGUILayout.HelpBox(e.Message, MessageType.Error);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        EditorGUILayout.HelpBox($"Could not read or save C# Console settings: {e.Message}",
                            MessageType.Error);
                    }
                },
            };
        }
    }
}
