import os
import sys
import tempfile
import types
import unittest
from unittest import mock

SCRIPT_ROOT = os.path.dirname(os.path.abspath(__file__))
CONSOLE_CLIENT_ROOT = os.path.dirname(os.path.dirname(SCRIPT_ROOT))
SITE_PACKAGES_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "site-packages")
_ADDED_SITE_PACKAGES_PATH = False

if CONSOLE_CLIENT_ROOT not in sys.path:
    sys.path.insert(0, CONSOLE_CLIENT_ROOT)

if SITE_PACKAGES_PATH not in sys.path:
    sys.path.insert(0, SITE_PACKAGES_PATH)
    _ADDED_SITE_PACKAGES_PATH = True

_original_bootstrap_module = sys.modules.get("csharp_bootstrap")
_original_repl_core_module = sys.modules.get("csharp_repl_core")
sys.modules["csharp_bootstrap"] = types.SimpleNamespace(bootstrap_repl_dependencies=lambda: None, ensure_supported_python=lambda: None)
sys.modules["csharp_repl_core"] = types.SimpleNamespace(run_repl=lambda _args: None)

CORE_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "csharpconsole_core")
_ADDED_CORE_PATH = False
if CORE_PATH not in sys.path:
    sys.path.insert(0, CORE_PATH)
    _ADDED_CORE_PATH = True

try:
    from repl import config, direct_launch
    import csharp_repl as repl
finally:
    if _original_bootstrap_module is not None:
        sys.modules["csharp_bootstrap"] = _original_bootstrap_module
    else:
        del sys.modules["csharp_bootstrap"]

    if _original_repl_core_module is not None:
        sys.modules["csharp_repl_core"] = _original_repl_core_module
    else:
        del sys.modules["csharp_repl_core"]

    if _ADDED_CORE_PATH:
        sys.path.remove(CORE_PATH)

    if _ADDED_SITE_PACKAGES_PATH:
        sys.path.remove(SITE_PACKAGES_PATH)


class ReplDirectLaunchHelpersTests(unittest.TestCase):
    def test_extract_project_path_from_command_line_parses_quoted_project_path(self):
        command_line = (
            '"C:/Program Files/Unity/Editor/Unity.exe" '
            '-batchmode '
            '-projectPath "E:/Unity Projects/PackagesDemo" '
            '-logFile -'
        )

        project_path = direct_launch.extract_project_path_from_command_line(command_line)

        self.assertEqual(project_path, "E:/Unity Projects/PackagesDemo")

    def test_extract_project_path_from_command_line_parses_unquoted_project_path(self):
        command_line = (
            '"C:/Program Files/Unity/Editor/Unity.exe" '
            '-batchmode '
            '-projectPath E:/UnityProjects/PackagesDemo '
            '-logFile -'
        )

        project_path = direct_launch.extract_project_path_from_command_line(command_line)

        self.assertEqual(project_path, "E:/UnityProjects/PackagesDemo")

    def test_extract_project_path_from_command_line_parses_lowercase_projectpath(self):
        command_line = (
            '"C:/Program Files/Unity/Editor/Unity.exe" '
            '-batchmode '
            '-projectpath "E:/UnityProjects/LowerCaseFlag" '
            '-logFile -'
        )

        project_path = direct_launch.extract_project_path_from_command_line(command_line)

        self.assertEqual(project_path, "E:/UnityProjects/LowerCaseFlag")

    def test_parse_windows_unity_processes_json_parses_pid_start_and_command_line(self):
        output = (
            '[{"ProcessId":4132,"CreationDate":"20260330091501.123456+480",'
            '"CommandLine":"C:/Program Files/Unity/Editor/Unity.exe '
            '-projectpath E:/UnityProjects/PackagesDemo"}]'
        )

        processes = direct_launch.parse_windows_unity_processes_json(output)

        self.assertEqual(len(processes), 1)
        self.assertEqual(processes[0]["pid"], 4132)
        self.assertIsInstance(processes[0]["create_time"], float)
        self.assertIn("-projectpath", processes[0]["command_line"])

    def test_read_project_temp_state_returns_none_when_temp_csharpconsole_dir_missing(self):
        with tempfile.TemporaryDirectory() as temp_project:
            result = direct_launch.read_project_temp_state(temp_project)

        self.assertIsNone(result)

    def test_discover_direct_launch_candidates_uses_effective_port_from_refresh_state(self):
        fake_process = {
            "pid": 111,
            "create_time": 1000,
            "command_line": '"Unity.exe" -projectPath "E:/UnityProjects/Healthy"',
        }
        fake_state = {"effectivePort": 14523}

        with mock.patch.object(direct_launch, "list_unity_editor_processes", return_value=[fake_process]), \
                mock.patch.object(direct_launch, "read_project_temp_state", return_value=fake_state) as read_state, \
                mock.patch.object(direct_launch, "probe_editor_health", return_value={"ok": True}) as probe_health:
            result = direct_launch.discover_direct_launch_candidates()

        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["port"], 14523)
        read_state.assert_called_once_with("E:/UnityProjects/Healthy")
        probe_health.assert_called_once_with(direct_launch.DEFAULT_LOOPBACK_HOST, 14523)

    def test_discover_direct_launch_candidates_skips_instance_without_refresh_state_port(self):
        fake_process = {
            "pid": 111,
            "create_time": 1000,
            "command_line": '"Unity.exe" -projectPath "E:/UnityProjects/Healthy"',
        }

        with mock.patch.object(direct_launch, "list_unity_editor_processes", return_value=[fake_process]), \
                mock.patch.object(direct_launch, "read_project_temp_state", return_value={}) as read_state, \
                mock.patch.object(direct_launch, "probe_editor_health") as probe_health:
            result = direct_launch.discover_direct_launch_candidates()

        self.assertEqual(result, [])
        read_state.assert_called_once_with("E:/UnityProjects/Healthy")
        probe_health.assert_not_called()

    def test_list_unity_editor_processes_skips_batchmode_workers(self):
        output = (
            '[{"ProcessId":4132,"CreationDate":"20260330091501.123456+480",'
            '"CommandLine":"Unity.exe -projectPath E:/UnityProjects/PackagesDemo"},'
            '{"ProcessId":4133,"CreationDate":"20260330091501.123456+480",'
            '"CommandLine":"Unity.exe -batchMode -name AssetImportWorker0 -projectPath E:/UnityProjects/PackagesDemo"}]'
        )

        processes = direct_launch.parse_windows_unity_processes_json(output)

        self.assertEqual([item["pid"] for item in processes], [4132])

    def test_discover_direct_launch_candidates_keeps_only_healthy_editors(self):
        fake_processes = [
            {
                "pid": 111,
                "create_time": 1000,
                "command_line": '"Unity.exe" -projectPath "E:/UnityProjects/Healthy"',
            },
            {
                "pid": 222,
                "create_time": 2000,
                "command_line": '"Unity.exe" -projectPath "E:/UnityProjects/Unhealthy"',
            },
        ]

        with mock.patch.object(direct_launch, "list_unity_editor_processes", return_value=fake_processes), \
                mock.patch.object(direct_launch, "read_project_temp_state", side_effect=[{"effectivePort": 14500}, {"effectivePort": 14501}]), \
                mock.patch.object(direct_launch, "probe_editor_health", side_effect=[{"ok": True}, {"ok": False}]):
            result = direct_launch.discover_direct_launch_candidates()

        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["pid"], 111)
        self.assertEqual(result[0]["port"], 14500)
        self.assertEqual(result[0]["projectPath"], "E:/UnityProjects/Healthy")

    def test_probe_editor_health_uses_post_with_json_and_timeout(self):
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.getcode.return_value = 200

        with mock.patch("urllib.request.urlopen", return_value=response) as urlopen_mock:
            result = direct_launch.probe_editor_health("127.0.0.1", 14500, timeout_seconds=1.25)

        self.assertEqual(result, {"ok": True})
        urlopen_mock.assert_called_once()
        request_arg, kwargs = urlopen_mock.call_args
        request = request_arg[0]
        self.assertEqual(request.full_url, "http://127.0.0.1:14500/CSharpConsole/health")
        self.assertEqual(request.data, b"{}")
        self.assertEqual(request.get_method(), "POST")
        self.assertEqual(request.headers.get("Content-type"), "application/json")
        self.assertEqual(kwargs.get("timeout"), 1.25)

    def test_format_direct_launch_candidate_label_returns_expected_text(self):
        candidate = {
            "pid": 31415,
            "start": "2026-03-30 09:15:00",
            "projectPath": "E:/UnityProjects/PackagesDemo",
        }

        label = direct_launch.format_direct_launch_candidate_label(candidate)

        self.assertEqual(
            label,
            "PID 31415 | E:/UnityProjects/PackagesDemo",
        )

    def test_format_direct_launch_candidate_label_uses_only_pid_and_project_path(self):
        candidate = {
            "pid": 31415,
            "start": "2026-03-30 09:15:00",
            "projectPath": "E:/UnityProjects/PackagesDemo",
        }

        label = direct_launch.format_direct_launch_candidate_label(candidate)

        self.assertEqual(label, "PID 31415 | E:/UnityProjects/PackagesDemo")
        self.assertNotIn("2026-03-30 09:15:00", label)


class ReplDirectLaunchEntryTests(unittest.TestCase):
    def test_main_uses_direct_launch_when_no_args(self):
        direct_args = object()
        with mock.patch.object(repl, "resolve_direct_launch_args", return_value=direct_args) as resolve_args, \
                mock.patch.object(repl, "parse_repl_args") as parse_args, \
                mock.patch.object(repl, "run_repl") as run_repl:
            repl.main([])

        resolve_args.assert_called_once_with()
        parse_args.assert_not_called()
        run_repl.assert_called_once_with(direct_args)

    def test_resolve_direct_launch_args_prints_discovery_status_messages(self):
        candidate = {"pid": 101, "port": 14500, "projectPath": "A"}
        status_messages = []

        with mock.patch.object(direct_launch, "discover_direct_launch_candidates", return_value=[candidate]), \
                mock.patch.object(repl, "select_direct_launch_candidate", return_value=candidate):
            result = repl.resolve_direct_launch_args(status_writer=status_messages.append)

        self.assertEqual(
            status_messages,
            [
                "Discovering Unity Editor instances...",
                "Discovered 1 Unity Editor instance(s).",
            ],
        )
        self.assertEqual(result.port, 14500)

    def test_main_uses_parse_repl_args_when_args_present(self):
        parsed_args = object()
        with mock.patch.object(repl, "parse_repl_args", return_value=parsed_args) as parse_args, \
                mock.patch.object(repl, "resolve_direct_launch_args") as resolve_args, \
                mock.patch.object(repl, "run_repl") as run_repl:
            repl.main(["--ip", "127.0.0.1", "--port", "14500", "--mode", "editor"])

        parse_args.assert_called_once_with(["--ip", "127.0.0.1", "--port", "14500", "--mode", "editor"])
        resolve_args.assert_not_called()
        run_repl.assert_called_once_with(parsed_args)

    def test_select_direct_launch_candidate_uses_numbered_text_prompt(self):
        candidate_a = {"pid": 101, "port": 14500, "projectPath": "A"}
        candidate_b = {"pid": 202, "port": 14501, "projectPath": "B"}

        with mock.patch.object(direct_launch, "format_direct_launch_candidate_label", side_effect=["A", "B"]) as format_label, \
                mock.patch("builtins.print") as print_mock, \
                mock.patch("builtins.input", return_value="2") as input_mock:
            result = repl.select_direct_launch_candidate([candidate_a, candidate_b])

        self.assertIs(result, candidate_b)
        self.assertEqual(format_label.call_count, 2)
        print_mock.assert_any_call("Select Unity Editor instance:")
        print_mock.assert_any_call("1. A")
        print_mock.assert_any_call("2. B")
        input_mock.assert_called_once()
        self.assertFalse(hasattr(repl, "radiolist_dialog"))

    def test_resolve_direct_launch_args_exits_cleanly_when_picker_cancelled(self):
        candidate = {"pid": 101, "port": 14500, "projectPath": "A"}

        with mock.patch.object(direct_launch, "discover_direct_launch_candidates", return_value=[candidate]), \
                mock.patch.object(repl, "select_direct_launch_candidate", return_value=None):
            with self.assertRaises(SystemExit) as cm:
                repl.resolve_direct_launch_args()

        self.assertEqual(cm.exception.code, 0)

    def test_resolve_direct_launch_args_prints_empty_discovery_status_when_no_candidate_found(self):
        status_messages = []

        with mock.patch.object(direct_launch, "discover_direct_launch_candidates", return_value=[]):
            with self.assertRaises(SystemExit) as cm:
                repl.resolve_direct_launch_args(status_writer=status_messages.append)

        self.assertEqual(cm.exception.code, 0)
        self.assertEqual(
            status_messages,
            [
                "Discovering Unity Editor instances...",
                "No available Unity Editor instances found.",
            ],
        )


if __name__ == "__main__":
    unittest.main()
