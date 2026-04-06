import re
import threading

from prompt_toolkit.completion import Completion, Completer, FuzzyCompleter, merge_completers

from . import client
from repl.command_expr import (
    is_valid_command_path,
    is_valid_partial_command_path,
    looks_like_command_expression_prefix,
)


class BuiltinCmdCompleter(Completer):
    def __init__(self, builtin_cmds, builtin_command_order):
        self._builtin_cmds = builtin_cmds
        self._builtin_command_order = builtin_command_order

    def get_completions(self, document, complete_event):
        text_before_cursor = document.text_before_cursor
        if not text_before_cursor or not text_before_cursor.startswith("/"):
            return

        slash_prefix = text_before_cursor
        start_position = -len(slash_prefix)
        for cmd in self._builtin_command_order:
            info = self._builtin_cmds.get(cmd)
            if info is None:
                continue
            completion_text = info.get("completion", cmd)
            if completion_text.lower().startswith(slash_prefix.lower()):
                yield Completion(
                    text=completion_text,
                    start_position=start_position,
                    display=completion_text,
                    display_meta=info.get("desc", ""),
                )


def _looks_like_command_expression_completion_input(text):
    stripped = (text or "").lstrip()
    if not stripped.startswith("@"):
        return False
    if stripped == "@":
        return True

    tail = stripped[1:]
    if not re.match(r"^[A-Za-z_]", tail):
        return False

    if "(" in tail:
        command_path = tail.partition("(")[0].strip()
        return is_valid_command_path(command_path)

    # Allow trailing dot (e.g. "@editor.") so the trigger fires for action completion.
    clean = tail.rstrip(".")
    return is_valid_partial_command_path(clean)


class CommandExpressionCompleter(Completer):
    def __init__(self, get_command_catalog):
        self._get_command_catalog = get_command_catalog
        self._catalog_cache_key = None
        self._namespace_candidates = []
        self._action_candidates = []
        self._command_lookup = {}

    @staticmethod
    def _iter_action_candidates(command):
        action = (command or {}).get("action", "")
        if isinstance(action, str) and action:
            yield action

    def _refresh_catalog_cache(self, catalog):
        cache_key = (id(catalog), len(catalog))
        if cache_key == self._catalog_cache_key:
            return

        self._catalog_cache_key = cache_key
        self._namespace_candidates = sorted({item.get("commandNamespace", "") for item in catalog if item.get("commandNamespace")})
        self._action_candidates = []
        self._command_lookup = {}

        for command in catalog:
            namespace = command.get("commandNamespace", "")
            summary = command.get("summary", "")
            if not namespace:
                continue
            meta = self._build_action_meta(command, summary)
            for action_or_alias in self._iter_action_candidates(command):
                full_name = f"{namespace}.{action_or_alias}"
                self._action_candidates.append((full_name, meta))
                self._command_lookup[full_name] = command

        self._action_candidates.sort(key=lambda item: item[0])

    @staticmethod
    def _build_action_meta(command, summary):
        args = command.get("arguments") or []
        if args:
            params = ", ".join(
                f"{a.get('name', '')}: {a.get('typeName', '').rsplit('.', 1)[-1]}"
                for a in args if a.get("name")
            )
            sig = f"({params})"
        else:
            sig = ""
        parts = [p for p in (sig, summary) if p]
        return "  ".join(parts)

    def get_completions(self, document, complete_event):
        text = document.text_before_cursor
        if not _looks_like_command_expression_completion_input(text):
            return

        text = text.lstrip()
        catalog = self._get_command_catalog()
        self._refresh_catalog_cache(catalog)
        if "(" not in text:
            typed = text[1:]

            for candidate in self._namespace_candidates:
                if candidate.startswith(typed):
                    yield Completion(candidate, start_position=-len(typed), display=candidate)

            path_prefix = typed.rpartition(".")[0]
            action_prefix = typed[len(path_prefix) + 1:] if path_prefix else typed
            for candidate, summary in self._action_candidates:
                if candidate in self._namespace_candidates:
                    continue
                if path_prefix:
                    if not candidate.startswith(path_prefix + "."):
                        continue
                    completion_text = candidate[len(path_prefix) + 1:]
                    if completion_text.startswith(action_prefix):
                        yield Completion(
                            completion_text,
                            start_position=-len(action_prefix),
                            display=completion_text,
                            display_meta=summary,
                        )
                elif candidate.startswith(typed):
                    yield Completion(candidate, start_position=-len(typed), display=candidate, display_meta=summary)
            return

        call_head, _, arg_tail = text[1:].partition("(")
        command = self._command_lookup.get(call_head)
        if command is None:
            return

        used_names = set(re.findall(r"([A-Za-z_][A-Za-z0-9_]*)\s*:", arg_tail))
        trailing_match = re.search(r"(?:^|,\s*)([A-Za-z_][A-Za-z0-9_]*)?$", arg_tail)
        typed_prefix = trailing_match.group(1) if trailing_match and trailing_match.group(1) else ""
        for argument in command.get("arguments") or []:
            name = argument.get("name", "")
            if not name or name in used_names:
                continue
            completion_text = f"{name}: "
            if name.startswith(typed_prefix):
                yield Completion(completion_text, start_position=-len(typed_prefix), display=completion_text, display_meta=argument.get("typeName", ""))


class RoslynCompleter(Completer):
    def __init__(self, is_completion_enabled, get_cmd_id):
        self._is_completion_enabled = is_completion_enabled
        self._get_cmd_id = get_cmd_id
        self._cache = {}
        self._fetch_lock = threading.Lock()

    def get_completions(self, document, complete_event):
        if not self._is_completion_enabled():
            return
        text = document.text_before_cursor
        stripped = (text or "").lstrip()
        if not text or stripped.startswith("/") or looks_like_command_expression_prefix(text):
            return
        dot_pos = text.rfind('.')
        if dot_pos < 0:
            return
        cache_key = text[:dot_pos + 1]
        filter_prefix = text[dot_pos + 1:]
        if cache_key in self._cache:
            items = self._cache[cache_key]
        else:
            with self._fetch_lock:
                if cache_key in self._cache:
                    items = self._cache[cache_key]
                else:
                    items = self._fetch(cache_key)
                    if items is None:
                        return
                    self._cache[cache_key] = items

        for item in items:
            label = item["label"]
            if not filter_prefix or label.lower().startswith(filter_prefix.lower()):
                acc = item.get("accessibility", "")
                detail = item.get("detail", "")
                if acc and acc != "Public":
                    detail = f"[{acc}] {detail}"
                yield Completion(text=label, start_position=-len(filter_prefix), display=label, display_meta=detail)

    def _fetch(self, code_up_to_dot):
        result = client.request_completion(code_up_to_dot, len(code_up_to_dot), str(self._get_cmd_id()))
        if result["ok"]:
            return result["data"].get("items", [])
        if result["summary"]:
            print((f"[completion] {result['summary']}\n"))
        return None

    def invalidate(self):
        self._cache.clear()


def invalidate_command_catalog(state):
    state["command_catalog_cache"] = None


def get_command_catalog(state, get_cmd_id):
    if state["command_catalog_cache"] is not None:
        return state["command_catalog_cache"]

    result = client.request_command("command", "list", str(get_cmd_id()), {})
    if not result["ok"]:
        summary = result.get("summary") or "Failed to load command catalog"
        print(f"[command] {summary}\n")
        return []

    data = result.get("data") or {}
    commands = data.get("commands")
    if commands is None:
        commands = data.get("resultJson", {}).get("commands") if isinstance(data.get("resultJson"), dict) else None
    state["command_catalog_cache"] = commands or []
    return state["command_catalog_cache"]


def accept_completion(event):
    buffer = event.current_buffer
    completion_state = buffer.complete_state
    if completion_state is None:
        return
    completion = completion_state.current_completion
    if completion is None and completion_state.completions:
        completion_state.go_to_index(0)
        completion = completion_state.current_completion
    if completion is not None:
        buffer.apply_completion(completion)


def trigger_completion_on_change(buff, is_completion_enabled):
    text_before_cursor = buff.document.text_before_cursor
    if text_before_cursor.startswith("/"):
        if text_before_cursor.startswith("//") or text_before_cursor.startswith("/*"):
            return
        if " " not in text_before_cursor:
            buff.start_completion(select_first=False)
        return
    if _looks_like_command_expression_completion_input(text_before_cursor):
        buff.start_completion(select_first=False)
        return
    if not is_completion_enabled():
        return
    if '.' in text_before_cursor:
        buff.start_completion(select_first=False)


def build_completers(builtin_cmds, builtin_command_order, state, get_cmd_id, is_completion_enabled):
    builtin_cmd_completer = BuiltinCmdCompleter(builtin_cmds, builtin_command_order)
    command_expr_completer = CommandExpressionCompleter(lambda: get_command_catalog(state, get_cmd_id))
    roslyn_completer = RoslynCompleter(is_completion_enabled, get_cmd_id)
    combined_completer = merge_completers([builtin_cmd_completer, command_expr_completer, roslyn_completer])
    fuzzy_completer = FuzzyCompleter(completer=combined_completer)
    return builtin_cmd_completer, command_expr_completer, roslyn_completer, combined_completer, fuzzy_completer
