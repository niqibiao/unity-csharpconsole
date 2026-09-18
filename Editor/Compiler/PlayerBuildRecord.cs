using System;
using System.IO;
using System.Linq;
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
    /// selected now rather than the one built. Inside the build callback there is no
    /// ambiguity: the newest artifacts are the ones just produced.
    /// </summary>
    internal class PlayerBuildRecord
    {
        /// <summary>Matches <c>Application.buildGUID</c> in the player this build produced.</summary>
        public string buildGuid;

        /// <summary>The managed assemblies as they shipped, after the linker ran.</summary>
        public string strippedAssembliesPath;

        /// <summary>
        /// The editor's unstripped mscorlib for the profile this build ran, resolved
        /// here because it depends on the target and backend of <em>this</em> build.
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
                var target = report.summary.platform;

                var record = new PlayerBuildRecord
                {
                    buildGuid = report.summary.guid.ToString(),
                    strippedAssembliesPath = FindStrippedAssemblies(projectRoot),
                    bclMscorlibPath = FindBclMscorlib(target),
                    defines = ReadPlayerDefines(projectRoot),
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
        /// Under Library/Bee/artifacts rather than in the build output, because an
        /// IL2CPP build converts these to C++ and ships no managed directory at all.
        /// </summary>
        private static string FindStrippedAssemblies(string projectRoot)
        {
            var artifacts = Path.Combine(projectRoot, "Library/Bee/artifacts");
            if (!Directory.Exists(artifacts))
            {
                return null;
            }

            string newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var program in Directory.GetDirectories(artifacts, "*PlayerBuildProgram"))
            {
                var managed = Path.Combine(program, "ManagedStripped");
                if (!Directory.Exists(managed))
                {
                    continue;
                }

                var stamp = Directory.GetLastWriteTimeUtc(managed);
                if (stamp > newestTime)
                {
                    newestTime = stamp;
                    newest = managed;
                }
            }

            return newest;
        }

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
        private static string ReadPlayerDefines(string projectRoot)
        {
            const string definePrefix = "-define:";

            var artifacts = Path.Combine(projectRoot, "Library/Bee/artifacts");
            if (!Directory.Exists(artifacts))
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

                return string.Join(";", lines
                    .Where(line => line.StartsWith(definePrefix, StringComparison.Ordinal))
                    .Select(line => line.Substring(definePrefix.Length)));
            }

            return "";
        }

        private static string FindBclMscorlib(BuildTarget target)
        {
            string platform;
            switch (target)
            {
                case BuildTarget.Android:
                case BuildTarget.StandaloneLinux64:
                    platform = "linux";
                    break;
                case BuildTarget.iOS:
                case BuildTarget.StandaloneOSX:
                    platform = "macos";
                    break;
                default:
                    platform = "win32";
                    break;
            }

            var named = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
            var flavour = PlayerSettings.GetScriptingBackend(named) == ScriptingImplementation.IL2CPP
                ? "unityaot"
                : "unityjit";

            var contents = EditorApplication.applicationContentsPath;
            var candidates = new[]
            {
                Path.Combine(contents, $"MonoBleedingEdge/lib/mono/{flavour}-{platform}/mscorlib.dll"),
                // Older layouts ship a single profile rather than one per platform.
                Path.Combine(contents, "MonoBleedingEdge/lib/mono/unity/mscorlib.dll"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
