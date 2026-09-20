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

        [SettingsProvider]
        internal static SettingsProvider CreateSettingsProvider()
        {
            return new SettingsProvider("Project/C# Console", SettingsScope.Project)
            {
                keywords = new HashSet<string> { "CSharpConsole", "Compile", "Export", "Build" },
                guiHandler = _ =>
                {
                    try
                    {
                        var settings = Load();
                        EditorGUI.BeginChangeCheck();
                        settings.exportCompileSetAfterBuild = EditorGUILayout.ToggleLeft(
                            new GUIContent("Export Compile Set After Build",
                                "Export CSharpConsoleCompileSet.zip beside the Player after building."),
                            settings.exportCompileSetAfterBuild);
                        if (EditorGUI.EndChangeCheck())
                        {
                            settings.Save();
                        }
                        EditorGUILayout.HelpBox(
                            "Export the Player's assemblies and defines for runtime REPL alignment. " +
                            "Disabling export leaves existing compile sets in place.", MessageType.Info);
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
