import json
import sys

from csharpconsole_core.output import emit_result, fail_and_exit, render_text_result

from .transcript import TranscriptEntry


def classify_error_kind(result):
    if result.get("ok"):
        return ""

    stage = (result.get("stage") or "").lower()
    result_type = (result.get("type") or "").lower()
    summary = (result.get("summary") or "").lower()

    if result_type == "builtin_error" or stage == "builtin":
        return "builtin_error"
    if result_type == "compile_error" or stage == "compile":
        return "compile_error"
    if "timeout" in summary or "timed out" in summary:
        return "timeout_error"
    if stage == "command" or result_type == "command_error":
        return "command_error"
    if any(token in summary for token in ("actively refused", "failed to establish a new connection", "connection refused", "connection reset", "name or service not known", "nodename nor servname", "temporarily unavailable")):
        return "connection_error"
    if "error post:" in summary or result_type in {"system_error", "transport_error"}:
        return "transport_error"
    return "transport_error"


def _render_command_result_text(result, rendered):
    if not result.get("ok") or (result.get("stage") or "").lower() != "command":
        return rendered

    data = result.get("data") if isinstance(result.get("data"), dict) else {}
    result_json = data.get("resultJson")
    if result_json in (None, "", {}, []):
        return rendered

    if isinstance(result_json, str):
        return result_json

    return json.dumps(result_json, ensure_ascii=False, indent=2)


def build_result_entry(result, extract_text_from_data):
    rendered = render_text_result(result, extract_text_from_data)
    rendered = _render_command_result_text(result, rendered)
    ok = bool(result.get("ok"))
    data = result.get("data") if isinstance(result.get("data"), dict) else {}
    silent = bool(data.get("silent"))
    text = "" if silent else (rendered if ok else (result.get("summary") or rendered))
    return TranscriptEntry(
        entry_type="result",
        ok=ok,
        stage=result.get("stage") or "",
        result_type=result.get("type") or "",
        error_kind="" if ok else classify_error_kind(result),
        summary=result.get("summary") or "",
        text=text,
        payload={"result": result, "silent": silent},
    )


def print_text_result(result, extract_text_from_data):
    entry = build_result_entry(result, extract_text_from_data)
    if entry.text:
        text = entry.text
        if not text.endswith("\n"):
            text += "\n"
        sys.stdout.write(text)
