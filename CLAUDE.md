# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

- Start the interactive REPL with direct-launch editor discovery:
  ```bash
  python "Editor/ExternalTool~/console-client/csharp_repl.py"
  ```
- Connect to a specific Unity Editor host:
  ```bash
  python "Editor/ExternalTool~/console-client/csharp_repl.py" --editor --ip 127.0.0.1 --port 14500
  ```
- Connect to a runtime player while using the editor as the compile server:
  ```bash
  python "Editor/ExternalTool~/console-client/csharp_repl.py" --mode runtime --ip 127.0.0.1 --port 15500 --compile-ip 127.0.0.1 --compile-port 14500
  ```
- Install Python REPL dependencies manually if bootstrap is not enough (normally first REPL launch auto-installs them into `site-packages`):
  ```bash
  python -m pip install --target "Editor/ExternalTool~/console-client/site-packages" --upgrade -r "Editor/ExternalTool~/console-client/requirements-repl.txt" --no-warn-script-location
  ```
- Run shared Python core tests:
  ```bash
  python -m unittest discover -s "Editor/ExternalTool~/console-client/tests/csharpconsole_core" -p "test_*.py" -v
  ```
- Run REPL product-layer tests:
  ```bash
  python -m unittest discover -s "Editor/ExternalTool~/console-client/tests/tests_repl" -p "test_*.py" -v
  ```
- Run a single test file:
  ```bash
  python "Editor/ExternalTool~/console-client/tests/tests_repl/test_repl_direct_launch.py" -v
  ```
- Run a single test method:
  ```bash
  python "Editor/ExternalTool~/console-client/tests/tests_repl/test_repl_direct_launch.py" ReplDirectLaunchEntryTests.test_main_uses_direct_launch_when_no_args -v
  ```

There is no repo-local lint/format configuration in this package repo, and no standalone build command beyond importing the package into a Unity project.

## Architecture

### Unity-side service and execution

- `Runtime/Service/ConsoleHttpService.cs` is the central HTTP service. It owns listener startup, session state, and request routing for REPL/editor/runtime endpoints including `/editor`, `/compile`, `/completion`, `/command`, `/health`, and `/execute`.
- Default ports are `14500` for the editor host and `15500` for the player host. The listener can advance to the next free port if the default is occupied.
- `Runtime/Executor/REPLExecutor.cs` loads compiled submission assemblies and invokes Roslyn script `<Factory>` methods while preserving submission state across executions.
- `Runtime/Interface/` defines the core abstractions: `IREPLCompiler`, `IREPLExecutor`, `IREPLCompletionProvider`.
- `Runtime/Service/Contracts/` holds request/response DTOs for editor, envelope, execution, and health endpoints.
- `Runtime/Service/Endpoints/` has standalone HTTP handlers for completion and health.
- `Runtime/Service/Internal/` contains service internals: dependency wiring (`ConsoleHttpServiceDependencies`), envelope formatting (`HttpEnvelopeFactory`), main-thread dispatch (`MainThreadRequestRunner`), and service registration (`ReplServiceRegistry`).

### Commands module

- `Runtime/Service/Commands/` is the independent command framework used by REPL `$namespace.action(...)` expressions.
- `Commands/Core/` — `CommandDescriptor` (metadata schema with id, namespace, action, arguments, editorOnly flag), `CommandRegistry` (registration/discovery), `CommandDispatcher` (execution with main-thread support), `CommandArgumentBinder` (reflection-based JSON-to-parameter binding), `CommandDiscoveryOptions` and `ICommandAssemblyFilter` (configurable assembly scanning).
- `Commands/Routing/` — `[CommandAction]` attribute-based discovery, `CommandRouter` (central routing with lazy init), `CommandHandlerBinding` (method-to-invoker binding with automatic parameter descriptors), `CommandEndpointHandler` (HTTP `/command` routing).
- `Commands/Handlers/` — built-in command implementations: catalog listing, editor commands, project manipulation, session management (reset, inspect, list). Handlers use ASP.NET minimal API-style signatures — primitive parameters are declared directly and bound automatically from JSON args.

### Editor-only bootstrap and compilation

- `Editor/EditorInitializer.cs` wires the editor compiler/executor factories into `ConsoleHttpService` on load and reinitializes around play mode transitions.
- `Editor/Compiler/BaseREPLCompiler.cs` is the core Roslyn script compiler and completion provider. It caches submission state and `using` directives, supports top-level script submissions, and swaps assembly references when compiling for runtime targets.
- `Editor/Compiler/EditorREPLCompiler.cs` handles editor-target compilation; `Editor/Compiler/RuntimeREPLCompiler.cs` handles compile-for-player flows.
- `Editor/EditorTools/ConsoleMenu.cs` provides the Unity menu entrypoints `Console/C#Console` and `Console/RemoteC#Console`, preferring Windows Terminal when available.

### Bundled Python client

- `Editor/ExternalTool~/console-client/csharp_repl.py` is the human-facing entrypoint.
- `Editor/ExternalTool~/console-client/repl/` is the REPL product layer: prompt-toolkit UI, direct-launch discovery, builtins, completion UX, runtime artifact handling, and interactive loop behavior.
- `Editor/ExternalTool~/console-client/csharpconsole_core/` is the reusable shared Python core: HTTP transport, command protocol, response parsing, config/models, and runtime artifact primitives.
- Keep reusable protocol/transport logic in `csharpconsole_core/`; keep interactive UX and REPL orchestration in `repl/`.

## Testing structure

- `Editor/ExternalTool~/console-client/tests/csharpconsole_core/` covers the shared Python core.
- `Editor/ExternalTool~/console-client/tests/tests_repl/` covers REPL product behavior such as direct-launch, completion, prompt behavior, and builtins.
- There are currently no Unity NUnit test assemblies in this repository.

## Project-specific notes

- This repo is a standalone UPM package repo and now also vendors the shared Python core used by the REPL.
- Editor mode is a single-host flow: the editor compiles and executes submissions locally.
- Runtime mode is a split flow: the editor-side compile server compiles, then the player executes the emitted assembly.
- Both asmdefs (`Editor/Zh1Zh1.CSharpConsole.Editor.asmdef` and `Runtime/Zh1Zh1.CSharpConsole.Runtime.asmdef`) have `autoReferenced: false`, so consuming projects must reference them explicitly.
- `Runtime/Zh1Zh1.CSharpConsole.Runtime.asmdef` is gated by `DEVELOPMENT_BUILD || UNITY_EDITOR`; do not assume the runtime service exists in non-development player builds.
- First REPL launch may install Python dependencies into `Editor/ExternalTool~/console-client/site-packages`.
- REPL sessions (compiler/executor pairs) are tracked with last-access timestamps and automatically evicted after 6 hours of idle time. Eviction runs lazily during `/health` requests — there is no background timer. This is by design: the REPL client and CLI both poll `/health` regularly, and the 6-hour window is generous enough that no active workflow is disrupted.
- The separate non-interactive CLI lives in another repo; this package repo owns the Unity package, HTTP service, REPL product layer, shared Python core, and related tests.
- `AGENTS.md` mirrors this file for Codex; keep both in sync when updating.
- `RELEASE.md` has the release checklist: bump `package.json` version, verify in `PackagesDemo`, then tag as `vX.Y.Z`.
- Minimum Unity version is 2022.3 (`package.json`).