using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Zh1Zh1.CSharpConsole.Service
{
    /// <summary>
    /// The compile sets this editor holds, and which player build each one is for.
    ///
    /// A compile set is the zip a player build leaves beside itself: the assemblies it
    /// shipped, the symbols it was compiled with (<see cref="DefinesFileName"/>) and the
    /// build it came from (<see cref="GuidFileName"/>). Registering one ties it to that
    /// build, so every later runtime compile for a player reporting the build finds it
    /// without the caller naming it again. Skipping ties the build to no set; that is a
    /// decision too, and is remembered the same way.
    ///
    /// Kept under the project's Library/ so decisions outlive editor restarts. Entries
    /// name their set relative to that root, so a moved project keeps them too.
    /// </summary>
    public static class CompileSetStore
    {
        public const string GuidFileName = "build-guid.txt";
        public const string DefinesFileName = "runtime-defines.txt";

        private const string SkipMarker = "skip";

        private static string s_Root;

        private static string SetsRoot => Path.Combine(s_Root, "sets");

        private static string DecisionsRoot => Path.Combine(s_Root, "builds");

        /// <summary>
        /// Where plain DLL uploads are extracted. Those are named by the caller on every
        /// request and never registered, so they stay out of the project.
        /// </summary>
        private static string UploadsRoot => Path.Combine(Path.GetTempPath(), "CSharpConsoleCache", "compileserver");

        /// <summary>Must be called on the main thread: the project root comes from Application.dataPath.</summary>
        internal static void Initialize(string projectRoot)
        {
            s_Root = Path.Combine(projectRoot, "Library", "CSharpConsole", "CompileSets");
        }

        internal static string ExtractUpload(byte[] zipBytes)
        {
            return Extract(zipBytes, UploadsRoot);
        }

        internal static string ExtractCompileSet(byte[] zipBytes)
        {
            return Extract(zipBytes, SetsRoot);
        }

        /// <summary>
        /// Extracts a zip into a directory under <paramref name="root"/> named by its
        /// content, or returns the one an identical zip already produced.
        /// </summary>
        private static string Extract(byte[] zipBytes, string root)
        {
            string contentHash;
            using (var sha = SHA256.Create())
            {
                contentHash = BitConverter.ToString(sha.ComputeHash(zipBytes)).Replace("-", "").Substring(0, 16);
            }

            var extractDir = Path.Combine(root, contentHash);
            if (Directory.Exists(extractDir))
            {
                return extractDir;
            }

            Directory.CreateDirectory(root);
            var tmpDir = extractDir + $".tmp.{System.Diagnostics.Process.GetCurrentProcess().Id}";
            try
            {
                Directory.CreateDirectory(tmpDir);
                using (var zipStream = new MemoryStream(zipBytes))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    archive.ExtractToDirectory(tmpDir);
                }

                Directory.Move(tmpDir, extractDir);
            }
            catch
            {
                try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
                throw;
            }

            return extractDir;
        }

        /// <summary>The build a set directory names, or null when it names none.</summary>
        internal static string ReadBuildGuid(string setDirectory)
        {
            if (string.IsNullOrEmpty(setDirectory))
            {
                return null;
            }

            var path = Path.Combine(setDirectory, GuidFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        /// <summary>
        /// The build a compile set zip names, or null when it names none. Read before
        /// extracting, so a zip that is refused never lands in Library/.
        /// </summary>
        internal static string ReadBuildGuid(byte[] zipBytes)
        {
            using (var zipStream = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry(GuidFileName);
                if (entry == null)
                {
                    return null;
                }

                using (var reader = new StreamReader(entry.Open()))
                {
                    return reader.ReadToEnd().Trim();
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="buildGuid"/> can name a decision on disk. It arrives from
        /// an uploaded zip or a player's answer, so it is checked before it becomes a path.
        /// </summary>
        internal static bool IsValidBuildGuid(string buildGuid)
        {
            if (string.IsNullOrEmpty(buildGuid) || buildGuid.Length > 64)
            {
                return false;
            }

            foreach (var c in buildGuid)
            {
                if (!Uri.IsHexDigit(c) && c != '-')
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool SameBuild(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal static void Register(string buildGuid, string setDirectory)
        {
            WriteDecision(buildGuid, Path.GetFileName(setDirectory));
        }

        internal static void Skip(string buildGuid)
        {
            WriteDecision(buildGuid, SkipMarker);
        }

        /// <summary>
        /// Whether anything was decided for <paramref name="buildGuid"/>. When it was,
        /// <paramref name="setDirectory"/> is the set to compile against, or null when
        /// alignment was skipped. A registered set whose files have since been cleaned up
        /// counts as undecided, so the caller is asked again rather than compiling against
        /// nothing.
        /// </summary>
        internal static bool TryLookup(string buildGuid, out string setDirectory)
        {
            setDirectory = null;
            if (!IsValidBuildGuid(buildGuid))
            {
                return false;
            }

            var entry = DecisionPath(buildGuid);
            if (!File.Exists(entry))
            {
                return false;
            }

            var value = File.ReadAllText(entry).Trim();
            if (value == SkipMarker)
            {
                return true;
            }

            var directory = Path.Combine(SetsRoot, value);
            if (!Directory.Exists(directory))
            {
                return false;
            }

            setDirectory = directory;
            return true;
        }

        private static void WriteDecision(string buildGuid, string value)
        {
            Directory.CreateDirectory(DecisionsRoot);
            File.WriteAllText(DecisionPath(buildGuid), value);
        }

        private static string DecisionPath(string buildGuid)
        {
            return Path.Combine(DecisionsRoot, buildGuid.ToLowerInvariant());
        }
    }
}
