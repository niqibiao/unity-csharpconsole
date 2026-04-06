using System;
using System.Reflection;
#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Profiling;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class ProfilerCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(ProfilerCommandActions));
#endif
        }

#if UNITY_EDITOR
        private static readonly Type s_ProfilerDriverType = Type.GetType("UnityEditorInternal.ProfilerDriver, UnityEditor");
        private static readonly PropertyInfo s_DeepProfilingProp = s_ProfilerDriverType?.GetProperty("deepProfiling", BindingFlags.Public | BindingFlags.Static);
        private static readonly PropertyInfo s_FirstFrameIndexProp = s_ProfilerDriverType?.GetProperty("firstFrameIndex", BindingFlags.Public | BindingFlags.Static);
        private static readonly PropertyInfo s_LastFrameIndexProp = s_ProfilerDriverType?.GetProperty("lastFrameIndex", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo s_SaveProfileMethod = s_ProfilerDriverType?.GetMethod("SaveProfile", BindingFlags.Public | BindingFlags.Static);

        [Serializable]
        private sealed class StartResult
        {
            public bool started;
            public bool deepProfiling;
            public string logFile = "";
        }

        [CommandAction("profiler", "start", editorOnly: true, summary: "Start Profiler recording")]
        private static CommandResponse Start(bool deep = false, string logFile = "")
        {
            var result = ConsoleHttpService.RunOnEditorThread(() =>
            {
                if (s_DeepProfilingProp != null)
                {
                    s_DeepProfilingProp.SetValue(null, deep);
                }

                if (!string.IsNullOrEmpty(logFile))
                {
                    CommandHelpers.EnsureDirectoryExists(logFile);
                    Profiler.logFile = logFile;
                    Profiler.enableBinaryLog = true;
                }

                Profiler.enabled = true;

                return new StartResult
                {
                    started = Profiler.enabled,
                    deepProfiling = deep,
                    logFile = Profiler.logFile ?? ""
                };
            });

            return result.started
                ? CommandResponseFactory.Ok("Profiler started", JsonUtility.ToJson(result))
                : CommandResponseFactory.ValidationError("Failed to start profiler");
        }

        [Serializable]
        private sealed class StopResult
        {
            public bool stopped;
        }

        [CommandAction("profiler", "stop", editorOnly: true, summary: "Stop Profiler recording")]
        private static CommandResponse Stop()
        {
            var result = ConsoleHttpService.RunOnEditorThread(() =>
            {
                Profiler.enabled = false;
                Profiler.enableBinaryLog = false;
                Profiler.logFile = "";

                return new StopResult { stopped = !Profiler.enabled };
            });

            return result.stopped
                ? CommandResponseFactory.Ok("Profiler stopped", JsonUtility.ToJson(result))
                : CommandResponseFactory.ValidationError("Failed to stop profiler");
        }

        [Serializable]
        private sealed class StatusResult
        {
            public bool enabled;
            public bool deepProfiling;
            public string logFile = "";
            public int frameCount;
        }

        [CommandAction("profiler", "status", editorOnly: true, summary: "Get current Profiler state")]
        private static CommandResponse Status()
        {
            var result = ConsoleHttpService.RunOnEditorThread(() =>
            {
                var isDeep = s_DeepProfilingProp != null && (bool)s_DeepProfilingProp.GetValue(null);
                var first = s_FirstFrameIndexProp != null ? (int)s_FirstFrameIndexProp.GetValue(null) : 0;
                var last = s_LastFrameIndexProp != null ? (int)s_LastFrameIndexProp.GetValue(null) : 0;

                return new StatusResult
                {
                    enabled = Profiler.enabled,
                    deepProfiling = isDeep,
                    logFile = Profiler.logFile ?? "",
                    frameCount = Math.Max(0, last - first)
                };
            });

            return CommandResponseFactory.Ok($"Profiler {(result.enabled ? "enabled" : "disabled")}", JsonUtility.ToJson(result));
        }

        [Serializable]
        private sealed class SaveResult
        {
            public string savePath = "";
            public bool saved;
        }

        [CommandAction("profiler", "save", editorOnly: true, summary: "Save recorded profiler data to a .raw file")]
        private static CommandResponse Save(string savePath)
        {
            if (string.IsNullOrEmpty(savePath))
            {
                return CommandResponseFactory.ValidationError("savePath is required for profiler/save");
            }

            if (s_SaveProfileMethod == null)
            {
                return CommandResponseFactory.ValidationError("ProfilerDriver.SaveProfile is not available");
            }

            var result = ConsoleHttpService.RunOnEditorThread(() =>
            {
                CommandHelpers.EnsureDirectoryExists(savePath);
                s_SaveProfileMethod.Invoke(null, new object[] { savePath });
                var saved = System.IO.File.Exists(savePath);

                return new SaveResult
                {
                    savePath = savePath,
                    saved = saved
                };
            });

            return result.saved
                ? CommandResponseFactory.Ok($"Profiler data saved to '{result.savePath}'", JsonUtility.ToJson(result))
                : CommandResponseFactory.ValidationError($"Failed to save profiler data to '{savePath}'");
        }
#endif
    }
}
