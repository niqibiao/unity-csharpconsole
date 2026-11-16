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
    /// Exports a <see cref="PlayerBuildRecord"/> as each player build finishes.
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
                var settings = CSharpConsoleSettings.Load();
                if (!settings.exportCompileSetAfterBuild)
                {
                    return;
                }

                var exportPath = settings.ResolveExportPath();

                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
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
                var record = ReadArtifacts(projectRoot, engineDirectory, backend,
                    (report.summary.options & BuildOptions.Development) != 0, report.summary.outputPath,
                    (report.summary.options & BuildOptions.InstallInBuildFolder) != 0,
                    report.summary.platform == BuildTarget.StandaloneOSX,
                    report.summary.platform == BuildTarget.Android, report.summary.guid.ToString());

                CompileSetExporter.Export(record, exportPath);
            }
            catch (Exception e)
            {
                // A build that succeeded must not be reported as failed over this; the
                // player just has no compile set to align against.
                ConsoleLog.Warning($"Could not export the compile set for this player build: {e.Message}");
            }
        }

        internal static PlayerBuildRecord ReadArtifacts(string projectRoot, string engineDirectory, string backend,
            bool development, string outputPath, bool installInBuildFolder, bool appBundle, bool stagedOutput,
            string buildGuid)
        {
            var graph = FindPlayerGraph(projectRoot, engineDirectory, backend, development, outputPath,
                installInBuildFolder, appBundle, stagedOutput);
            var (strippedAssembliesPath, bclMscorlibPath, engineAssembly) =
                ParseLinkerArguments(ReadLinkerCommandLine(projectRoot, graph), backend);
            var defines = ReadPlayerDefines(Path.Combine(projectRoot, "Library/Bee/artifacts"), development, engineAssembly, backend);
            if (string.IsNullOrEmpty(defines))
            {
                throw new InvalidOperationException("Could not identify the compiler arguments for this player's target and backend.");
            }

            return new PlayerBuildRecord
            {
                buildGuid = buildGuid,
                strippedAssembliesPath = Path.GetFullPath(Path.Combine(projectRoot, strippedAssembliesPath)),
                bclMscorlibPath = string.IsNullOrEmpty(bclMscorlibPath) ? null
                    : Path.GetFullPath(Path.Combine(projectRoot, bclMscorlibPath)),
                defines = defines,
            };
        }

        /// <summary>
        /// The Bee graph Unity built this player with, found through the configuration
        /// Unity wrote beside it: every player build leaves a Player*-inputdata.json
        /// next to its Player*.dag.json, and the build being recorded is the one whose
        /// player support, backend, configuration, output path and install mode are the
        /// ones just built.
        ///
        /// The graph is what makes the linker's arguments trustworthy. Picked from the
        /// response files alone, they could belong to another build -- the newest file
        /// is not this build's when this build changed nothing, since Bee rewrites a
        /// file only when its content changes -- and some builds have none: their linker
        /// arguments are written into the graph itself. Graphs this build cannot be told
        /// apart from are refused rather than guessed between.
        ///
        /// Measured with Unity 6, an Android graph builds the Gradle project in Unity's
        /// staging directory, one per backend, and never names the APK it becomes, so
        /// for <paramref name="stagedOutput"/> the output path is not compared.
        /// </summary>
        private static string FindPlayerGraph(string projectRoot, string engineDirectory, string backend,
            bool development, string outputPath, bool installInBuildFolder, bool appBundle, bool stagedOutput)
        {
            var bee = Path.Combine(projectRoot, "Library/Bee");
            if (!Directory.Exists(bee))
            {
                throw new InvalidOperationException("The project has no Library/Bee directory, so this build left no player graph to read.");
            }

            var playerSupport = NormalizePath(projectRoot, engineDirectory, false);
            var destination = NormalizePath(projectRoot, outputPath, appBundle);
            var matches = Directory.GetFiles(bee, "Player*-inputdata.json")
                .Where(input =>
                {
                    var config = JsonUtility.FromJson<PlayerGraphInput>(
                        File.ReadAllText(input).Replace(PlayerBuildConfigKey, "\"" + nameof(PlayerGraphInput.PlayerBuildConfig) + "\""))
                        ?.PlayerBuildConfig;
                    return config != null
                        && BuiltWithBackend(config, backend)
                        && config.Development == development
                        && config.InstallIntoBuildsFolder == installInBuildFolder
                        && string.Equals(NormalizePath(projectRoot, config.PlayerPackage, false), playerSupport, s_PathComparison)
                        && (stagedOutput
                            || string.Equals(NormalizePath(projectRoot, config.DestinationPath, appBundle), destination, s_PathComparison));
                })
                .Select(input => input.Substring(0, input.Length - "-inputdata.json".Length) + ".dag.json")
                .ToArray();

            if (matches.Length != 1)
            {
                throw new InvalidOperationException(matches.Length == 0
                    ? $"No player graph in {bee} matches this build ({backend}, {(development ? "Development" : "Release")}, {outputPath})."
                    : $"Several player graphs match this build, and none can be told to be its own: {string.Join(", ", matches.Select(Path.GetFileName))}.");
            }

            return matches[0];
        }

        /// <summary>
        /// Unity 6 names the backend, as IL2CPP or Mono; Unity 2022 records only whether
        /// it was IL2CPP.
        /// </summary>
        private static bool BuiltWithBackend(PlayerGraphConfig config, string backend)
        {
            return string.IsNullOrEmpty(config.ScriptingBackend)
                ? config.UseIl2Cpp == string.Equals(backend, "Il2Cpp", StringComparison.OrdinalIgnoreCase)
                : string.Equals(config.ScriptingBackend, backend, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The key Unity's player build program files the build configuration under. It
        /// names a type, which a field cannot, so it is renamed before parsing.
        /// </summary>
        private const string PlayerBuildConfigKey = "\"PlayerBuildProgramLibrary.Data.PlayerBuildConfig\"";

        /// <summary>
        /// Unity records paths relative or absolute, with either separator. Compare them
        /// as absolute paths, ignoring case on Windows. A macOS app bundle compares
        /// without its .app suffix: an editor may add the suffix to the path it builds
        /// while the build report keeps the path as requested.
        /// </summary>
        private static string NormalizePath(string projectRoot, string path, bool appBundle)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }

            var normalized = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('\\', '/'))).Replace('\\', '/').TrimEnd('/');
            if (appBundle && normalized.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - ".app".Length);
            }

            return normalized;
        }

        private static readonly StringComparison s_PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// The command line of the one UnityLinker step in <paramref name="graphPath"/>,
        /// with each response file it names (<c>@file</c>) read in place of the
        /// reference. A graph with no linker step, or several, has nothing to say which
        /// stripped assemblies this player shipped.
        /// </summary>
        private static string ReadLinkerCommandLine(string projectRoot, string graphPath)
        {
            if (!File.Exists(graphPath))
            {
                throw new InvalidOperationException($"The player graph {graphPath} is missing.");
            }

            var linkers = (JsonUtility.FromJson<BeeGraph>(File.ReadAllText(graphPath))?.Nodes ?? Array.Empty<BeeNode>())
                .Where(node => node?.Annotation?.StartsWith("UnityLinker ", StringComparison.Ordinal) == true)
                .ToArray();
            if (linkers.Length != 1)
            {
                throw new InvalidOperationException(linkers.Length == 0
                    ? $"The player graph {Path.GetFileName(graphPath)} has no UnityLinker step; this build did not strip its assemblies."
                    : $"The player graph {Path.GetFileName(graphPath)} has {linkers.Length} UnityLinker steps, and no way to tell which one this build shipped.");
            }

            return s_CommandLineToken.Replace(linkers[0].Action ?? "", token =>
            {
                var value = token.Value.Replace("\"", "");
                if (!value.StartsWith("@", StringComparison.Ordinal) || value.Length == 1)
                {
                    return token.Value;
                }

                var path = Path.Combine(projectRoot, value.Substring(1));
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"UnityLinker's response file {path} is missing.");
                }

                return File.ReadAllText(path);
            });
        }

        // A token can include a quoted span, e.g. --out="path with spaces" or @"file name".
        private static readonly Regex s_CommandLineToken = new Regex("(?:[^\\s\"]|\"[^\"]*\")+");

        /// <summary>
        /// From the command line Unity handed UnityLinker: where it wrote the stripped
        /// assemblies (as given, usually relative to the project), the unstripped
        /// mscorlib of the profile it linked against, and the engine core module it was
        /// given. All come from the one command line, so they describe the same build
        /// and need no mapping from target and backend to BCL profile.
        ///
        /// The stripped assemblies are read from the linker's output directory, usually
        /// under Library/Bee/artifacts, rather than the build output, because an IL2CPP
        /// build converts them to C++ and ships no managed directory.
        /// </summary>
        private static (string strippedAssembliesPath, string bclMscorlibPath, string engineAssembly) ParseLinkerArguments(string commandLine, string expectedBackend)
        {
            string output = null;
            string mscorlib = null;
            string engineAssembly = null;
            string backend = null;
            foreach (Match token in s_CommandLineToken.Matches(commandLine))
            {
                var argument = token.Value.Replace("\"", "");
                if (argument.StartsWith("--out=", StringComparison.Ordinal))
                {
                    output = argument.Substring("--out=".Length);
                }
                else if (argument.StartsWith("--dotnetruntime=", StringComparison.Ordinal))
                {
                    backend = argument.Substring("--dotnetruntime=".Length);
                }
                else if (argument.StartsWith("--allowed-assembly=", StringComparison.Ordinal))
                {
                    var value = argument.Substring("--allowed-assembly=".Length);
                    var name = Path.GetFileName(value);
                    if (string.Equals(name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        mscorlib = value;
                    }
                    else if (string.Equals(name, "UnityEngine.CoreModule.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        engineAssembly = value;
                    }
                }
            }

            if (!string.Equals(backend, expectedBackend, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The linker step ran for the {backend ?? "unknown"} backend, not this build's {expectedBackend}.");
            }

            if (output == null || Path.GetFileName(output.TrimEnd('/', '\\')) != "ManagedStripped")
            {
                throw new InvalidOperationException($"The linker step wrote to {output ?? "no directory"}, not a ManagedStripped directory.");
            }

            return (output, mscorlib, engineAssembly);
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

        [Serializable]
        private class PlayerGraphInput
        {
            public PlayerGraphConfig PlayerBuildConfig;
        }

        /// <summary>The fields of Unity's player build configuration a build is matched on.</summary>
        [Serializable]
        private class PlayerGraphConfig
        {
            public string DestinationPath;
            public string PlayerPackage;
            public bool UseIl2Cpp;
            public string ScriptingBackend;
            public bool Development;
            public bool InstallIntoBuildsFolder;
        }

        [Serializable]
        private class BeeGraph
        {
            public BeeNode[] Nodes;
        }

        [Serializable]
        private class BeeNode
        {
            public string Annotation;
            public string Action;
        }
    }
}
