import os
import sys

from csharpconsole_core.runtime_artifacts_base import prepare_runtime_artifacts as prepare_runtime_artifacts_base, zip_directory as zip_directory_base

_RUNTIME_DEFINES_FILE_NAME = "runtime-defines.txt"
_RUNTIME_DEFINES_LOG_PREFIX = "[CSharpConsole][RuntimeDefines]"


def runtime_defines_log(prefix, message):
    print(f"{prefix} {message}", file=sys.stderr)


def normalize_define_line(text):
    if text is None:
        return ""
    normalized = text.replace("\r", "").replace("\n", ";")
    parts = [p.strip() for p in normalized.split(";") if p.strip()]
    return ";".join(parts)


def load_define_line_from_file(path, log_func):
    if not path or not os.path.isfile(path):
        return ""
    try:
        with open(path, "r", encoding="utf-8") as f:
            return normalize_define_line(f.read())
    except Exception as e:
        log_func(f"Failed to read runtime defines file '{path}': {e}")
        return ""


def resolve_runtime_define_line(runtime_dir, explicit_runtime_defines_path, runtime_defines_file_name, log_func):
    define_line = load_define_line_from_file(explicit_runtime_defines_path, log_func)
    if define_line:
        return define_line
    if runtime_dir and os.path.isdir(runtime_dir):
        runtime_defines_file = os.path.join(runtime_dir, runtime_defines_file_name)
        define_line = load_define_line_from_file(runtime_defines_file, log_func)
        if define_line:
            return define_line
    return ""


def zip_directory(dir_path, runtime_defines_file_name, extra_runtime_defines_path=None):
    return zip_directory_base(dir_path, extra_runtime_defines_path, runtime_defines_file_name)


def prepare_runtime_artifacts(runtime_mode, runtime_dll_path, runtime_defines_path, runtime_defines_file_name, upload_zip_to_compile_server, resolve_runtime_define_line, set_runtime_define_line):
    return prepare_runtime_artifacts_base(
        runtime_mode=runtime_mode,
        runtime_dll_path=runtime_dll_path,
        extra_file_path=runtime_defines_path,
        extra_archive_name=runtime_defines_file_name,
        upload_zip_to_compile_server=upload_zip_to_compile_server,
        resolve_extra_value=lambda runtime_dir, extra_path, _zip_bytes: resolve_runtime_define_line(runtime_dir, extra_path),
        set_extra_value=set_runtime_define_line,
        success_extra_key="runtimeDefinesPath",
    )
