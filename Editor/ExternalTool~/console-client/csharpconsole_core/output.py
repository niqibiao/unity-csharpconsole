import json


def green(s):
    return f"\033[1;32m{s}\033[0m"


def yellow(s):
    return f"\033[1;33m{s}\033[0m"


def red(s):
    return f"\033[1;31m{s}\033[0m"


def render_text_result(result, extract_text_from_data):
    text = extract_text_from_data(result.get("data", {}))
    if text is None:
        text = result.get("summary", "")
    return text.replace("\\n", "\n").replace("\\t", "\t")


def print_text_result(result, extract_text_from_data, success_formatter=None, error_formatter=None):
    rendered = render_text_result(result, extract_text_from_data)
    if result["ok"]:
        print(success_formatter(rendered) if success_formatter else rendered)
    else:
        error_text = result.get("summary", rendered)
        print(error_formatter(error_text) if error_formatter else error_text)


def emit_result(result, as_json=True, print_text=None):
    if as_json:
        print(json.dumps(result, ensure_ascii=False))
    else:
        print_text(result)


def fail_and_exit(result, as_json=True, emit=None):
    emit(result, as_json=as_json)
    raise SystemExit(result["exitCode"])
