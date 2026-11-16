"""Run the Bee regression and optionally replay read-only Player build captures."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile


TESTS = Path(__file__).resolve().parent


def build_runner(work):
    sdk = int(subprocess.check_output(["dotnet", "--version"], text=True).split(".")[0])
    if sdk < 8:
        raise ValueError("Replay requires .NET SDK 8 or newer.")
    framework = f"net{sdk}.0"
    (work / "Replay.csproj").write_text(
        f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
        f'<OutputType>Exe</OutputType><TargetFramework>{framework}</TargetFramework>'
        f'<NoWarn>0649</NoWarn></PropertyGroup></Project>',
        encoding="utf-8",
    )
    for name in ("PlayerBuildRecord.cs", "CompileSetExporter.cs"):
        shutil.copyfile(TESTS.parent / "Editor/Compiler" / name, work / name)
    shutil.copyfile(TESTS / "PlayerBuildRecordReplay.cs", work / "PlayerBuildRecordReplay.cs")
    # The Editor REPL uses the trailing expression as its result. In this executable,
    # print the same value so we exercise the very same regression cases.
    body, result = (TESTS / "PlayerBuildRecordRegression.cs").read_text(encoding="utf-8").rstrip().rsplit("\n", 1)
    (work / "Program.cs").write_text(
        "if (args.Length > 0) { PlayerBuildRecordReplay.Export(args); return; }\n"
        + body + "\nSystem.Console.WriteLine(" + result + ");\n",
        encoding="utf-8",
    )
    subprocess.run(["dotnet", "build", "--nologo", "-v", "quiet"], cwd=work, check=True)
    return ["dotnet", str(work / "bin/Debug" / framework / "Replay.dll")]


def zip_hashes(archive):
    with zipfile.ZipFile(archive) as source:
        return {name: hashlib.sha256(source.read(name)).hexdigest() for name in source.namelist()}


def replay(runner, work, capture_path):
    capture = json.loads(capture_path.read_text(encoding="utf-8-sig"))
    base = capture_path.resolve().parent
    expected = (base / capture["expectedZip"]).resolve(strict=True)
    rsp = (base / capture["compilerRsp"]).resolve(strict=True)
    backend_define = {"Il2Cpp": "ENABLE_IL2CPP", "Mono": "ENABLE_MONO"}[capture["backend"]]
    defines = [
        line.strip().removeprefix("-define:")
        for line in rsp.read_text(encoding="utf-8-sig").splitlines()
        if line.strip().startswith("-define:")
    ]
    if ("UNITY_EDITOR" in defines or backend_define not in defines
            or ("DEVELOPMENT_BUILD" in defines) != capture["development"]):
        raise ValueError(f"{rsp} does not match the captured Player configuration.")

    # Keep all writes in this new directory. The production reader opens the original
    # Bee files and linker output read-only, even when --out is an absolute path.
    with tempfile.TemporaryDirectory(dir=work) as temp:
        result = Path(temp) / "compile-set.zip"
        subprocess.run(runner + [
            str((base / capture["projectRoot"]).resolve(strict=True)),
            capture["playerSupport"],
            capture["backend"],
            str(capture["development"]),
            capture["destination"],
            str(capture.get("installInBuildFolder", False)),
            str(capture.get("macAppBundle", False)),
            str(capture.get("stagedOutput", False)),
            capture["buildGuid"],
            str(result),
        ], cwd=work, check=True)
        with zipfile.ZipFile(result) as archive:
            actual_defines = archive.read("runtime-defines.txt").decode()
        if actual_defines.split(";") != defines:
            raise ValueError(f"Exported defines differ from the captured compiler response {rsp}.")
        got, want = zip_hashes(result), zip_hashes(expected)
        different = sorted(name for name in got.keys() | want.keys() if got.get(name) != want.get(name))
        if different:
            raise ValueError(f"ZIP entries differ from {expected}: {', '.join(different)}")
        print(f"PASS: {capture_path.name}, {len(want)} ZIP entries match by SHA-256")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--replay", type=Path, action="append", default=[], metavar="CAPTURE_JSON")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="console-bee-replay-") as temp:
        work = Path(temp)
        runner = build_runner(work)
        subprocess.run(runner, cwd=work, check=True)
        for capture in args.replay:
            replay(runner, work, capture)


if __name__ == "__main__":
    main()
