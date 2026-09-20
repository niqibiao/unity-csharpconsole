# Editor REPL regression scripts

These C# files are executable Editor REPL submissions, kept outside Unity's asset import. They throw when a regression is detected and return `PASS` on success.

Copy a script to the consuming project's `Temp/CSharpConsole/AgentScratch/`, then execute it in Editor mode with the package REPL's `/dofile` or unity-cli's `exec --file`.

- `PlayerBuildRecordRegression.cs` checks build argument selection with newer artifacts from another target or scripting backend. It does not require those platform modules to be installed.
- `CompileSetReferencesRegression.cs` requires a successful Player build and its exported `CSharpConsoleCompileSet.zip`. It checks reference isolation, completion, continuing a session after a compile error, legacy reference modes, and invalid reference directories.
- `RuntimeCompileSetSessionRegression.cs` checks completion after registration,
  replacement and skip changes, compiler invalidation, explicit overrides, and
  session cleanup. It uses an independent registry and removes its unique
  compile-set store fixtures in `finally`.

The build-record and reference scripts create isolated fixtures under the
consuming project's `Temp/`. The session script cleans up its own unique entries
under `Library/CSharpConsole/CompileSets/`. None changes build settings or
application source.

`test_compile_set_http.py` checks registration against unreachable, malformed, and
mismatched fake players. It requires a local Editor with this package loaded;
all requests are rejected and no compile-set decisions should be written. Set
`CSHARPCONSOLE_TEST_URL=http://127.0.0.1:14500/CSharpConsole` (adjust the port), then
run `python -B -m unittest discover -s Tests~ -p "test_*.py" -v`. Without the
environment variable, these live tests skip.
