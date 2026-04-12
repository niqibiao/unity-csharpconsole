using System;
using System.Linq;
using UnityEditor.Compilation;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    public class EditorREPLCompiler : BaseREPLCompiler
    {
        private static string s_DefaultDefines;

        public EditorREPLCompiler()
            : base("EditorScript_", GetCachedDefaultDefines(), cacheReferences: true)
        {
        }

        /// <summary>
        /// Resolve and cache defines on the main thread so that background HTTP
        /// threads never call CompilationPipeline.GetDefinesFromAssemblyName
        /// (which internally requires EditorUserBuildSettings.activeBuildTarget).
        /// Call this from InitializeOnLoadMethod or similar main-thread entrypoint.
        /// </summary>
        internal static void WarmUpDefaultDefines()
        {
            if (s_DefaultDefines == null)
            {
                s_DefaultDefines = ResolveDefaultDefines();
            }
        }

        private static string GetCachedDefaultDefines()
        {
            if (s_DefaultDefines == null)
            {
                s_DefaultDefines = ResolveDefaultDefines();
            }

            return s_DefaultDefines;
        }

        private static string ResolveDefaultDefines()
        {
            var assemblyName = typeof(EditorREPLCompiler).Assembly.GetName().Name;
            var defines = CompilationPipeline.GetDefinesFromAssemblyName(assemblyName);
            if (defines == null || defines.Length == 0)
            {
                ConsoleLog.Warning($"Unable to resolve scripting defines for assembly '{assemblyName}'. Falling back to empty define set.");
                return string.Empty;
            }

            return string.Join(";", defines.Where(static symbol => !string.IsNullOrWhiteSpace(symbol)).Distinct());
        }
    }
}
