using System;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    // Answers "what is this process and where does it keep files" for whichever
    // side responds. The paths matter most for a remote player: they are where
    // its logs, saves and profiler captures land, and the download route needs
    // an absolute path to retrieve any of them.
    internal static class RuntimeInfoCommandActions
    {
        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(RuntimeInfoCommandActions));
        }

        [Serializable]
        private sealed class RuntimeInfoResult
        {
            public bool isEditor;
            public string platform = "";
            public string deviceModel = "";
            public string operatingSystem = "";
            public string processorType = "";
            public int processorCount;
            public int systemMemorySizeMb;
            public string graphicsDeviceName = "";
            public string unityVersion = "";
            public string applicationVersion = "";
            public string productName = "";
            public string companyName = "";
            public int screenWidth;
            public int screenHeight;
            public int targetFrameRate;
            public bool isDebugBuild;

            // Empty when the platform writes no log file of its own. Android
            // routes to logcat and iOS to the device console, so on those a
            // project's own logging decides where a retrievable file lives.
            public string consoleLogPath = "";
            public string persistentDataPath = "";
            public string temporaryCachePath = "";
            public string streamingAssetsPath = "";
            public string dataPath = "";
        }

        [CommandAction(
            "runtime",
            "info",
            summary: "Report the responding process's device facts and well-known file paths",
            resultType: typeof(RuntimeInfoResult))]
        private static CommandResponse Info()
        {
            var result = new RuntimeInfoResult
            {
                isEditor = Application.isEditor,
                platform = Application.platform.ToString(),
                deviceModel = SystemInfo.deviceModel ?? "",
                operatingSystem = SystemInfo.operatingSystem ?? "",
                processorType = SystemInfo.processorType ?? "",
                processorCount = SystemInfo.processorCount,
                systemMemorySizeMb = SystemInfo.systemMemorySize,
                graphicsDeviceName = SystemInfo.graphicsDeviceName ?? "",
                unityVersion = Application.unityVersion ?? "",
                applicationVersion = Application.version ?? "",
                productName = Application.productName ?? "",
                companyName = Application.companyName ?? "",
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                targetFrameRate = Application.targetFrameRate,
                isDebugBuild = Debug.isDebugBuild,
                consoleLogPath = Application.consoleLogPath ?? "",
                persistentDataPath = Application.persistentDataPath ?? "",
                temporaryCachePath = Application.temporaryCachePath ?? "",
                streamingAssetsPath = Application.streamingAssetsPath ?? "",
                dataPath = Application.dataPath ?? ""
            };

            var where = result.isEditor ? "editor" : result.platform;
            return CommandResponseFactory.Ok(
                $"Runtime info for {where} ({result.screenWidth}x{result.screenHeight})",
                JsonUtility.ToJson(result));
        }
    }
}
