using System.Collections.Generic;
using System.Threading.Tasks;
using Zh1Zh1.CSharpConsole.Service;
using UnityEditor;
using UnityEngine;

namespace Zh1Zh1.CSharpConsole.Editor.EditorTools
{
    public struct CSharpIPPort
    {
        public bool RemoteIsEditor;
        public string IP;
        public int Port;
        public string RuntimeDllPath;
        public string CompileServerIP;
        public int CompileServerPort;
        public string RuntimeDefinesPath;
    }

    public sealed class RemoteConsoleWindow : EditorWindow
    {
        private readonly static string s_RuntimeDLLPathKey = $"{nameof(RemoteConsoleWindow)}_RUNTIME_DLL_PATH_KEY";
        private readonly static string s_CompileServerIPKey = $"{nameof(RemoteConsoleWindow)}_COMPILE_SERVER_IP_KEY";
        private readonly static string s_CompileServerPortKey = $"{nameof(RemoteConsoleWindow)}_COMPILE_SERVER_PORT_KEY";
        private readonly static string s_RuntimeDefinesPathKey = $"{nameof(RemoteConsoleWindow)}_RUNTIME_DEFINES_PATH_KEY";
        private readonly static string s_IPHistoryCountKey = $"{nameof(RemoteConsoleWindow)}_IP_HISTORY_COUNT";
        private readonly static string s_IPHistoryItemKey = $"{nameof(RemoteConsoleWindow)}_IP_HISTORY_";
        private readonly static string s_CompileIPHistoryCountKey = $"{nameof(RemoteConsoleWindow)}_COMPILE_IP_HISTORY_COUNT";
        private readonly static string s_CompileIPHistoryItemKey = $"{nameof(RemoteConsoleWindow)}_COMPILE_IP_HISTORY_";

        private const float LABEL_WIDTH = 140f;

        public string CompileServerIP = "127.0.0.1";
        public int CompileServerPort = ConsoleHttpService.EDITOR_PORT;
        public string IPAddress = "127.0.0.1";
        public bool RemoteIsEditor;
        public string RuntimeDllPath;
        public string RuntimeDefinesPath;

        private string m_EditorPort;
        private string m_PlayerPort;
        private string m_Port;
        private bool m_PrevRemoteIsEditor;
        private TaskCompletionSource<CSharpIPPort> m_Task;
        private List<string> m_IPHistory = new();
        private List<string> m_CompileIPHistory = new();

        public static async Task<CSharpIPPort> ShowWindow(int editorPort, int playerPort)
        {
            var w = GetWindow<RemoteConsoleWindow>("C# Console Connection");
            w.minSize = new Vector2(600, 300);
            w.maxSize = new Vector2(600, 300);
            w.m_Task = new TaskCompletionSource<CSharpIPPort>();
            w.m_EditorPort = editorPort.ToString();
            w.m_PlayerPort = playerPort.ToString();
            w.RemoteIsEditor = false;
            w.m_PrevRemoteIsEditor = false;
            w.m_Port = playerPort.ToString();
            w.RuntimeDllPath = EditorPrefs.GetString(s_RuntimeDLLPathKey, "");
            w.RuntimeDefinesPath = EditorPrefs.GetString(s_RuntimeDefinesPathKey, "");
            w.CompileServerIP = EditorPrefs.GetString(s_CompileServerIPKey, "127.0.0.1");
            w.CompileServerPort = EditorPrefs.GetInt(s_CompileServerPortKey, ConsoleHttpService.EDITOR_PORT);
            w.IPAddress = EditorPrefs.GetString(s_IPHistoryItemKey + "0", "127.0.0.1");
            LoadHistory(s_IPHistoryCountKey, s_IPHistoryItemKey, w.m_IPHistory);
            LoadHistory(s_CompileIPHistoryCountKey, s_CompileIPHistoryItemKey, w.m_CompileIPHistory);
            return await w.m_Task.Task;
        }

        private static void LoadHistory(string countKey, string itemKey, List<string> list)
        {
            list.Clear();
            int count = EditorPrefs.GetInt(countKey, 0);
            for (int i = 0; i < count; i++)
            {
                string val = EditorPrefs.GetString(itemKey + i, "");
                if (!string.IsNullOrEmpty(val))
                    list.Add(val);
            }
        }

        private static void SaveHistory(string value, string countKey, string itemKey)
        {
            if (string.IsNullOrEmpty(value))
                return;

            var list = new List<string> { value };
            var count = EditorPrefs.GetInt(countKey, 0);
            for (var i = 0; i < count; i++)
            {
                var existing = EditorPrefs.GetString(itemKey + i, "");
                if (!string.IsNullOrEmpty(existing) && existing != value)
                    list.Add(existing);
            }

            EditorPrefs.SetInt(countKey, list.Count);
            for (var i = 0; i < list.Count; i++)
                EditorPrefs.SetString(itemKey + i, list[i]);
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = LABEL_WIDTH;

            EditorGUILayout.HelpBox(
                $"Editor port range: {ConsoleHttpService.EDITOR_PORT}–{ConsoleHttpService.EDITOR_PORT + 9}    " +
                $"Runtime port range: {ConsoleHttpService.PLAYER_PORT}–{ConsoleHttpService.PLAYER_PORT + 9}",
                MessageType.Info);
            EditorGUILayout.Space(4);

            DrawCompileServerGroup();
            EditorGUILayout.Space(4);
            DrawRuntimeClientGroup();
            EditorGUILayout.Space(4);

            if (!RemoteIsEditor)
                DrawOptionalSettingsGroup();

            GUILayout.FlexibleSpace();
            DrawConnectButton();
            EditorGUILayout.Space(6);
        }

        private void DrawCompileServerGroup()
        {
            EditorGUILayout.LabelField("Compile Server", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                var rect = EditorGUILayout.GetControlRect();
                var labelRect = new Rect(rect.x, rect.y, LABEL_WIDTH, rect.height);
                var fieldRect = new Rect(rect.x + LABEL_WIDTH, rect.y, rect.width - LABEL_WIDTH - 20, rect.height);
                var btnRect = new Rect(rect.xMax - 18, rect.y, 18, rect.height);

                EditorGUI.LabelField(labelRect, "IP Address");
                CompileServerIP = EditorGUI.TextField(fieldRect, CompileServerIP);

                if (m_CompileIPHistory.Count > 0 && EditorGUI.DropdownButton(btnRect, GUIContent.none, FocusType.Passive))
                {
                    var menu = new GenericMenu();
                    foreach (var ip in m_CompileIPHistory)
                    {
                        var captured = ip;
                        menu.AddItem(new GUIContent(ip), ip == CompileServerIP, () =>
                        {
                            CompileServerIP = captured;
                            Repaint();
                        });
                    }
                    menu.DropDown(btnRect);
                }

                CompileServerPort = EditorGUILayout.IntField("Port", CompileServerPort);
            }
        }

        private void DrawRuntimeClientGroup()
        {
            EditorGUILayout.LabelField("Runtime Client", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                var rect = EditorGUILayout.GetControlRect();
                var labelRect = new Rect(rect.x, rect.y, LABEL_WIDTH, rect.height);
                var fieldRect = new Rect(rect.x + LABEL_WIDTH, rect.y, rect.width - LABEL_WIDTH - 20, rect.height);
                var btnRect = new Rect(rect.xMax - 18, rect.y, 18, rect.height);

                EditorGUI.LabelField(labelRect, "IP Address");
                IPAddress = EditorGUI.TextField(fieldRect, IPAddress);

                if (m_IPHistory.Count > 0 && EditorGUI.DropdownButton(btnRect, GUIContent.none, FocusType.Passive))
                {
                    var menu = new GenericMenu();
                    foreach (var ip in m_IPHistory)
                    {
                        var captured = ip;
                        menu.AddItem(new GUIContent(ip), ip == IPAddress, () =>
                        {
                            IPAddress = captured;
                            Repaint();
                        });
                    }
                    menu.DropDown(btnRect);
                }

                m_Port = EditorGUILayout.TextField("Port", m_Port);

                RemoteIsEditor = EditorGUILayout.Toggle("Remote Is Editor", RemoteIsEditor);
                if (RemoteIsEditor != m_PrevRemoteIsEditor)
                {
                    m_Port = RemoteIsEditor ? m_EditorPort : m_PlayerPort;
                    m_PrevRemoteIsEditor = RemoteIsEditor;
                }
            }
        }

        private void DrawOptionalSettingsGroup()
        {
            EditorGUILayout.LabelField("Runtime Client (optional settings, leave empty to use defaults)", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                DrawFolderPathField("Runtime Dll Path", ref RuntimeDllPath);
                DrawFilePathField("Runtime Defines File", ref RuntimeDefinesPath, "txt");
            }
        }

        private static void DrawFolderPathField(string label, ref string path)
        {
            DrawBrowsePathField(label, ref path, (l, p) => EditorUtility.OpenFolderPanel(l, p, ""));
        }

        private static void DrawFilePathField(string label, ref string path, string extension)
        {
            DrawBrowsePathField(label, ref path, (l, p) => EditorUtility.OpenFilePanel(l, p, extension));
        }

        private static void DrawBrowsePathField(string label, ref string path, System.Func<string, string, string> browse)
        {
            var rect = EditorGUILayout.GetControlRect();
            var labelRect = new Rect(rect.x, rect.y, LABEL_WIDTH, rect.height);
            var fieldRect = new Rect(rect.x + LABEL_WIDTH, rect.y, rect.width - LABEL_WIDTH - 24, rect.height);
            var btnRect = new Rect(rect.xMax - 22, rect.y, 22, rect.height);

            EditorGUI.LabelField(labelRect, label);
            path = EditorGUI.TextField(fieldRect, path);

            if (GUI.Button(btnRect, "..."))
            {
                string selected = browse(label, path ?? "");
                if (!string.IsNullOrEmpty(selected))
                    path = selected;
            }
        }

        private void DrawConnectButton()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Connect", GUILayout.Width(200), GUILayout.Height(28)))
                    Connect();
                GUILayout.FlexibleSpace();
            }
        }

        private void Connect()
        {
            if (m_Task == null)
            {
                ConsoleLog.Warning("Window state lost (domain reload?). Please reopen the window from menu.");
                Close();
                return;
            }

            if (!int.TryParse(m_Port, out var port) || port < 1 || port > 65535)
            {
                ConsoleLog.Warning($"Invalid runtime port: '{m_Port}'. Enter a number between 1 and 65535.");
                return;
            }

            if (CompileServerPort < 1 || CompileServerPort > 65535)
            {
                ConsoleLog.Warning($"Invalid compile server port: {CompileServerPort}. Enter a number between 1 and 65535.");
                return;
            }

            if (string.IsNullOrWhiteSpace(CompileServerIP))
                CompileServerIP = "127.0.0.1";

            SaveHistory(IPAddress, s_IPHistoryCountKey, s_IPHistoryItemKey);
            SaveHistory(CompileServerIP, s_CompileIPHistoryCountKey, s_CompileIPHistoryItemKey);
            EditorPrefs.SetString(s_RuntimeDLLPathKey, RuntimeDllPath ?? "");
            EditorPrefs.SetString(s_RuntimeDefinesPathKey, RuntimeDefinesPath ?? "");
            EditorPrefs.SetString(s_CompileServerIPKey, CompileServerIP);
            EditorPrefs.SetInt(s_CompileServerPortKey, CompileServerPort);
            ConsoleLog.Info($"Remote console configured: RuntimeClient={IPAddress}:{port}, CompileServer={CompileServerIP}:{CompileServerPort}");
            m_Task.SetResult(new CSharpIPPort
            {
                RemoteIsEditor = RemoteIsEditor,
                IP = IPAddress,
                Port = port,
                RuntimeDllPath = RemoteIsEditor ? "" : (RuntimeDllPath ?? ""),
                CompileServerIP = CompileServerIP,
                CompileServerPort = CompileServerPort,
                RuntimeDefinesPath = RemoteIsEditor ? "" : (RuntimeDefinesPath ?? "")
            });
            Close();
        }
    }
}
