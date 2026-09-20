using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Zh1Zh1.CSharpConsole.Service;

namespace Zh1Zh1.CSharpConsole.Editor.Compiler
{
    /// <summary>
    /// Writes a build's compile set next to the player as a zip.
    ///
    /// A submission bound for a player is compiled in an editor but runs in the player.
    /// Compiled against the editor's own assemblies and symbols, it proves nothing: code
    /// calling an API the build stripped compiles and then fails in the player, and every
    /// #if takes whichever branch the editor's symbols pick. This zip is what lets an
    /// editor compile against the build instead -- whoever debugs the player registers it
    /// with their editor once, and every later submission for that build uses it.
    ///
    /// The layout is the one <see cref="CompileSetStore"/> reads: assemblies,
    /// <see cref="CompileSetStore.DefinesFileName"/> and
    /// <see cref="CompileSetStore.GuidFileName"/> at the root of the zip.
    /// </summary>
    internal static class CompileSetExporter
    {
        /// <summary>
        /// Beside the player rather than inside it: the zip is for whoever debugs the
        /// build, not for the build to load, and a player directory is often copied or
        /// packaged wholesale.
        /// </summary>
        private const string FileName = "CSharpConsoleCompileSet.zip";

        /// <summary>
        /// Compiling against the stripped set alone does not work: script submissions
        /// are wrapped in an async method, and a player that never awaits anything has
        /// had AsyncTaskMethodBuilder's members stripped out of mscorlib, which Roslyn
        /// reports as CS0656 before it looks at the submission at all. The BCL copy the
        /// editor ships is substituted for that one assembly, so the compiler keeps the
        /// members it requires while every other assembly still shows the build's real,
        /// stripped surface.
        /// </summary>
        private const string SubstitutedAssembly = "mscorlib.dll";

        internal static void Export(PlayerBuildRecord record, string playerOutputPath)
        {
            if (string.IsNullOrEmpty(record.strippedAssembliesPath) || !Directory.Exists(record.strippedAssembliesPath))
            {
                ConsoleLog.Warning($"Could not find the assemblies this build shipped, so no {FileName} was exported for it.");
                return;
            }

            var directory = Path.GetDirectoryName(playerOutputPath);
            if (string.IsNullOrEmpty(directory))
            {
                ConsoleLog.Warning($"Could not work out where to put {FileName}: this build reported no output path.");
                return;
            }

            var zipPath = Path.Combine(directory, FileName);
            var bcl = record.bclMscorlibPath;
            var substitute = !string.IsNullOrEmpty(bcl) && File.Exists(bcl);

            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var dll in Directory.GetFiles(record.strippedAssembliesPath, "*.dll"))
                {
                    var name = Path.GetFileName(dll);
                    if (substitute && string.Equals(name, SubstitutedAssembly, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddFile(archive, dll, name);
                }

                if (substitute)
                {
                    AddFile(archive, bcl, SubstitutedAssembly);
                }
                else
                {
                    ConsoleLog.Warning(
                        $"No editor copy of {SubstitutedAssembly} for this target; submissions may fail to compile "
                        + "with CS0656 if the build stripped the members Roslyn needs for a script.");
                }

                AddText(archive, CompileSetStore.DefinesFileName, record.defines ?? "");
                AddText(archive, CompileSetStore.GuidFileName, record.buildGuid);
            }

            ConsoleLog.Info($"Exported the compile set for build {record.buildGuid} to {zipPath}");
        }

        /// <summary>
        /// Entries are written by hand rather than with CreateFromDirectory: that lives in
        /// System.IO.Compression.FileSystem, which is not part of every scripting profile,
        /// while ZipArchive itself is.
        /// </summary>
        private static void AddFile(ZipArchive archive, string sourcePath, string entryName)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var source = File.OpenRead(sourcePath);
            using var target = entry.Open();
            source.CopyTo(target);
        }

        private static void AddText(ZipArchive archive, string entryName, string text)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var target = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(text);
            target.Write(bytes, 0, bytes.Length);
        }
    }
}
