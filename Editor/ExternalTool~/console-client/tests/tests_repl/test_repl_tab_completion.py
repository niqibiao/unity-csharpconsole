import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

SCRIPT_ROOT = os.path.dirname(os.path.abspath(__file__))
CONSOLE_CLIENT_ROOT = os.path.dirname(os.path.dirname(SCRIPT_ROOT))
SITE_PACKAGES_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "site-packages")
_ADDED_SITE_PACKAGES_PATH = False

if CONSOLE_CLIENT_ROOT not in sys.path:
    sys.path.insert(0, CONSOLE_CLIENT_ROOT)

if SITE_PACKAGES_PATH not in sys.path:
    sys.path.insert(0, SITE_PACKAGES_PATH)
    _ADDED_SITE_PACKAGES_PATH = True

CORE_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "csharpconsole_core")
_ADDED_CORE_PATH = False
if CORE_PATH not in sys.path:
    sys.path.insert(0, CORE_PATH)
    _ADDED_CORE_PATH = True

try:
    from prompt_toolkit.keys import Keys
    from prompt_toolkit.clipboard import InMemoryClipboard
    from prompt_toolkit.clipboard.pyperclip import PyperclipClipboard
    from prompt_toolkit.layout.containers import FloatContainer
    from prompt_toolkit.layout.menus import CompletionsMenu
    from prompt_toolkit.key_binding.bindings.mouse import load_mouse_bindings
    from prompt_toolkit.search import SearchDirection
    from repl import builtins as repl_builtins
    from repl import client, config, scroll_router, session_ui, viewport_policy
    from repl.command_expr import (
    looks_like_command_expression_prefix,
    parse_command_expression,
)
    from repl.completion import CommandExpressionCompleter, RoslynCompleter
    from repl.transcript import TranscriptEntry
    import csharp_repl_core as repl
finally:
    if _ADDED_CORE_PATH:
        sys.path.remove(CORE_PATH)

    if _ADDED_SITE_PACKAGES_PATH:
        sys.path.remove(SITE_PACKAGES_PATH)


class FakeCompletion:
    def __init__(self, text, start_position=0):
        self.text = text
        self.display = text
        self.start_position = start_position


class FakeCompletionState:
    def __init__(self, *completion_texts, selected_index=None):
        self.completions = [FakeCompletion(text) for text in completion_texts]
        self.complete_index = selected_index

    def go_to_index(self, index):
        self.complete_index = index

    @property
    def current_completion(self):
        if self.complete_index is None:
            return None
        if 0 <= self.complete_index < len(self.completions):
            return self.completions[self.complete_index]
        return None


class FakeBuffer:
    def __init__(self, semantic_texts=(), selected_index=None):
        self.complete_state = (
            FakeCompletionState(*semantic_texts, selected_index=selected_index)
            if semantic_texts
            else None
        )
        self.inserted = []
        self.applied_completions = []
        self.text = ""

    def insert_text(self, text):
        self.inserted.append(text)
        self.text += text

    def apply_completion(self, completion):
        self.applied_completions.append(completion.text)
        if completion.start_position < 0:
            self.text = self.text[: completion.start_position]
        self.text += completion.text
        self.complete_state = None


class FakeEvent:
    def __init__(self, buffer):
        self.current_buffer = buffer


class FakeDocument:
    def __init__(self, text_before_cursor):
        self.text_before_cursor = text_before_cursor
        self.text = text_before_cursor


class FakeToolbarBuffer:
    def __init__(self, text="", semantic_texts=(), selected_index=None):
        self.document = FakeDocument(text)
        self.complete_state = (
            FakeCompletionState(*semantic_texts, selected_index=selected_index)
            if semantic_texts
            else None
        )


class FakeHistory:
    def __init__(self):
        self.entries = []

    def append_string(self, text):
        self.entries.append(text)


class ReplStateOverride:
    def __init__(self, *, runtime_mode, enableml, enable_completion, ip, port, compile_ip, compile_port, runtime_ip, runtime_port):
        self.runtime_mode = runtime_mode
        self.enableml = enableml
        self.enable_completion = enable_completion
        self.ip = ip
        self.port = port
        self.compile_ip = compile_ip
        self.compile_port = compile_port
        self.runtime_ip = runtime_ip
        self.runtime_port = runtime_port

    def __enter__(self):
        self.previous = {
            "runtime_mode": config.runtime_mode,
            "enableml": repl.enableml,
            "enable_completion": repl.enable_completion,
            "ip": config.ip,
            "port": config.port,
            "compile_ip": config.compile_ip,
            "compile_port": config.compile_port,
            "runtime_ip": config.runtime_ip,
            "runtime_port": config.runtime_port,
        }
        config.runtime_mode = self.runtime_mode
        repl.enableml = self.enableml
        repl.enable_completion = self.enable_completion
        config.ip = self.ip
        config.port = self.port
        config.compile_ip = self.compile_ip
        config.compile_port = self.compile_port
        config.runtime_ip = self.runtime_ip
        config.runtime_port = self.runtime_port
        return self

    def __exit__(self, exc_type, exc, tb):
        config.runtime_mode = self.previous["runtime_mode"]
        repl.enableml = self.previous["enableml"]
        repl.enable_completion = self.previous["enable_completion"]
        config.ip = self.previous["ip"]
        config.port = self.previous["port"]
        config.compile_ip = self.previous["compile_ip"]
        config.compile_port = self.previous["compile_port"]
        config.runtime_ip = self.previous["runtime_ip"]
        config.runtime_port = self.previous["runtime_port"]


class FakeEventHook:
    def __init__(self):
        self.handlers = []

    def __iadd__(self, handler):
        self.handlers.append(handler)
        return self


class ApplicationSpy:
    def __init__(self, *args, **kwargs):
        self.args = args
        self.kwargs = kwargs
        self.layout = kwargs.get("layout")
        self.style = kwargs.get("style")
        self.current_buffer = None
        self.run_calls = 0
        self.invalidate_calls = 0

    def run(self):
        self.run_calls += 1
        return None

    def invalidate(self):
        self.invalidate_calls += 1


class FakeCurrentBufferForSubmit:
    def __init__(self, text=""):
        self.document = FakeDocument(text)
        self.insertions = []

    def insert_text(self, value):
        self.insertions.append(value)
        self.document.text += value
        self.document.text_before_cursor += value


class FakeLayout:
    def __init__(self):
        self.focused = None

    def focus(self, target):
        self.focused = target


class FakeAppForSubmit:
    def __init__(self, text=""):
        self.current_buffer = FakeCurrentBufferForSubmit(text)
        self.exit_result = None
        self.layout = FakeLayout()

    def exit(self, result=None):
        self.exit_result = result


class BuiltinCommandCompletionTests(unittest.TestCase):
    def create_builtin_registry(self):
        registry = repl_builtins.BuiltinRegistry()
        repl_builtins.register_default_builtins(
            registry,
            {
                "set_enable_completion": lambda _enabled: None,
                "roslyn_invalidate": lambda: None,
                "invalidate_command_catalog": lambda: None,
                "execute_repl_snippet": lambda _message, reset=False: None,
            },
        )
        return registry

    def test_builtin_commands_use_slash_prefix_and_expected_order(self):
        registry = self.create_builtin_registry()

        self.assertEqual(
            registry.order,
            [
                "/help",
                "/completion",
                "/theme",
                "/using",
                "/define",
                "/reload",
                "/reset",
                "/clear",
                "/dofile",
            ],
            "Builtin command completion order should advertise slash-prefixed commands",
        )

    def test_builtin_commands_advertise_slash_parameter_formats(self):
        registry = self.create_builtin_registry()

        self.assertEqual(
            registry.commands["/completion"]["completion"],
            "/completion <0|1>",
            "Completion builtin should advertise slash-prefixed usage",
        )
        self.assertEqual(
            registry.commands["/dofile"]["completion"],
            "/dofile <path>",
            "Dofile builtin should advertise slash-prefixed usage",
        )

    def test_builtin_completer_matches_line_start_slash_prefix(self):
        completions = list(repl.builtin_cmd_completer.get_completions(FakeDocument("/do"), None))

        self.assertTrue(
            any(completion.text == "/dofile <path>" for completion in completions),
            "Builtin completer should suggest slash-prefixed builtin commands from line-start '/' input",
        )

    def test_builtin_completer_does_not_match_mid_line_slash(self):
        completions = list(repl.builtin_cmd_completer.get_completions(FakeDocument("Debug.Log(\"x\"); /do"), None))

        self.assertEqual(
            completions,
            [],
            "Builtin completer should not activate for slash text that is not at line start",
        )

    def test_builtin_completer_ignores_line_comment_prefix(self):
        completions = list(repl.builtin_cmd_completer.get_completions(FakeDocument("// comment"), None))

        self.assertEqual(
            completions,
            [],
            "Builtin completer should not activate for C# line comments that begin with '//'",
        )

    def test_builtin_completer_ignores_block_comment_prefix(self):
        completions = list(repl.builtin_cmd_completer.get_completions(FakeDocument("/* comment"), None))

        self.assertEqual(
            completions,
            [],
            "Builtin completer should not activate for C# block comments that begin with '/*'",
        )


class FakeChangeBuffer:
    def __init__(self, text_before_cursor):
        self.document = FakeDocument(text_before_cursor)
        self.start_completion_calls = []

    def start_completion(self, select_first=False):
        self.start_completion_calls.append(select_first)


class BuiltinCommandFeedbackTests(unittest.TestCase):
    def create_builtin_registry(self):
        registry = repl_builtins.BuiltinRegistry()
        repl_builtins.register_default_builtins(
            registry,
            {
                "set_enable_completion": lambda _enabled: None,
                "roslyn_invalidate": lambda: None,
                "invalidate_command_catalog": lambda: None,
                "execute_repl_snippet": lambda _message, reset=False: None,
                "clear_transcript": lambda: False,
            },
        )
        return registry

    def test_open_local_file_queues_external_open_until_after_render_when_repl_is_running(self):
        previous_get_app = repl_builtins.get_app_or_none
        previous_run_in_terminal = repl_builtins.run_in_terminal
        previous_startfile = getattr(repl_builtins.os, "startfile", None)

        calls = []

        class _AppStub:
            _is_running = True
            def __init__(self):
                self._csharpconsole_queue_external_open = lambda opener: calls.append(("queue", opener))

        def _run_in_terminal(func, render_cli_done=False, in_executor=False):
            calls.append(("run_in_terminal", render_cli_done, in_executor))
            return None

        try:
            repl_builtins.get_app_or_none = lambda: _AppStub()
            repl_builtins.run_in_terminal = _run_in_terminal
            repl_builtins.os.startfile = lambda path: calls.append(("startfile", path))

            repl_builtins.open_local_file("Defines.txt")
        finally:
            repl_builtins.get_app_or_none = previous_get_app
            repl_builtins.run_in_terminal = previous_run_in_terminal
            if previous_startfile is not None:
                repl_builtins.os.startfile = previous_startfile

        self.assertEqual(calls[0][0], "queue")
        self.assertTrue(callable(calls[0][1]))
        self.assertEqual(len(calls), 1)

    def test_using_builtin_prints_manual_edit_help_without_opening_file(self):
        registry = self.create_builtin_registry()
        previous_path = repl_builtins.config._default_using_path
        previous_open_config_file = repl_builtins.open_config_file
        open_calls = []

        try:
            with tempfile.TemporaryDirectory() as temp_dir:
                target_path = os.path.join(temp_dir, "DefaultUsing.cs")
                repl_builtins.config._default_using_path = target_path
                repl_builtins.open_config_file = lambda *args, **kwargs: open_calls.append((args, kwargs))

                payload = repl_builtins.process_builtin_cmd("/using", registry.commands)
                self.assertTrue(os.path.isfile(target_path))
                with open(target_path, "r", encoding="utf-8") as created_file:
                    self.assertEqual(
                        created_file.read(),
                        "// One using per line, for example:\n// using System;\n// using UnityEngine;\n",
                    )
        finally:
            repl_builtins.config._default_using_path = previous_path
            repl_builtins.open_config_file = previous_open_config_file

        self.assertTrue(payload["handled"])
        self.assertTrue(payload["result"]["ok"])
        output_text = payload["result"]["data"]["text"]
        self.assertEqual(open_calls, [])
        self.assertIn("Open this file and edit it manually:", output_text)
        self.assertIn(os.path.abspath(target_path), output_text)
        self.assertIn("using System;", output_text)
        self.assertIn("Only lines in the form 'using Namespace;' are loaded.", output_text)
        self.assertIn("After saving, run /reload", output_text)

    def test_define_builtin_prints_manual_edit_help_without_opening_file(self):
        registry = self.create_builtin_registry()
        previous_path = repl_builtins.config._default_define_path
        previous_open_config_file = repl_builtins.open_config_file
        open_calls = []

        try:
            with tempfile.TemporaryDirectory() as temp_dir:
                target_path = os.path.join(temp_dir, "Defines.txt")
                repl_builtins.config._default_define_path = target_path
                repl_builtins.open_config_file = lambda *args, **kwargs: open_calls.append((args, kwargs))

                payload = repl_builtins.process_builtin_cmd("/define", registry.commands)
                self.assertTrue(os.path.isfile(target_path))
                with open(target_path, "r", encoding="utf-8") as created_file:
                    self.assertEqual(
                        created_file.read(),
                        "// Format: SYM1;SYM2;... Clear the file to use editor defaults\n",
                    )
        finally:
            repl_builtins.config._default_define_path = previous_path
            repl_builtins.open_config_file = previous_open_config_file

        self.assertTrue(payload["handled"])
        self.assertTrue(payload["result"]["ok"])
        output_text = payload["result"]["data"]["text"]
        self.assertEqual(open_calls, [])
        self.assertIn("Open this file and edit it manually:", output_text)
        self.assertIn(os.path.abspath(target_path), output_text)
        self.assertIn("SYM1;SYM2;SYM3", output_text)
        self.assertIn("Only the first non-empty line that does not start with // is used.", output_text)
        self.assertIn("Clear the file to use editor default defines.", output_text)
        self.assertIn("After saving, run /reload", output_text)

    def test_builtin_clear_command_uses_transcript_clear_when_available(self):
        registry = repl_builtins.BuiltinRegistry()
        clear_calls = []
        repl_builtins.register_default_builtins(
            registry,
            {
                "set_enable_completion": lambda _enabled: None,
                "roslyn_invalidate": lambda: None,
                "invalidate_command_catalog": lambda: None,
                "execute_repl_snippet": lambda _message, reset=False: None,
                "clear_transcript": lambda: clear_calls.append("clear") or True,
            },
        )

        previous_os_name = repl.os.name
        previous_os_system = repl.os.system
        os_calls = []
        try:
            repl.os.name = "nt"
            repl.os.system = lambda command: os_calls.append(command)

            payload = repl_builtins.process_builtin_cmd("/clear", registry.commands)
        finally:
            repl.os.name = previous_os_name
            repl.os.system = previous_os_system

        self.assertTrue(payload["handled"])
        self.assertTrue(payload["result"]["ok"])
        self.assertEqual(clear_calls, ["clear"])
        self.assertEqual(os_calls, [])
        self.assertEqual(payload["result"]["data"]["text"], "")
        self.assertTrue(payload["result"]["data"]["silent"])

    def test_builtin_clear_command_clears_terminal_without_default_success_message(self):
        previous_os_name = repl.os.name
        previous_os_system = repl.os.system
        clear_calls = []
        try:
            repl.os.name = "nt"
            repl.os.system = lambda command: clear_calls.append(command)

            stream = io.StringIO()
            with redirect_stdout(stream):
                handled = repl.process_builtin_cmd("/clear")

            self.assertTrue(handled)
            self.assertEqual(clear_calls, ["cls"])
            self.assertEqual(stream.getvalue(), "")
        finally:
            repl.os.name = previous_os_name
            repl.os.system = previous_os_system

    def test_builtin_command_with_existing_output_keeps_existing_message(self):
        previous_enable_completion = repl.enable_completion
        try:
            stream = io.StringIO()
            with redirect_stdout(stream):
                handled = repl.process_builtin_cmd("/completion 9")

            self.assertTrue(handled)
            self.assertEqual(stream.getvalue(), "Usage: /completion 0|1\n\n")
        finally:
            repl.enable_completion = previous_enable_completion

    def test_process_builtin_cmd_ignores_non_slash_prefixed_input(self):
        registry = repl_builtins.BuiltinRegistry()

        result = repl_builtins.process_builtin_cmd("@clear", registry.commands)

        self.assertEqual(
            result,
            {"handled": False, "result": None},
            "Legacy at-prefixed commands should not be treated as builtins anymore",
        )

    def test_process_builtin_cmd_ignores_line_comment_prefix(self):
        registry = repl_builtins.BuiltinRegistry()

        result = repl_builtins.process_builtin_cmd("// comment", registry.commands)

        self.assertEqual(
            result,
            {"handled": False, "result": None},
            "C# line comments should be treated as code input, not slash builtins",
        )

    def test_process_builtin_cmd_reports_unknown_slash_command_as_builtin_error(self):
        registry = repl_builtins.BuiltinRegistry()

        result = repl_builtins.process_builtin_cmd("/notacommand", registry.commands)

        self.assertTrue(result["handled"])
        payload = result["result"]
        self.assertEqual(payload["ok"], False)
        self.assertEqual(payload["type"], "builtin_error")
        self.assertEqual(payload["summary"], "Unknown command: /notacommand")
        self.assertEqual(payload["data"]["text"], "")
        self.assertEqual(payload["data"]["silent"], False)


class AcceptCompletionTests(unittest.TestCase):
    def invoke(self, buffer):
        repl.accept_completion(FakeEvent(buffer))

    def test_accepts_selected_completion(self):
        buffer = FakeBuffer(
            semantic_texts=("WriteLine", "Write"),
            selected_index=0,
        )

        self.invoke(buffer)

        self.assertEqual(
            buffer.applied_completions,
            ["WriteLine"],
            "Tab should accept the selected completion item",
        )
        self.assertEqual(
            buffer.inserted,
            [],
            "Completion should be applied via apply_completion instead of insert_text",
        )
        self.assertIsNone(
            buffer.complete_state,
            "Completion state should be cleared after accepting",
        )

    def test_selects_first_item_when_none_selected(self):
        buffer = FakeBuffer(
            semantic_texts=("WriteLine", "Write"),
            selected_index=None,
        )

        self.invoke(buffer)

        self.assertEqual(
            buffer.applied_completions,
            ["WriteLine"],
            "Tab should auto-select and accept the first completion when none is selected",
        )
        self.assertIsNone(
            buffer.complete_state,
            "Completion state should be cleared after accepting",
        )

    def test_no_completion_menu_is_no_op(self):
        buffer = FakeBuffer()

        self.invoke(buffer)

        self.assertEqual(
            buffer.inserted,
            [],
            "Tab should do nothing when no completion menu is open",
        )
        self.assertIsNone(
            buffer.complete_state,
            "Completion state should remain empty when there is nothing to accept",
        )


class PromptToolkitIntegrationTests(unittest.TestCase):
    def test_theme_command_shows_syntax_highlighted_code_preview_panel(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            self.assertFalse(session.theme_preview_container.filter())

            session.default_buffer.text = "/theme material"

            self.assertTrue(session.theme_preview_container.filter())
            label_text = "".join(text for _style, text in session.theme_preview_label.text())
            preview_fragments = session.theme_preview_code.text()
            preview_text = "".join(text for _style, text, *_rest in preview_fragments)
            self.assertIn("material", label_text)
            self.assertIn("public sealed class ThemePreview : MonoBehaviour", preview_text)
            self.assertIn("// Comments, types, numbers, strings, null and interpolation", preview_text)
            self.assertIn("[System.Serializable]", preview_text)
            self.assertIn("List<int>", preview_text)
            self.assertIn("= true", preview_text)
            self.assertIn("(string)null", preview_text)
            self.assertIn('string player = "Codex"', preview_text)
            self.assertIn('Debug.Log($"Player:{player}', preview_text)
            self.assertLessEqual(len(preview_text.splitlines()), 9)
            self.assertTrue(any("pygments." in style for style, _text, *_rest in preview_fragments))

            session.default_buffer.text = "Debug.Log(1);"
            self.assertFalse(session.theme_preview_container.filter())
        finally:
            repl.theme_manager.clear_preview()
            repl.Application = previous_application
            repl.session = previous_session

    def test_ensure_prompt_session_enables_csharp_highlighting(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        lexer = session.lexer
        self.assertIsNotNone(
            lexer,
            "REPL shell should configure a lexer so typed C# code is syntax highlighted",
        )

        highlighted_line = lexer.lex_document(repl.Document("using System;"))(0)
        self.assertTrue(
            any(text == "using" and style for style, text in highlighted_line),
            "The C# lexer should apply a visible style to C# keywords like 'using'",
        )
        self.assertEqual(
            lexer.__class__.__name__,
            "PygmentsLexer",
            "REPL should use prompt_toolkit's PygmentsLexer adapter for C# highlighting",
        )
        self.assertIsNotNone(
            session.style,
            "REPL shell should provide a Pygments-based style so lexer tokens render with visible colors",
        )

    def test_ensure_prompt_session_layout_restores_completion_menu_float(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertIsInstance(
            session.app.layout.container,
            FloatContainer,
            "Custom REPL layout should be wrapped in FloatContainer so completion overlays can be rendered",
        )
        completion_floats = [flt for flt in session.app.layout.container.floats if isinstance(flt.content, CompletionsMenu)]
        self.assertTrue(
            completion_floats,
            "Custom REPL layout should include prompt_toolkit CompletionsMenu float so completion menu is visible",
        )

    def test_ensure_prompt_session_uses_supported_clipboard_backend(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertIsInstance(
            session.app.kwargs.get("clipboard"),
            (PyperclipClipboard, InMemoryClipboard),
            "REPL application should provide either a system clipboard backend or the documented in-memory fallback",
        )

    def test_ensure_prompt_session_input_control_has_search_buffer(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertIsNotNone(
            session.input_control.search_buffer_control,
            "Input buffer control should be wired to a search buffer so Ctrl+R reverse search works again",
        )

    def test_transcript_input_fragments_use_lexer_styles(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        entry = TranscriptEntry(entry_type="input", text="using System;", created_at="2026-04-05T00:00:00.000Z")
        fragments = session.transcript_control._render_entry_fragments(entry)

        self.assertTrue(
            any("pygments.keyword" in style for style, _text, *_rest in fragments),
            "Transcript input should reuse the configured lexer styles so C# code in history is syntax highlighted",
        )


class ReverseSearchBindingTests(unittest.TestCase):
    def test_ctrl_r_binding_is_present(self):
        bound_sequences = [binding.keys for binding in repl.bindings.bindings]
        self.assertNotIn(
            ("c-r",),
            bound_sequences,
            "Base REPL bindings should not shadow prompt_toolkit's emacs reverse-search binding",
        )

    def test_application_shell_restores_search_toolbar_control(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertIsNotNone(session.search_buffer_control)
        self.assertIs(session.search_buffer_control, session.search_toolbar.control)
        self.assertIs(session.input_control.search_buffer_control, session.search_buffer_control)

    def test_application_shell_search_toolbar_prompt_shows_search_shortcuts(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            processor = session.search_buffer_control.input_processors[0]
            search_state = type("SearchStateStub", (), {"direction": SearchDirection.BACKWARD})()
            session.search_toolbar.control.searcher_search_state = search_state

            import prompt_toolkit.widgets.toolbars as toolbars_module
            original_toolbars_get_app = toolbars_module.get_app
            toolbars_module.get_app = lambda: type(
                "AppStub",
                (),
                {"layout": type("LayoutStub", (), {"search_links": [session.search_toolbar.control]})()},
            )()
            try:
                prompt_text = processor.text()
            finally:
                toolbars_module.get_app = original_toolbars_get_app
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(prompt_text, "")

    def test_is_search_active_reads_layout_search_flag(self):
        previous_get_app = repl.get_app
        try:
            repl.get_app = lambda: type(
                "AppStub",
                (),
                {"layout": type("LayoutStub", (), {"is_searching": True})()},
            )()
            self.assertTrue(repl._is_search_active())
        finally:
            repl.get_app = previous_get_app


class ScrollRouterModuleTests(unittest.TestCase):
    def test_scroll_router_prioritizes_completion_then_transcript(self):
        self.assertEqual(
            scroll_router.resolve_wheel_target(completion_open=True),
            scroll_router.WHEEL_TARGET_COMPLETION,
        )
        self.assertEqual(
            scroll_router.resolve_wheel_target(completion_open=False),
            scroll_router.WHEEL_TARGET_TRANSCRIPT,
        )


class ViewportPolicyModuleTests(unittest.TestCase):
    def test_compute_input_visible_lines_caps_at_max(self):
        self.assertEqual(viewport_policy.compute_input_visible_lines("one", max_visible_lines=8), 1)
        self.assertEqual(viewport_policy.compute_input_visible_lines("a\nb\nc", max_visible_lines=8), 3)
        self.assertEqual(viewport_policy.compute_input_visible_lines("\n".join(str(i) for i in range(20)), max_visible_lines=8), 8)

    def test_is_transcript_at_bottom_uses_render_info(self):
        window = type("WindowStub", (), {"render_info": type("RenderInfo", (), {"vertical_scroll": 12, "content_height": 20, "window_height": 8})()})()
        self.assertTrue(viewport_policy.is_transcript_at_bottom(window))

        window = type("WindowStub", (), {"render_info": type("RenderInfo", (), {"vertical_scroll": 10, "content_height": 20, "window_height": 8})()})()
        self.assertFalse(viewport_policy.is_transcript_at_bottom(window))


class MouseSupportTests(unittest.TestCase):
    def test_application_shell_enables_prompt_toolkit_mouse_support(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertTrue(
            session.app.kwargs.get("mouse_support"),
            "REPL application should enable prompt_toolkit mouse support so wheel behavior follows the active subwindow",
        )

    def test_base_bindings_override_scroll_wheel_keys_for_transcript_routing(self):
        bound_sequences = [binding.keys for binding in repl.bindings.bindings]
        self.assertIn((Keys.ScrollUp,), bound_sequences)
        self.assertIn((Keys.ScrollDown,), bound_sequences)
        self.assertIn((Keys.WindowsMouseEvent,), bound_sequences)
        self.assertIn((Keys.Vt100MouseEvent,), bound_sequences)

    def test_application_merged_key_bindings_include_default_mouse_bindings(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            merged = session.app.kwargs.get("key_bindings")
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertIsNotNone(merged)
        self.assertIsNotNone(load_mouse_bindings())

    def test_scroll_up_binding_calls_transcript_window_scroll(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _WindowStub:
            def __init__(self):
                self.up_calls = 0
                self.render_info = object()

            def _scroll_up(self):
                self.up_calls += 1

        class _ShellStub:
            def __init__(self):
                self.transcript_window = _WindowStub()
                self.default_buffer = type("BufferStub", (), {"complete_state": None})()

            def scroll_transcript_window_up(self):
                self.transcript_window._scroll_up()

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": False})()
        try:
            scroll_up_binding = next(binding for binding in repl.bindings.bindings if binding.keys == (Keys.ScrollUp,))
            scroll_up_binding.handler(type("FakeScrollEvent", (), {})())
        finally:
            repl.get_app = previous_search
            repl.session = previous_session

        self.assertEqual(shell.transcript_window.up_calls, 1)

    def test_scroll_down_binding_calls_transcript_window_scroll(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _WindowStub:
            def __init__(self):
                self.down_calls = 0
                self.render_info = object()

            def _scroll_down(self):
                self.down_calls += 1

        class _ShellStub:
            def __init__(self):
                self.transcript_window = _WindowStub()
                self.default_buffer = type("BufferStub", (), {"complete_state": None})()

            def scroll_transcript_window_down(self):
                self.transcript_window._scroll_down()

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": False})()
        try:
            scroll_down_binding = next(binding for binding in repl.bindings.bindings if binding.keys == (Keys.ScrollDown,))
            scroll_down_binding.handler(type("FakeScrollEvent", (), {})())
        finally:
            repl.get_app = previous_search
            repl.session = previous_session

        self.assertEqual(shell.transcript_window.down_calls, 1)

    def test_scroll_up_binding_routes_to_transcript_during_search(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _WindowStub:
            def __init__(self):
                self.up_calls = 0

            def _scroll_up(self):
                self.up_calls += 1

        class _ShellStub:
            def __init__(self):
                self.transcript_window = _WindowStub()
                self.default_buffer = type("BufferStub", (), {"complete_state": None})()

            def scroll_transcript_window_up(self):
                self.transcript_window._scroll_up()

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": True})()
        try:
            scroll_up_binding = next(binding for binding in repl.bindings.bindings if binding.keys == (Keys.ScrollUp,))
            scroll_up_binding.handler(type("FakeScrollEvent", (), {})())
        finally:
            repl.get_app = previous_search
            repl.session = previous_session

        self.assertEqual(shell.transcript_window.up_calls, 1)

    def test_scroll_up_binding_does_not_route_to_transcript_when_completion_menu_open(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _WindowStub:
            def __init__(self):
                self.up_calls = 0

            def _scroll_up(self):
                self.up_calls += 1

        class _ShellStub:
            def __init__(self):
                self.transcript_window = _WindowStub()
                self.default_buffer = type("BufferStub", (), {"complete_state": object()})()

            def scroll_transcript_window_up(self):
                self.transcript_window._scroll_up()

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": False})()
        try:
            scroll_up_binding = next(binding for binding in repl.bindings.bindings if binding.keys == (Keys.ScrollUp,))
            scroll_up_binding.handler(type("FakeScrollEvent", (), {})())
        finally:
            repl.get_app = previous_search
            repl.session = previous_session

        self.assertEqual(shell.transcript_window.up_calls, 0)


class CompletionTriggerTests(unittest.TestCase):
    def test_dot_still_triggers_semantic_completion(self):
        buffer = FakeChangeBuffer("Debug.")

        repl._trigger_completion_on_change(buffer)

        self.assertEqual(
            buffer.start_completion_calls,
            [False],
            "Typing '.' should trigger semantic completion without auto-selecting the first item",
        )

    def test_line_start_slash_triggers_builtin_completion(self):
        buffer = FakeChangeBuffer("/")

        repl._trigger_completion_on_change(buffer)

        self.assertEqual(
            buffer.start_completion_calls,
            [False],
            "Typing '/' at line start should trigger builtin command completion",
        )

    def test_line_start_slash_still_triggers_builtin_completion_when_semantic_completion_is_disabled(self):
        previous = repl.enable_completion
        repl.enable_completion = False
        try:
            buffer = FakeChangeBuffer("/")

            repl._trigger_completion_on_change(buffer)

            self.assertEqual(
                buffer.start_completion_calls,
                [False],
                "Builtin command completion should remain available at line-start '/' even when semantic completion is disabled",
            )
        finally:
            repl.enable_completion = previous

    def test_builtin_completion_stops_after_command_arguments_begin(self):
        buffer = FakeChangeBuffer("/dofile test.cs")

        repl._trigger_completion_on_change(buffer)

        self.assertEqual(
            buffer.start_completion_calls,
            [],
            "Builtin command completion should stop once the user starts typing command arguments",
        )

    def test_mid_line_slash_does_not_trigger_builtin_completion(self):
        buffer = FakeChangeBuffer("foo /")

        repl._trigger_completion_on_change(buffer)

        self.assertEqual(
            buffer.start_completion_calls,
            [],
            "Typing '/' away from line start should not hijack normal code entry with builtin command completion",
        )

    def test_line_comment_prefix_does_not_trigger_builtin_completion(self):
        buffer = FakeChangeBuffer("//")

        repl._trigger_completion_on_change(buffer)

        self.assertEqual(
            buffer.start_completion_calls,
            [],
            "Typing '//' at line start should be treated as normal C# comment input, not builtin completion",
        )

    def test_block_comment_prefix_does_not_trigger_builtin_completion(self):
        buffer = FakeChangeBuffer("/*")

        repl._trigger_completion_on_change(buffer)

        self.assertEqual(
            buffer.start_completion_calls,
            [],
            "Typing '/*' at line start should be treated as normal C# comment input, not builtin completion",
        )


class CommandExpressionPrefixAndParsingTests(unittest.TestCase):
    def test_command_expression_prefix_requires_action_call_shape(self):
        self.assertFalse(looks_like_command_expression_prefix("@"))
        self.assertFalse(looks_like_command_expression_prefix("@game"))
        self.assertTrue(looks_like_command_expression_prefix("  @game.pause"))
        self.assertTrue(looks_like_command_expression_prefix("@game.pause()"))
        self.assertTrue(looks_like_command_expression_prefix("@game.scene.pause()"))
        self.assertTrue(looks_like_command_expression_prefix("@editor.status"))

    def test_command_expression_prefix_treats_escaped_identifiers_as_csharp_code(self):
        self.assertFalse(looks_like_command_expression_prefix("@class"))
        self.assertFalse(looks_like_command_expression_prefix("  @namespace"))

    def test_command_expression_prefix_rejects_legacy_dollar_symbol(self):
        self.assertFalse(looks_like_command_expression_prefix("$"))
        self.assertFalse(looks_like_command_expression_prefix("$game.pause"))

    def test_parse_command_expression_accepts_at_prefixed_action(self):
        parsed = parse_command_expression("@game.pause()")

        self.assertEqual(parsed, ("game", "pause", {}))

    def test_parse_command_expression_accepts_dotted_action(self):
        parsed = parse_command_expression("@game.scene.pause(target: \"Player\")")

        self.assertEqual(parsed, ("game", "scene.pause", {"target": "Player"}))

    def test_parse_command_expression_rejects_legacy_dollar_prefixed_action(self):
        parsed = parse_command_expression("$game.pause()")

        self.assertIsNone(parsed)


class CommandActionCompletionRoutingTests(unittest.TestCase):
    def test_action_completion_is_driven_by_at_prefix(self):
        completer = CommandExpressionCompleter(
            lambda: [
                {
                    "commandNamespace": "game",
                    "action": "pause",
                    "arguments": [],
                }
            ]
        )

        completions = list(completer.get_completions(FakeDocument("@game.p"), None))

        self.assertTrue(
            any(completion.text == "pause" for completion in completions),
            "@-prefixed action text should use command action completion",
        )

    def test_action_completion_uses_catalog_summary_and_signature(self):
        completer = CommandExpressionCompleter(
            lambda: [
                {
                    "commandNamespace": "project",
                    "action": "scene.open",
                    "summary": "Open a Unity scene",
                    "arguments": [{"name": "scenePath", "typeName": "System.String"}],
                }
            ]
        )

        completions = list(completer.get_completions(FakeDocument("@project.sc"), None))
        completion_by_text = {completion.text: completion for completion in completions}

        self.assertIn("scene.open", completion_by_text)
        self.assertEqual(completion_by_text["scene.open"].display_meta_text, "(scenePath: String)  Open a Unity scene")

    def test_multi_segment_action_prefix_continues_to_complete_after_second_dot(self):
        completer = CommandExpressionCompleter(
            lambda: [
                {
                    "commandNamespace": "project",
                    "action": "scene.open",
                    "summary": "Open a Unity scene",
                    "arguments": [],
                }
            ]
        )

        completions = list(completer.get_completions(FakeDocument("@project.scene.o"), None))

        self.assertTrue(
            any(completion.text == "open" for completion in completions),
            "Dotted action prefixes should keep matching so completions continue after the first dot",
        )

    def test_roslyn_completer_does_not_participate_for_slash_prefixed_input(self):
        previous_request_completion = client.request_completion
        requests = []

        def _request_completion_stub(*args, **kwargs):
            requests.append((args, kwargs))
            return {"ok": True, "data": {"items": []}, "summary": ""}

        client.request_completion = _request_completion_stub
        try:
            completer = RoslynCompleter(lambda: True, lambda: 1)
            completions = list(completer.get_completions(FakeDocument("/game.pause"), None))
        finally:
            client.request_completion = previous_request_completion

        self.assertEqual(completions, [])
        self.assertEqual(
            requests,
            [],
            "Roslyn completion should not run for slash-prefixed command input",
        )

    def test_roslyn_completer_does_not_participate_for_at_prefixed_input(self):
        previous_request_completion = client.request_completion
        requests = []

        def _request_completion_stub(*args, **kwargs):
            requests.append((args, kwargs))
            return {"ok": True, "data": {"items": []}, "summary": ""}

        client.request_completion = _request_completion_stub
        try:
            completer = RoslynCompleter(lambda: True, lambda: 1)
            completions = list(completer.get_completions(FakeDocument("@game.pause"), None))
        finally:
            client.request_completion = previous_request_completion

        self.assertEqual(completions, [])
        self.assertEqual(
            requests,
            [],
            "Roslyn completion should not run for @-prefixed action input",
        )

    def test_roslyn_completer_participates_for_escaped_identifier_input(self):
        previous_request_completion = client.request_completion
        requests = []

        def _request_completion_stub(*args, **kwargs):
            requests.append((args, kwargs))
            return {"ok": True, "data": {"items": []}, "summary": ""}

        client.request_completion = _request_completion_stub
        try:
            completer = RoslynCompleter(lambda: True, lambda: 1)
            completions = list(completer.get_completions(FakeDocument("@class."), None))
        finally:
            client.request_completion = previous_request_completion

        self.assertEqual(completions, [])
        self.assertEqual(
            len(requests),
            1,
            "Roslyn completion should run for escaped C# identifiers like '@class.'",
        )


class InputSizingTests(unittest.TestCase):
    def test_input_height_stays_one_line_for_single_line_text(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            session.default_buffer.text = "Debug.Log(1);"
            height = session._get_input_height()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(height.preferred, 1)
        self.assertEqual(height.min, height.preferred)

    def test_input_height_grows_for_multiline_text(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            session.default_buffer.text = "line1\nline2\nline3"
            height = session._get_input_height()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(height.preferred, 3)
        self.assertEqual(height.min, height.preferred)

    def test_input_height_caps_at_max_visible_lines_for_long_multiline_text(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            session.default_buffer.text = "\n".join(f"line{i}" for i in range(12))
            height = session._get_input_height()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(height.min, repl.MAX_INPUT_VISIBLE_LINES)
        self.assertEqual(height.max, repl.MAX_INPUT_VISIBLE_LINES)
        self.assertEqual(height.preferred, repl.MAX_INPUT_VISIBLE_LINES)


class TranscriptAutoScrollTests(unittest.TestCase):
    def test_append_input_transcript_scrolls_to_bottom(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            session.transcript_window.vertical_scroll = 0
            session.append_input_transcript("Debug.Log(1);")
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(len(session.transcript_state.entries), 1)
        self.assertGreaterEqual(session.transcript_window.vertical_scroll, 0)
        self.assertGreater(session.app.invalidate_calls, 0)

    def test_scroll_transcript_to_bottom_uses_render_info_when_available(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            session.transcript_window.render_info = type("RenderInfo", (), {"content_height": 30, "window_height": 8})()
            session.scroll_transcript_to_bottom()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(session.transcript_window.vertical_scroll, 22)

    def test_input_height_change_preserves_transcript_tail_when_at_bottom(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
            session.transcript_window.render_info = type("RenderInfo", (), {"vertical_scroll": 12, "content_height": 20, "window_height": 8})()
            session.default_buffer.text = "line1\nline2"
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(session.transcript_window.vertical_scroll, 12)
        self.assertGreater(session.app.invalidate_calls, 0)


class InteractiveRunModelTests(unittest.TestCase):
    def test_handle_submitted_message_processes_input_without_exiting_application(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        processed = []
        try:
            session = repl.ensure_prompt_session()
            app = FakeAppForSubmit("Debug.Log(1);")
            event = type("FakeSubmitEvent", (), {"app": app})()
            session.run_interactive(lambda message: processed.append(message))
            session._on_submit = lambda message: processed.append(message)
            session.handle_submitted_message("Debug.Log(1);", event)
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertEqual(processed, ["Debug.Log(1);"])
        self.assertEqual(app.exit_result, None)
        self.assertEqual(app.layout.focused, session.input_control)
        self.assertEqual(session.app.run_calls, 1)


class SubmitBindingTests(unittest.TestCase):
    def test_enter_submits_by_default(self):
        previous_session = repl.session
        previous_pending = repl._pending_quit_confirmation
        try:
            repl._pending_quit_confirmation = False
            repl.session = type("FakeSession", (), {"history": FakeHistory()})()
            app = FakeAppForSubmit("var a = 1;")
            event = type("FakeEnterEvent", (), {"app": app, "current_buffer": app.current_buffer})()

            enter_binding = next(binding for binding in repl.bindings.bindings if binding.keys == ("c-m",))
            enter_binding.handler(event)

            self.assertEqual(app.exit_result, "var a = 1;")
            self.assertEqual(repl.session.history.entries, ["var a = 1;"])
        finally:
            repl.session = previous_session
            repl._pending_quit_confirmation = previous_pending

    def test_ctrl_j_inserts_newline_without_submitting(self):
        previous_session = repl.session
        previous_pending = repl._pending_quit_confirmation
        try:
            repl._pending_quit_confirmation = False
            repl.session = type("FakeSession", (), {"history": FakeHistory()})()
            app = FakeAppForSubmit("var a = 1;")
            event = type("FakeCtrlJEvent", (), {"app": app, "current_buffer": app.current_buffer})()

            newline_binding = next(binding for binding in repl.bindings.bindings if binding.keys == ("c-j",))
            newline_binding.handler(event)

            self.assertEqual(app.current_buffer.insertions, ["\n"])
            self.assertEqual(app.current_buffer.document.text, "var a = 1;\n")
            self.assertIsNone(app.exit_result)
            self.assertEqual(repl.session.history.entries, [])
        finally:
            repl.session = previous_session
            repl._pending_quit_confirmation = previous_pending


class MultilineSubmitBindingTests(unittest.TestCase):
    def test_escape_enter_submit_sequence_is_not_bound(self):
        bound_sequences = [binding.keys for binding in repl.bindings.bindings]

        self.assertNotIn(
            ("escape", "c-m"),
            bound_sequences,
            "Esc+Enter submit should remain unbound",
        )

    def test_escape_is_not_bound_as_standalone_clear_key(self):
        bound_sequences = [binding.keys for binding in repl.bindings.bindings]

        self.assertNotIn(
            ("escape",),
            bound_sequences,
            "Standalone Esc binding should be removed so Esc no longer enters prompt_toolkit's odd prefix/repeat state during normal editing",
        )


class EscapeAndCtrlCTests(unittest.TestCase):
    def test_pending_quit_confirmation_filter_tracks_state(self):
        previous = repl._pending_quit_confirmation
        try:
            repl._pending_quit_confirmation = False
            self.assertFalse(
                repl._pending_quit_confirmation_filter(),
                "Pending-quit filter must stay inactive during normal typing so prompt_toolkit self-insert keeps working",
            )

            repl._pending_quit_confirmation = True
            self.assertTrue(
                repl._pending_quit_confirmation_filter(),
                "Pending-quit filter should activate only while quit confirmation is pending",
            )
        finally:
            repl._pending_quit_confirmation = previous

    def test_ctrl_c_copies_selection_instead_of_clearing_input(self):
        previous = repl._pending_quit_confirmation
        repl._pending_quit_confirmation = False
        try:
            class FakeClipboard:
                def __init__(self):
                    self.data = None

                def set_data(self, data):
                    self.data = data

            class FakeBufferForCopy:
                def __init__(self):
                    self.text = "Debug.Log(1);"
                    self.document = FakeDocument(self.text)
                    self.selection_state = object()
                    self.reset_called = False
                    self.copy_calls = 0

                def copy_selection(self):
                    self.copy_calls += 1
                    self.selection_state = None
                    return "copied-text"

                def reset(self):
                    self.reset_called = True

            class FakeApp:
                def __init__(self):
                    self.exited = False
                    self.clipboard = FakeClipboard()

                def exit(self, result=None):
                    self.exited = True

            buffer = FakeBufferForCopy()
            event = type("FakeCtrlCEvent", (), {"app": FakeApp(), "current_buffer": buffer})()

            repl.handle_ctrl_c(event)

            self.assertEqual(buffer.copy_calls, 1)
            self.assertFalse(buffer.reset_called)
            self.assertEqual(event.app.clipboard.data, "copied-text")
            self.assertFalse(repl._pending_quit_confirmation)
            self.assertFalse(event.app.exited)
        finally:
            repl._pending_quit_confirmation = previous

    def test_ctrl_c_copies_transcript_selection_while_input_keeps_focus(self):
        previous = repl._pending_quit_confirmation
        previous_session = repl.session
        repl._pending_quit_confirmation = False
        try:
            class FakeClipboard:
                def __init__(self):
                    self.data = None

                def set_data(self, data):
                    self.data = data

            class FakeTranscriptControl:
                def __init__(self):
                    self.selection_state = object()
                    self.copy_calls = 0

                def copy_selection(self):
                    self.copy_calls += 1
                    self.selection_state = None
                    return "transcript-copied"

            class FakeApp:
                def __init__(self):
                    self.exited = False
                    self.clipboard = FakeClipboard()
                    self.invalidate_calls = 0

                def exit(self, result=None):
                    self.exited = True

                def invalidate(self):
                    self.invalidate_calls += 1

            transcript_control = FakeTranscriptControl()
            repl.session = type("SessionStub", (), {"transcript_control": transcript_control, "app": FakeApp()})()
            event = type("FakeCtrlCEvent", (), {"app": repl.session.app, "current_buffer": FakeToolbarBuffer("Debug.Log(1);")})()

            repl.handle_ctrl_c(event)

            self.assertEqual(transcript_control.copy_calls, 1)
            self.assertEqual(event.app.clipboard.data, "transcript-copied")
            self.assertFalse(repl._pending_quit_confirmation)
            self.assertFalse(event.app.exited)
            self.assertEqual(event.app.invalidate_calls, 1)
        finally:
            repl.session = previous_session
            repl._pending_quit_confirmation = previous

    def test_ctrl_c_clears_current_input_text_when_buffer_has_text(self):
        previous = repl._pending_quit_confirmation
        repl._pending_quit_confirmation = False
        try:
            class FakeBufferForReset:
                def __init__(self):
                    self.text = "Debug.Log(1);"
                    self.document = FakeDocument(self.text)
                    self.selection_state = None
                    self.reset_called = False

                def reset(self):
                    self.text = ""
                    self.document = FakeDocument("")
                    self.reset_called = True

            class FakeApp:
                def __init__(self):
                    self.exited = False

                def exit(self, result=None):
                    self.exited = True

            buffer = FakeBufferForReset()
            event = type("FakeCtrlCEvent", (), {"app": FakeApp(), "current_buffer": buffer})()

            repl.handle_ctrl_c(event)

            self.assertTrue(buffer.reset_called)
            self.assertEqual(buffer.text, "")
            self.assertFalse(repl._pending_quit_confirmation)
            self.assertFalse(event.app.exited)
        finally:
            repl._pending_quit_confirmation = previous

    def test_first_ctrl_c_sets_pending_quit_confirmation(self):
        previous = repl._pending_quit_confirmation
        repl._pending_quit_confirmation = False
        try:
            class FakeApp:
                def __init__(self):
                    self.exited = False

                def exit(self, result=None):
                    self.exited = True

            event = type(
                "FakeCtrlCEvent",
                (),
                {"app": FakeApp(), "current_buffer": FakeToolbarBuffer()},
            )()

            repl.handle_ctrl_c(event)

            self.assertTrue(repl._pending_quit_confirmation)
            self.assertFalse(event.app.exited)
        finally:
            repl._pending_quit_confirmation = previous

    def test_second_ctrl_c_exits_when_confirmation_is_pending(self):
        previous = repl._pending_quit_confirmation
        repl._pending_quit_confirmation = True
        try:
            class FakeApp:
                def __init__(self):
                    self.exited = False

                def exit(self, result=None):
                    self.exited = True

            event = type(
                "FakeCtrlCEvent",
                (),
                {"app": FakeApp(), "current_buffer": FakeToolbarBuffer()},
            )()

            repl.handle_ctrl_c(event)

            self.assertFalse(repl._pending_quit_confirmation)
            self.assertTrue(event.app.exited)
        finally:
            repl._pending_quit_confirmation = previous


class FooterSessionTextTests(unittest.TestCase):
    def test_footer_session_text_shows_mode_target_and_command_id(self):
        with ReplStateOverride(
            runtime_mode=False,
            enableml=1,
            enable_completion=True,
            ip="127.0.0.1",
            port=14500,
            compile_ip="127.0.0.1",
            compile_port=14500,
            runtime_ip="127.0.0.1",
            runtime_port=15500,
        ):
            self.assertEqual(
                session_ui.build_footer_session_text(config, "cmd-123"),
                [
                    ("class:footer.session.label", "[session] "),
                    ("class:footer.session.key", "workMode="),
                    ("class:footer.session.value", "editor"),
                    ("", "  "),
                    ("class:footer.session.key", "target="),
                    ("class:footer.session.value", "127.0.0.1:14500"),
                    ("", "  "),
                    ("class:footer.session.key", "cmdId="),
                    ("class:footer.session.value", "cmd-123"),
                ],
            )


class CommonTextResultTests(unittest.TestCase):
    def test_text_result_leaves_blank_line_between_outputs(self):
        import io
        from contextlib import redirect_stdout

        stream = io.StringIO()
        with redirect_stdout(stream):
            client.print_text_result({"ok": True, "data": {"text": "alpha"}})
            client.print_text_result({"ok": True, "data": {"text": "beta"}})

        self.assertEqual(stream.getvalue(), "alpha\nbeta\n")


class PromptStyleTests(unittest.TestCase):
    def test_session_ui_style_rules_include_footer_and_transcript_tokens(self):
        style_rules = dict(session_ui.build_session_style_rules())

        self.assertIn("footer.session.label", style_rules)
        self.assertIn("footer.session.key", style_rules)
        self.assertIn("footer.session.value", style_rules)
        self.assertIn("footer.status.left", style_rules)
        self.assertIn("footer.status.right", style_rules)
        self.assertIn("transcript.timestamp", style_rules)
        self.assertIn("transcript.separator", style_rules)
        self.assertIn("input.divider", style_rules)
        self.assertIn("transcript.info.prefix", style_rules)
        self.assertIn("transcript.input.prefix", style_rules)
        self.assertIn("transcript.input.text", style_rules)
        self.assertIn("transcript.result.prefix", style_rules)
        self.assertIn("transcript.result.text", style_rules)
        self.assertIn("transcript.notice.accessibility.text", style_rules)
        self.assertIn("transcript.error.compile_error.prefix", style_rules)
        self.assertIn("transcript.error.action_required.text", style_rules)
        self.assertIn("transcript.error.timeout_error.prefix", style_rules)
        self.assertIn("transcript.error.connection_error.prefix", style_rules)
        self.assertIn("transcript.error.transport_error.prefix", style_rules)
        self.assertIn("transcript.error.command_error.prefix", style_rules)

        def _bg(style):
            for token in style.split():
                if token.startswith("bg:"):
                    return token
            return None

        compile_bg = _bg(style_rules["transcript.error.compile_error.prefix"])
        action_bg = _bg(style_rules["transcript.error.action_required.text"])
        timeout_bg = _bg(style_rules["transcript.error.timeout_error.prefix"])
        connection_bg = _bg(style_rules["transcript.error.connection_error.prefix"])
        transport_bg = _bg(style_rules["transcript.error.transport_error.prefix"])
        command_bg = _bg(style_rules["transcript.error.command_error.prefix"])

        self.assertIsNotNone(compile_bg)
        self.assertIsNotNone(action_bg)
        self.assertNotEqual(action_bg, compile_bg)
        self.assertIn("bold", style_rules["transcript.error.action_required.text"].split())
        self.assertIsNotNone(timeout_bg)
        self.assertIsNotNone(connection_bg)
        self.assertIsNotNone(transport_bg)
        self.assertIsNotNone(command_bg)

        self.assertEqual(
            len({compile_bg, timeout_bg, connection_bg, transport_bg, command_bg}),
            5,
            "Each transcript error category should have a distinct background style category",
        )


class TranscriptRenderingHelpersTests(unittest.TestCase):
    def test_render_transcript_input_block_uses_input_styles(self):
        self.assertEqual(
            session_ui.render_transcript_input_block("Debug.Log(1);", "2026-04-04T12:34:56.000Z"),
            [
                ("class:transcript.timestamp", "[12:34:56] "),
                ("class:transcript.input.prefix", "> "),
                ("class:transcript.input.text", "Debug.Log(1);"),
            ],
        )

    def test_render_transcript_result_block_uses_result_styles(self):
        self.assertEqual(
            session_ui.render_transcript_result_block("1", "2026-04-04T12:34:57.000Z"),
            [
                ("class:transcript.timestamp", "[12:34:57] "),
                ("class:transcript.result.prefix", "< "),
                ("class:transcript.result.text", "1"),
            ],
        )

    def test_render_transcript_round_separator_uses_separator_style(self):
        self.assertEqual(
            session_ui.render_transcript_round_separator(12),
            [
                ("class:transcript.separator", session_ui.ROUND_SEPARATOR_CHAR * 12),
            ],
        )

    def test_render_input_divider_uses_full_width_fill(self):
        self.assertEqual(
            session_ui.render_input_divider(8),
            [
                ("class:input.divider", session_ui.INPUT_DIVIDER_CHAR * 8),
            ],
        )

    def test_render_transcript_error_block_uses_error_kind_style_keys(self):
        self.assertEqual(
            session_ui.render_transcript_error_block("compile_error", "CS1002", "2026-04-04T12:34:58.000Z"),
            [
                ("class:transcript.timestamp", "[12:34:58] "),
                ("class:transcript.error.compile_error.prefix", "! "),
                ("class:transcript.error.compile_error.text", "CS1002"),
            ],
        )
        self.assertEqual(
            session_ui.render_transcript_error_block("timeout_error", "Timed out", "2026-04-04T12:34:59.000Z"),
            [
                ("class:transcript.timestamp", "[12:34:59] "),
                ("class:transcript.error.timeout_error.prefix", "! "),
                ("class:transcript.error.timeout_error.text", "Timed out"),
            ],
        )
        self.assertEqual(
            session_ui.render_transcript_error_block("connection_error", "Connection refused", "2026-04-04T12:35:00.000Z"),
            [
                ("class:transcript.timestamp", "[12:35:00] "),
                ("class:transcript.error.connection_error.prefix", "! "),
                ("class:transcript.error.connection_error.text", "Connection refused"),
            ],
        )
        self.assertEqual(
            session_ui.render_transcript_error_block("transport_error", "Malformed response", "2026-04-04T12:35:01.000Z"),
            [
                ("class:transcript.timestamp", "[12:35:01] "),
                ("class:transcript.error.transport_error.prefix", "! "),
                ("class:transcript.error.transport_error.text", "Malformed response"),
            ],
        )
        self.assertEqual(
            session_ui.render_transcript_error_block("command_error", "Unknown action", "2026-04-04T12:35:02.000Z"),
            [
                ("class:transcript.timestamp", "[12:35:02] "),
                ("class:transcript.error.command_error.prefix", "! "),
                ("class:transcript.error.command_error.text", "Unknown action"),
            ],
        )


class ResultTranscriptWiringRegressionTests(unittest.TestCase):
    def test_try_process_command_expression_returns_false_for_escaped_identifier(self):
        previous_session = repl.session
        previous_request_command = client.request_command

        class _AppSpy:
            def __init__(self):
                self.invalidate_calls = 0

            def invalidate(self):
                self.invalidate_calls += 1

        class _SessionSpy:
            def __init__(self):
                self.transcript_state = repl.TranscriptState()
                self.app = _AppSpy()

        requests = []
        repl.session = _SessionSpy()
        client.request_command = lambda *_args, **_kwargs: requests.append((_args, _kwargs))
        try:
            handled = repl.try_process_command_expression("@class")
        finally:
            current_session = repl.session
            client.request_command = previous_request_command
            repl.session = previous_session

        self.assertFalse(handled)
        self.assertEqual(requests, [])
        self.assertEqual(current_session.transcript_state.entries, [])

    def test_try_process_command_expression_appends_transcript_entry_without_type_mismatch(self):
        previous_session = repl.session
        previous_request_command = client.request_command

        class _AppSpy:
            def __init__(self):
                self.invalidate_calls = 0

            def invalidate(self):
                self.invalidate_calls += 1

        class _SessionSpy:
            def __init__(self):
                self.transcript_state = repl.TranscriptState()
                self.app = _AppSpy()

        repl.session = _SessionSpy()
        client.request_command = lambda *_args, **_kwargs: {
            "ok": False,
            "stage": "command",
            "type": "command_error",
            "summary": "Unknown action",
            "data": {"text": "Unknown action\n"},
        }
        try:
            handled = repl.try_process_command_expression("@game.pause()")
        finally:
            current_session = repl.session
            client.request_command = previous_request_command
            repl.session = previous_session

        self.assertTrue(handled)
        self.assertEqual(len(current_session.transcript_state.entries), 1)
        entry = current_session.transcript_state.entries[0]
        self.assertEqual(entry.entry_type, "result")
        self.assertEqual(entry.error_kind, "command_error")

    def test_execute_repl_snippet_appends_transcript_entry_without_double_building(self):
        previous_session = repl.session
        previous_runtime_mode = config.runtime_mode
        previous_execute_editor_request = client.execute_editor_request

        class _AppSpy:
            def __init__(self):
                self.invalidate_calls = 0

            def invalidate(self):
                self.invalidate_calls += 1

        class _SessionSpy:
            def __init__(self):
                self.transcript_state = repl.TranscriptState()
                self.app = _AppSpy()

        repl.session = _SessionSpy()
        config.runtime_mode = False

        result_payload = {
            "ok": True,
            "stage": "execute",
            "type": "",
            "summary": "ok",
            "data": {"text": "42\n"},
        }

        client.execute_editor_request = (
            lambda _message, _cmd_id, reset=False, invalidate_completion=None: result_payload
        )

        try:
            repl.execute_repl_snippet("1+41", reset=False)
        finally:
            current_session = repl.session
            client.execute_editor_request = previous_execute_editor_request
            config.runtime_mode = previous_runtime_mode
            repl.session = previous_session

        self.assertEqual(len(current_session.transcript_state.entries), 1)
        entry = current_session.transcript_state.entries[0]
        self.assertEqual(entry.entry_type, "result")
        self.assertTrue(entry.ok)
        self.assertEqual(entry.text, "42\n")


class TitleAndToolbarTextTests(unittest.TestCase):
    def test_editor_title_uses_editor_ip(self):
        with ReplStateOverride(
            runtime_mode=False,
            enableml=1,
            enable_completion=True,
            ip="127.0.0.1",
            port=14500,
            compile_ip="127.0.0.1",
            compile_port=14500,
            runtime_ip="127.0.0.1",
            runtime_port=15500,
        ):
            title = repl._build_terminal_title()

        self.assertEqual(title, "c# REPL/127.0.0.1:14500")

    def test_runtime_title_uses_runtime_ip(self):
        with ReplStateOverride(
            runtime_mode=True,
            enableml=1,
            enable_completion=True,
            ip="127.0.0.1",
            port=14500,
            compile_ip="10.0.0.2",
            compile_port=14500,
            runtime_ip="10.0.0.9",
            runtime_port=15500,
        ):
            title = repl._build_terminal_title()

        self.assertEqual(title, "c# REPL/10.0.0.9:15500")

    def test_application_shell_uses_custom_bottom_anchored_layout(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = ApplicationSpy
        try:
            session = repl.ensure_prompt_session()
        finally:
            repl.Application = previous_application
            repl.session = previous_session

        self.assertIsNotNone(
            session.app.layout,
            "Application shell should build a prompt_toolkit Layout instance",
        )
        self.assertIsNotNone(
            session.transcript_control,
            "Application shell should expose transcript control for transcript rendering",
        )
        self.assertIsNotNone(
            session.transcript_window,
            "Application shell should expose transcript window for transcript rendering",
        )
        self.assertIsNotNone(
            session.input_control,
            "Application shell should expose input control for the editable input area",
        )
        self.assertIsNotNone(
            session.input_divider,
            "Application shell should render a visible divider between transcript history and input area",
        )
        self.assertEqual(
            session.footer_line_1_left.text(),
            session_ui.build_footer_status_left_text(session.default_buffer, repl._pending_quit_confirmation, repl.enableml),
            "First footer line left segment should stay wired to toolbar hint text",
        )
        self.assertEqual(
            session.footer_line_1_right.text(),
            session_ui.build_footer_status_right_text(repl.enableml, repl.enable_completion),
            "First footer line right segment should show completion state",
        )
        self.assertEqual(
            session.footer_line_2_left.text(),
            session_ui.build_footer_common_shortcuts_text(),
            "Second footer line left segment should render common shortcuts",
        )
        self.assertEqual(
            session.footer_line_2_right.text(),
            session_ui.build_footer_session_text(config, repl.cmd_id),
            "Second footer line right segment should render session metadata via session_ui helper",
        )

    def test_footer_status_left_uses_default_submit_shortcuts(self):
        left = session_ui.build_footer_status_left_text(
            FakeToolbarBuffer(), pending_quit_confirmation=False, enableml=1, searching=False
        )

        self.assertEqual(
            left,
            [
                ("class:footer.status.left", "[/] commands  ·  [@] actions"),
            ],
        )

    def test_footer_status_right_shows_completion_state(self):
        right = session_ui.build_footer_status_right_text(enableml=1, enable_completion=True)

        self.assertEqual(
            right,
            [
                ("class:footer.status.right", "● completion"),
            ],
        )

    def test_footer_common_shortcuts_text_lists_shared_actions(self):
        self.assertEqual(
            session_ui.build_footer_common_shortcuts_text(),
            [
                ("class:footer.status.left", "[Ctrl+Enter] newline  ·  [Ctrl+R] history"),
            ],
        )

    def test_footer_status_left_switches_when_completion_menu_is_visible(self):
        left = session_ui.build_footer_status_left_text(
            FakeToolbarBuffer(semantic_texts=("WriteLine",), selected_index=0),
            pending_quit_confirmation=False,
            enableml=1,
            searching=False,
        )

        self.assertEqual(
            left,
            [
                ("class:footer.status.left", "[↑↓] select  ·  [Tab] accept  ·  [/] commands  ·  [@] actions"),
            ],
        )

    def test_prompt_message_uses_minimal_left_prompt(self):
        prompt = repl._build_prompt_message()

        self.assertEqual(
            prompt,
            [
                ("class:prompt.sep", "> "),
            ],
        )

    def test_prompt_continuation_uses_minimal_left_marker(self):
        continuation = repl._build_prompt_continuation(0, 0, 0)

        self.assertEqual(
            continuation,
            [
                ("class:prompt.sep", "· "),
            ],
        )

    def test_footer_status_left_keeps_submit_guidance_when_buffer_has_text(self):
        left = session_ui.build_footer_status_left_text(
            FakeToolbarBuffer(text="Debug"), pending_quit_confirmation=False, enableml=1, searching=False
        )

        self.assertEqual(
            left,
            [
                ("class:footer.status.left", "[Ctrl+C] clear"),
            ],
        )

    def test_footer_status_right_reflects_completion_off(self):
        right = session_ui.build_footer_status_right_text(enableml=1, enable_completion=False)

        self.assertEqual(
            right,
            [
                ("class:footer.status.right", "○ completion"),
            ],
        )

    def test_footer_status_left_shows_quit_confirmation_when_pending(self):
        left = session_ui.build_footer_status_left_text(
            FakeToolbarBuffer(), pending_quit_confirmation=True, enableml=1, searching=False
        )

        self.assertEqual(
            left,
            [
                ("class:footer.status.left", "[Ctrl+C] quit  ·  [Any key] cancel"),
            ],
        )

    def test_footer_status_left_shows_search_shortcuts_when_searching(self):
        left = session_ui.build_footer_status_left_text(
            FakeToolbarBuffer(), pending_quit_confirmation=False, enableml=1, searching=True
        )

        self.assertEqual(
            left,
            [
                ("class:footer.status.left", "[Enter] accept  ·  [↑↓] prev/next  ·  [Ctrl+C] cancel"),
            ],
        )

    def test_help_info_does_not_print_to_stdout(self):
        import io
        from contextlib import redirect_stdout

        stream = io.StringIO()
        with redirect_stdout(stream):
            repl.print_help_info()

        self.assertEqual(stream.getvalue(), "")


if __name__ == "__main__":
    unittest.main()
