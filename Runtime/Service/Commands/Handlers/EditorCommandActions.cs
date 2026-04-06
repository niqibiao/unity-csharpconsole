using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

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
        private sealed class ConsoleGetResult
        {
            public bool available;
            public int count;
            public ConsoleEntry[] entries = Array.Empty<ConsoleEntry>();
            public string error = "";
        }

        [Serializable]
        private sealed class ConsoleEntry
        {
            public string condition = "";
            public int mode;
            public int instanceId;
        }

        private enum PlaymodeLifecycleState
        {
            EditMode = 0,
            EnteringPlaymode = 1,
            PlayMode = 2,
            ExitingPlaymode = 3
        }

        private enum PendingPlaymodeTransition
        {
            None = 0,
            Entering = 1,
            Exiting = 2
        }

        private static PlaymodeLifecycleState s_PlaymodeLifecycleState;
        private static bool s_PlaymodeLifecycleTrackingInitialized;
        private static PendingPlaymodeTransition s_PendingPlaymodeTransition;

        [CommandAction("editor", "status", editorOnly: true, summary: "Get editor state and play mode info")]
        private static CommandResponse EditorStatus()
        {
            var health = ConsoleHttpService.BuildHealthResponseSnapshot();
            var result = ConsoleHttpService.RunOnEditorThread(() => new EditorStatusResult
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
            });

            return CommandResponseFactory.Ok("Editor status fetched", JsonUtility.ToJson(result));
        }

        [CommandAction("editor", "playmode.status", editorOnly: true, summary: "Get current play mode state")]
        private static CommandResponse PlaymodeStatus()
        {
            var result = ConsoleHttpService.RunOnEditorThread(() => new PlaymodeStatusResult
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                isCompiling = EditorApplication.isCompiling
            });

            return CommandResponseFactory.Ok("Playmode status fetched", JsonUtility.ToJson(result));
        }

        [CommandAction("editor", "playmode.enter", editorOnly: true, summary: "Enter play mode")]
        private static CommandResponse EnterPlaymode()
        {
            var validationError = ConsoleHttpService.RunOnEditorThread(() =>
            {
                var error = ValidatePlaymodeTransition(enter: true);
                if (string.IsNullOrEmpty(error))
                {
                    s_PendingPlaymodeTransition = PendingPlaymodeTransition.Entering;
                    EditorApplication.delayCall -= EnterPlaymodeAfterCommandResponse;
                    EditorApplication.delayCall += EnterPlaymodeAfterCommandResponse;
                }

                return error;
            });

            return string.IsNullOrEmpty(validationError)
                ? CommandResponseFactory.Ok("Requested enter playmode", "{}")
                : CommandResponseFactory.ValidationError(validationError);
        }

        [CommandAction("editor", "playmode.exit", editorOnly: true, summary: "Exit play mode")]
        private static CommandResponse ExitPlaymode()
        {
            var validationError = ConsoleHttpService.RunOnEditorThread(() =>
            {
                var error = ValidatePlaymodeTransition(enter: false);
                if (string.IsNullOrEmpty(error))
                {
                    s_PendingPlaymodeTransition = PendingPlaymodeTransition.Exiting;
                    EditorApplication.delayCall -= ExitPlaymodeAfterCommandResponse;
                    EditorApplication.delayCall += ExitPlaymodeAfterCommandResponse;
                }

                return error;
            });

            return string.IsNullOrEmpty(validationError)
                ? CommandResponseFactory.Ok("Requested exit playmode", "{}")
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

        [CommandAction("editor", "console.get", editorOnly: true, summary: "Get editor console log entries")]
        private static CommandResponse GetConsole()
        {
            var result = ConsoleHttpService.RunOnEditorThread(GetConsoleEntries);
            return CommandResponseFactory.Ok("Fetched editor console entries", JsonUtility.ToJson(result));
        }

        [CommandAction("editor", "console.clear", editorOnly: true, summary: "Clear the editor console")]
        private static CommandResponse ClearConsole()
        {
            var cleared = ConsoleHttpService.RunOnEditorThread(ClearConsoleEntries);
            return cleared
                ? CommandResponseFactory.Ok("Cleared editor console", "{}")
                : CommandResponseFactory.ValidationError("Editor console clear is unavailable on this Unity version");
        }

        private static void EnterPlaymodeAfterCommandResponse()
        {
            s_PendingPlaymodeTransition = PendingPlaymodeTransition.None;
            if (string.IsNullOrEmpty(ValidatePlaymodeTransition(enter: true)))
            {
                EditorApplication.isPlaying = true;
            }
        }

        private static void ExitPlaymodeAfterCommandResponse()
        {
            s_PendingPlaymodeTransition = PendingPlaymodeTransition.None;
            if (string.IsNullOrEmpty(ValidatePlaymodeTransition(enter: false)))
            {
                EditorApplication.isPlaying = false;
            }
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
                lifecycleState,
                s_PendingPlaymodeTransition);
        }

        private static string ValidatePlaymodeTransition(
            bool enter,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            PlaymodeLifecycleState lifecycleState,
            PendingPlaymodeTransition pendingTransition)
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

                if (pendingTransition != PendingPlaymodeTransition.None ||
                    lifecycleState == PlaymodeLifecycleState.EnteringPlaymode ||
                    lifecycleState == PlaymodeLifecycleState.ExitingPlaymode)
                {
                    return "Cannot enter playmode while another playmode transition is in progress";
                }

                return "";
            }

            if (pendingTransition != PendingPlaymodeTransition.None ||
                lifecycleState == PlaymodeLifecycleState.EnteringPlaymode ||
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

        private static ConsoleGetResult GetConsoleEntries()
        {
            var result = new ConsoleGetResult();

            try
            {
                var editorAssembly = typeof(EditorWindow).Assembly;
                var logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries");
                var logEntryType = editorAssembly.GetType("UnityEditor.LogEntry");
                if (logEntriesType == null || logEntryType == null)
                {
                    result.available = false;
                    result.error = "UnityEditor.LogEntries API not found";
                    return result;
                }

                var getCount = logEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var getEntry = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (getCount == null || getEntry == null)
                {
                    result.available = false;
                    result.error = "UnityEditor.LogEntries methods not found";
                    return result;
                }

                result.available = true;
                var count = (int)getCount.Invoke(null, null);
                var maxCount = Math.Max(0, count);

                var conditionField = logEntryType.GetField("condition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var modeField = logEntryType.GetField("mode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var instanceIdField = logEntryType.GetField("instanceID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                var collected = new List<ConsoleEntry>(maxCount);
                for (var i = 0; i < maxCount; i++)
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    var read = (bool)getEntry.Invoke(null, new[] { i, entry });
                    if (!read)
                    {
                        continue;
                    }

                    collected.Add(new ConsoleEntry
                    {
                        condition = conditionField?.GetValue(entry)?.ToString() ?? "",
                        mode = modeField != null ? Convert.ToInt32(modeField.GetValue(entry)) : 0,
                        instanceId = instanceIdField != null ? Convert.ToInt32(instanceIdField.GetValue(entry)) : 0
                    });
                }

                result.entries = collected.ToArray();
                result.count = result.entries.Length;
            }
            catch (Exception e)
            {
                result.available = false;
                result.error = e.Message;
                result.entries = Array.Empty<ConsoleEntry>();
                result.count = 0;
            }

            return result;
        }

        private static bool ClearConsoleEntries()
        {
            var editorAssembly = typeof(EditorWindow).Assembly;
            var logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries");
            var clear = logEntriesType?.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (clear == null)
            {
                return false;
            }

            clear.Invoke(null, null);
            return true;
        }
#endif
    }
}
