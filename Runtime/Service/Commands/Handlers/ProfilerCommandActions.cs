using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    // Recording is UnityEngine.Profiling, which players support in development
    // builds, so start/stop/status work on device. Only save needs the editor:
    // it goes through ProfilerDriver. On device, point start at a logFile and
    // retrieve the binary log with the download route instead.
    internal static class ProfilerCommandActions
    {
        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(ProfilerCommandActions));
        }

        // Resolved by name so the player simply gets null members rather than a
        // missing-assembly reference; every use below is null-guarded.
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

        [CommandAction(
            "profiler",
            "start",
            summary: "Start Profiler recording",
            resultType: typeof(StartResult))]
        private static CommandResponse Start(bool deep = false, string logFile = "")
        {
            // Deep profiling is an editor toggle; a player reports it as off
            // rather than claiming a setting it never applied.
            var deepApplied = deep && s_DeepProfilingProp != null;
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

            var result = new StartResult
            {
                started = Profiler.enabled,
                deepProfiling = deepApplied,
                logFile = Profiler.logFile ?? ""
            };

            return result.started
                ? CommandResponseFactory.Ok("Profiler started", JsonUtility.ToJson(result))
                : CommandResponseFactory.ValidationError("Failed to start profiler");
        }

        [Serializable]
        private sealed class StopResult
        {
            public bool stopped;
        }

        [CommandAction(
            "profiler",
            "stop",
            summary: "Stop Profiler recording",
            resultType: typeof(StopResult))]
        private static CommandResponse Stop()
        {
            Profiler.enabled = false;
            Profiler.enableBinaryLog = false;
            Profiler.logFile = "";

            var result = new StopResult { stopped = !Profiler.enabled };

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

        [CommandAction(
            "profiler",
            "status",
            summary: "Get current Profiler state",
            resultType: typeof(StatusResult))]
        private static CommandResponse Status()
        {
            var isDeep = s_DeepProfilingProp != null && (bool)s_DeepProfilingProp.GetValue(null);
            var first = s_FirstFrameIndexProp != null ? (int)s_FirstFrameIndexProp.GetValue(null) : 0;
            var last = s_LastFrameIndexProp != null ? (int)s_LastFrameIndexProp.GetValue(null) : 0;

            var result = new StatusResult
            {
                enabled = Profiler.enabled,
                deepProfiling = isDeep,
                logFile = Profiler.logFile ?? "",
                frameCount = Math.Max(0, last - first)
            };

            return CommandResponseFactory.Ok($"Profiler {(result.enabled ? "enabled" : "disabled")}", JsonUtility.ToJson(result));
        }

        [Serializable]
        private sealed class SaveResult
        {
            public string savePath = "";
            public bool saved;
        }

        [CommandAction(
            "profiler",
            "save",
            editorOnly: true,
            summary: "Save recorded profiler data to a .raw file",
            resultType: typeof(SaveResult))]
        private static CommandResponse Save(
            [CommandArgument(NonEmpty = true)] string savePath)
        {
            if (string.IsNullOrEmpty(savePath))
            {
                return CommandResponseFactory.ValidationError("savePath is required for profiler/save");
            }

            if (s_SaveProfileMethod == null)
            {
                return CommandResponseFactory.ValidationError("ProfilerDriver.SaveProfile is not available");
            }

            CommandHelpers.EnsureDirectoryExists(savePath);
            s_SaveProfileMethod.Invoke(null, new object[] { savePath });
            var saved = System.IO.File.Exists(savePath);

            var result = new SaveResult
            {
                savePath = savePath,
                saved = saved
            };

            return result.saved
                ? CommandResponseFactory.Ok($"Profiler data saved to '{result.savePath}'", JsonUtility.ToJson(result))
                : CommandResponseFactory.ValidationError($"Failed to save profiler data to '{savePath}'");
        }
    }
}
