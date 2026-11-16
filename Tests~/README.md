# Editor REPL regression scripts

These C# files are executable Editor REPL submissions, kept outside Unity's asset import. They throw when a regression is detected and return `PASS` on success.

Copy a script to the consuming project's `Temp/CSharpConsole/AgentScratch/`, then execute it in Editor mode with the package REPL's `/dofile` or unity-cli's `exec --file`.

- `PlayerBuildRecordRegression.cs` checks that the player graph matching the build is found among graphs from other targets, backends, configurations and output paths, including Unity 6 and Android graphs and macOS bundles without `.app`; that linker arguments are read from a response file or inline; and that missing or ambiguous graphs are refused. It does not require those platform modules to be installed.
- `CompileSetReferencesRegression.cs` requires a successful Player build and its exported `CSharpConsoleCompileSet.zip`. It checks reference isolation, completion, continuing a session after a compile error, legacy reference modes, and invalid reference directories.
- `RuntimeCompileSetSessionRegression.cs` checks completion after registration,
  replacement and skip changes, compiler invalidation, explicit overrides, and
  session cleanup. It uses an independent registry and removes its unique
  compile-set store fixtures in `finally`.

The build-record and reference scripts create isolated fixtures under the
consuming project's `Temp/`. The session script cleans up its own unique entries
under `Library/CSharpConsole/CompileSets/`. None changes build settings or
application source.

`run_player_build_record_tests.py` runs `PlayerBuildRecordRegression.cs` without
Unity: it compiles `PlayerBuildRecord.cs` and `CompileSetExporter.cs` with the shims
in `PlayerBuildRecordReplay.cs` and requires the .NET SDK 8 or newer. Run
`python -B Tests~/run_player_build_record_tests.py`. Each `--replay CAPTURE_JSON`
also exports a compile set from a real build's files and compares every ZIP entry
with the ZIP that build exported, by SHA-256. A capture names files in place, so
replay it before the project builds again; the harness writes only to its own
temporary directory.

`test_compile_set_http.py` checks registration against unreachable, malformed, and
mismatched fake players. It requires a local Editor with this package loaded;
all requests are rejected and no compile-set decisions should be written. Set
`CSHARPCONSOLE_TEST_URL=http://127.0.0.1:14500/CSharpConsole` (adjust the port), then
run `python -B -m unittest discover -s Tests~ -p "test_*.py" -v`. Without the
environment variable, these live tests skip.

`HierarchyRegression.cs` creates a temporary scene and prefab, then restores the
active scene and removes its prefab folder. It writes JSON samples under
`Temp/CSharpConsole/AgentScratch/hierarchy-regression/`. Set
`CSHARPCONSOLE_HIERARCHY_RESULTS` to that absolute directory and run
`python -B -m unittest discover -s Tests~ -p test_hierarchy_results.py -v`.
The independent JSON parser checks deep and empty trees, escaping, requested
and safety depth limits, exact and exceeded node budgets, qualified component
names, and absence of serialization warnings.
