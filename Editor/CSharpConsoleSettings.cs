using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Editor
{
    [Serializable]
    internal sealed class CSharpConsoleSettings
    {
        public bool exportCompileSetAfterBuild = true;
        public string exportCompileSetPath = "";

        internal static string FilePath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            "ProjectSettings/CSharpConsoleSettings.json");

        internal static CSharpConsoleSettings Load()
        {
            var settings = new CSharpConsoleSettings();
            if (File.Exists(FilePath))
            {
                // Keep the default when an older settings file omits the field.
                JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), settings);
            }
            return settings;
        }

        internal void Save()
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true) + Environment.NewLine);
        }

        internal string ResolveExportPath()
        {
            if (string.IsNullOrWhiteSpace(exportCompileSetPath))
            {
                return null;
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
                        EditorGUI.BeginChangeCheck();
                        settings.exportCompileSetAfterBuild = EditorGUILayout.ToggleLeft(
                            new GUIContent("Export Compile Set After Build",
                                "Export the Player's compile set after building."),
                            settings.exportCompileSetAfterBuild);
                        using (new EditorGUI.DisabledScope(!settings.exportCompileSetAfterBuild))
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            settings.exportCompileSetPath = EditorGUILayout.TextField(
                                new GUIContent("Export ZIP Path",
                                    "Full ZIP file path. Relative paths are resolved from the project root. " +
                                    "Leave empty to export CSharpConsoleCompileSet.zip beside the Player."),
                                settings.exportCompileSetPath);
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
                        if (EditorGUI.EndChangeCheck())
                        {
                            settings.Save();
                        }
                        EditorGUILayout.HelpBox(
                            "Export the Player's assemblies and defines for runtime REPL alignment. " +
                            "Leave the ZIP path empty to export beside the Player. " +
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
