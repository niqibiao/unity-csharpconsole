import asyncio
import io
import os
import sys
import unittest
from contextlib import redirect_stdout

SCRIPT_ROOT = os.path.dirname(os.path.abspath(__file__))
CONSOLE_CLIENT_ROOT = os.path.dirname(os.path.dirname(SCRIPT_ROOT))
SITE_PACKAGES_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "site-packages")
_ADDED_SITE_PACKAGES_PATH = False
CORE_PATH = os.path.join(CONSOLE_CLIENT_ROOT, "csharpconsole_core")
_ADDED_CORE_PATH = False

if CONSOLE_CLIENT_ROOT not in sys.path:
    sys.path.insert(0, CONSOLE_CLIENT_ROOT)

if SITE_PACKAGES_PATH not in sys.path:
    sys.path.insert(0, SITE_PACKAGES_PATH)
    _ADDED_SITE_PACKAGES_PATH = True

if CORE_PATH not in sys.path:
    sys.path.insert(0, CORE_PATH)
    _ADDED_CORE_PATH = True

from prompt_toolkit.application.current import create_app_session
from prompt_toolkit.data_structures import Point, Size
from prompt_toolkit.document import Document
from prompt_toolkit.input import create_pipe_input
from prompt_toolkit.mouse_events import MouseButton, MouseEvent, MouseEventType
from prompt_toolkit.output import DummyOutput

import csharp_repl_core as repl
from repl import builtins, output
from repl import loop
from repl import scroll_router
from repl import session_ui
from repl import viewport_policy
from repl.transcript import TranscriptEntry, TranscriptState
from repl.transcript_control import TranscriptControl


def _extract_text_from_data(data):
    return (data or {}).get("text", "")


class _ApplicationSpy:
    def __init__(self, *args, **kwargs):
        self.args = args
        self.kwargs = kwargs
        self.layout = kwargs.get("layout")
        self.style = kwargs.get("style")
        self.output = type("Output", (), {"get_size": lambda _self: type("Size", (), {"columns": 80})()})()
        self.invalidate_calls = 0

    def invalidate(self):
        self.invalidate_calls += 1

    def run(self):
        return None


class _SizedDummyOutput(DummyOutput):
    def __init__(self, columns=80, rows=24):
        super().__init__()
        self._size = Size(rows=rows, columns=columns)

    def get_size(self):
        return self._size


class ReplApplicationShellViewportWiringTests(unittest.TestCase):
    def _create_session(self):
        previous_session = repl.session
        previous_application = repl.Application
        repl.session = None
        repl.Application = _ApplicationSpy
        self.addCleanup(setattr, repl, "Application", previous_application)
        self.addCleanup(setattr, repl, "session", previous_session)
        return repl.ensure_prompt_session()

    def test_submit_current_buffer_exits_with_text_in_prompt_mode_without_interactive_callback(self):
        session = self._create_session()
        session.default_buffer.text = "Debug.Log(123);"

        class _EventApp:
            def __init__(self, buffer):
                self.current_buffer = buffer
                self.exit_calls = []

            def exit(self, result=None):
                self.exit_calls.append(result)

        class _Event:
            def __init__(self, app):
                self.app = app

        event_app = _EventApp(session.default_buffer)
        event = _Event(event_app)

        repl._submit_current_buffer(event)

        self.assertEqual(event_app.exit_calls, ["Debug.Log(123);"])

    def test_get_input_height_uses_available_input_width(self):
        session = self._create_session()
        session.default_buffer.text = "abcdef"

        previous_compute = viewport_policy.compute_input_height
        recorded = []

        def _spy(document_text, available_width=None, max_visible_lines=8):
            recorded.append((document_text, available_width, max_visible_lines))
            return previous_compute(
                document_text,
                available_width=available_width,
                max_visible_lines=max_visible_lines,
            )

        viewport_policy.compute_input_height = _spy
        self.addCleanup(setattr, viewport_policy, "compute_input_height", previous_compute)

        try:
            session._get_available_width = lambda: 6
            height = session._get_input_height()
        finally:
            session._get_available_width = repl.ReplApplicationShell._get_available_width.__get__(session, repl.ReplApplicationShell)

        self.assertEqual(height.preferred, 2)
        self.assertTrue(recorded)
        self.assertEqual(recorded[-1][0], "abcdef")
        self.assertEqual(recorded[-1][1], 4)

    def test_handle_input_text_changed_pins_transcript_after_visible_height_change_and_invalidates_app(self):
        session = self._create_session()

        class _RenderInfo:
            vertical_scroll = 12
            content_height = 20
            window_height = 8

        session.transcript_window.render_info = _RenderInfo()
        session.transcript_window.vertical_scroll = 12
        session.default_buffer.text = "line1\nline2"

        previous_compute_visible = viewport_policy.compute_input_visible_lines
        previous_is_bottom = viewport_policy.is_transcript_at_bottom
        previous_pin_bottom = viewport_policy.pin_transcript_to_bottom

        visible_calls = []
        bottom_checks = []
        pin_calls = []

        def _compute_visible(document_text, available_width=None, max_visible_lines=8):
            visible_calls.append((document_text, available_width, max_visible_lines))
            return 2

        def _is_bottom(window):
            bottom_checks.append(window)
            return True

        def _pin_bottom(window):
            pin_calls.append(window)

        viewport_policy.compute_input_visible_lines = _compute_visible
        viewport_policy.is_transcript_at_bottom = _is_bottom
        viewport_policy.pin_transcript_to_bottom = _pin_bottom
        self.addCleanup(setattr, viewport_policy, "compute_input_visible_lines", previous_compute_visible)
        self.addCleanup(setattr, viewport_policy, "is_transcript_at_bottom", previous_is_bottom)
        self.addCleanup(setattr, viewport_policy, "pin_transcript_to_bottom", previous_pin_bottom)

        session._last_input_visible_lines = 1
        session._get_available_width = lambda: 8
        session.app.invalidate_calls = 0

        session._handle_input_text_changed(session.default_buffer)

        self.assertEqual(session.app.invalidate_calls, 1)
        self.assertEqual(session._last_input_visible_lines, 2)
        self.assertEqual(len(bottom_checks), 1)
        self.assertEqual(pin_calls, [session.transcript_window])
        self.assertTrue(visible_calls)
        self.assertEqual(visible_calls[-1][1], 6)

    def test_application_merges_key_bindings_with_custom_handlers_before_default_mouse(self):
        previous_merge = repl.merge_key_bindings
        recorded_sequences = []

        def _merge_spy(sequence):
            recorded_sequences.append(sequence)
            return previous_merge(sequence)

        repl.merge_key_bindings = _merge_spy
        self.addCleanup(setattr, repl, "merge_key_bindings", previous_merge)

        self._create_session()

        self.assertTrue(recorded_sequences)
        self.assertIs(recorded_sequences[-1][0], repl.bindings)
        self.assertIs(recorded_sequences[-1][1], repl.default_mouse_bindings)

    def test_append_input_transcript_pins_to_bottom(self):
        session = self._create_session()

        previous_pin_bottom = viewport_policy.pin_transcript_to_bottom
        pin_calls = []

        def _pin_bottom(window):
            pin_calls.append(window)

        viewport_policy.pin_transcript_to_bottom = _pin_bottom
        self.addCleanup(setattr, viewport_policy, "pin_transcript_to_bottom", previous_pin_bottom)

        session.app.invalidate_calls = 0
        session.append_input_transcript("Debug.Log(1);")

        self.assertEqual(pin_calls, [session.transcript_window])
        self.assertEqual(session.app.invalidate_calls, 1)
        self.assertEqual(session.transcript_state.entries[-1].entry_type, "input")

    def test_append_result_transcript_entry_pins_to_bottom(self):
        session = self._create_session()

        previous_pin_bottom = viewport_policy.pin_transcript_to_bottom
        pin_calls = []

        def _pin_bottom(window):
            pin_calls.append(window)

        viewport_policy.pin_transcript_to_bottom = _pin_bottom
        self.addCleanup(setattr, viewport_policy, "pin_transcript_to_bottom", previous_pin_bottom)

        session.app.invalidate_calls = 0
        entry = TranscriptEntry(entry_type="result", ok=True, text="1", summary="ok")
        session.append_result_transcript_entry(entry)

        self.assertEqual(pin_calls, [session.transcript_window])
        self.assertEqual(session.app.invalidate_calls, 1)
        self.assertIs(session.transcript_state.entries[-1], entry)

    def test_handle_submitted_message_resets_buffer_and_refocuses_input(self):
        session = self._create_session()

        submitted = []
        session._on_submit = lambda text: submitted.append(text)
        session.default_buffer.text = "line1\nline2"
        session._last_input_visible_lines = 4
        session.app.invalidate_calls = 0

        class _LayoutStub:
            def __init__(self):
                self.focus_calls = []

            def focus(self, target):
                self.focus_calls.append(target)

        class _EventAppStub:
            def __init__(self):
                self.layout = _LayoutStub()

        class _EventStub:
            def __init__(self):
                self.app = _EventAppStub()

        event = _EventStub()

        session.handle_submitted_message("Debug.Log(1);", event)

        self.assertEqual(submitted, ["Debug.Log(1);"])
        self.assertEqual(session.default_buffer.text, "")
        self.assertEqual(session._last_input_visible_lines, 1)
        self.assertEqual(event.app.layout.focus_calls, [session.input_control])
        self.assertEqual(session.app.invalidate_calls, 1)

    def test_after_render_delays_external_open_in_background_task(self):
        session = self._create_session()
        previous_delay = repl.EXTERNAL_OPEN_DELAY_SECONDS
        scheduled = []
        open_calls = []

        def _create_background_task(coro):
            scheduled.append(coro)
            return coro

        self.addCleanup(setattr, repl, "EXTERNAL_OPEN_DELAY_SECONDS", previous_delay)
        repl.EXTERNAL_OPEN_DELAY_SECONDS = 0
        session.app.create_background_task = _create_background_task
        session.queue_external_open(lambda: open_calls.append("open"))

        session._handle_after_render(session.app)

        self.assertEqual(open_calls, [])
        self.assertEqual(len(scheduled), 1)

        asyncio.run(scheduled[0])

        self.assertEqual(open_calls, ["open"])
        self.assertFalse(session._external_open_task_active)


class PromptToolkitIntegrationRegressionTests(unittest.TestCase):
    def test_transcript_render_info_counts_wrapped_lines_as_scrollable_height(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    session.transcript_state.append_result(
                        TranscriptEntry(entry_type="result", ok=True, text="X" * 120, summary="ok")
                    )
                    session.app.renderer.render(session.app, session.app.layout)

                    self.assertIsNotNone(session.transcript_window.render_info)
                    self.assertGreater(
                        session.transcript_window.render_info.content_height,
                        session.transcript_window.render_info.window_height,
                        "Wrapped transcript content should produce scrollable height in the real prompt_toolkit Window render info",
                    )

        asyncio.run(_run_test())

    def test_input_window_height_matches_wrapped_visible_lines_without_extra_growth(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=12)):
                    session = repl.ensure_prompt_session()
                    session.default_buffer.set_document(Document("X" * 60, cursor_position=60), bypass_readonly=True)
                    session.app.renderer.render(session.app, session.app.layout)

                    root = session.app.layout.container.content
                    main_block = root.children[0].content
                    input_row = next(
                        container
                        for container in main_block.children
                        if any(
                            getattr(child, "content", None) is session.input_control
                            for child in getattr(container, "children", [])
                        )
                    )
                    input_window = next(
                        child for child in input_row.children if getattr(child, "content", None) is session.input_control
                    )
                    prompt_window = next(child for child in input_row.children if child is not input_window)

                    self.assertEqual(session._get_input_height().preferred, 3)
                    self.assertEqual(
                        input_window.render_info.window_height,
                        3,
                        "Input window should match wrapped visible line count instead of stretching taller than its content",
                    )
                    self.assertEqual(
                        prompt_window.render_info.window_height,
                        3,
                        "Prompt column should stay aligned with the input window height",
                    )

        asyncio.run(_run_test())

    def test_theme_preview_keeps_history_visible_in_standard_terminal(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=100, rows=24)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    session.transcript_state.append_input("Debug.Log(42);")
                    session.transcript_state.append_result(
                        TranscriptEntry(entry_type="result", ok=True, text="42", summary="ok")
                    )
                    session.default_buffer.set_document(
                        Document("/theme material", cursor_position=len("/theme material")),
                        bypass_readonly=True,
                    )
                    session.app.renderer.render(session.app, session.app.layout)

                    self.assertTrue(session.theme_preview_container.filter())
                    self.assertGreaterEqual(
                        session.transcript_window.render_info.window_height,
                        8,
                        "Compact theme preview should leave enough rows to read transcript history in a 24-row terminal",
                    )
                    screen = session.app.renderer._last_screen
                    rendered_rows = [
                        "".join(screen.data_buffer[row][column].char for column in range(100)).rstrip()
                        for row in range(24)
                    ]
                    preview_row = next(index for index, text in enumerate(rendered_rows) if "[theme preview]" in text)
                    history_row = next(index for index, text in enumerate(rendered_rows) if "Debug.Log(42);" in text)
                    self.assertLess(
                        preview_row,
                        history_row,
                        "Theme preview should stay above transcript history so an upward completion menu cannot cover it",
                    )

        asyncio.run(_run_test())

    def test_transcript_scroll_persists_after_scroll_down_and_render(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    session.transcript_state.append_result(
                        TranscriptEntry(entry_type="result", ok=True, text="X" * 120, summary="ok")
                    )
                    session.app.renderer.render(session.app, session.app.layout)

                    self.assertGreater(
                        session.transcript_window.render_info.content_height,
                        session.transcript_window.render_info.window_height,
                    )

                    session.scroll_transcript_window_down()
                    session.app.renderer.render(session.app, session.app.layout)

                    self.assertGreater(
                        session.transcript_window.vertical_scroll,
                        0,
                        "Transcript scroll position should persist after wheel/down scrolling instead of being reset on render",
                    )

        asyncio.run(_run_test())

    def test_transcript_scroll_can_continue_across_many_wheel_up_steps(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    for index in range(6):
                        session.transcript_state.append_result(
                            TranscriptEntry(entry_type="result", ok=True, text=(str(index) + "-") * 40, summary="ok")
                        )
                    session.app.renderer.render(session.app, session.app.layout)

                    initial_scroll = session.transcript_window.vertical_scroll
                    self.assertGreater(initial_scroll, 0)

                    for _ in range(8):
                        session.scroll_transcript_window_up()
                        session.app.renderer.render(session.app, session.app.layout)

                    self.assertLess(
                        session.transcript_window.vertical_scroll,
                        initial_scroll - 4,
                        "Transcript should keep scrolling upward across repeated wheel events instead of getting stuck after a few steps",
                    )

        asyncio.run(_run_test())

    def test_transcript_wheel_up_moves_to_previous_round_start(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    for index in range(8):
                        session.transcript_state.append_input(f"cmd {index} {'X' * 30}")
                        session.transcript_state.append_result(
                            TranscriptEntry(
                                entry_type="result",
                                ok=True,
                                text=f"result {index} {'Y' * 40}",
                                summary="ok",
                            )
                        )
                    session.app.renderer.render(session.app, session.app.layout)

                    initial_scroll = session.transcript_window.vertical_scroll
                    round_starts = [line for line in session.transcript_control._round_starts if line < initial_scroll]
                    self.assertTrue(round_starts)
                    previous_round_start = round_starts[-1]

                    session.scroll_transcript_window_up()
                    session.app.renderer.render(session.app, session.app.layout)

                    self.assertEqual(
                        session.transcript_window.vertical_scroll,
                        previous_round_start,
                        "Wheel-up should jump to the previous round start so fully hidden rounds can re-enter view immediately",
                    )

        asyncio.run(_run_test())

    def test_transcript_wheel_up_can_reach_leading_result_only_round(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    session.transcript_state.append_result(
                        TranscriptEntry(entry_type="result", ok=True, text="bootstrap " + ("Z" * 40), summary="ok")
                    )
                    for index in range(1, 11):
                        session.transcript_state.append_input(f"cmd {index}")
                        session.transcript_state.append_result(
                            TranscriptEntry(entry_type="result", ok=True, text=f"result {index}", summary="ok")
                        )
                    session.app.renderer.render(session.app, session.app.layout)

                    for _ in range(20):
                        session.scroll_transcript_window_up()
                        session.app.renderer.render(session.app, session.app.layout)

                    self.assertEqual(
                        session.transcript_window.vertical_scroll,
                        0,
                        "Scrolling up repeatedly should reach the very first leading result-only round",
                    )

        asyncio.run(_run_test())

    def test_transcript_wheel_down_returns_to_tail_after_round_navigation(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    for index in range(1, 11):
                        session.transcript_state.append_input(f"cmd {index} {'X' * 20}")
                        session.transcript_state.append_result(
                            TranscriptEntry(entry_type="result", ok=True, text=f"result {index} {'Y' * 20}", summary="ok")
                        )
                    session.app.renderer.render(session.app, session.app.layout)

                    tail_scroll = session.transcript_window.vertical_scroll
                    for _ in range(5):
                        session.scroll_transcript_window_up()
                        session.app.renderer.render(session.app, session.app.layout)

                    for _ in range(10):
                        session.scroll_transcript_window_down()
                        session.app.renderer.render(session.app, session.app.layout)

                    self.assertEqual(
                        session.transcript_window.vertical_scroll,
                        tail_scroll,
                        "Scrolling back down should return to the original tail position instead of stopping at an intermediate round",
                    )

        asyncio.run(_run_test())

    def test_input_height_change_keeps_transcript_pinned_after_scrolling_back_to_tail(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    for index in range(1, 11):
                        session.transcript_state.append_input(f"cmd {index} {'X' * 20}")
                        session.transcript_state.append_result(
                            TranscriptEntry(entry_type="result", ok=True, text=f"result {index} {'Y' * 20}", summary="ok")
                        )
                    session.app.renderer.render(session.app, session.app.layout)

                    for _ in range(5):
                        session.scroll_transcript_window_up()
                        session.app.renderer.render(session.app, session.app.layout)
                    for _ in range(10):
                        session.scroll_transcript_window_down()
                        session.app.renderer.render(session.app, session.app.layout)

                    tail_scroll = session.transcript_window.vertical_scroll
                    session.default_buffer.set_document(Document("X" * 60, cursor_position=60), bypass_readonly=True)
                    session._handle_input_text_changed(session.default_buffer)
                    session.app.renderer.render(session.app, session.app.layout)

                    self.assertEqual(
                        session.transcript_window.vertical_scroll,
                        session.transcript_control.get_vertical_scroll(),
                    )
                    self.assertGreaterEqual(
                        session.transcript_window.vertical_scroll,
                        tail_scroll,
                        "After returning to tail, input height changes should keep transcript pinned to the bottom",
                    )

        asyncio.run(_run_test())

    def test_transcript_exact_bootstrap_and_1_to_10_scenario_returns_to_original_tail(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    session.transcript_state.append_result(
                        TranscriptEntry(entry_type="result", ok=True, text="bootstrap-only", summary="ok")
                    )
                    for index in range(1, 11):
                        session.transcript_state.append_input(str(index))
                        session.transcript_state.append_result(
                            TranscriptEntry(entry_type="result", ok=True, text=f"res {index}", summary="ok")
                        )
                    session.app.renderer.render(session.app, session.app.layout)

                    original_tail_scroll = session.transcript_window.vertical_scroll

                    for _ in range(20):
                        session.scroll_transcript_window_up()
                        session.app.renderer.render(session.app, session.app.layout)

                    for _ in range(20):
                        session.scroll_transcript_window_down()
                        session.app.renderer.render(session.app, session.app.layout)

                    self.assertEqual(
                        session.transcript_window.vertical_scroll,
                        original_tail_scroll,
                        "Bootstrap + 1..10 scenario should return to the exact original tail position after scrolling back down",
                    )

        asyncio.run(_run_test())

    def test_new_message_while_browsing_history_repins_transcript_to_tail(self):
        async def _run_test():
            previous_session = repl.session
            repl.session = None
            self.addCleanup(setattr, repl, "session", previous_session)

            with create_pipe_input() as pipe_input:
                with create_app_session(input=pipe_input, output=_SizedDummyOutput(columns=24, rows=10)):
                    session = repl.ensure_prompt_session()
                    session.transcript_state.clear()
                    for index in range(1, 11):
                        session.transcript_state.append_input(str(index))
                        session.transcript_state.append_result(
                            TranscriptEntry(entry_type="result", ok=True, text=f"res {index}", summary="ok")
                        )
                    session.app.renderer.render(session.app, session.app.layout)
                    tail_scroll = session.transcript_window.vertical_scroll

                    for _ in range(5):
                        session.scroll_transcript_window_up()
                        session.app.renderer.render(session.app, session.app.layout)

                    session.append_result_transcript_entry(
                        TranscriptEntry(entry_type="result", ok=True, text="new tail result", summary="ok")
                    )
                    session.app.renderer.render(session.app, session.app.layout)

                    self.assertGreaterEqual(
                        session.transcript_window.vertical_scroll,
                        tail_scroll,
                        "A new incoming message should repin transcript browsing back to the tail",
                    )

        asyncio.run(_run_test())


class TranscriptControlMouseHandlingTests(unittest.TestCase):
    def test_transcript_control_remains_non_focusable_for_normal_typing(self):
        control = TranscriptControl(TranscriptState())

        self.assertFalse(control.is_focusable())

    def test_split_submission_action_is_rendered_as_separate_highlighted_lines(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_result(
            TranscriptEntry(
                entry_type="result",
                ok=False,
                error_kind="compile_error",
                text=(
                    "(1,1): error CS0433: The type exists in both assemblies"
                    "\n\n[REPL ACTION REQUIRED]"
                    "\nSplit this code into two REPL submissions:"
                    "\n  1. Submit the expression that uses the ambiguous type first."
                    "\n  2. Submit the non-public member access separately afterward."
                    "\n\nReason: ignoring accessibility exposed same-named types."
                ),
                summary="Compile failed",
                created_at="2026-04-05T12:34:56.000Z",
            )
        )

        content = control.create_content(width=160, height=20)
        rendered_lines = [content.get_line(index) for index in range(content.line_count)]
        action_lines = [
            fragments
            for fragments in rendered_lines
            if any("[REPL ACTION REQUIRED]" in text or "Submit the " in text for _style, text, *_rest in fragments)
        ]
        reason_line = next(
            fragments
            for fragments in rendered_lines
            if any("Reason:" in text for _style, text, *_rest in fragments)
        )

        self.assertEqual(len(action_lines), 3)
        self.assertTrue(
            all(
                any(style == "class:transcript.error.action_required.text" for style, _text, *_rest in fragments)
                for fragments in action_lines
            )
        )
        self.assertTrue(
            any(style == "class:transcript.error.compile_error.text" for style, _text, *_rest in reason_line)
        )

    def test_successful_accessibility_fallback_notice_is_highlighted_above_result(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_result(
            TranscriptEntry(
                entry_type="result",
                ok=True,
                text=(
                    "[REPL NOTICE]\n"
                    "Symbol conflict detected: this submission was recompiled with standard C# accessibility.\n"
                    "Non-public member access is unavailable in this submission.\n"
                    "Later submissions still try the REPL accessibility bypass first.\n\n"
                    "ManualAmbiguityProbe.Collision"
                ),
                summary="OK",
                created_at="2026-04-05T12:34:56.000Z",
            )
        )

        content = control.create_content(width=160, height=20)
        rendered_lines = [content.get_line(index) for index in range(content.line_count)]
        notice_lines = rendered_lines[:4]
        result_line = rendered_lines[-1]

        self.assertTrue(
            all(
                any(style == "class:transcript.notice.accessibility.text" for style, _text, *_rest in fragments)
                for fragments in notice_lines
            )
        )
        self.assertTrue(
            any(
                style == "class:transcript.result.text" and "ManualAmbiguityProbe.Collision" in text
                for style, text, *_rest in result_line
            )
        )

    def test_multiline_input_continuation_aligns_under_content_column(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append(
            TranscriptEntry(
                entry_type="input",
                text="line1\nline2",
                created_at="2026-04-05T12:34:56.000Z",
            )
        )

        content = control.create_content(width=80, height=10)

        self.assertEqual(
            "".join(text for _style, text in content.get_line(0)),
            "[12:34:56] > line1",
        )
        self.assertEqual(
            "".join(text for _style, text in content.get_line(1)),
            "             line2",
        )

    def test_multiline_result_continuation_aligns_under_content_column(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_result(
            TranscriptEntry(
                entry_type="result",
                ok=True,
                text="line1\nline2",
                summary="ok",
                created_at="2026-04-05T12:34:56.000Z",
            )
        )

        content = control.create_content(width=80, height=10)

        self.assertEqual(
            "".join(text for _style, text in content.get_line(0)),
            "[12:34:56] < line1",
        )
        self.assertEqual(
            "".join(text for _style, text in content.get_line(1)),
            "             line2",
        )

    def test_result_trailing_newline_does_not_render_extra_blank_continuation_line(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_result(
            TranscriptEntry(
                entry_type="result",
                ok=True,
                text="line1\n",
                summary="ok",
                created_at="2026-04-05T12:34:56.000Z",
            )
        )

        content = control.create_content(width=80, height=10)

        self.assertEqual(content.line_count, 1)
        self.assertEqual(
            "".join(text for _style, text in content.get_line(0)),
            "[12:34:56] < line1",
        )

    def test_mouse_drag_selects_transcript_text_for_copy(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_result(TranscriptEntry(entry_type="result", ok=True, text="alpha beta", summary="ok"))
        control.create_content(width=80, height=10)

        line_text = control._line_plain_texts[0]
        start_x = line_text.index("alpha")
        end_x = line_text.index("beta") + len("beta")

        control.mouse_handler(
            MouseEvent(
                position=Point(x=start_x, y=0),
                event_type=MouseEventType.MOUSE_DOWN,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=end_x, y=0),
                event_type=MouseEventType.MOUSE_MOVE,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=end_x, y=0),
                event_type=MouseEventType.MOUSE_UP,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )

        clipboard_data = control.copy_selection()

        self.assertEqual(clipboard_data.text, "alpha beta")
        self.assertIsNone(control.selection_state)

    def test_mouse_drag_to_line_end_includes_last_character(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_result(TranscriptEntry(entry_type="result", ok=True, text="abc", summary="ok"))
        control.create_content(width=80, height=10)

        line_text = control._line_plain_texts[0]
        start_x = line_text.index("a")
        last_x = len(line_text) - 1

        control.mouse_handler(
            MouseEvent(
                position=Point(x=start_x, y=0),
                event_type=MouseEventType.MOUSE_DOWN,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=last_x, y=0),
                event_type=MouseEventType.MOUSE_MOVE,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=last_x, y=0),
                event_type=MouseEventType.MOUSE_UP,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )

        clipboard_data = control.copy_selection()

        self.assertEqual(clipboard_data.text, "abc")

    def test_mouse_drag_selection_persists_when_move_and_up_report_no_button(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_result(TranscriptEntry(entry_type="result", ok=True, text="alpha beta", summary="ok"))
        control.create_content(width=80, height=10)

        line_text = control._line_plain_texts[0]
        start_x = line_text.index("alpha")
        end_x = line_text.index("beta") + len("beta")

        control.mouse_handler(
            MouseEvent(
                position=Point(x=start_x, y=0),
                event_type=MouseEventType.MOUSE_DOWN,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=end_x, y=0),
                event_type=MouseEventType.MOUSE_MOVE,
                button=MouseButton.NONE,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=end_x, y=0),
                event_type=MouseEventType.MOUSE_UP,
                button=MouseButton.NONE,
                modifiers=frozenset(),
            )
        )

        clipboard_data = control.copy_selection()

        self.assertEqual(clipboard_data.text, "alpha beta")

    def test_mouse_drag_across_blank_separator_line_keeps_selection(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_input("first")
        state.append_result(TranscriptEntry(entry_type="result", ok=True, text="second", summary="ok"))
        control.create_content(width=80, height=10)

        first_line_text = control._line_plain_texts[0]
        result_line_index = 2
        result_line_text = control._line_plain_texts[result_line_index]

        start_x = first_line_text.index("first")
        end_x = result_line_text.index("second") + len("second")

        control.mouse_handler(
            MouseEvent(
                position=Point(x=start_x, y=0),
                event_type=MouseEventType.MOUSE_DOWN,
                button=MouseButton.LEFT,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=0, y=1),
                event_type=MouseEventType.MOUSE_MOVE,
                button=MouseButton.NONE,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=end_x, y=result_line_index),
                event_type=MouseEventType.MOUSE_MOVE,
                button=MouseButton.NONE,
                modifiers=frozenset(),
            )
        )
        control.mouse_handler(
            MouseEvent(
                position=Point(x=end_x, y=result_line_index),
                event_type=MouseEventType.MOUSE_UP,
                button=MouseButton.NONE,
                modifiers=frozenset(),
            )
        )

        clipboard_data = control.copy_selection()

        self.assertIn("first", clipboard_data.text)
        self.assertIn("second", clipboard_data.text)
        self.assertIn("\n\n", clipboard_data.text)

    def test_completed_round_renders_trailing_separator_before_next_input_exists(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_input("first")
        state.append_result(TranscriptEntry(entry_type="result", ok=True, text="done-1", summary="ok"))

        control.create_content(width=80, height=12)

        self.assertIn("done-1", control._line_plain_texts[2])
        self.assertEqual(control._line_plain_texts[3], session_ui.ROUND_SEPARATOR_CHAR * 80)
        self.assertEqual(len(control._line_plain_texts), 4)

    def test_round_separator_has_no_blank_line_before_or_after(self):
        state = TranscriptState()
        control = TranscriptControl(state)
        state.append_input("first")
        state.append_result(TranscriptEntry(entry_type="result", ok=True, text="done-1", summary="ok"))
        state.append_input("second")
        state.append_result(TranscriptEntry(entry_type="result", ok=True, text="done-2", summary="ok"))

        control.create_content(width=80, height=12)

        separator = session_ui.ROUND_SEPARATOR_CHAR * 80
        separator_indexes = [
            index for index, line in enumerate(control._line_plain_texts) if line == separator
        ]
        self.assertEqual(separator_indexes, [3, 7])
        for index in separator_indexes:
            if index > 0:
                self.assertNotEqual(control._line_plain_texts[index - 1], "")
            if index + 1 < len(control._line_plain_texts):
                self.assertNotEqual(control._line_plain_texts[index + 1], "")

    def test_mouse_scroll_up_moves_to_previous_round_target(self):
        control = TranscriptControl(TranscriptState())
        state = control._transcript_state
        for index in range(1, 6):
            state.append_input(f"cmd {index}")
            state.append_result(TranscriptEntry(entry_type="result", ok=True, text=f"res {index}", summary="ok"))

        control.create_content(width=24, height=5)
        control._follow_tail = False
        control._scroll_anchor_line = control._scroll_targets[-1]

        result = control.mouse_handler(
            MouseEvent(
                position=Point(x=0, y=0),
                event_type=MouseEventType.SCROLL_UP,
                button=MouseButton.NONE,
                modifiers=frozenset(),
            )
        )

        self.assertIsNone(result)
        self.assertEqual(control._scroll_anchor_line, control._scroll_targets[-2])

    def test_mouse_scroll_down_moves_to_next_round_target(self):
        control = TranscriptControl(TranscriptState())
        state = control._transcript_state
        for index in range(1, 6):
            state.append_input(f"cmd {index}")
            state.append_result(TranscriptEntry(entry_type="result", ok=True, text=f"res {index}", summary="ok"))

        control.create_content(width=24, height=5)
        control._follow_tail = False
        control._scroll_anchor_line = control._scroll_targets[1]

        result = control.mouse_handler(
            MouseEvent(
                position=Point(x=0, y=0),
                event_type=MouseEventType.SCROLL_DOWN,
                button=MouseButton.NONE,
                modifiers=frozenset(),
            )
        )

        self.assertIsNone(result)
        self.assertEqual(control._scroll_anchor_line, control._scroll_targets[2])


class WheelRoutingTests(unittest.TestCase):
    def test_resolve_wheel_target_returns_completion_when_completion_open(self):
        self.assertEqual(
            scroll_router.resolve_wheel_target(completion_open=True),
            scroll_router.WHEEL_TARGET_COMPLETION,
        )

    def test_resolve_wheel_target_returns_transcript_when_completion_closed(self):
        self.assertEqual(
            scroll_router.resolve_wheel_target(completion_open=False),
            scroll_router.WHEEL_TARGET_TRANSCRIPT,
        )

    def test_route_wheel_up_scrolls_transcript_when_completion_closed(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _ShellStub:
            def __init__(self):
                self.default_buffer = type("BufferStub", (), {"complete_state": None})()
                self.transcript_up_calls = 0

            def scroll_transcript_window_up(self):
                self.transcript_up_calls += 1

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": False})()
        try:
            repl._route_wheel_up(type("EventStub", (), {})())
        finally:
            repl.session = previous_session
            repl.get_app = previous_search

        self.assertEqual(shell.transcript_up_calls, 1)

    def test_route_wheel_up_moves_completion_when_completion_open(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _BufferStub:
            def __init__(self):
                self.complete_state = object()
                self.complete_previous_calls = []

            def complete_previous(self, count=1, disable_wrap_around=False):
                self.complete_previous_calls.append((count, disable_wrap_around))

        class _ShellStub:
            def __init__(self):
                self.default_buffer = _BufferStub()
                self.transcript_up_calls = 0

            def scroll_transcript_window_up(self):
                self.transcript_up_calls += 1

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": False})()
        try:
            repl._route_wheel_up(type("EventStub", (), {})())
        finally:
            repl.session = previous_session
            repl.get_app = previous_search

        self.assertEqual(shell.default_buffer.complete_previous_calls, [(3, True)])
        self.assertEqual(shell.transcript_up_calls, 0)

    def test_route_wheel_down_moves_completion_when_completion_open(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _BufferStub:
            def __init__(self):
                self.complete_state = object()
                self.complete_next_calls = []

            def complete_next(self, count=1, disable_wrap_around=False):
                self.complete_next_calls.append((count, disable_wrap_around))

        class _ShellStub:
            def __init__(self):
                self.default_buffer = _BufferStub()
                self.transcript_down_calls = 0

            def scroll_transcript_window_down(self):
                self.transcript_down_calls += 1

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": False})()
        try:
            repl._route_wheel_down(type("EventStub", (), {})())
        finally:
            repl.session = previous_session
            repl.get_app = previous_search

        self.assertEqual(shell.default_buffer.complete_next_calls, [(3, True)])
        self.assertEqual(shell.transcript_down_calls, 0)

    def test_route_wheel_down_scrolls_transcript_when_completion_closed(self):
        previous_session = repl.session
        previous_search = repl.get_app

        class _ShellStub:
            def __init__(self):
                self.default_buffer = type("BufferStub", (), {"complete_state": None})()
                self.transcript_down_calls = 0

            def scroll_transcript_window_down(self):
                self.transcript_down_calls += 1

        shell = _ShellStub()
        repl.session = shell
        repl.get_app = lambda: type("AppStub", (), {"is_searching": False})()
        try:
            repl._route_wheel_down(type("EventStub", (), {})())
        finally:
            repl.session = previous_session
            repl.get_app = previous_search

        self.assertEqual(shell.transcript_down_calls, 1)


class TranscriptStateTests(unittest.TestCase):
    def test_transcript_state_appends_and_clears_entries(self):
        state = TranscriptState()

        input_entry = state.append_input("Debug.Log(1);")
        info_entry = state.append_info("Connected")

        result_entry = TranscriptEntry(entry_type="result", ok=True, text="1", summary="OK")
        state.append_result(result_entry)

        self.assertEqual(len(state.entries), 3)
        self.assertEqual(input_entry.entry_type, "input")
        self.assertEqual(info_entry.entry_type, "info")
        self.assertIs(state.entries[-1], result_entry)

        state.clear()
        self.assertEqual(state.entries, [])

    def test_transcript_entry_created_at_is_available_for_timestamp_rendering(self):
        entry = TranscriptEntry(entry_type="input", text="Debug.Log(1);", created_at="2026-04-04T12:34:56.000Z")

        self.assertEqual(entry.created_at, "2026-04-04T12:34:56.000Z")
        self.assertEqual(session_ui.format_transcript_timestamp(entry.created_at), "12:34:56")


class ResultClassificationTests(unittest.TestCase):
    def test_build_result_entry_classifies_compile_error(self):
        result = {
            "ok": False,
            "stage": "compile",
            "type": "compile_error",
            "summary": "Compile failed: CS1002",
            "data": {"text": "compile output"},
        }

        entry = output.build_result_entry(result, _extract_text_from_data)

        self.assertFalse(entry.ok)
        self.assertEqual(entry.error_kind, "compile_error")

    def test_build_result_entry_classifies_timeout_error(self):
        result = {
            "ok": False,
            "stage": "execute",
            "type": "runtime_error",
            "summary": "Timed out waiting for Unity service recovery",
            "data": {"text": "timeout"},
        }

        entry = output.build_result_entry(result, _extract_text_from_data)

        self.assertEqual(entry.error_kind, "timeout_error")

    def test_build_result_entry_classifies_connection_error(self):
        result = {
            "ok": False,
            "stage": "execute",
            "type": "system_error",
            "summary": "Error post: HTTPConnectionPool host=127.0.0.1 Failed to establish a new connection",
            "data": {"text": ""},
        }

        entry = output.build_result_entry(result, _extract_text_from_data)

        self.assertEqual(entry.error_kind, "connection_error")

    def test_build_result_entry_classifies_transport_error(self):
        result = {
            "ok": False,
            "stage": "execute",
            "type": "system_error",
            "summary": "Error post: malformed response",
            "data": {"text": ""},
        }

        entry = output.build_result_entry(result, _extract_text_from_data)

        self.assertEqual(entry.error_kind, "transport_error")

    def test_build_result_entry_classifies_command_error(self):
        result = {
            "ok": False,
            "stage": "command",
            "type": "system_error",
            "summary": "Command failed",
            "data": {"text": ""},
        }

        entry = output.build_result_entry(result, _extract_text_from_data)

        self.assertEqual(entry.error_kind, "command_error")

    def test_build_result_entry_classifies_builtin_error(self):
        result = {
            "ok": False,
            "stage": "builtin",
            "type": "builtin_error",
            "summary": "Builtin failed",
            "data": {"text": ""},
        }

        entry = output.build_result_entry(result, _extract_text_from_data)

        self.assertEqual(entry.error_kind, "builtin_error")


class OutputRenderingTests(unittest.TestCase):
    def test_build_result_entry_renders_non_empty_command_result_json(self):
        result = {
            "ok": True,
            "stage": "command",
            "type": "ok",
            "summary": "Editor status fetched",
            "data": {
                "command": {"commandNamespace": "editor", "action": "status", "sessionId": "sid-1"},
                "resultJson": {"initialized": True, "port": 14500, "editorState": "ready"},
                "nextAction": "",
            },
        }

        entry = output.build_result_entry(result, lambda data: data["text"] if "text" in (data or {}) else None)

        self.assertTrue(entry.ok)
        self.assertEqual(
            entry.text,
            '{\n  "initialized": true,\n  "port": 14500,\n  "editorState": "ready"\n}',
        )

    def test_build_result_entry_keeps_summary_for_empty_command_result_json(self):
        result = {
            "ok": True,
            "stage": "command",
            "type": "ok",
            "summary": "Requested enter playmode",
            "data": {
                "command": {"commandNamespace": "editor", "action": "playmode.enter", "sessionId": "sid-1"},
                "resultJson": {},
                "nextAction": "",
            },
        }

        entry = output.build_result_entry(result, lambda data: data["text"] if "text" in (data or {}) else None)

        self.assertTrue(entry.ok)
        self.assertEqual(entry.text, "Requested enter playmode")

    def test_print_text_result_preserves_trailing_newline_verbatim(self):
        result = {
            "ok": True,
            "stage": "builtin",
            "type": "",
            "summary": "Usage",
            "data": {"text": "Usage: /usage\n"},
        }

        buffer = io.StringIO()
        with redirect_stdout(buffer):
            output.print_text_result(result, _extract_text_from_data)

        self.assertEqual(buffer.getvalue(), "Usage: /usage\n")


class ViewportPolicyInputHeightTests(unittest.TestCase):
    def test_compute_input_height_counts_wrapped_lines_for_single_logical_line(self):
        dimension = viewport_policy.compute_input_height("abcdef", available_width=2)

        self.assertEqual(dimension.preferred, 3)

    def test_compute_input_height_counts_wrapped_lines_across_multiple_logical_lines(self):
        dimension = viewport_policy.compute_input_height("abcd\nefghij", available_width=3)

        self.assertEqual(dimension.preferred, 4)

    def test_compute_input_height_caps_wrapped_lines_at_max_visible_lines(self):
        dimension = viewport_policy.compute_input_height("x" * 40, available_width=1)

        self.assertEqual(dimension.preferred, 8)


class BuiltinPayloadTests(unittest.TestCase):
    def setUp(self):
        self.registry = builtins.BuiltinRegistry()

    def test_process_builtin_cmd_reports_unknown_slash_command_as_builtin_error(self):
        payload = builtins.process_builtin_cmd("/missing", self.registry.commands)

        self.assertTrue(payload["handled"])
        result = payload["result"]
        self.assertFalse(result["ok"])
        self.assertEqual(result["stage"], "builtin")
        self.assertEqual(result["type"], "builtin_error")
        self.assertEqual(result["summary"], "Unknown command: /missing")
        self.assertEqual(result["data"].get("text"), "")
        self.assertFalse(result["data"].get("silent"))

    def test_process_builtin_cmd_returns_success_payload_when_builtin_has_no_output(self):
        @self.registry.decorator("/ok", "ok")
        def _ok(_message):
            return None

        payload = builtins.process_builtin_cmd("/ok", self.registry.commands)

        self.assertTrue(payload["handled"])
        result = payload["result"]
        self.assertTrue(result["ok"])
        self.assertEqual(result["stage"], "builtin")
        self.assertEqual(result["data"].get("text"), "success\n")
        self.assertFalse(result["data"].get("silent"))

    def test_process_builtin_cmd_returns_builtin_output_payload(self):
        @self.registry.decorator("/usage", "usage")
        def _usage(_message):
            print("Usage: /usage")
            return None

        payload = builtins.process_builtin_cmd("/usage", self.registry.commands)

        self.assertTrue(payload["handled"])
        self.assertEqual(payload["result"]["data"].get("text"), "Usage: /usage\n")

    def test_process_builtin_cmd_marks_silent_success_payload(self):
        @self.registry.decorator("/silent", "silent")
        def _silent(_message):
            return "silent-success"

        payload = builtins.process_builtin_cmd("/silent", self.registry.commands)

        self.assertTrue(payload["handled"])
        self.assertTrue(payload["result"]["ok"])
        self.assertEqual(payload["result"]["data"].get("text"), "")
        self.assertTrue(payload["result"]["data"].get("silent"))


class LoopTranscriptWiringTests(unittest.TestCase):
    def test_execute_repl_snippet_appends_result_entry(self):
        state = TranscriptState()
        result_payload = {
            "ok": True,
            "stage": "execute",
            "type": "",
            "summary": "ok",
            "data": {"text": "42\n"},
        }

        invalidations = []

        def execute_editor_request(message, command_id, reset=False, invalidate_completion=None):
            if invalidate_completion is not None:
                invalidate_completion()
            return result_payload

        loop.execute_repl_snippet(
            message="1+41",
            reset=False,
            runtime_mode=False,
            cmd_id="cmd-1",
            execute_runtime_request=lambda *_args, **_kwargs: None,
            execute_editor_request=execute_editor_request,
            append_result_entry=lambda result: state.append_result(result),
            build_result_entry=lambda result: output.build_result_entry(result, _extract_text_from_data),
            invalidate_completion=lambda: invalidations.append("called"),
        )

        self.assertEqual(invalidations, ["called"])
        self.assertEqual(len(state.entries), 1)
        self.assertEqual(state.entries[0].entry_type, "result")
        self.assertTrue(state.entries[0].ok)
        self.assertEqual(state.entries[0].text, "42\n")

    def test_try_process_command_expression_appends_error_entry_on_parse_error(self):
        state = TranscriptState()

        handled = loop.try_process_command_expression(
            message="@bad(",
            cmd_id="cmd-1",
            looks_like_command_expression_prefix=lambda _text: True,
            parse_command_expression=lambda _text: (_ for _ in ()).throw(ValueError("expected ')'")),
            request_command=lambda *_args, **_kwargs: None,
            append_result_entry=lambda result: state.append_result(result),
            build_result_entry=lambda result: output.build_result_entry(result, _extract_text_from_data),
        )

        self.assertTrue(handled)
        self.assertEqual(len(state.entries), 1)
        self.assertEqual(state.entries[0].entry_type, "result")
        self.assertFalse(state.entries[0].ok)
        self.assertEqual(state.entries[0].error_kind, "command_error")
        self.assertIn("syntax error", state.entries[0].text)

    def test_start_repl_appends_input_before_processing_each_message(self):
        state = TranscriptState()
        prompted_messages = ["/completion 1", "@game.pause()", "Debug.Log(1);", None]

        class SessionStub:
            style = None

            def __init__(self, queue):
                self.queue = list(queue)

            def prompt(self, _message):
                return self.queue.pop(0)

        session = SessionStub(prompted_messages)
        processed = []
        previous_set_title = loop.set_title
        previous_patch_stdout = loop.patch_stdout

        class _NoopPatchStdout:
            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, tb):
                return False

        loop.set_title = lambda _title: None
        loop.patch_stdout = lambda: _NoopPatchStdout()
        try:
            loop.start_repl(
                ensure_prompt_session=lambda: session,
                build_terminal_title=lambda: "title",
                runtime_mode=False,
                runtime_dll_path="",
                runtime_defines_path="",
                build_startup_banner=lambda: [("", "banner")],
                print_help_info=lambda: None,
                execute_startup_snippet=lambda: None,
                process_builtin_cmd=lambda message: processed.append(("builtin", message)) or (message == "/completion 1"),
                try_process_command_expression_func=lambda message: processed.append(("command", message)) or (message == "@game.pause()"),
                execute_repl_snippet_func=lambda message: processed.append(("execute", message)),
                build_prompt_message=lambda: [("", "> ")],
                append_input_entry=lambda text: state.append_input(text),
            )
        finally:
            loop.patch_stdout = previous_patch_stdout
            loop.set_title = previous_set_title

        self.assertEqual(
            [entry.text for entry in state.entries],
            ["/completion 1", "@game.pause()", "Debug.Log(1);"],
        )
        self.assertEqual(
            processed,
            [
                ("builtin", "/completion 1"),
                ("builtin", "@game.pause()"),
                ("command", "@game.pause()"),
                ("builtin", "Debug.Log(1);"),
                ("command", "Debug.Log(1);"),
                ("execute", "Debug.Log(1);"),
            ],
        )

    def test_start_repl_uses_single_run_interactive_path_when_available(self):
        state = TranscriptState()
        previous_set_title = loop.set_title
        previous_patch_stdout = loop.patch_stdout

        class _NoopPatchStdout:
            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, tb):
                return False

        class SessionStub:
            style = None

            def __init__(self, queue):
                self.queue = list(queue)
                self.run_interactive_calls = 0

            def run_interactive(self, on_submit):
                self.run_interactive_calls += 1
                for message in self.queue:
                    on_submit(message)

            def prompt(self, _message):
                raise AssertionError("prompt() should not be used when run_interactive exists")

        session = SessionStub(["/completion 1", "@game.pause()", "Debug.Log(1);", None])
        processed = []

        loop.set_title = lambda _title: None
        loop.patch_stdout = lambda: _NoopPatchStdout()
        try:
            loop.start_repl(
                ensure_prompt_session=lambda: session,
                build_terminal_title=lambda: "title",
                runtime_mode=False,
                runtime_dll_path="",
                runtime_defines_path="",
                build_startup_banner=lambda: [("", "banner")],
                print_help_info=lambda: None,
                execute_startup_snippet=lambda: None,
                process_builtin_cmd=lambda message: processed.append(("builtin", message)) or (message == "/completion 1"),
                try_process_command_expression_func=lambda message: processed.append(("command", message)) or (message == "@game.pause()"),
                execute_repl_snippet_func=lambda message: processed.append(("execute", message)),
                build_prompt_message=lambda: [("", "> ")],
                append_input_entry=lambda text: state.append_input(text),
            )
        finally:
            loop.patch_stdout = previous_patch_stdout
            loop.set_title = previous_set_title

        self.assertEqual(session.run_interactive_calls, 1)
        self.assertEqual(
            [entry.text for entry in state.entries],
            ["/completion 1", "@game.pause()", "Debug.Log(1);"],
        )
        self.assertEqual(
            processed,
            [
                ("builtin", "/completion 1"),
                ("builtin", "@game.pause()"),
                ("command", "@game.pause()"),
                ("builtin", "Debug.Log(1);"),
                ("command", "Debug.Log(1);"),
                ("execute", "Debug.Log(1);"),
            ],
        )


if __name__ == "__main__":
    try:
        unittest.main()
    finally:
        if _ADDED_CORE_PATH:
            sys.path.remove(CORE_PATH)
        if _ADDED_SITE_PACKAGES_PATH:
            sys.path.remove(SITE_PACKAGES_PATH)
