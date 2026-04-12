using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;
using Zh1Zh1.CSharpConsole.Service.Internal;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class EditorCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(EditorCommandActions));
#endif
        }

#if UNITY_EDITOR
        [Serializable]
        private sealed class EditorStatusResult
        {
            public bool initialized;
            public int port;
            public bool refreshing;
            public int generation;
            public string editorState = "";
            public bool isPlaying;
            public bool isPaused;
            public bool isCompiling;
            public bool isUpdating;
        }

        [Serializable]
        private sealed class PlaymodeStatusResult
        {
            public bool isPlaying;
            public bool isPaused;
            public bool isPlayingOrWillChangePlaymode;
            public bool isCompiling;
        }

        [Serializable]
        private sealed class ConsoleMarkResult
        {
            public string logPath = "";
            public string id = "";
            public string label = "";
            public string timestampUtc = "";
            public string markerText = "";
        }

        private enum PlaymodeLifecycleState
        {
            EditMode = 0,
            EnteringPlaymode = 1,
            PlayMode = 2,
            ExitingPlaymode = 3
        }

        private static PlaymodeLifecycleState s_PlaymodeLifecycleState;
        private static bool s_PlaymodeLifecycleTrackingInitialized;

        private static MethodInfo s_LogEntriesClearMethod;
        private static bool s_LogEntriesClearResolved;

        [CommandAction("editor", "status", editorOnly: true, summary: "Get editor state and play mode info")]
        private static CommandResponse EditorStatus()
        {
            var health = ConsoleHttpService.BuildHealthResponseSnapshot();
            var result = new EditorStatusResult
            {
                initialized = health.initialized,
                port = health.port,
                refreshing = health.refreshing,
                generation = health.generation,
                editorState = health.editorState ?? "",
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating
            };

            return CommandResponseFactory.Ok("Editor status fetched", JsonUtility.ToJson(result));
        }

        [CommandAction("editor", "playmode.status", editorOnly: true, summary: "Get current play mode state")]
        private static CommandResponse PlaymodeStatus()
        {
            var result = new PlaymodeStatusResult
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                isCompiling = EditorApplication.isCompiling
            };

            return CommandResponseFactory.Ok("Playmode status fetched", JsonUtility.ToJson(result));
        }

        [CommandAction("editor", "playmode.enter", editorOnly: true, runOnMainThread: false, summary: "Enter play mode")]
        private static CommandResponse EnterPlaymode() => SetPlaymode(enter: true);

        [CommandAction("editor", "playmode.exit", editorOnly: true, runOnMainThread: false, summary: "Exit play mode")]
        private static CommandResponse ExitPlaymode() => SetPlaymode(enter: false);

        private static CommandResponse SetPlaymode(bool enter)
        {
            var validationError = MainThreadRequestRunner.RunOnMainThread(() => ValidatePlaymodeTransition(enter));

            if (string.IsNullOrEmpty(validationError))
            {
                MainThreadRequestRunner.Post(() =>
                {
                    if (EditorApplication.isPlaying != enter)
                    {
                        EditorApplication.isPlaying = enter;
                    }
                });
            }

            return string.IsNullOrEmpty(validationError)
                ? CommandResponseFactory.Ok($"Requested {(enter ? "enter" : "exit")} playmode", "{}")
                : CommandResponseFactory.ValidationError(validationError);
        }

        [CommandAction("editor", "menu.open", editorOnly: true, summary: "Open a menu item by path")]
        private static CommandResponse OpenMenu(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
            {
                return CommandResponseFactory.ValidationError("menuPath is required for editor/menu.open");
            }

            return CommandResponseFactory.ValidationError("editor/menu.open is blocked in non-interactive mode due to modal-dialog risk");
        }

        [CommandAction("editor", "window.open", editorOnly: true, summary: "Open an editor window by type name")]
        private static CommandResponse OpenWindow(string typeName, bool utility = false)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return CommandResponseFactory.ValidationError("typeName is required for editor/window.open");
            }

            return CommandResponseFactory.ValidationError("editor/window.open is blocked in non-interactive mode due to modal-dialog risk");
        }

        [CommandAction("editor", "console.clear", editorOnly: true, summary: "Clear the editor console")]
        private static CommandResponse ClearConsole()
        {
            var cleared = ClearConsoleEntries();
            return cleared
                ? CommandResponseFactory.Ok("Cleared editor console", "{}")
                : CommandResponseFactory.ValidationError("Editor console clear is unavailable on this Unity version");
        }

        [CommandAction("editor", "console.mark", editorOnly: true, summary: "Write a searchable marker into the editor log and return the log file path")]
        private static CommandResponse MarkConsole(string label = "")
        {
            var markerId = Guid.NewGuid().ToString("N");
            var timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var trimmedLabel = (label ?? "").Trim();
            var markerText = string.IsNullOrEmpty(trimmedLabel)
                ? $"[C#Console][ConsoleMark] id={markerId} utc={timestampUtc}"
                : $"[C#Console][ConsoleMark] id={markerId} utc={timestampUtc} label={trimmedLabel}";

            UnityEngine.Debug.Log(markerText);

            var result = new ConsoleMarkResult
            {
                logPath = ResolveEditorLogPath(),
                id = markerId,
                label = trimmedLabel,
                timestampUtc = timestampUtc,
                markerText = markerText
            };

            return CommandResponseFactory.Ok($"Wrote console marker '{markerId}'", JsonUtility.ToJson(result));
        }

        private static string ValidatePlaymodeTransition(bool enter)
        {
            EnsurePlaymodeLifecycleTrackingInitialized();
            var lifecycleState = GetCurrentPlaymodeLifecycleState();
            return ValidatePlaymodeTransition(
                enter,
                EditorApplication.isCompiling,
                EditorApplication.isUpdating,
                EditorApplication.isPlaying,
                lifecycleState);
        }

        private static string ValidatePlaymodeTransition(
            bool enter,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            PlaymodeLifecycleState lifecycleState)
        {
            if (isCompiling || isUpdating)
            {
                return "Cannot change playmode while editor is compiling or updating";
            }
            if (enter)
            {
                if (lifecycleState == PlaymodeLifecycleState.PlayMode || isPlaying)
                {
                    return "Already in playmode";
                }

                if (lifecycleState == PlaymodeLifecycleState.EnteringPlaymode ||
                    lifecycleState == PlaymodeLifecycleState.ExitingPlaymode)
                {
                    return "Cannot enter playmode while another playmode transition is in progress";
                }

                return "";
            }

            if (lifecycleState == PlaymodeLifecycleState.EnteringPlaymode ||
                lifecycleState == PlaymodeLifecycleState.ExitingPlaymode)
            {
                return "Cannot exit playmode while another playmode transition is in progress";
            }

            if (lifecycleState == PlaymodeLifecycleState.EditMode || !isPlaying)
            {
                return "Already out of playmode";
            }

            return "";
        }

        private static void EnsurePlaymodeLifecycleTrackingInitialized()
        {
            if (s_PlaymodeLifecycleTrackingInitialized)
            {
                return;
            }

            s_PlaymodeLifecycleState = DetermineInitialPlaymodeLifecycleState();
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            s_PlaymodeLifecycleTrackingInitialized = true;
        }

        private static PlaymodeLifecycleState GetCurrentPlaymodeLifecycleState()
        {
            EnsurePlaymodeLifecycleTrackingInitialized();

            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                s_PlaymodeLifecycleState = PlaymodeLifecycleState.EditMode;
                return s_PlaymodeLifecycleState;
            }

            if (s_PlaymodeLifecycleState == PlaymodeLifecycleState.ExitingPlaymode)
            {
                return s_PlaymodeLifecycleState;
            }

            if (EditorApplication.isPlaying)
            {
                s_PlaymodeLifecycleState = PlaymodeLifecycleState.PlayMode;
                return s_PlaymodeLifecycleState;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                s_PlaymodeLifecycleState = PlaymodeLifecycleState.EnteringPlaymode;
                return s_PlaymodeLifecycleState;
            }

            s_PlaymodeLifecycleState = PlaymodeLifecycleState.EditMode;
            return s_PlaymodeLifecycleState;
        }

        private static PlaymodeLifecycleState DetermineInitialPlaymodeLifecycleState()
        {
            if (EditorApplication.isPlaying)
            {
                return PlaymodeLifecycleState.PlayMode;
            }

            return EditorApplication.isPlayingOrWillChangePlaymode
                ? PlaymodeLifecycleState.EnteringPlaymode
                : PlaymodeLifecycleState.EditMode;
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange stateChange)
        {
            s_PlaymodeLifecycleState = stateChange switch
            {
                PlayModeStateChange.ExitingEditMode => PlaymodeLifecycleState.EnteringPlaymode,
                PlayModeStateChange.EnteredPlayMode => PlaymodeLifecycleState.PlayMode,
                PlayModeStateChange.ExitingPlayMode => PlaymodeLifecycleState.ExitingPlaymode,
                PlayModeStateChange.EnteredEditMode => PlaymodeLifecycleState.EditMode,
                _ => s_PlaymodeLifecycleState,
            };
        }

        private static bool ClearConsoleEntries()
        {
            if (!s_LogEntriesClearResolved)
            {
                var logEntriesType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                s_LogEntriesClearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                s_LogEntriesClearResolved = true;
            }

            if (s_LogEntriesClearMethod == null)
            {
                return false;
            }

            s_LogEntriesClearMethod.Invoke(null, null);
            return true;
        }

        private static string ResolveEditorLogPath()
        {
            try
            {
                if (!string.IsNullOrEmpty(Application.consoleLogPath))
                {
                    return Application.consoleLogPath;
                }
            }
            catch
            {
                // Fall back to Unity's default editor log locations below.
            }

            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(localAppData))
                    {
                        return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
                    }
                }

                if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                    if (!string.IsNullOrEmpty(home))
                    {
                        return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
                    }
                }

                if (Application.platform == RuntimePlatform.LinuxEditor)
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                    if (!string.IsNullOrEmpty(home))
                    {
                        return Path.Combine(home, ".config", "unity3d", "Editor.log");
                    }
                }
            }
            catch
            {
                // Ignore fallback resolution failures and return empty below.
            }

            return "";
        }
#endif
    }
}
