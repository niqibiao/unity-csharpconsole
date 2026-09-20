using UnityEngine;
using Zh1Zh1.CSharpConsole.Service;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    public class RuntimeREPLCompiler : BaseREPLCompiler
    {
        /// <summary>
        /// A set a build exported -- one that names its build -- carries the symbols that
        /// build was compiled with, and those are used whatever a request sends: a
        /// request's defines come from a file the caller was configured with, which may
        /// describe another build. Any other directory's defines file is only the default
        /// for a request that carries none. Without either there are no symbols, and every
        /// #if in a submission takes its #else branch.
        /// </summary>
        public RuntimeREPLCompiler(string runtimeDllPath)
            : this(runtimeDllPath, CompileSetStore.ReadBuildGuid(runtimeDllPath) != null)
        {
        }

        private RuntimeREPLCompiler(string runtimeDllPath, bool isBuildCompileSet)
            : base("RuntimeScript_", CompileSetStore.ReadDefines(runtimeDllPath) ?? "", cacheReferences: true, runtimeDllPath: runtimeDllPath,
                ignoreRequestDefines: isBuildCompileSet, useOnlyRuntimeReferences: isBuildCompileSet)
        {
            ConsoleLog.Debug($"RuntimeREPLCompiler created with runtimeDllPath={runtimeDllPath}");
        }
    }
}
