#!/usr/bin/env python3
"""Static checks that need no Unity installation.

Two kinds of drift can land here without anyone noticing. The README action
tables can fall behind the `[CommandAction]` declarations, which is what
happened before #12 added ten missing rows. And a tracked file can arrive
without its `.meta`, which only shows up when someone imports the package.

Run from anywhere:

    python .github/scripts/check_package.py
"""

import re
import subprocess
import sys
from pathlib import Path, PurePosixPath

REPO_ROOT = Path(__file__).resolve().parents[2]

# The first two positional arguments are the namespace and the action. The
# attribute is written on one line in a few places and spread over five in
# most, so the pattern has to tolerate newlines between them.
COMMAND_ACTION = re.compile(r'\[CommandAction\(\s*"([^"]+)"\s*,\s*"([^"]+)"')

READMES = ("README.md", "README_zh.md")

SEPARATOR_CELL = re.compile(r"^:?-+:?$")


def tracked_files():
    result = subprocess.run(
        ["git", "ls-files", "-z"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        check=True,
    )
    return [path for path in result.stdout.split("\0") if path]


def split_row(line):
    """Cells of a Markdown table row, or None if the line is not one."""
    stripped = line.strip()
    if not stripped.startswith("|"):
        return None
    return [cell.strip() for cell in stripped.strip("|").split("|")]


def declared_actions(files):
    """Every `<namespace>/<action>` the package registers, in declaration order."""
    found = []
    for relative in files:
        if not relative.endswith(".cs"):
            continue
        text = (REPO_ROOT / relative).read_text("utf-8")
        for namespace, action in COMMAND_ACTION.findall(text):
            found.append((f"{namespace}/{action}", relative))
    return found


def documented_actions(relative):
    """The action table of one README: its ids and the sentence counting them."""
    lines = (REPO_ROOT / relative).read_text("utf-8").splitlines()

    headers = [
        index
        for index, line in enumerate(lines)
        if (cells := split_row(line)) and len(cells) >= 3 and cells[1] == "Action"
    ]
    if len(headers) != 1:
        raise LookupError(
            f"{relative}: expected exactly one table with an 'Action' column, "
            f"found {len(headers)}"
        )
    header = headers[0]

    ids, namespace = [], ""
    for line in lines[header + 1 :]:
        cells = split_row(line)
        if cells is None:
            break
        if all(SEPARATOR_CELL.match(cell) for cell in cells if cell):
            continue
        if cells[0]:
            namespace = cells[0].strip("*")
        ids.append(f"{namespace}/{cells[1].strip('`')}")

    # The paragraph directly above the table states how many actions and
    # namespaces there are. Both READMEs word it differently, so match on the
    # numbers rather than the prose.
    summary = next(
        (line.strip() for line in reversed(lines[:header]) if line.strip()), ""
    )
    return ids, summary


def check_action_tables(files):
    declared = declared_actions(files)
    ids = [action for action, _ in declared]
    problems = []

    duplicates = sorted({action for action in ids if ids.count(action) > 1})
    if duplicates:
        problems.append(f"declared twice in C#: {', '.join(duplicates)}")

    expected = set(ids)
    namespaces = {action.split("/", 1)[0] for action in expected}

    for relative in READMES:
        documented, summary = documented_actions(relative)
        missing = sorted(expected - set(documented))
        extra = sorted(set(documented) - expected)
        if missing:
            problems.append(f"{relative}: declared but not in the table: {', '.join(missing)}")
        if extra:
            problems.append(f"{relative}: in the table but not declared: {', '.join(extra)}")

        counts = set(re.findall(r"\d+", summary))
        if str(len(expected)) not in counts or str(len(namespaces)) not in counts:
            problems.append(
                f"{relative}: the sentence above the table reads {summary!r}, "
                f"but there are {len(expected)} actions in {len(namespaces)} namespaces"
            )

    return f"{len(expected)} actions in {len(namespaces)} namespaces", problems


def unity_visible(relative):
    """Whether Unity's importer looks at this path at all."""
    for part in PurePosixPath(relative).parts:
        if part.startswith(".") or part.endswith("~") or part.endswith(".tmp"):
            return False
        if part.lower() == "cvs":
            return False
    return True


def check_meta_files(files):
    tracked = set(files)
    directories = set()
    for path in files:
        parent = PurePosixPath(path).parent
        while str(parent) != ".":
            directories.add(str(parent))
            parent = parent.parent

    assets = [
        path
        for path in sorted(tracked | directories)
        if not path.endswith(".meta") and unity_visible(path)
    ]
    missing = [path for path in assets if path + ".meta" not in tracked]
    orphans = [
        path
        for path in sorted(tracked)
        if path.endswith(".meta")
        and path[:-5] not in tracked
        and path[:-5] not in directories
    ]

    problems = []
    if missing:
        problems.append("no .meta for: " + ", ".join(missing))
    if orphans:
        problems.append(".meta with nothing behind it: " + ", ".join(orphans))
    return f"{len(assets)} imported paths", problems


def main():
    files = tracked_files()
    failed = False

    for name, check in (("action tables", check_action_tables), ("meta files", check_meta_files)):
        try:
            detail, problems = check(files)
        except LookupError as error:
            print(f"FAIL  {name}\n        {error}")
            failed = True
            continue
        if problems:
            failed = True
            print(f"FAIL  {name}")
            for problem in problems:
                print(f"        {problem}")
        else:
            print(f"ok    {name} ({detail})")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
