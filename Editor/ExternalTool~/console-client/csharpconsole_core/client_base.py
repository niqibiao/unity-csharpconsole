import time
import uuid

import requests

from .models import make_result, new_run_id


def generate_session_id(explicit_session=None):
    if explicit_session:
        return explicit_session
    return str(uuid.uuid4())


def read_code_from_args(args):
    if getattr(args, "code", None) is not None:
        return args.code
    if getattr(args, "code_file", None):
        with open(args.code_file, "r", encoding="utf-8") as f:
            return f.read()
    raise ValueError("Missing code input")


def execute_editor_request(post_json, parse_text_http_response, get_default_define_line, get_default_using_prefix, message, session_id, reset=False, invalidate_completion=None):
    start = time.time()
    run_id = new_run_id()
    payload = {
        "uuid": session_id,
        "reset": reset,
        "defines": get_default_define_line(),
        "defaultUsing": get_default_using_prefix(),
        "content": message,
    }
    try:
        raw = post_json("editor", payload, 10)
        if invalidate_completion:
            invalidate_completion()
        return parse_text_http_response(raw, "execute", session_id, "editor", run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "execute", "system_error", 3, f"Error post: {e}", session_id, "editor", run_id, (time.time() - start) * 1000)


def execute_runtime_request(post_json, parse_text_http_response, get_default_define_line, get_default_using_prefix, runtime_ip, runtime_port, runtime_dll_path, message, session_id, reset=False, invalidate_completion=None):
    start = time.time()
    run_id = new_run_id()
    payload = {
        "uuid": session_id,
        "reset": reset,
        "defines": get_default_define_line(),
        "defaultUsing": get_default_using_prefix(),
        "content": message,
        "targetIP": runtime_ip,
        "targetPort": runtime_port,
        "runtimeDllPath": runtime_dll_path,
    }
    try:
        raw = post_json("compile", payload, 30)
        if invalidate_completion:
            invalidate_completion()
        return parse_text_http_response(raw, "execute", session_id, "runtime", run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "unknown", "system_error", 3, f"Error post: {e}", session_id, "runtime", run_id, (time.time() - start) * 1000)


def compile_editor_request(post_json, parse_compile_only_http_response, get_default_define_line, get_default_using_prefix, message, session_id):
    start = time.time()
    run_id = new_run_id()
    payload = {
        "uuid": session_id,
        "defines": get_default_define_line(),
        "defaultUsing": get_default_using_prefix(),
        "content": message,
    }
    try:
        raw = post_json("editor-compile", payload, 10)
        return parse_compile_only_http_response(raw, session_id, "editor", run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "compile", "system_error", 3, f"Error post: {e}", session_id, "editor", run_id, (time.time() - start) * 1000)
    except Exception as e:
        return make_result(False, "compile", "system_error", 3, str(e), session_id, "editor", run_id, (time.time() - start) * 1000)


def compile_runtime_request(post_json, parse_compile_only_http_response, get_default_define_line, get_default_using_prefix, runtime_ip, runtime_port, runtime_dll_path, message, session_id):
    start = time.time()
    run_id = new_run_id()
    payload = {
        "uuid": session_id,
        "defines": get_default_define_line(),
        "defaultUsing": get_default_using_prefix(),
        "content": message,
        "targetIP": runtime_ip,
        "targetPort": runtime_port,
        "runtimeDllPath": runtime_dll_path,
    }
    try:
        raw = post_json("runtime-compile", payload, 30)
        return parse_compile_only_http_response(raw, session_id, "runtime", run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "compile", "system_error", 3, f"Error post: {e}", session_id, "runtime", run_id, (time.time() - start) * 1000)
    except Exception as e:
        return make_result(False, "compile", "system_error", 3, str(e), session_id, "runtime", run_id, (time.time() - start) * 1000)


def execute_compiled_payload(post_json_to_execute, parse_text_http_response, current_mode_name, session_id, dll_base64, class_name):
    start = time.time()
    run_id = new_run_id()
    payload = {
        "uuid": session_id,
        "reset": False,
        "dllBase64": dll_base64,
        "className": class_name,
    }
    try:
        raw = post_json_to_execute(payload, 30)
        return parse_text_http_response(raw, "execute", session_id, current_mode_name(), run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "execute", "system_error", 3, f"Error post: {e}", session_id, current_mode_name(), run_id, (time.time() - start) * 1000)


def request_completion(post_json, parse_completion_http_response, current_mode_name, get_default_define_line, get_default_using_prefix, runtime_mode, runtime_dll_path, code, cursor_position, session_id):
    start = time.time()
    run_id = new_run_id()
    payload = {
        "uuid": session_id,
        "code": code,
        "cursorPosition": cursor_position,
        "defines": get_default_define_line(),
        "defaultUsing": get_default_using_prefix(),
        "runtimeDllPath": runtime_dll_path if runtime_mode else "",
    }
    try:
        raw = post_json("completion", payload, 2)
        return parse_completion_http_response(raw, session_id, current_mode_name(), run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "compile", "system_error", 3, f"Completion request failed: {e}", session_id, current_mode_name(), run_id, (time.time() - start) * 1000)
    except Exception as e:
        return make_result(False, "compile", "system_error", 3, str(e), session_id, current_mode_name(), run_id, (time.time() - start) * 1000)


def request_health(post_json, parse_health_http_response, current_mode_name):
    start = time.time()
    run_id = new_run_id()
    try:
        raw = post_json("health", {}, 2)
        return parse_health_http_response(raw, current_mode_name(), run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "bootstrap", "system_error", 3, f"Health check failed: {e}", "", current_mode_name(), run_id, (time.time() - start) * 1000)
    except Exception as e:
        return make_result(False, "bootstrap", "system_error", 3, str(e), "", current_mode_name(), run_id, (time.time() - start) * 1000)


def request_refresh(post_json, parse_refresh_http_response, current_mode_name):
    start = time.time()
    run_id = new_run_id()
    try:
        raw = post_json("refresh", {}, 2)
        return parse_refresh_http_response(raw, current_mode_name(), run_id, (time.time() - start) * 1000)
    except requests.RequestException as e:
        return make_result(False, "bootstrap", "system_error", 3, f"Refresh request failed: {e}", "", current_mode_name(), run_id, (time.time() - start) * 1000)
    except Exception as e:
        return make_result(False, "bootstrap", "system_error", 3, str(e), "", current_mode_name(), run_id, (time.time() - start) * 1000)


def wait_for_service_recovery(request_health, current_mode_name, timeout_seconds, poll_interval_seconds=1.0):
    start = time.time()
    last_summary = "Waiting for Unity service to recover"
    while time.time() - start < timeout_seconds:
        health = request_health()
        if health["ok"]:
            data = health.get("data", {})
            operation = data.get("operation") or {}
            phase = operation.get("phase") or ""
            editor_state = data.get("editorState") or ""
            if phase == "failed":
                return make_result(False, "bootstrap", "system_error", 3, operation.get("message") or "Unity refresh failed", "", current_mode_name(), duration_ms=(time.time() - start) * 1000, data=data)
            if data.get("initialized") and editor_state == "ready" and phase in ("", "ready"):
                health["summary"] = "Unity service recovered after refresh"
                health["durationMs"] = int((time.time() - start) * 1000)
                return health
            last_summary = operation.get("message") or f"Unity service state: {editor_state or 'unknown'}"
        else:
            last_summary = health.get("summary") or last_summary
        time.sleep(poll_interval_seconds)

    return make_result(False, "bootstrap", "system_error", 3, f"Timed out waiting for Unity service recovery: {last_summary}", "", current_mode_name(), duration_ms=(time.time() - start) * 1000)
