// Standalone .NET replay harness. Unity's build callback is exercised in Editor builds;
// the shim here lets the same production reader/exporter run on captured files.
using System;
using System.Text.Json;
using Zh1Zh1.CSharpConsole.Editor.Compiler;

internal static class PlayerBuildRecordReplay
{
    internal static void Export(string[] args)
    {
        var record = PlayerBuildRecorder.ReadArtifacts(args[0], args[1], args[2],
            bool.Parse(args[3]), args[4], bool.Parse(args[5]), bool.Parse(args[6]), bool.Parse(args[7]), args[8]);
        CompileSetExporter.Export(record, args[9]);
    }
}

namespace UnityEngine
{
    public static class Application
    {
        public static string dataPath => throw new NotSupportedException();
    }

    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions s_Options = new JsonSerializerOptions { IncludeFields = true };

        public static T FromJson<T>(string text) => JsonSerializer.Deserialize<T>(text, s_Options);
    }
}

namespace UnityEditor
{
    [Flags]
    public enum BuildOptions { Development = 1, InstallInBuildFolder = 2 }
    public enum BuildTargetGroup { Standalone }
    public enum BuildTarget { StandaloneOSX, Android }
    public enum StandaloneBuildSubtarget { Server }
    public enum ScriptingImplementation { Mono2x, IL2CPP }

    public static class BuildPipeline
    {
        public static string GetPlaybackEngineDirectory(BuildTarget target, BuildOptions options) =>
            throw new NotSupportedException();
    }

    public static class PlayerSettings
    {
        public static ScriptingImplementation GetScriptingBackend(Build.NamedBuildTarget target) =>
            throw new NotSupportedException();
    }
}

namespace UnityEditor.Build
{
    public interface IPostprocessBuildWithReport
    {
        int callbackOrder { get; }
        void OnPostprocessBuild(Reporting.BuildReport report);
    }

    public struct NamedBuildTarget
    {
        public static NamedBuildTarget Server => throw new NotSupportedException();
        public static NamedBuildTarget FromBuildTargetGroup(BuildTargetGroup group) => throw new NotSupportedException();
    }
}

namespace UnityEditor.Build.Reporting
{
    public enum BuildResult { Failed, Cancelled }

    public class BuildReport
    {
        public BuildSummary summary => throw new NotSupportedException();
    }

    public class BuildSummary
    {
        public BuildResult result => throw new NotSupportedException();
        public BuildOptions options => throw new NotSupportedException();
        public BuildTarget platform => throw new NotSupportedException();
        public BuildTargetGroup platformGroup => throw new NotSupportedException();
        public string outputPath => throw new NotSupportedException();
        public Guid guid => throw new NotSupportedException();
        public T GetSubtarget<T>() => throw new NotSupportedException();
    }
}

namespace Zh1Zh1.CSharpConsole
{
    internal static class ConsoleLog
    {
        public static void Info(string message) => Console.WriteLine(message);
        public static void Warning(string message) => Console.Error.WriteLine(message);
    }
}

namespace Zh1Zh1.CSharpConsole.Editor
{
    internal class CSharpConsoleSettings
    {
        public static CSharpConsoleSettings Load() => throw new NotSupportedException();
        public bool exportCompileSetAfterBuild => throw new NotSupportedException();
        public string ResolveExportPath() => throw new NotSupportedException();
    }
}

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    // The Editor regression locates the recorder in the compiler's assembly.
    public class RuntimeREPLCompiler { }
}

namespace Zh1Zh1.CSharpConsole.Service
{
    internal static class CompileSetStore
    {
        public const string DefinesFileName = "runtime-defines.txt";
        public const string GuidFileName = "build-guid.txt";
    }
}
