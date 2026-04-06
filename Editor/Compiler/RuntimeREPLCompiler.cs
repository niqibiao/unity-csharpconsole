using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    public class RuntimeREPLCompiler : BaseREPLCompiler
    {
        public RuntimeREPLCompiler(string runtimeDllPath)
            : base("RuntimeScript_", "", cacheReferences: true, runtimeDllPath: runtimeDllPath)
        {
            ConsoleLog.Debug($"RuntimeREPLCompiler created with runtimeDllPath={runtimeDllPath}");
        }
    }
}
