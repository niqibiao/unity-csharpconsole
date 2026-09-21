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

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        internal static string FilePath => Path.Combine(
            ProjectRoot,
            "ProjectSettings/CSharpConsoleSettings.json");

        internal static CSharpConsoleSettings Load()
        {
            var settings = new CSharpConsoleSettings();
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
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

            var path = exportCompileSetPath.Trim().Replace('\\', '/');
            if (IsRootedPath(path))
            {
                throw new ArgumentException(
                    "Export ZIP Path must be relative to the project root (for example, Builds/Console.zip). Absolute paths are not supported.");
            }
            if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Export ZIP Path must include a file name ending in .zip.");
            }
            return Path.GetFullPath(Path.Combine(ProjectRoot, path));
        }

        private static bool IsRootedPath(string path)
        {
            // Reject Windows drive paths even when validating shared settings on another OS.
            return Path.IsPathRooted(path) || (path.Length >= 2 && path[1] == ':');
        }

        private static string GetProjectRelativePath(string path)
        {
            var fullPath = Path.GetFullPath(Path.Combine(ProjectRoot, path));
            var relativePath = Path.GetRelativePath(ProjectRoot, fullPath).Replace('\\', '/');
            // A different drive or UNC share cannot be expressed relative to the project.
            return IsRootedPath(relativePath) ? null : relativePath;
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
                        var defaultPath = CompileSetExporter.DefaultRelativePath;
                        EditorGUI.BeginChangeCheck();
                        settings.exportCompileSetAfterBuild = EditorGUILayout.ToggleLeft(
                            new GUIContent("Export Compile Set After Build",
                                "Export the Player's compile set after building."),
                            settings.exportCompileSetAfterBuild);
                        using (new EditorGUI.DisabledScope(!settings.exportCompileSetAfterBuild))
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PrefixLabel(new GUIContent("Export ZIP Path",
                                "A ZIP file path relative to the project root. Absolute paths are not supported."));
                            var wasOverridden = settings.overrideExportCompileSetPath;
                            settings.overrideExportCompileSetPath = EditorGUILayout.ToggleLeft(
                                "Override", settings.overrideExportCompileSetPath, GUILayout.Width(80));
                            if (!wasOverridden && settings.overrideExportCompileSetPath
                                && string.IsNullOrWhiteSpace(settings.exportCompileSetPath))
                            {
                                settings.exportCompileSetPath = defaultPath;
                            }
                            using (new EditorGUI.DisabledScope(!settings.overrideExportCompileSetPath))
                            {
                                var path = EditorGUILayout.TextField(settings.overrideExportCompileSetPath
                                    ? settings.exportCompileSetPath
                                    : defaultPath);
                                if (settings.overrideExportCompileSetPath)
                                {
                                    settings.exportCompileSetPath = path;
                                }
                                if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                                {
                                    var selected = EditorUtility.SaveFilePanel("Export Compile Set",
                                        ProjectRoot,
                                        "CSharpConsoleCompileSet", "zip");
                                    if (!string.IsNullOrEmpty(selected))
                                    {
                                        var relativePath = GetProjectRelativePath(selected);
                                        if (relativePath == null)
                                        {
                                            EditorUtility.DisplayDialog("Choose a Relative Export Path",
                                                "Choose a location on the same drive or network share as the project so it can be stored as a relative path.",
                                                "OK");
                                        }
                                        else
                                        {
                                            settings.exportCompileSetPath = relativePath;
                                            GUI.changed = true;
                                        }
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
                            "Turn off Override to export to " + CompileSetExporter.DefaultRelativePath + ". " +
                            "Custom paths must be relative to the project root (for example, Builds/Console.zip or ../Exports/Console.zip); " +
                            "absolute paths are not supported. Missing folders are created. " +
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
