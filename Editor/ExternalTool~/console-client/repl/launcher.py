import argparse

from . import config
from . import direct_launch as direct_launch_helpers


def parse_repl_args(argv):
    parser = argparse.ArgumentParser(description="CSharp Console REPL")
    config.add_common_connection_args(parser)
    return parser.parse_args(argv)


def has_explicit_connection_args(argv):
    connection_flags = {
        "--ip",
        "--port",
        "--editor",
        "--mode",
        "--runtime-dll-path",
        "--compile-ip",
        "--compile-port",
        "--runtime-defines",
    }
    return any(
        arg in connection_flags
        or any(arg.startswith(flag + "=") for flag in connection_flags)
        for arg in argv
    )


def build_direct_launch_editor_args(candidate):
    return argparse.Namespace(
        ip=config.DEFAULT_LOOPBACK_HOST,
        port=candidate.get("port", config.DEFAULT_EDITOR_PORT),
        editor=True,
        mode="editor",
        runtime_dll_path="",
        compile_ip=config.DEFAULT_LOOPBACK_HOST,
        compile_port=config.DEFAULT_EDITOR_PORT,
        runtime_defines="",
    )


def select_direct_launch_candidate(candidates):
    labels = [direct_launch_helpers.format_direct_launch_candidate_label(candidate) for candidate in candidates]
    print("Select Unity Editor instance:")
    for index, label in enumerate(labels, start=1):
        print(f"{index}. {label}")

    while True:
        raw_value = input(f"Enter selection [1-{len(candidates)}] (blank to cancel): ").strip()
        if not raw_value:
            return None
        if raw_value.isdigit():
            selected_index = int(raw_value)
            if 1 <= selected_index <= len(candidates):
                return candidates[selected_index - 1]

        print("Invalid selection. Enter a number from the list or press Enter to cancel.")


def resolve_direct_launch_args():
    candidates = direct_launch_helpers.discover_direct_launch_candidates()
    selected = select_direct_launch_candidate(candidates) if candidates else None
    if selected is None:
        raise SystemExit(0)
    return build_direct_launch_editor_args(selected)
