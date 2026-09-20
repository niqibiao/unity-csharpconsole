# Editor REPL regression scripts

These C# files are executable Editor REPL submissions, kept outside Unity's asset import. They throw when a regression is detected and return `PASS` on success.

Copy a script to the consuming project's `Temp/CSharpConsole/AgentScratch/`, then execute it in Editor mode with the package REPL's `/dofile` or unity-cli's `exec --file`.

- `PlayerBuildRecordRegression.cs` checks build argument selection with newer artifacts from another target or scripting backend. It does not require those platform modules to be installed.
- `CompileSetReferencesRegression.cs` requires a successful Player build and its exported `CSharpConsoleCompileSet.zip`. It checks reference isolation, completion, continuing a session after a compile error, legacy reference modes, and invalid reference directories.

Both scripts create isolated fixtures under the consuming project's `Temp/`; they do not change build settings or application source.
