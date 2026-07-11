using System;
using System.Collections.Generic;
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
                ConsoleLog.Error($"Python {MinPythonMajor}.{MinPythonMinor}+ not found. Please install a supported Python version and add it to your user or system PATH.");
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


            // 优先用 wt.exe 让 Windows Terminal 托管 REPL（规避 prompt_toolkit 在部分 ConPTY 下的
            // NoConsoleScreenBufferError 闪退）；wt 不存在时回退直连 python。
            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(wt))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = wt,
                    Arguments = $"--title {Q("C# Console")} -d {Q(s_ToolDir)} -- {Q(python)} {pyArgs}",
                    // UseShellExecute=true 让 ShellExecute 解析 wt 的 app-execution alias。
                    UseShellExecute = true
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
            foreach (var exe in EnumeratePythonCandidates())
            {
                if (TryGetSupportedPython(exe, out var resolved))
                {
                    return resolved;
                }
            }

            return null;
        }

        // 跨 进程PATH + user PATH(注册表) + system PATH(注册表) 收集候选 python 可执行文件的绝对路径，
        // 返回绝对路径以兼容 user PATH，并避免 wt.exe 在自身（可能陈旧的）环境里解析裸名失败。
        private static IEnumerable<string> EnumeratePythonCandidates()
        {
            var names = new[] { "python3.exe", "python.exe" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in EnumeratePathDirectories())
            {
                foreach (var name in names)
                {
                    string full;
                    try
                    {
                        full = Path.Combine(dir, name);
                    }
                    catch
                    {
                        continue;
                    }

                    if (seen.Add(full) && File.Exists(full))
                    {
                        yield return full;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumeratePathDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
            };

            foreach (var src in sources)
            {
                if (string.IsNullOrEmpty(src))
                {
                    continue;
                }

                foreach (var dir in src.Split(Path.PathSeparator))
                {
                    var d = dir.Trim().Trim('"');
                    // 跳过微软商店的 python 占位 alias（0 字节 reparse point，直接启动会弹商店或报找不到文件）
                    if (d.Length == 0 || d.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (seen.Add(d))
                    {
                        yield return d;
                    }
                }
            }
        }

        private static bool TryGetSupportedPython(string exe, out string resolved)
        {
            resolved = null;

            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                p?.WaitForExit(3000);
                if (p == null || p.ExitCode != 0)
                {
                    return false;
                }

                var output = p.StandardOutput.ReadToEnd().Trim();
                if (string.IsNullOrEmpty(output))
                {
                    output = p.StandardError.ReadToEnd().Trim();
                }

                if (TryParsePythonVersion(output, out var major, out var minor)
                    && IsSupportedPythonVersion(major, minor))
                {
                    resolved = exe;
                    return true;
                }
            }
            catch
            {
                // not a usable python, skip
            }

            return false;
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
