using System.IO;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    public class RuntimeREPLCompiler : BaseREPLCompiler
    {
        public RuntimeREPLCompiler(string runtimeDllPath)
            : base("RuntimeScript_", ReadCompileSetDefines(runtimeDllPath), cacheReferences: true, runtimeDllPath: runtimeDllPath)
        {
            ConsoleLog.Debug($"RuntimeREPLCompiler created with runtimeDllPath={runtimeDllPath}");
        }

        /// <summary>
        /// The symbols the set's build was compiled with, used when a request carries none
        /// of its own. Without a set there are none, and every #if in a submission takes its
        /// #else branch.
        /// </summary>
        private static string ReadCompileSetDefines(string runtimeDllPath)
        {
            if (string.IsNullOrEmpty(runtimeDllPath))
            {
                return "";
            }

            var path = Path.Combine(runtimeDllPath, CompileSetStore.DefinesFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
    }
}
