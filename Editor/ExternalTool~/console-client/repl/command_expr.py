import json
import re


_IDENTIFIER_PATTERN = r"[A-Za-z_][A-Za-z0-9_]*"
_SEGMENT_PATTERN = r"[A-Za-z_][A-Za-z0-9_\-]*"
_COMMAND_PATH_PATTERN = re.compile(
    rf"{_SEGMENT_PATTERN}\.{_SEGMENT_PATTERN}(?:\.{_SEGMENT_PATTERN})*"
)
_COMMAND_PARTIAL_PATH_PATTERN = re.compile(
    rf"{_SEGMENT_PATTERN}(?:\.{_SEGMENT_PATTERN}(?:\.{_SEGMENT_PATTERN})*)?"
)


def is_valid_command_path(command_path):
    return bool(_COMMAND_PATH_PATTERN.fullmatch((command_path or "").strip()))


def is_valid_partial_command_path(command_path):
    return bool(_COMMAND_PARTIAL_PATH_PATTERN.fullmatch((command_path or "").strip()))


def looks_like_command_expression_prefix(text):
    stripped = (text or "").lstrip()
    if not stripped.startswith("@"):
        return False

    open_paren_index = stripped.find("(")
    if open_paren_index >= 0:
        command_path = stripped[1:open_paren_index].strip()
        return is_valid_command_path(command_path)

    command_path = stripped[1:].rstrip(";").strip()
    return is_valid_command_path(command_path)


def parse_command_value(raw):
    value = raw.strip()
    if not value:
        raise ValueError("missing argument value")
    return json.loads(value)


def split_command_arguments(args_text):
    parts = []
    current = []
    depth = 0
    in_string = False
    escape = False
    for ch in args_text:
        if in_string:
            current.append(ch)
            if escape:
                escape = False
            elif ch == "\\":
                escape = True
            elif ch == '"':
                in_string = False
            continue

        if ch == '"':
            in_string = True
            current.append(ch)
            continue
        if ch in "[{":
            depth += 1
            current.append(ch)
            continue
        if ch in "]}":
            depth -= 1
            if depth < 0:
                raise ValueError("invalid JSON value in command arguments")
            current.append(ch)
            continue
        if ch == "," and depth == 0:
            parts.append("".join(current).strip())
            current = []
            continue
        current.append(ch)

    if in_string or depth != 0:
        raise ValueError("invalid JSON value in command arguments")

    trailing = "".join(current).strip()
    if trailing:
        parts.append(trailing)
    return parts


def parse_command_expression(message):
    text = (message or "").strip().rstrip(";")
    if not looks_like_command_expression_prefix(text):
        return None

    open_paren_index = text.find("(")
    if open_paren_index >= 0:
        if not text.endswith(")"):
            raise ValueError("missing closing ')' in command expression")
        command_path = text[1:open_paren_index].strip()
        args_text = text[open_paren_index + 1:-1].strip()
    else:
        command_path = text[1:].strip()
        args_text = ""

    if not command_path or "." not in command_path:
        raise ValueError("command path must be <namespace>.<action>")

    command_namespace, action = command_path.split(".", 1)
    if not command_namespace or not action:
        raise ValueError("command path must be <namespace>.<action>")

    args = {}
    if args_text:
        for part in split_command_arguments(args_text):
            if not part:
                continue
            name, separator, value = part.partition(":")
            if separator != ":":
                raise ValueError(f"invalid named argument '{part}'")
            arg_name = name.strip()
            if not re.fullmatch(_IDENTIFIER_PATTERN, arg_name):
                raise ValueError(f"invalid argument name '{arg_name}'")
            args[arg_name] = parse_command_value(value)

    return command_namespace, action, args
