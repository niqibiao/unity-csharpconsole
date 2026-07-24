import json
import os
import re
import subprocess
import urllib.error
import urllib.request
from datetime import datetime

from .config import DEFAULT_EDITOR_PORT, DEFAULT_LOOPBACK_HOST


def extract_project_path_from_command_line(command_line):
    if not command_line:
        return None

    quoted_match = re.search(r'-projectpath\s+"([^"]+)"', command_line, flags=re.IGNORECASE)
    if quoted_match:
        return quoted_match.group(1)

    unquoted_match = re.search(r'-projectpath\s+([^\s]+)', command_line, flags=re.IGNORECASE)
    if unquoted_match:
        return unquoted_match.group(1)

    return None


def _read_project_refresh_state(project_path):
    state_paths = (
        os.path.join(
            project_path,
            "Library",
            "CSharpConsole",
            "RefreshState",
            "v1",
            "refresh_state.json",
        ),
        # Compatibility with package versions that persisted discovery state
        # under the rebuildable Temp directory.
        os.path.join(project_path, "Temp", "CSharpConsole", "refresh_state.json"),
    )
    for state_path in state_paths:
        if not os.path.isfile(state_path):
            continue
        try:
            with open(state_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            continue
    return None


def read_project_temp_state(project_path):
    """Compatibility name retained for callers that patch the old helper."""
    return _read_project_refresh_state(project_path)


def read_project_refresh_state(project_path):
    return read_project_temp_state(project_path)


def is_batchmode_worker_command_line(command_line):
    text = (command_line or "").lower()
    return "-batchmode" in text or "assetimportworker" in text or '"-name" "assetimportworker' in text


def parse_windows_unity_processes_json(output):
    try:
        raw_items = json.loads(output)
    except Exception:
        return []

    if isinstance(raw_items, dict):
        raw_items = [raw_items]
    if not isinstance(raw_items, list):
        return []

    processes = []
    for item in raw_items:
        if not isinstance(item, dict):
            continue

        pid_raw = item.get("ProcessId")
        command_line = item.get("CommandLine") or ""
        if is_batchmode_worker_command_line(command_line):
            continue
        creation_text = item.get("CreationDate") or ""

        try:
            pid_value = int(pid_raw)
        except Exception:
            continue

        create_time = None
        stamp_match = re.match(r"^(\d{14})", str(creation_text))
        if stamp_match:
            try:
                create_time = datetime.strptime(stamp_match.group(1), "%Y%m%d%H%M%S").timestamp()
            except Exception:
                create_time = None

        processes.append({
            "pid": pid_value,
            "create_time": create_time,
            "command_line": command_line,
        })

    return processes


def list_unity_editor_processes():
    command = (
        "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" "
        "| Select-Object ProcessId,CreationDate,CommandLine "
        "| ConvertTo-Json -Compress"
    )

    try:
        output = subprocess.check_output(
            ["powershell", "-NoProfile", "-Command", command],
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
        )
    except Exception:
        return []

    return parse_windows_unity_processes_json(output)


def list_listening_ports_for_pid(pid):
    if pid is None:
        return []

    try:
        output = subprocess.check_output(
            ["netstat", "-ano", "-p", "tcp"],
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
        )
    except Exception:
        return []

    ports = []
    for raw_line in output.splitlines():
        line = raw_line.strip()
        if not line.startswith("TCP"):
            continue

        parts = line.split()
        if len(parts) < 5:
            continue

        state = parts[3].upper()
        pid_text = parts[4]
        if state != "LISTENING" or pid_text != str(pid):
            continue

        local_address = parts[1]
        if ":" not in local_address:
            continue

        port_text = local_address.rsplit(":", 1)[-1]
        try:
            port_value = int(port_text)
        except Exception:
            continue

        ports.append(port_value)

    return sorted(set(ports))


def probe_editor_health(host, port, timeout_seconds=1.0):
    url = f"http://{host}:{port}/CSharpConsole/health"
    request = urllib.request.Request(
        url,
        data=b"{}",
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            if response.getcode() != 200:
                return {"ok": False, "status": response.getcode()}
            return {"ok": True}
    except urllib.error.HTTPError as ex:
        return {"ok": False, "status": ex.code}
    except OSError as ex:
        return {"ok": False, "error": str(getattr(ex, "reason", ex))}


def is_valid_console_port(port):
    try:
        value = int(port)
    except Exception:
        return False
    return DEFAULT_EDITOR_PORT <= value < DEFAULT_EDITOR_PORT + 100


def read_editor_port_from_project_state(project_path):
    state = read_project_refresh_state(project_path)
    if not isinstance(state, dict):
        return None

    for key in ("effectivePort", "editorPort", "port"):
        value = state.get(key)
        if is_valid_console_port(value):
            return int(value)
    return None


def discover_direct_launch_candidates():
    candidates = []
    for process in list_unity_editor_processes():
        pid = process.get("pid")
        project_path = extract_project_path_from_command_line(process.get("command_line") or "") or ""
        port = read_editor_port_from_project_state(project_path)
        if not port:
            continue

        health = probe_editor_health(DEFAULT_LOOPBACK_HOST, port)
        if not health.get("ok"):
            continue

        create_time = process.get("create_time")
        if create_time is None:
            start = "unknown"
        else:
            try:
                start = datetime.fromtimestamp(create_time).strftime("%Y-%m-%d %H:%M:%S")
            except Exception:
                start = "unknown"

        project_path = extract_project_path_from_command_line(process.get("command_line") or "") or ""
        candidates.append({
            "pid": pid,
            "port": port,
            "start": start,
            "projectPath": project_path,
        })

    return candidates


def format_direct_launch_candidate_label(candidate):
    return f"PID {candidate.get('pid')} | {candidate.get('projectPath')}"
