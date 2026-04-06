using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

namespace Zh1Zh1.CSharpConsole.Service.Commands.Handlers
{
    internal static class ScreenshotCommandActions
    {
        internal static void Register(CommandRouter router)
        {
#if UNITY_EDITOR
            router.RegisterAttributedHandlers(typeof(ScreenshotCommandActions));
#endif
        }

#if UNITY_EDITOR
        [Serializable]
        private sealed class ScreenshotResult
        {
            public string savePath = "";
            public int width;
            public int height;
        }

        [CommandAction("screenshot", "scene-view", editorOnly: true, summary: "Capture the current Scene View")]
        private static CommandResponse CaptureSceneView(string savePath, int width = 0, int height = 0)
        {
            if (string.IsNullOrEmpty(savePath))
                return CommandResponseFactory.ValidationError("savePath is required for screenshot/scene-view");

            return CommandHelpers.MainThreadCommand<ScreenshotResult>(
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

        [CommandAction("screenshot", "game-view", editorOnly: true, summary: "Capture the Game View")]
        private static CommandResponse CaptureGameView(string savePath, int width = 0, int height = 0, int superSize = 1)
        {
            if (string.IsNullOrEmpty(savePath))
                return CommandResponseFactory.ValidationError("savePath is required for screenshot/game-view");

            return CommandHelpers.MainThreadCommand<ScreenshotResult>(
                () =>
                {
                    var captureSuperSize = superSize > 0 ? superSize : 1;

                    if (EditorApplication.isPlaying)
                    {
                        CommandHelpers.EnsureDirectoryExists(savePath);
                        ScreenCapture.CaptureScreenshot(savePath, captureSuperSize);

                        // CaptureScreenshot writes asynchronously at end-of-frame;
                        // fall through to camera-based capture for an immediate file.
                    }

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
                    CommandHelpers.ImportAssetIfUnderAssets(savePath);

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
#endif
    }
}
