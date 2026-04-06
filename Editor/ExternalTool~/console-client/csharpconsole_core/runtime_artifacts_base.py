import io
import os
import time
import zipfile

from .models import make_result, new_run_id


def zip_directory(dir_path, extra_file_path=None, extra_archive_name=None):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, _dirs, files in os.walk(dir_path):
            for fname in files:
                full_path = os.path.join(root, fname)
                arc_name = os.path.relpath(full_path, dir_path)
                zf.write(full_path, arc_name)
        if extra_file_path and extra_archive_name and os.path.isfile(extra_file_path):
            norm_dir = os.path.normcase(os.path.abspath(dir_path))
            norm_extra = os.path.normcase(os.path.abspath(extra_file_path))
            if not norm_extra.startswith(norm_dir + os.sep):
                zf.write(extra_file_path, extra_archive_name)
    return buf.getvalue()


def prepare_runtime_artifacts(runtime_mode, runtime_dll_path, extra_file_path, extra_archive_name, upload_zip_to_compile_server, resolve_extra_value, set_extra_value, success_extra_key):
    if not runtime_mode:
        return make_result(True, "bootstrap", "", 0, "Editor mode does not require runtime artifacts", "", "editor")

    start = time.time()
    run_id = new_run_id()
    try:
        if runtime_dll_path:
            zip_bytes = zip_directory(runtime_dll_path, extra_file_path, extra_archive_name)
            extra_value = resolve_extra_value(runtime_dll_path, extra_file_path, zip_bytes)
            server_dll_path, server_extra_path = upload_zip_to_compile_server(zip_bytes)
            set_extra_value(extra_value)
            return make_result(True, "bootstrap", "", 0, "Runtime DLL directory uploaded", "", "runtime", run_id, (time.time() - start) * 1000, {
                "runtimeDllPath": server_dll_path,
                success_extra_key: server_extra_path or extra_file_path,
            })
        return make_result(True, "bootstrap", "", 0, "No runtime artifact upload needed", "", "runtime", run_id, (time.time() - start) * 1000)
    except Exception as e:
        return make_result(False, "bootstrap", "system_error", 3, str(e), "", "runtime", run_id, (time.time() - start) * 1000)
