import io
import os
import subprocess
import sys
from contextlib import redirect_stdout

from csharpconsole_core.models import make_result
from prompt_toolkit.application.current import get_app_or_none
from prompt_toolkit.application.run_in_terminal import run_in_terminal

from . import client, config


class BuiltinRegistry:
    def __init__(self):
        self.commands = {}
        self.order = ["/help", "/completion", "/using", "/define", "/reload", "/reset", "/clear", "/dofile"]

    def decorator(self, cmd, description, completion=None):
        def register(func):
            self.commands[cmd] = {"func": func, "desc": description, "completion": completion or cmd}
            return func
        return register


def _build_builtin_result_payload(ok, summary, output_text="", silent=False):
    return make_result(
        ok=ok,
        stage="builtin",
        result_type="" if ok else "builtin_error",
        exit_code=0 if ok else 1,
        summary=summary,
        session_id="",
        mode="repl",
        data={"text": output_text, "silent": bool(silent)},
    )


def process_builtin_cmd(message, builtin_cmds):
    if message is None or not message or message[0] != "/":
        return {"handled": False, "result": None}
    if message.startswith("//") or message.startswith("/*"):
        return {"handled": False, "result": None}

    command_token, _, remainder = message.rstrip().partition(" ")
    command_info = builtin_cmds.get(command_token.lower())
    if command_info is None:
        return {
            "handled": True,
            "result": _build_builtin_result_payload(False, f"Unknown command: {command_token.lower()}", ""),
        }

    output_buffer = io.StringIO()
    with redirect_stdout(output_buffer):
        try:
            result = command_info["func"](remainder.strip())
        except Exception as exc:
            command_output = output_buffer.getvalue()
            if command_output and not command_output.endswith("\n"):
                command_output += "\n"
            return {
                "handled": True,
                "result": _build_builtin_result_payload(False, str(exc), command_output),
            }

    command_output = output_buffer.getvalue()
    if command_output:
        summary = command_output.strip() or "success"
        return {
            "handled": True,
            "result": _build_builtin_result_payload(True, summary, command_output),
        }
    if result != "silent-success":
        return {
            "handled": True,
            "result": _build_builtin_result_payload(True, "success", "success\n"),
        }
    return {
        "handled": True,
        "result": _build_builtin_result_payload(True, "success", "", silent=True),
    }


def open_local_file(path):
    abs_path = os.path.abspath(path)

    def _open_file():
        try:
            if os.name == "nt":
                os.startfile(abs_path)
            elif sys.platform == "darwin":
                subprocess.run(["open", abs_path], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            else:
                subprocess.run(["xdg-open", abs_path], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            return True
        except Exception:
            print((f"Open the file manually: {abs_path}\n"))
            return False

    app = get_app_or_none()
    if app is not None and getattr(app, "_is_running", False):
        queue_external_open = getattr(app, "_csharpconsole_queue_external_open", None)
        if callable(queue_external_open):
            queue_external_open(_open_file)
            return

        run_in_terminal(_open_file, render_cli_done=True, in_executor=False)
        return

    _open_file()


def open_config_file(path, default_contents=""):
    if not os.path.isfile(path):
        with open(path, "w", encoding="utf-8") as f:
            f.write(default_contents)

    open_local_file(path)
    print(f"Target file: {os.path.abspath(path)}\n")


def ensure_config_file(path, default_contents=""):
    if not os.path.isfile(path):
        with open(path, "w", encoding="utf-8") as f:
            f.write(default_contents)


def print_config_file_edit_help(path, format_lines, notes=None):
    abs_path = os.path.abspath(path)
    print(f"Open this file and edit it manually:\n{abs_path}\n")
    print("Format:")
    for line in format_lines:
        print(line)
    print("")
    if notes:
        for note in notes:
            print(note)
        print("")
    print("After saving, run /reload to reload the default using and define files.\n")


def register_default_builtins(registry, state):
    @registry.decorator("/help", "Show available commands")
    def show_help(message):
        lines = []
        for cmd in registry.order:
            info = registry.commands.get(cmd)
            if info is None:
                continue
            desc = info.get("desc", "")
            lines.append(f"  {cmd:<16} {desc}")
        lines.append("")
        lines.append("Type @ to browse and execute registered actions (e.g. @editor.status()).")
        print("\n".join(lines) + "\n")

    @registry.decorator("/completion", "Set semantic completion: 0 disable, 1 enable", completion="/completion <0|1>")
    def set_completion(message):
        value = message.strip()
        if value == "0":
            state["set_enable_completion"](False)
            return
        if value == "1":
            state["set_enable_completion"](True)
            state["roslyn_invalidate"]()
            return
        print("Usage: /completion 0|1\n")

    @registry.decorator("/using", "Show how to edit the default using file")
    def edit_default_using(message):
        ensure_config_file(
            config._default_using_path,
            "// One using per line, for example:\n// using System;\n// using UnityEngine;\n",
        )
        print_config_file_edit_help(
            config._default_using_path,
            [
                "using System;",
                "using UnityEngine;",
            ],
            notes=[
                "Only lines in the form 'using Namespace;' are loaded.",
                "Blank lines and lines starting with // or # are ignored.",
            ],
        )

    @registry.decorator("/define", "Show how to edit default defines as SYM1;SYM2;...")
    def edit_default_define(message):
        ensure_config_file(
            config._default_define_path,
            "// Format: SYM1;SYM2;... Clear the file to use editor defaults\n",
        )
        print_config_file_edit_help(
            config._default_define_path,
            [
                "SYM1;SYM2;SYM3",
            ],
            notes=[
                "Only the first non-empty line that does not start with // is used.",
                "Clear the file to use editor default defines.",
            ],
        )

    @registry.decorator("/reload", "Reload default using and define files")
    def reload_default_using_and_define(message):
        state["invalidate_command_catalog"]()
        client.reset_cached_config()
        client.get_default_using_prefix(force_reload=True)
        print((f"reloadUsing: \n{client._DEFAULT_USING_PREFIX_CACHE}\n"))
        client.get_default_define_line(force_reload=True)
        define_display = client._DEFAULT_DEFINE_CACHE if client._DEFAULT_DEFINE_CACHE else "Use editor default defines"
        define_source = ""
        if client._DEFAULT_DEFINE_CACHE:
            define_source = " (from runtime-defines.txt)" if config.runtime_mode else " (from Defines.txt)"
        print((f"reloadDefine{define_source}: \n{define_display}\n"))

    @registry.decorator("/reset", "Reset the console environment")
    def reset_console_environment(message):
        state["execute_repl_snippet"]("", reset=True)
        state["invalidate_command_catalog"]()
        reload_default_using_and_define("")

    @registry.decorator("/clear", "Clear the terminal")
    def clear_terminal(message):
        clear_transcript = state.get("clear_transcript")
        if callable(clear_transcript) and clear_transcript():
            return "silent-success"
        os.system("cls" if os.name == "nt" else "clear")
        return "silent-success"

    @registry.decorator("/dofile", "Execute code from a file", completion="/dofile <path>")
    def do_file(message):
        full_path = message.replace('\\', '/')
        if os.path.isfile(full_path):
            with open(full_path, "r", encoding="utf-8") as f:
                code = f.read()
            state["execute_repl_snippet"](code)
        else:
            print((f"File not found: {full_path}\n"))
