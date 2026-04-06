import asyncio
import os
import sys
import uuid
from datetime import datetime

from prompt_toolkit.application import Application
from prompt_toolkit.application.current import get_app
from prompt_toolkit.buffer import Buffer
from prompt_toolkit.clipboard import InMemoryClipboard
from prompt_toolkit.completion import ThreadedCompleter
from prompt_toolkit.document import Document
from prompt_toolkit.filters import Condition, has_focus, is_searching
from prompt_toolkit.history import FileHistory
from prompt_toolkit.key_binding import KeyBindings, merge_key_bindings
from prompt_toolkit.key_binding.bindings.emacs import load_emacs_search_bindings
from prompt_toolkit.key_binding.bindings.mouse import load_mouse_bindings
from prompt_toolkit.keys import Keys
from prompt_toolkit.layout import HSplit, Layout, VSplit, Window
from prompt_toolkit.layout.containers import Float, FloatContainer
from prompt_toolkit.layout.controls import BufferControl, FormattedTextControl
from prompt_toolkit.layout.menus import CompletionsMenu
from prompt_toolkit.lexers import PygmentsLexer
from prompt_toolkit.shortcuts import set_title
from prompt_toolkit.styles import Style, merge_styles, style_from_pygments_cls
from prompt_toolkit.widgets import SearchToolbar

try:
    from prompt_toolkit.clipboard.pyperclip import PyperclipClipboard
except Exception:
    PyperclipClipboard = None

try:
    from pygments.lexers.dotnet import CSharpLexer as PygmentsCSharpLexer
    from pygments.styles import get_style_by_name
except Exception:
    PygmentsCSharpLexer = None
    get_style_by_name = None

from repl import client, config
from repl import output
from repl import session_ui
from repl import scroll_router, viewport_policy
from repl.transcript import TranscriptState
from repl.transcript_control import TranscriptControl
from repl.builtins import BuiltinRegistry, process_builtin_cmd as _process_builtin_cmd_impl, register_default_builtins
from repl.command_expr import parse_command_expression, looks_like_command_expression_prefix
from repl.loop import (
    execute_repl_snippet as _execute_repl_snippet_impl,
    run_repl as _run_repl_impl,
    start_repl as _start_repl_impl,
    try_process_command_expression as _try_process_command_expression_impl,
)
from repl.completion import (
    accept_completion,
    build_completers,
    get_command_catalog as _get_command_catalog_impl,
    invalidate_command_catalog as _invalidate_command_catalog_impl,
    trigger_completion_on_change as _trigger_completion_on_change_impl,
)

if os.name == "nt":
    os.system("")

bindings = KeyBindings()
enableml = 1
enable_completion = True
_builtin_registry = BuiltinRegistry()
builtin_cmds = _builtin_registry.commands
_builtin_command_order = _builtin_registry.order
session = None
_pending_quit_confirmation = False
_command_catalog_state = {"command_catalog_cache": None}
cmd_id = ""
history = FileHistory(config._log_file_path)
MAX_INPUT_VISIBLE_LINES = 8
TRANSCRIPT_WHEEL_SCROLL_LINES = 3
EXTERNAL_OPEN_DELAY_SECONDS = 0.35
default_mouse_bindings = load_mouse_bindings()


def _is_completion_enabled():
    return enable_completion


def _get_cmd_id():
    return cmd_id


builtin_cmd_completer, command_expr_completer, roslyn_completer, combined_completer, fuzzy_completer = build_completers(
    builtin_cmds,
    _builtin_command_order,
    _command_catalog_state,
    _get_cmd_id,
    _is_completion_enabled,
)


def _set_enable_completion(value):
    global enable_completion
    enable_completion = value


def _set_enableml(value):
    global enableml
    enableml = value


def invalidate_command_catalog():
    return _invalidate_command_catalog_impl(_command_catalog_state)


def get_command_catalog():
    return _get_command_catalog_impl(_command_catalog_state, _get_cmd_id)


def _trigger_completion_on_change(buff):
    return _trigger_completion_on_change_impl(buff, _is_completion_enabled)


@Condition
def _pending_quit_confirmation_filter():
    return _pending_quit_confirmation


def _reset_prompt_transient_state():
    global _pending_quit_confirmation
    _pending_quit_confirmation = False


def _submit_current_buffer(event):
    _reset_prompt_transient_state()
    text = event.app.current_buffer.document.text
    session.history.append_string(text)

    shell = ensure_prompt_session()
    interactive_callback = getattr(shell, "_on_submit", None)
    if callable(interactive_callback) and hasattr(shell, "handle_submitted_message"):
        shell.handle_submitted_message(text, event)
        return

    event.app.exit(result=text)


def clear_current_input(event):
    _reset_prompt_transient_state()
    event.current_buffer.reset()


def handle_ctrl_c(event):
    global _pending_quit_confirmation
    transcript_control = getattr(session, "transcript_control", None)
    if transcript_control is not None and getattr(transcript_control, "selection_state", None) is not None:
        event.app.clipboard.set_data(transcript_control.copy_selection())
        _reset_prompt_transient_state()
        if session is not None and getattr(session, "app", None) is not None:
            session.app.invalidate()
        return

    buffer = getattr(event, "current_buffer", None)
    if buffer is not None and getattr(buffer, "selection_state", None) is not None:
        clipboard_data = buffer.copy_selection()
        event.app.clipboard.set_data(clipboard_data)
        _reset_prompt_transient_state()
        return

    buffer_text = ""
    if buffer is not None and getattr(buffer, "document", None) is not None:
        buffer_text = getattr(buffer.document, "text", "") or getattr(buffer.document, "text_before_cursor", "")

    if buffer_text:
        clear_current_input(event)
        return

    if _pending_quit_confirmation:
        _pending_quit_confirmation = False
        event.app.exit(result=None)
        return
    _pending_quit_confirmation = True


@bindings.add('enter', filter=has_focus("DEFAULT_BUFFER") & ~is_searching)
def _(event):
    _submit_current_buffer(event)


@bindings.add('c-j', filter=has_focus("DEFAULT_BUFFER") & ~is_searching)
def _(event):
    global _pending_quit_confirmation
    _pending_quit_confirmation = False
    event.app.current_buffer.insert_text("\n")


@bindings.add('c-c', filter=has_focus("DEFAULT_BUFFER"))
def _(event):
    handle_ctrl_c(event)


@bindings.add('<any>', filter=has_focus("DEFAULT_BUFFER") & _pending_quit_confirmation_filter)
def _(event):
    global _pending_quit_confirmation
    if event.key_sequence[-1].key != 'c-c':
        _pending_quit_confirmation = False


@bindings.add('tab')
def _(event):
    accept_completion(event)


def _is_completion_menu_open():
    try:
        shell = ensure_prompt_session()
        return bool(getattr(shell.default_buffer, "complete_state", None) is not None)
    except Exception:
        return False


def _get_wheel_target():
    return scroll_router.resolve_wheel_target(
        completion_open=_is_completion_menu_open(),
    )


def _invoke_default_mouse_binding(key, event):
    for binding in default_mouse_bindings.bindings:
        if binding.keys == (key,):
            return binding.handler(event)
    return NotImplemented


def _route_wheel_up(event):
    shell = ensure_prompt_session()
    target = _get_wheel_target()
    if target == scroll_router.WHEEL_TARGET_COMPLETION:
        if hasattr(shell.default_buffer, "complete_previous"):
            shell.default_buffer.complete_previous(count=3, disable_wrap_around=True)
        return None
    shell.scroll_transcript_window_up()
    return None


def _route_wheel_down(event):
    shell = ensure_prompt_session()
    target = _get_wheel_target()
    if target == scroll_router.WHEEL_TARGET_COMPLETION:
        if hasattr(shell.default_buffer, "complete_next"):
            shell.default_buffer.complete_next(count=3, disable_wrap_around=True)
        return None
    shell.scroll_transcript_window_down()
    return None


@bindings.add(Keys.ScrollUp)
def _(event):
    return _route_wheel_up(event)


@bindings.add(Keys.ScrollDown)
def _(event):
    return _route_wheel_down(event)


@bindings.add(Keys.WindowsMouseEvent)
def _(event):
    try:
        _button, event_type, _x, _y = event.data.split(";")
    except Exception:
        return _invoke_default_mouse_binding(Keys.WindowsMouseEvent, event)
    if event_type == "SCROLL_UP":
        return _route_wheel_up(event)
    if event_type == "SCROLL_DOWN":
        return _route_wheel_down(event)
    return _invoke_default_mouse_binding(Keys.WindowsMouseEvent, event)


@bindings.add(Keys.Vt100MouseEvent)
def _(event):
    data = getattr(event, "data", "") or ""
    try:
        if data[2] == "M":
            mouse_event = ord(data[3])
        else:
            payload = data[2:]
            if payload.startswith("<"):
                payload = payload[1:]
            mouse_event = int(payload[:-1].split(";")[0])
    except Exception:
        return _invoke_default_mouse_binding(Keys.Vt100MouseEvent, event)

    if mouse_event in (64, 96):
        return _route_wheel_up(event)
    if mouse_event in (65, 97):
        return _route_wheel_down(event)
    return _invoke_default_mouse_binding(Keys.Vt100MouseEvent, event)


def _build_terminal_title():
    return session_ui.build_terminal_title(config)


def _build_startup_banner():
    return session_ui.build_startup_banner(config, cmd_id)


def _is_search_active():
    try:
        return bool(getattr(get_app().layout, "is_searching", False))
    except Exception:
        return False


def _build_clipboard():
    if PyperclipClipboard is not None:
        try:
            return PyperclipClipboard()
        except Exception:
            pass
    return InMemoryClipboard()


def _get_current_prompt_time():
    return datetime.now().strftime("%H:%M:%S")


def _build_prompt_message():
    return session_ui.build_prompt_message()


def _build_prompt_continuation(width, line_number, wrap_count):
    return session_ui.build_prompt_continuation(width, line_number, wrap_count)


class ReplApplicationShell:
    def __init__(self, *, style, lexer):
        self.style = style
        self.lexer = lexer
        self.history = history
        self.transcript_state = TranscriptState()
        self._prompt_message = _build_prompt_message()
        self._on_submit = None
        self._pending_external_open = None
        self._external_open_task_active = False
        self.search_toolbar = SearchToolbar(
            backward_search_prompt="",
            forward_search_prompt="",
            text_if_not_searching="",
        )

        self.default_buffer = Buffer(
            name="DEFAULT_BUFFER",
            multiline=True,
            history=self.history,
            completer=ThreadedCompleter(fuzzy_completer),
            complete_while_typing=False,
        )
        self.default_buffer.on_text_changed += _trigger_completion_on_change
        self.default_buffer.on_text_changed += self._handle_input_text_changed
        self._last_input_visible_lines = 1

        self.transcript_control = TranscriptControl(
            self.transcript_state,
            code_fragment_renderer=self._render_transcript_code_fragments,
        )
        self.transcript_window = Window(
            content=self.transcript_control,
            wrap_lines=False,
            always_hide_cursor=True,
            get_vertical_scroll=lambda _window: self.transcript_control.get_vertical_scroll(),
        )
        self.input_divider = FormattedTextControl(self._render_input_divider, focusable=False)
        self.input_control = BufferControl(
            buffer=self.default_buffer,
            lexer=self.lexer,
            search_buffer_control=self.search_toolbar.control,
            preview_search=True,
        )
        self.search_buffer_control = self.search_toolbar.control
        self.footer_line_1_left = FormattedTextControl(
            lambda: session_ui.build_footer_status_left_text(self.default_buffer, _pending_quit_confirmation, enableml, searching=_is_search_active()),
            focusable=False,
        )
        self.footer_line_1_right = FormattedTextControl(
            lambda: session_ui.build_footer_status_right_text(enableml, enable_completion),
            focusable=False,
        )
        self.footer_line_2_left = FormattedTextControl(lambda: session_ui.build_footer_common_shortcuts_text(), focusable=False)
        self.footer_line_2_right = FormattedTextControl(lambda: session_ui.build_footer_session_text(config, cmd_id), focusable=False)

        root = HSplit(
            [
                self.transcript_window,
                Window(content=self.input_divider, height=1),
                VSplit(
                    [
                        Window(content=FormattedTextControl(self._render_prompt_message), width=self._get_prompt_width(), height=self._get_input_height, dont_extend_width=True),
                        Window(content=self.input_control, wrap_lines=True, height=self._get_input_height),
                    ],
                    height=self._get_input_height,
                ),
                Window(content=self.search_buffer_control, height=1),
                VSplit(
                    [
                        Window(content=self.footer_line_1_left, height=1),
                        Window(content=self.footer_line_1_right, height=1, dont_extend_width=True),
                    ]
                ),
                VSplit(
                    [
                        Window(content=self.footer_line_2_left, height=1),
                        Window(content=self.footer_line_2_right, height=1, dont_extend_width=True),
                    ]
                ),
            ]
        )

        layout = Layout(
            FloatContainer(
                content=root,
                floats=[
                    Float(
                        xcursor=True,
                        ycursor=True,
                        content=CompletionsMenu(max_height=8),
                    ),
                ],
            ),
            focused_element=self.input_control,
        )

        self.app = Application(
            layout=layout,
            key_bindings=merge_key_bindings([bindings, default_mouse_bindings, load_emacs_search_bindings()]),
            style=self.style,
            clipboard=_build_clipboard(),
            full_screen=True,
            mouse_support=True,
            after_render=self._handle_after_render,
        )
        self.app._csharpconsole_queue_external_open = self.queue_external_open

    def _render_prompt_message(self):
        return self._prompt_message

    def _get_prompt_width(self):
        return 2

    def _get_available_width(self):
        try:
            return max(1, int(getattr(self.app.output, "get_size")().columns,))
        except Exception:
            return 80

    def _get_input_available_width(self):
        return max(1, self._get_available_width() - self._get_prompt_width())

    def _get_input_document_text(self):
        return getattr(getattr(self.default_buffer, "document", None), "text", "") or ""

    def _get_input_height(self):
        return viewport_policy.compute_input_height(
            self._get_input_document_text(),
            available_width=self._get_input_available_width(),
            max_visible_lines=MAX_INPUT_VISIBLE_LINES,
        )

    def _render_transcript_code_fragments(self, text):
        if self.lexer is None:
            return [("class:transcript.input.text", text or "")]

        document = Document(text or "")
        lex_line = self.lexer.lex_document(document)
        fragments = []
        for index, _line in enumerate(document.lines):
            fragments.extend(lex_line(index))
            if index < len(document.lines) - 1:
                fragments.append(("", "\n"))
        return fragments or [("class:transcript.input.text", text or "")]

    def _render_input_divider(self):
        return session_ui.render_input_divider(self._get_available_width())

    def queue_external_open(self, opener):
        self._pending_external_open = opener
        self.app.invalidate()

    def _handle_after_render(self, app):
        if self._external_open_task_active or self._pending_external_open is None:
            return

        opener = self._pending_external_open
        self._pending_external_open = None
        self._external_open_task_active = True

        async def _run_external_open():
            try:
                await asyncio.sleep(EXTERNAL_OPEN_DELAY_SECONDS)
                opener()
            except Exception:
                pass
            finally:
                self._external_open_task_active = False

        create_background_task = getattr(self.app, "create_background_task", None)
        if callable(create_background_task):
            create_background_task(_run_external_open())
            return

        try:
            opener()
        except Exception:
            pass
        finally:
            self._external_open_task_active = False

    def _handle_input_text_changed(self, _buffer):
        was_at_bottom = viewport_policy.is_transcript_at_bottom(self.transcript_window)
        visible_lines = viewport_policy.compute_input_visible_lines(
            self._get_input_document_text(),
            available_width=self._get_input_available_width(),
            max_visible_lines=MAX_INPUT_VISIBLE_LINES,
        )
        if visible_lines != self._last_input_visible_lines:
            self._last_input_visible_lines = visible_lines
            if was_at_bottom:
                viewport_policy.pin_transcript_to_bottom(self.transcript_window)
            self.app.invalidate()

    def scroll_transcript_window_up(self):
        self.transcript_control.move_cursor_up(count=TRANSCRIPT_WHEEL_SCROLL_LINES)
        self.app.invalidate()

    def scroll_transcript_window_down(self):
        self.transcript_control.move_cursor_down(count=TRANSCRIPT_WHEEL_SCROLL_LINES)
        self.app.invalidate()

    def scroll_transcript_to_bottom(self):
        self.transcript_control.follow_tail()
        viewport_policy.pin_transcript_to_bottom(self.transcript_window)
        self.app.invalidate()

    def append_input_transcript(self, text):
        self.transcript_state.append_input(text)
        self.scroll_transcript_to_bottom()

    def append_info_transcript(self, text):
        self.transcript_state.append_info(text)
        self.scroll_transcript_to_bottom()

    def append_result_transcript_entry(self, entry):
        self.transcript_state.append_result(entry)
        self.scroll_transcript_to_bottom()

    def handle_submitted_message(self, text, event=None):
        if callable(self._on_submit):
            self._on_submit(text)
        self.default_buffer.reset(Document(""))
        self._last_input_visible_lines = 1
        if event is not None:
            event.app.layout.focus(self.input_control)
        self.app.invalidate()

    def run_interactive(self, on_submit):
        _reset_prompt_transient_state()
        self._on_submit = on_submit
        self._prompt_message = _build_prompt_message()
        self.default_buffer.reset(Document(""))
        self._last_input_visible_lines = 1
        self.app.invalidate()
        self.app.run()
        self._on_submit = None

    def prompt(self, message):
        _reset_prompt_transient_state()
        self._prompt_message = message or _build_prompt_message()
        self.default_buffer.reset(Document(""))
        self.app.invalidate()
        return self.app.run()


def ensure_prompt_session():
    global session
    if session is not None:
        return session
    lexer = PygmentsLexer(PygmentsCSharpLexer) if PygmentsCSharpLexer is not None else None
    pygments_style = style_from_pygments_cls(get_style_by_name("dracula")) if get_style_by_name is not None else None
    extra_style = Style.from_dict({
        "banner.label": "bold ansiwhite",
        "banner.key": "ansibrightblack",
        "banner.value": "bold ansicyan",
        "prompt": "bold ansiwhite",
        "prompt.time": "ansibrightblack",
        "prompt.sep": "bold ansicyan",
        "status.hint": "ansiwhite",
        "status.sep": "ansibrightblack",
        "status.mode": "bold ansicyan",
        "pygments.keyword": "bold ansimagenta",
        "pygments.name": "ansiwhite",
        "pygments.name.class": "bold ansigreen",
        "pygments.name.function": "bold ansigreen",
        "pygments.literal.string": "ansiyellow",
        "pygments.literal.number": "ansiblue",
        "pygments.operator": "bold ansicyan",
        **dict(session_ui.build_session_style_rules()),
    })
    style = merge_styles([pygments_style, extra_style]) if pygments_style is not None else extra_style
    session = ReplApplicationShell(style=style, lexer=lexer)
    return session


def _clear_prompt_transcript():
    global session
    if session is None:
        return False
    session.transcript_state.clear()
    session.transcript_control.follow_tail()
    session.transcript_control.selection_state = None
    session.app.invalidate()
    return True


register_default_builtins(
    _builtin_registry,
    {
        "set_enable_completion": _set_enable_completion,
        "roslyn_invalidate": lambda: roslyn_completer.invalidate(),
        "invalidate_command_catalog": invalidate_command_catalog,
        "execute_repl_snippet": lambda message, reset=False: execute_repl_snippet(message, reset=reset),
        "clear_transcript": _clear_prompt_transcript,
    },
)


def _extract_text_from_data(data):
    return (data or {}).get("text", "")


def append_result_transcript(result):
    global session
    if session is None:
        client.print_text_result(result)
        return False
    entry = output.build_result_entry(result, _extract_text_from_data)
    session.append_result_transcript_entry(entry)
    return True


def append_result_transcript_entry(entry):
    global session
    if session is None:
        result_payload = (entry.payload or {}).get("result", {})
        if result_payload:
            client.print_text_result(result_payload)
        elif entry.text:
            text = entry.text
            if not text.endswith("\n"):
                text += "\n"
            sys.stdout.write(text)
        return False
    if hasattr(session, "append_result_transcript_entry"):
        session.append_result_transcript_entry(entry)
    else:
        session.transcript_state.append_result(entry)
        session.app.invalidate()
    return True


def process_builtin_cmd(message):
    payload = _process_builtin_cmd_impl(message, builtin_cmds)
    if payload.get("handled") and payload.get("result") is not None:
        append_result_transcript(payload["result"])
    return payload.get("handled", False)


def try_process_command_expression(message):
    return _try_process_command_expression_impl(
        message,
        cmd_id,
        looks_like_command_expression_prefix,
        parse_command_expression,
        client.request_command,
        append_result_transcript_entry,
        lambda result: output.build_result_entry(result, _extract_text_from_data),
    )


def execute_repl_snippet(message, reset=False):
    return _execute_repl_snippet_impl(
        message,
        reset,
        config.runtime_mode,
        cmd_id,
        client.execute_runtime_request,
        client.execute_editor_request,
        append_result_transcript_entry,
        lambda result: output.build_result_entry(result, _extract_text_from_data),
        roslyn_completer.invalidate,
    )


def _build_startup_snippet(runtime_mode):
    mode = " (runtime)" if runtime_mode else ""
    return f'$"Connected — Unity {{UnityEngine.Application.unityVersion}}, {{UnityEngine.Application.platform}}{mode}. Type / to see available commands."'


def start_repl():
    global cmd_id
    shell = ensure_prompt_session()
    shell.transcript_state.clear()
    cmd_id = str(uuid.uuid4())
    shell.app.invalidate()
    return _start_repl_impl(
        ensure_prompt_session,
        _build_terminal_title,
        config.runtime_mode,
        config.runtime_dll_path,
        config.runtime_defines_path,
        _build_startup_banner,
        print_help_info,
        lambda: execute_repl_snippet(_build_startup_snippet(config.runtime_mode)),
        process_builtin_cmd,
        try_process_command_expression,
        lambda message: execute_repl_snippet(message),
        _build_prompt_message,
        lambda text: ensure_prompt_session().append_input_transcript(text),
        lambda text: ensure_prompt_session().append_info_transcript(text),
    )


def print_help_info():
    return



def run_repl(args):
    return _run_repl_impl(
        args,
        config.configure_globals,
        client.prepare_runtime_artifacts,
        client.fail_and_exit,
        start_repl,
    )
