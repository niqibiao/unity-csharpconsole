from datetime import datetime


ROUND_SEPARATOR_CHAR = "─"
INPUT_DIVIDER_CHAR = "━"


def get_session_target(config_module):
    if config_module.runtime_mode:
        return "runtime", config_module.runtime_ip, config_module.runtime_port
    return "editor", config_module.ip, config_module.port


def build_terminal_title(config_module):
    _, target_ip, target_port = get_session_target(config_module)
    return f"c# REPL/{target_ip}:{target_port}"


def build_startup_banner(config_module, cmd_id):
    work_mode, target_ip, target_port = get_session_target(config_module)
    return [
        ("class:banner.label", "[session] "),
        ("class:banner.key", "workMode="),
        ("class:banner.value", work_mode),
        ("", "  "),
        ("class:banner.key", "target="),
        ("class:banner.value", f"{target_ip}:{target_port}"),
        ("", "  "),
        ("class:banner.key", "cmdId="),
        ("class:banner.value", str(cmd_id)),
    ]


def _format_short_cmd_id(cmd_id):
    value = str(cmd_id or "")
    if len(value) <= 8:
        return value
    return value[:8]


def build_footer_session_text(config_module, cmd_id):
    work_mode, target_ip, target_port = get_session_target(config_module)
    return [
        ("class:footer.session.label", "[session] "),
        ("class:footer.session.key", "workMode="),
        ("class:footer.session.value", work_mode),
        ("", "  "),
        ("class:footer.session.key", "target="),
        ("class:footer.session.value", f"{target_ip}:{target_port}"),
        ("", "  "),
        ("class:footer.session.key", "cmdId="),
        ("class:footer.session.value", _format_short_cmd_id(cmd_id)),
    ]


def build_footer_common_shortcuts_text():
    return [
        ("class:footer.status.left", "[Ctrl+Enter] newline  ·  [Ctrl+R] history"),
    ]


def has_completion_menu(buffer):
    return buffer is not None and getattr(buffer, "complete_state", None) is not None


def get_text_before_cursor(buffer):
    if buffer is None or getattr(buffer, "document", None) is None:
        return ""
    return getattr(buffer.document, "text_before_cursor", "")


def get_toolbar_state(buffer, pending_quit_confirmation, enableml, searching=False):
    return {
        "pending_quit_confirmation": pending_quit_confirmation,
        "has_text": bool(get_text_before_cursor(buffer)),
        "has_completion_menu": has_completion_menu(buffer),
        "multiline": bool(enableml),
        "searching": bool(searching),
    }


def build_multiline_toolbar_parts(state):
    return build_singleline_toolbar_parts(state)


def build_singleline_toolbar_parts(state):
    if state["pending_quit_confirmation"]:
        return ["[Ctrl+C] quit", "[Any key] cancel"]
    if state["searching"]:
        return ["[Enter] accept", "[↑↓] prev/next", "[Ctrl+C] cancel"]

    parts = []
    if state["has_completion_menu"]:
        parts.append("[↑↓] select")
        parts.append("[Tab] accept")
    parts.append("[Ctrl+C] clear" if state["has_text"] else "[/] commands")
    if not state["has_text"]:
        parts.append("[@] actions")
    return parts


def build_toolbar_parts(state):
    return build_singleline_toolbar_parts(state)


def get_toolbar_primary_text(buffer, pending_quit_confirmation, enableml, searching=False):
    return "  ·  ".join(build_toolbar_parts(get_toolbar_state(buffer, pending_quit_confirmation, enableml, searching=searching)))


def build_mode_status_segment(enableml, enable_completion):
    return ""


def get_current_prompt_time():
    return datetime.now().strftime("%H:%M:%S")


def build_footer_status_left_text(buffer, pending_quit_confirmation, enableml, searching=False):
    return [
        ("class:footer.status.left", get_toolbar_primary_text(buffer, pending_quit_confirmation, enableml, searching=searching)),
    ]


def build_footer_status_right_text(enableml, enable_completion):
    completion_dot = "●" if enable_completion else "○"
    return [
        ("class:footer.status.right", f"{completion_dot} completion"),
    ]


def build_bottom_toolbar_text(buffer, pending_quit_confirmation, enableml, searching=False):
    return build_footer_status_left_text(buffer, pending_quit_confirmation, enableml, searching=searching)


def build_rprompt_text(buffer, pending_quit_confirmation, enableml, enable_completion):
    return [
        ("class:prompt.time", get_current_prompt_time()),
    ]


def build_prompt_message():
    return [
        ("class:prompt.sep", "> "),
    ]


def build_prompt_continuation(width, line_number, wrap_count):
    return [
        ("class:prompt.sep", "· "),
    ]


def format_transcript_timestamp(created_at):
    if not created_at:
        return "--:--:--"
    try:
        return datetime.fromisoformat(created_at.replace("Z", "+00:00")).strftime("%H:%M:%S")
    except ValueError:
        return str(created_at)


def build_fill_line(width, fill_char):
    width = max(1, int(width or 1))
    return fill_char * width


def render_transcript_round_separator(width=0):
    return [
        ("class:transcript.separator", build_fill_line(width, ROUND_SEPARATOR_CHAR)),
    ]


def render_input_divider(width=0):
    return [
        ("class:input.divider", build_fill_line(width, INPUT_DIVIDER_CHAR)),
    ]


def _render_timestamp_prefix(created_at):
    return [("class:transcript.timestamp", f"[{format_transcript_timestamp(created_at)}] ")]


def render_transcript_input_block(text, created_at="", code_fragments=None):
    return [
        *_render_timestamp_prefix(created_at),
        ("class:transcript.input.prefix", "> "),
        *(code_fragments if code_fragments is not None else [("class:transcript.input.text", text or "")]),
    ]


def render_transcript_result_block(text, created_at=""):
    return [
        *_render_timestamp_prefix(created_at),
        ("class:transcript.result.prefix", "< "),
        ("class:transcript.result.text", text or ""),
    ]


def render_transcript_info_block(text, created_at=""):
    return [
        *_render_timestamp_prefix(created_at),
        ("class:transcript.info.prefix", "· "),
        ("class:transcript.info.text", text or ""),
    ]


def render_transcript_error_block(error_kind, text, created_at=""):
    kind = error_kind or "transport_error"
    return [
        *_render_timestamp_prefix(created_at),
        (f"class:transcript.error.{kind}.prefix", "! "),
        (f"class:transcript.error.{kind}.text", text or ""),
    ]


def build_session_style_rules():
    return [
        ("footer.session.label", "bold ansiwhite"),
        ("footer.session.key", "ansibrightblack"),
        ("footer.session.value", "bold ansicyan"),
        ("footer.status.left", "ansiwhite"),
        ("footer.status.right", "bold ansicyan"),
        ("transcript.timestamp", "ansibrightblack"),
        ("transcript.separator", "ansibrightblack"),
        ("input.divider", "ansibrightblack"),
        ("transcript.input.prefix", "bold ansicyan"),
        ("transcript.input.text", "ansiwhite"),
        ("transcript.result.prefix", "bold ansigreen"),
        ("transcript.result.text", "ansiwhite"),
        ("transcript.info.prefix", "bold ansibrightblack"),
        ("transcript.info.text", "ansibrightblack"),
        ("transcript.error.compile_error.prefix", "bold ansiwhite bg:ansired"),
        ("transcript.error.compile_error.text", "ansiwhite bg:ansired"),
        ("transcript.error.timeout_error.prefix", "bold ansiblack bg:ansiyellow"),
        ("transcript.error.timeout_error.text", "ansiblack bg:ansiyellow"),
        ("transcript.error.connection_error.prefix", "bold ansiwhite bg:ansimagenta"),
        ("transcript.error.connection_error.text", "ansiwhite bg:ansimagenta"),
        ("transcript.error.transport_error.prefix", "bold ansiwhite bg:ansibrightblack"),
        ("transcript.error.transport_error.text", "ansiwhite bg:ansibrightblack"),
        ("transcript.error.command_error.prefix", "bold ansiwhite bg:ansiblue"),
        ("transcript.error.command_error.text", "ansiwhite bg:ansiblue"),
    ]
