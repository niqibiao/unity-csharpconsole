#if DEVELOPMENT_BUILD && !UNITY_EDITOR && !ZH1ZH1_CSC_DISABLE_AUTOSTART
using UnityEngine;
using UnityEngine.Scripting;

// A project can use the console without referencing its assemblies. The linker
// still needs to process this one for Unity's startup callback.
[assembly: AlwaysLinkAssembly]

namespace Zh1Zh1.CSharpConsole
{
    /// <summary>
    /// Unity 2022's startup scan omits the console's Runtime assembly because its asmdef
    /// needs DEVELOPMENT_BUILD, which the scan does not pass. Keep this asmdef
    /// unconstrained and use the Player compiler's defines to include the callback.
    /// </summary>
    internal static class RuntimeAutoStart
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Start()
        {
            RuntimeInitializer.ConsoleInitialize();
        }
    }
}
#endif
