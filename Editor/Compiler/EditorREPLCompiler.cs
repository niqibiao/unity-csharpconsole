using System.Linq;
using UnityEditor.Compilation;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    public class EditorREPLCompiler : BaseREPLCompiler
    {
        public EditorREPLCompiler(string defaultDefines)
            : base("EditorScript_", defaultDefines ?? "", cacheReferences: true)
        {
        }

        // Must be called on the main thread — GetDefinesFromAssemblyName
        // internally requires EditorUserBuildSettings.activeBuildTarget.
        internal static string ResolveDefaultDefines()
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
