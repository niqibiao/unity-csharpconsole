import os

from . import config, output, runtime_artifacts
from csharpconsole_core import client_base, command_protocol, models, response_parser, transport_http

make_result = models.make_result
new_run_id = models.new_run_id
request_command_via_protocol = command_protocol.request_command

_DEFAULT_USING_PREFIX_CACHE = None
_DEFAULT_DEFINE_CACHE = None
_RUNTIME_DEFINE_LINE_CACHE = None
_RUNTIME_DEFINE_LINE_OVERRIDE = None


def _runtime_defines_log(message):
    runtime_artifacts.runtime_defines_log(runtime_artifacts._RUNTIME_DEFINES_LOG_PREFIX, message)


def _load_define_line_from_file(path):
    return runtime_artifacts.load_define_line_from_file(path, _runtime_defines_log)


def _resolve_runtime_define_line(runtime_dir, explicit_runtime_defines_path):
    return runtime_artifacts.resolve_runtime_define_line(runtime_dir, explicit_runtime_defines_path, runtime_artifacts._RUNTIME_DEFINES_FILE_NAME, _runtime_defines_log)


def _set_runtime_define_line(define_line):
    global _RUNTIME_DEFINE_LINE_OVERRIDE, _RUNTIME_DEFINE_LINE_CACHE, _DEFAULT_DEFINE_CACHE
    _RUNTIME_DEFINE_LINE_OVERRIDE = define_line or ""
    _RUNTIME_DEFINE_LINE_CACHE = _RUNTIME_DEFINE_LINE_OVERRIDE
    _DEFAULT_DEFINE_CACHE = _RUNTIME_DEFINE_LINE_OVERRIDE


def _parse_runtime_define_line():
    global _RUNTIME_DEFINE_LINE_CACHE
    if _RUNTIME_DEFINE_LINE_CACHE is not None:
        return _RUNTIME_DEFINE_LINE_CACHE

    if _RUNTIME_DEFINE_LINE_OVERRIDE is not None:
        _RUNTIME_DEFINE_LINE_CACHE = _RUNTIME_DEFINE_LINE_OVERRIDE
        return _RUNTIME_DEFINE_LINE_CACHE

    _RUNTIME_DEFINE_LINE_CACHE = _load_define_line_from_file(config.runtime_defines_path)
    return _RUNTIME_DEFINE_LINE_CACHE


def get_default_define_line(force_reload=False):
    global _DEFAULT_DEFINE_CACHE
    if _DEFAULT_DEFINE_CACHE is not None and not force_reload:
        return _DEFAULT_DEFINE_CACHE

    if config.runtime_mode:
        runtime_define_line = _parse_runtime_define_line()
        if runtime_define_line:
            _DEFAULT_DEFINE_CACHE = runtime_define_line
            return _DEFAULT_DEFINE_CACHE
    else:
        if os.path.isfile(config._default_define_path):
            try:
                with open(config._default_define_path, "r", encoding="utf-8") as f:
                    for line in f:
                        s = line.strip()
                        if not s or s.startswith("//"):
                            continue
                        _DEFAULT_DEFINE_CACHE = s
                        return _DEFAULT_DEFINE_CACHE
            except Exception:
                pass

    _DEFAULT_DEFINE_CACHE = ""
    return _DEFAULT_DEFINE_CACHE


def get_default_using_prefix(force_reload=False):
    global _DEFAULT_USING_PREFIX_CACHE
    if _DEFAULT_USING_PREFIX_CACHE is not None and not force_reload:
        return _DEFAULT_USING_PREFIX_CACHE
    if os.path.isfile(config._default_using_path):
        try:
            with open(config._default_using_path, "r", encoding="utf-8") as f:
                lines = []
                for l in f:
                    s = l.strip()
                    if not s or s.startswith("//") or s.startswith("#"):
                        continue
                    if s.startswith("using ") and s.endswith(";"):
                        lines.append(s)
            if lines:
                _DEFAULT_USING_PREFIX_CACHE = "\n".join(lines) + "\n"
                return _DEFAULT_USING_PREFIX_CACHE
        except Exception:
            pass
    _DEFAULT_USING_PREFIX_CACHE = ""
    return _DEFAULT_USING_PREFIX_CACHE


def generate_session_id(explicit_session=None):
    return client_base.generate_session_id(explicit_session)


def read_code_from_args(args):
    return client_base.read_code_from_args(args)


def print_text_result(result):
    output.print_text_result(result, response_parser._extract_text_from_data)


def emit_result(result, as_json=True):
    output.emit_result(result, as_json=as_json, print_text=print_text_result)


def fail_and_exit(result, as_json=True):
    output.fail_and_exit(result, as_json=as_json, emit=emit_result)


def post_json(endpoint, payload, timeout_seconds):
    return transport_http.post_json(config.current_server_base_url(), endpoint, payload, timeout_seconds)


def post_json_to_execute(payload, timeout_seconds):
    return transport_http.post_json_to_execute(config.current_execute_base_url(), payload, timeout_seconds)


def execute_editor_request(message, session_id, reset=False, invalidate_completion=None):
    return client_base.execute_editor_request(post_json, response_parser.parse_text_http_response, get_default_define_line, get_default_using_prefix, message, session_id, reset=reset, invalidate_completion=invalidate_completion)


def execute_runtime_request(message, session_id, reset=False, invalidate_completion=None):
    return client_base.execute_runtime_request(post_json, response_parser.parse_text_http_response, get_default_define_line, get_default_using_prefix, config.runtime_ip, config.runtime_port, config.runtime_dll_path, message, session_id, reset=reset, invalidate_completion=invalidate_completion)


def compile_editor_request(message, session_id):
    return client_base.compile_editor_request(post_json, response_parser.parse_compile_only_http_response, get_default_define_line, get_default_using_prefix, message, session_id)


def compile_runtime_request(message, session_id):
    return client_base.compile_runtime_request(post_json, response_parser.parse_compile_only_http_response, get_default_define_line, get_default_using_prefix, config.runtime_ip, config.runtime_port, config.runtime_dll_path, message, session_id)


def execute_compiled_payload(session_id, dll_base64, class_name):
    return client_base.execute_compiled_payload(post_json_to_execute, response_parser.parse_text_http_response, config.current_mode_name, session_id, dll_base64, class_name)


def reset_session_request(session_id, invalidate_completion=None):
    if config.runtime_mode:
        return execute_runtime_request("", session_id, reset=True, invalidate_completion=invalidate_completion)
    return execute_editor_request("", session_id, reset=True, invalidate_completion=invalidate_completion)


def request_completion(code, cursor_position, session_id):
    return client_base.request_completion(post_json, response_parser.parse_completion_http_response, config.current_mode_name, get_default_define_line, get_default_using_prefix, config.runtime_mode, config.runtime_dll_path, code, cursor_position, session_id)


def request_command(command_namespace, action, session_id, args):
    normalized_args = {} if args is None else args
    return request_command_via_protocol(post_json_func=post_json, parse_command_http_response=response_parser.parse_command_http_response, current_mode_name=config.current_mode_name, command_namespace=command_namespace, action=action, session_id=session_id, raw_args=normalized_args, timeout_seconds=10)


def request_health():
    return client_base.request_health(post_json, response_parser.parse_health_http_response, config.current_mode_name)


def request_refresh():
    return client_base.request_refresh(post_json, response_parser.parse_refresh_http_response, config.current_mode_name)


def wait_for_service_recovery(timeout_seconds, poll_interval_seconds=1.0):
    return client_base.wait_for_service_recovery(request_health, config.current_mode_name, timeout_seconds, poll_interval_seconds=poll_interval_seconds)


def parse_upload_dlls_http_response(raw):
    return response_parser.parse_upload_dlls_http_response(raw, config.current_mode_name(), new_run_id(), 0)


def upload_zip_to_compile_server(zip_bytes):
    endpoint = f"http://{config.compile_ip}:{config.compile_port}/CSharpConsole/upload-dlls"
    raw = transport_http.post_binary(endpoint, zip_bytes, 600)
    data = parse_upload_dlls_http_response(raw)
    return data.get("runtimeDllPath", ""), data.get("runtimeDefinesPath", "")


def prepare_runtime_artifacts():
    result = runtime_artifacts.prepare_runtime_artifacts(
        runtime_mode=config.runtime_mode,
        runtime_dll_path=config.runtime_dll_path,
        runtime_defines_path=config.runtime_defines_path,
        runtime_defines_file_name=runtime_artifacts._RUNTIME_DEFINES_FILE_NAME,
        upload_zip_to_compile_server=upload_zip_to_compile_server,
        resolve_runtime_define_line=_resolve_runtime_define_line,
        set_runtime_define_line=_set_runtime_define_line,
    )
    if result.get("ok"):
        data = result.get("data") or {}
        if data.get("runtimeDllPath"):
            config.runtime_dll_path = data["runtimeDllPath"]
        if data.get("runtimeDefinesPath"):
            config.runtime_defines_path = data["runtimeDefinesPath"]
    return result


def reset_cached_config():
    global _DEFAULT_USING_PREFIX_CACHE, _DEFAULT_DEFINE_CACHE, _RUNTIME_DEFINE_LINE_CACHE, _RUNTIME_DEFINE_LINE_OVERRIDE
    config.reset_cached_config()
    _DEFAULT_USING_PREFIX_CACHE = None
    _DEFAULT_DEFINE_CACHE = None
    _RUNTIME_DEFINE_LINE_CACHE = None
    _RUNTIME_DEFINE_LINE_OVERRIDE = None
