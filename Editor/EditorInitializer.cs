using Zh1Zh1.CSharpConsole.Editor.Compiler;
using Zh1Zh1.CSharpConsole.Executor;
using Zh1Zh1.CSharpConsole.Service;
using UnityEditor;

namespace Zh1Zh1.CSharpConsole.Editor
{
    public static class EditorInitializer
    {
        private readonly static bool s_IsWorkerProcess = AssetDatabase.IsAssetImportWorkerProcess();

        [InitializeOnLoadMethod]
        private static void CSharpConsoleInitialize()
        {
            if (s_IsWorkerProcess)
            {
                return;
            }

            ConsoleHttpService.Shutdown();
            var defaultDefines = EditorREPLCompiler.ResolveDefaultDefines();
            ConsoleHttpService.InitializeForEditor(() => new EditorREPLCompiler(defaultDefines), () => new REPLExecutor(), runtimeDllPath => new RuntimeREPLCompiler(runtimeDllPath));
            ConsoleLog.Info("CSharpConsole initialize finished");
        }

        [InitializeOnLoadMethod]
        private static void RegisterEditorCallbacks()
        {
            if (s_IsWorkerProcess)
            {
                return;
            }

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ConsoleHttpService.RegisterRefreshLifecycleCallbacks();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                case PlayModeStateChange.ExitingPlayMode:
                    CSharpConsoleInitialize();
                    break;
            }
        }
    }
}
