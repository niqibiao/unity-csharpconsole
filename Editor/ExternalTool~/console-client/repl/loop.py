from prompt_toolkit.patch_stdout import patch_stdout
from prompt_toolkit.shortcuts import set_title

from csharpconsole_core.models import make_result

from . import client, config


def execute_repl_snippet(message, reset, runtime_mode, cmd_id, execute_runtime_request, execute_editor_request, append_result_entry, build_result_entry, invalidate_completion):
    if runtime_mode:
        result = execute_runtime_request(message, str(cmd_id), reset=reset, invalidate_completion=invalidate_completion)
    else:
        result = execute_editor_request(message, str(cmd_id), reset=reset, invalidate_completion=invalidate_completion)
    append_result_entry(build_result_entry(result))


def try_process_command_expression(message, cmd_id, looks_like_command_expression_prefix, parse_command_expression, request_command, append_result_entry, build_result_entry):
    stripped = (message or "").strip()
    if not looks_like_command_expression_prefix(stripped):
        return False
    try:
        parsed = parse_command_expression(stripped)
    except ValueError as e:
        syntax_error_result = make_result(
            ok=False,
            stage="command",
            result_type="command_error",
            exit_code=1,
            summary=f"[command] syntax error: {e}",
            session_id=str(cmd_id),
            mode="repl",
            data={"text": f"[command] syntax error: {e}\n"},
        )
        append_result_entry(build_result_entry(syntax_error_result))
        return True

    command_namespace, action, args = parsed
    result = request_command(command_namespace, action, str(cmd_id), args)
    append_result_entry(build_result_entry(result))
    return True


def start_repl(ensure_prompt_session, build_terminal_title, runtime_mode, runtime_dll_path, runtime_defines_path, build_startup_banner, print_help_info, execute_startup_snippet, process_builtin_cmd, try_process_command_expression_func, execute_repl_snippet_func, build_prompt_message, append_input_entry, append_info_entry=None):
    session = ensure_prompt_session()
    set_title(build_terminal_title())

    if runtime_mode and append_info_entry is not None:
        if runtime_dll_path:
            append_info_entry(f"runtimeDllPath: {runtime_dll_path}")
        if runtime_defines_path:
            append_info_entry(f"runtimeDefines: {runtime_defines_path}")

    print_help_info()
    execute_startup_snippet()

    def _handle_message(message):
        if message is None:
            return
        append_input_entry(message)
        if process_builtin_cmd(message):
            return
        if try_process_command_expression_func(message):
            return
        execute_repl_snippet_func(message)

    if hasattr(session, "run_interactive"):
        with patch_stdout():
            session.run_interactive(_handle_message)
        return

    while True:
        with patch_stdout():
            message = session.prompt(build_prompt_message())

        if message is None:
            break
        _handle_message(message)


def run_repl(args, configure_globals, prepare_runtime_artifacts, fail_and_exit, start_repl_func):
    configure_globals(args)
    bootstrap_result = prepare_runtime_artifacts()
    if not bootstrap_result["ok"]:
        fail_and_exit(bootstrap_result, as_json=not getattr(args, "text", False))
    start_repl_func()
