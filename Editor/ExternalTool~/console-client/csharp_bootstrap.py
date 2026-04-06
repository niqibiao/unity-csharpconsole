import hashlib
import os
import subprocess
import sys

_script_dir = os.path.dirname(os.path.abspath(__file__))
_site_packages = os.path.join(_script_dir, "site-packages")


def _file_hash(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        h.update(f.read())
    return h.hexdigest()


def _dir_hash(path):
    h = hashlib.sha256()
    if not os.path.isdir(path):
        return ""
    for root, dirs, files in os.walk(path):
        dirs.sort()
        files.sort()
        for name in files:
            full_path = os.path.join(root, name)
            rel_path = os.path.relpath(full_path, path).replace("\\", "/")
            h.update(rel_path.encode("utf-8"))
            with open(full_path, "rb") as f:
                h.update(f.read())
    return h.hexdigest()


def _ensure_site_packages_on_path():
    if os.path.isdir(_site_packages) and _site_packages not in sys.path:
        sys.path.insert(0, _site_packages)


def _ensure_pip():
    if subprocess.call(
        [sys.executable, "-c", "import pip"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ) == 0:
        return

    subprocess.check_call(
        [sys.executable, "-m", "ensurepip", "--default-pip"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def ensure_deps(requirements_filename, marker_name):
    requirements_path = os.path.join(_script_dir, requirements_filename)
    if not os.path.isfile(requirements_path):
        _ensure_site_packages_on_path()
        return

    os.makedirs(_site_packages, exist_ok=True)
    marker_path = os.path.join(_site_packages, marker_name)
    requirements_hash = _file_hash(requirements_path)
    need_install = True

    if os.path.isfile(marker_path):
        with open(marker_path, "r", encoding="utf-8") as f:
            if f.read().strip() == requirements_hash:
                need_install = False

    if need_install:
        print(f"[bootstrap] Installing dependencies from {requirements_filename} ...", file=sys.stderr)
        _ensure_pip()
        subprocess.check_call([
            sys.executable,
            "-m",
            "pip",
            "install",
            "--target",
            _site_packages,
            "--upgrade",
            "-r",
            requirements_path,
            "--no-warn-script-location",
            "-q",
        ])
        with open(marker_path, "w", encoding="utf-8") as f:
            f.write(requirements_hash)
        print("[bootstrap] Done.", file=sys.stderr)

    _ensure_site_packages_on_path()


def bootstrap_repl_dependencies():
    ensure_deps("requirements-repl.txt", ".installed-repl")
