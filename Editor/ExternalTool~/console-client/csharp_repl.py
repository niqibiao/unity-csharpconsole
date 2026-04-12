import sys

from csharp_bootstrap import bootstrap_repl_dependencies, ensure_supported_python
ensure_supported_python()
bootstrap_repl_dependencies()

from repl import direct_launch as direct_launch_helpers
from repl.launcher import (
    build_direct_launch_editor_args,
    has_explicit_connection_args,
    parse_repl_args,
    select_direct_launch_candidate,
)

from csharp_repl_core import run_repl


def resolve_direct_launch_args(status_writer=print):
    if callable(status_writer):
        status_writer("Discovering Unity Editor instances...")
    candidates = direct_launch_helpers.discover_direct_launch_candidates()
    if callable(status_writer):
        if candidates:
            status_writer(f"Discovered {len(candidates)} Unity Editor instance(s).")
        else:
            status_writer("No available Unity Editor instances found.")
    selected = select_direct_launch_candidate(candidates) if candidates else None
    if selected is None:
        raise SystemExit(0)
    return build_direct_launch_editor_args(selected)


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv

    if not argv:
        args = resolve_direct_launch_args()
    else:
        args = parse_repl_args(argv)

    run_repl(args)


if __name__ == "__main__":
    main()
