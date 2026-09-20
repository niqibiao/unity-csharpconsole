using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    /// <summary>
    /// What a player build left behind for the compiler to use: the symbols it was
    /// compiled with, the assemblies it shipped, and the GUID identifying it.
    ///
    /// Captured by the build itself rather than reconstructed afterwards. Out of band all
    /// of it is ambiguous -- a project that has built several targets, or built again
    /// since, leaves several sets of artifacts with nothing in them saying which player
    /// is the one currently running, and the editor's own view reports the target
    /// selected now rather than the one built. Inside the build callback the report
    /// says which target and options were just built, and that picks them out.
    /// Recency alone does not: Bee rewrites a command-line file only when its content
    /// changes, so rebuilding a target unchanged leaves another target's files newer.
    /// </summary>
    internal class PlayerBuildRecord
    {
        /// <summary>Matches <c>Application.buildGUID</c> in the player this build produced.</summary>
        public string buildGuid;

        /// <summary>The managed assemblies as they shipped, after the linker ran.</summary>
        public string strippedAssembliesPath;

        /// <summary>
        /// The editor's unstripped mscorlib for the profile this build ran, as the linker
        /// was given it -- the profile depends on the target and backend of <em>this</em> build.
        /// </summary>
        public string bclMscorlibPath;

        public string defines;
    }

    /// <summary>
    /// Exports a <see cref="PlayerBuildRecord"/> beside each player build as it finishes.
    /// </summary>
    internal class PlayerBuildRecorder : IPostprocessBuildWithReport
    {
        /// <summary>Last, so the stripped assemblies this reads are final.</summary>
        public int callbackOrder
        {
            get { return int.MaxValue; }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            // Not "== Succeeded": the summary is still being assembled while
            // post-processors run, and asking for success here skips every build,
            // silently. Only the two outcomes already decided are worth declining.
            if (report.summary.result == BuildResult.Failed
                || report.summary.result == BuildResult.Cancelled)
            {
                return;
            }

            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                var artifacts = Path.Combine(projectRoot, "Library/Bee/artifacts");
                var engineDirectory = BuildPipeline.GetPlaybackEngineDirectory(report.summary.platform, report.summary.options);
                var namedTarget = report.summary.platformGroup == BuildTargetGroup.Standalone
                    && report.summary.GetSubtarget<StandaloneBuildSubtarget>() == StandaloneBuildSubtarget.Server
                    ? NamedBuildTarget.Server : NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
                // The same in-build setting Unity's Bee postprocessor uses. A support
                // directory alone cannot distinguish cached Mono and IL2CPP arguments.
                var backend = PlayerSettings.GetScriptingBackend(namedTarget) switch
                {
                    ScriptingImplementation.IL2CPP => "Il2Cpp",
                    ScriptingImplementation.Mono2x => "Mono",
                    _ => throw new InvalidOperationException("Unsupported scripting backend for compile-set export."),
                };
                var (strippedAssembliesPath, bclMscorlibPath, engineAssembly) = ReadLinkerArguments(projectRoot, artifacts, engineDirectory, backend);
                var development = (report.summary.options & BuildOptions.Development) != 0;
                var defines = ReadPlayerDefines(artifacts, development, engineAssembly, backend);
                if (string.IsNullOrEmpty(defines))
                {
                    throw new InvalidOperationException("Could not identify the compiler arguments for this player's target and backend.");
                }

                var record = new PlayerBuildRecord
                {
                    buildGuid = report.summary.guid.ToString(),
                    strippedAssembliesPath = strippedAssembliesPath,
                    bclMscorlibPath = bclMscorlibPath,
                    defines = defines,
                };

                CompileSetExporter.Export(record, report.summary.outputPath);
            }
            catch (Exception e)
            {
                // A build that succeeded must not be reported as failed over this; the
                // player just has no compile set to align against.
                ConsoleLog.Warning($"Could not export the compile set for this player build: {e.Message}");
            }
        }

        /// <summary>
        /// The command line Unity handed UnityLinker for this build: where it wrote the
        /// stripped assemblies, and the unstripped mscorlib of the profile it linked
        /// against. Both come from the one file, so they describe the same build and need
        /// no mapping from build target and scripting backend to BCL profile.
        ///
        /// Under Library/Bee/artifacts rather than in the build output, because an IL2CPP
        /// build converts the stripped assemblies to C++ and ships no managed directory.
        /// Every target built leaves a linker command line there; this build's is the one
        /// that links engine modules from <paramref name="engineDirectory"/>, the target's
        /// player support.
        /// </summary>
        private static (string strippedAssembliesPath, string bclMscorlibPath, string engineAssembly) ReadLinkerArguments(string projectRoot, string artifacts, string engineDirectory, string expectedBackend)
        {
            var rspDirectory = Path.Combine(artifacts, "rsp");
            if (!Directory.Exists(rspDirectory))
            {
                return (null, null, null);
            }

            var engine = string.IsNullOrEmpty(engineDirectory) ? null : engineDirectory.Replace('\\', '/').TrimEnd('/') + "/";

            // Other tools write their command lines here too; the linker's is the one
            // whose output is the stripped directory.
            foreach (var rsp in Directory.GetFiles(rspDirectory, "*.rsp").OrderByDescending(path => File.GetLastWriteTimeUtc(path)))
            {
                string output = null;
                string mscorlib = null;
                string engineAssembly = null;
                string backend = null;
                var linksThisTarget = engine == null;
                foreach (Match argument in s_LinkerArgument.Matches(File.ReadAllText(rsp)))
                {
                    var value = argument.Groups[2].Success ? argument.Groups[2].Value : argument.Groups[3].Value;
                    if (argument.Groups[1].Value == "out")
                    {
                        output = value;
                    }
                    else if (argument.Groups[1].Value == "dotnetruntime")
                    {
                        backend = value;
                    }
                    else if (string.Equals(Path.GetFileName(value), "mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        mscorlib = value;
                    }
                    else if (engine != null && value.Replace('\\', '/').StartsWith(engine, StringComparison.OrdinalIgnoreCase))
                    {
                        linksThisTarget = true;
                        if (string.Equals(Path.GetFileName(value), "UnityEngine.CoreModule.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            engineAssembly = value;
                        }
                    }
                }

                if (linksThisTarget && string.Equals(backend, expectedBackend, StringComparison.OrdinalIgnoreCase)
                    && output != null && Path.GetFileName(output) == "ManagedStripped")
                {
                    return (Path.GetFullPath(Path.Combine(projectRoot, output)), mscorlib, engineAssembly);
                }
            }

            return (null, null, null);
        }

        /// <summary>One <c>--name=value</c> argument, the value quoted or bare.</summary>
        private static readonly Regex s_LinkerArgument = new Regex("--(out|allowed-assembly|dotnetruntime)=(?:\"([^\"]*)\"|(\\S+))");

        /// <summary>
        /// The command line Unity handed Roslyn for this build, which is the only exact
        /// record of what the player was compiled with. CompilationPipeline reports the
        /// editor's view instead: measured against an IL2CPP build it claimed
        /// ENABLE_MONO and added UNITY_INCLUDE_TESTS.
        ///
        /// Assembly-CSharp is preferred because a submission is written as game code and
        /// that is the assembly game code lands in. A project that puts everything
        /// behind asmdefs has none, and any player assembly will do -- they differ only
        /// in the versionDefines each package declares for itself.
        /// </summary>
        private static string ReadPlayerDefines(string artifacts, bool development, string engineAssembly, string backend)
        {
            const string definePrefix = "-define:";

            var backendDefine = string.Equals(backend, "Il2Cpp", StringComparison.OrdinalIgnoreCase) ? "ENABLE_IL2CPP"
                : string.Equals(backend, "Mono", StringComparison.OrdinalIgnoreCase) ? "ENABLE_MONO" : null;
            if (!Directory.Exists(artifacts) || string.IsNullOrEmpty(engineAssembly) || backendDefine == null)
            {
                return "";
            }

            var candidates = Directory.GetDirectories(artifacts, "*.dag")
                .SelectMany(dag => Directory.GetFiles(dag, "*.rsp"))
                .OrderByDescending(rsp => Path.GetFileName(rsp) == "Assembly-CSharp.rsp")
                .ThenByDescending(rsp => File.GetLastWriteTimeUtc(rsp));

            foreach (var rsp in candidates)
            {
                var lines = File.ReadAllLines(rsp).Select(raw => raw.Trim()).ToArray();

                // Editor and player assemblies are compiled through separate Bee graphs.
                // The player's is told apart by what its rsp does not define rather than
                // by directory name, which encodes the target and configuration in a form
                // Unity does not document.
                if (lines.Contains(definePrefix + "UNITY_EDITOR"))
                {
                    continue;
                }

                // Development and release players are compiled through separate graphs as
                // well, told apart the same way.
                if (lines.Contains(definePrefix + "DEVELOPMENT_BUILD") != development)
                {
                    continue;
                }

                // A cached rebuild need not touch its rsp. Match the linker inputs,
                // not timestamps from another target/backend built more recently.
                if (!lines.Contains(definePrefix + backendDefine)
                    || !lines.Any(line => line.StartsWith("-r:", StringComparison.Ordinal)
                        && string.Equals(line.Substring(3).Trim('"').Replace('\\', '/'),
                            engineAssembly.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return string.Join(";", lines
                    .Where(line => line.StartsWith(definePrefix, StringComparison.Ordinal))
                    .Select(line => line.Substring(definePrefix.Length)));
            }

            return "";
        }
    }
}
