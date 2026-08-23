using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    // Game View capture is ScreenCapture, which players support, so a device can
    // be photographed and the file retrieved with the download route. Scene View
    // capture stays editor-only: a player has no scene view.
    internal static class ScreenshotCommandActions
    {
        internal static void Register(CommandRouter router)
        {
            router.RegisterAttributedHandlers(typeof(ScreenshotCommandActions));
        }

        [Serializable]
        private sealed class ScreenshotResult
        {
            public string savePath = "";
            public int width;
            public int height;
        }

#if UNITY_EDITOR
        [CommandAction(
            "screenshot",
            "scene_view",
            editorOnly: true,
            summary: "Capture the current Scene View",
            resultType: typeof(ScreenshotResult))]
        private static CommandResponse CaptureSceneView(
            [CommandArgument(NonEmpty = true)] string savePath,
            [CommandArgument(Minimum = 0)] int width = 0,
            [CommandArgument(Minimum = 0)] int height = 0)
        {
            if (string.IsNullOrEmpty(savePath))
                return CommandResponseFactory.ValidationError("savePath is required for screenshot/scene_view");

            return CommandHelpers.RunCommand<ScreenshotResult>(
                () =>
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null)
                        return (error: "No active Scene View found", result: (ScreenshotResult)null);

                    var cam = sceneView.camera;
                    if (cam == null)
                        return (error: "Scene View camera is not available", result: (ScreenshotResult)null);

                    var w = width > 0 ? width : (int)sceneView.position.width;
                    var h = height > 0 ? height : (int)sceneView.position.height;

                    if (w <= 0 || h <= 0)
                        return (error: "Invalid capture dimensions", result: (ScreenshotResult)null);

                    var bytes = CommandHelpers.CaptureCamera(cam, w, h);
                    CommandHelpers.EnsureDirectoryExists(savePath);
                    System.IO.File.WriteAllBytes(savePath, bytes);
                    CommandHelpers.ImportAssetIfUnderAssets(savePath);

                    return (error: (string)null, result: new ScreenshotResult
                    {
                        savePath = savePath,
                        width = w,
                        height = h
                    });
                },
                r => $"Captured Scene View ({r.width}x{r.height})"
            );
        }

#endif

        [CommandAction(
            "screenshot",
            "game_view",
            summary: "Capture the Game View",
            resultType: typeof(ScreenshotResult))]
        private static CommandResponse CaptureGameView(
            [CommandArgument(NonEmpty = true)] string savePath,
            [CommandArgument(Minimum = 0)] int width = 0,
            [CommandArgument(Minimum = 0)] int height = 0,
            [CommandArgument(Minimum = 1)] int superSize = 1)
        {
            if (string.IsNullOrEmpty(savePath))
                return CommandResponseFactory.ValidationError("savePath is required for screenshot/game_view");

            var captureSuperSize = superSize > 0 ? superSize : 1;

#if UNITY_EDITOR
            var rendersLiveFrames = EditorApplication.isPlaying;
#else
            // A player is always presenting frames, so it always takes the
            // ScreenCapture path and never the camera-render fallback.
            var rendersLiveFrames = true;
#endif

            if (rendersLiveFrames)
            {
                // CaptureScreenshot schedules a write at end-of-frame; the file
                // will not exist immediately after this command returns.
                CommandHelpers.EnsureDirectoryExists(savePath);
                ScreenCapture.CaptureScreenshot(savePath, captureSuperSize);

                var screenWidth = Screen.width * captureSuperSize;
                var screenHeight = Screen.height * captureSuperSize;
                var result = new ScreenshotResult
                {
                    savePath = savePath,
                    width = screenWidth,
                    height = screenHeight
                };

                return CommandResponseFactory.Ok(
                    $"Screenshot scheduled ({result.width}x{result.height}) — file will be written at end-of-frame",
                    JsonUtility.ToJson(result));
            }

            return CommandHelpers.RunCommand<ScreenshotResult>(
                () =>
                {
                    var cam = Camera.main;
                    if (cam == null)
                    {
                        var allCams = Camera.allCameras;
                        if (allCams.Length > 0) cam = allCams[0];
                    }

                    if (cam == null)
                        return (error: "No camera available for Game View capture", result: (ScreenshotResult)null);

                    var w = (width > 0 ? width : cam.pixelWidth) * captureSuperSize;
                    var h = (height > 0 ? height : cam.pixelHeight) * captureSuperSize;

                    if (w <= 0 || h <= 0)
                        return (error: "Invalid capture dimensions", result: (ScreenshotResult)null);

                    var bytes = CommandHelpers.CaptureCamera(cam, w, h);
                    CommandHelpers.EnsureDirectoryExists(savePath);
                    System.IO.File.WriteAllBytes(savePath, bytes);
#if UNITY_EDITOR
                    CommandHelpers.ImportAssetIfUnderAssets(savePath);
#endif

                    return (error: (string)null, result: new ScreenshotResult
                    {
                        savePath = savePath,
                        width = w,
                        height = h
                    });
                },
                r => $"Captured Game View ({r.width}x{r.height})"
            );
        }
    }
}
