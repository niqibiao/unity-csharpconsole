using System;
using System.Diagnostics;
using System.IO;
using Zh1Zh1.CSharpConsole.Service;
using UnityEditor;

namespace Zh1Zh1.CSharpConsole.Editor.EditorTools
{
    public static class ConsoleMenu
    {
        private const string LocalHost = "127.0.0.1";
        private const int MinPythonMajor = 3;
        private const int MinPythonMinor = 7;

        private readonly static string s_ToolDir = Path.GetFullPath("Packages/com.zh1zh1.csharpconsole/Editor/ExternalTool~/console-client");

#region CSharp Menu
        [MenuItem("Console/C#Console", false)]
        public static void LaunchLocalCSharpConsole()
        {
            LaunchCSharpConsole(true, LocalHost, ConsoleHttpService.Port, "", LocalHost, ConsoleHttpService.EDITOR_PORT, "");
        }

        [MenuItem("Console/RemoteC#Console", false)]
        public static async void ConnectToRemoteCSharpConsole()
        {
            try
            {
                var ret = await RemoteConsoleWindow.ShowWindow(ConsoleHttpService.EDITOR_PORT, ConsoleHttpService.PLAYER_PORT);
                LaunchCSharpConsole(ret.RemoteIsEditor, ret.IP, ret.Port, ret.RuntimeDllPath, ret.CompileServerIP, ret.CompileServerPort, ret.RuntimeDefinesPath);
            }
            catch (Exception e)
            {
                ConsoleLog.Error($"Console menu error: {e}");
            }
        }
#endregion

#region CSharp Launcher
        private static void LaunchCSharpConsole(
            bool remoteIsEditor, string ip, int port, string runtimeDllPath,
            string compileServerIP, int compileServerPort, string runtimeDefinesPath)
        {
            var python = EnsureSupportedPython();
            if (string.IsNullOrEmpty(python))
            {
                ConsoleLog.Error($"Python {MinPythonMajor}.{MinPythonMinor}+ not found. Please install a supported Python version and add it to your system PATH.");
                return;
            }

            var script = Path.Combine(s_ToolDir, "csharp_repl.py");
            var pyArgs = $"{Q(script)} --ip {ip} --port {port} --compile-ip {compileServerIP} --compile-port {compileServerPort}";

            if (remoteIsEditor)
            {
                pyArgs += " --editor";
            }

            if (!string.IsNullOrEmpty(runtimeDllPath))
            {
                pyArgs += $" --runtime-dll-path {Q(runtimeDllPath)}";
            }

            if (!string.IsNullOrEmpty(runtimeDefinesPath))
            {
                pyArgs += $" --runtime-defines {Q(runtimeDefinesPath)}";
            }


            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(wt))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = wt,
                    Arguments = $"--title {Q("C# Console")} -d {Q(s_ToolDir)} -- {Q(python)} {pyArgs}",
                    UseShellExecute = false,
                    CreateNoWindow = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = pyArgs,
                    WorkingDirectory = s_ToolDir,
                    UseShellExecute = false,
                    CreateNoWindow = false
                });
            }
        }

        private static string Q(string s) => $"\"{s}\"";

        private static string EnsureSupportedPython()
        {
            foreach (var name in new[] { "python3", "python" })
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    p?.WaitForExit(3000);
                    if (p == null || p.ExitCode != 0)
                    {
                        continue;
                    }

                    var output = p.StandardOutput.ReadToEnd().Trim();
                    if (string.IsNullOrEmpty(output))
                    {
                        output = p.StandardError.ReadToEnd().Trim();
                    }

                    if (TryParsePythonVersion(output, out var major, out var minor)
                        && IsSupportedPythonVersion(major, minor))
                    {
                        return name;
                    }
                }
                catch
                {
                    // not found, try next
                }
            }

            return null;
        }

        private static bool IsSupportedPythonVersion(int major, int minor)
        {
            return major > MinPythonMajor || (major == MinPythonMajor && minor >= MinPythonMinor);
        }

        private static bool TryParsePythonVersion(string output, out int major, out int minor)
        {
            major = 0;
            minor = 0;

            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            const string prefix = "Python ";
            if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var versionText = output.Substring(prefix.Length).Trim();
            var parts = versionText.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            return int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor);
        }
#endregion
    }
}
