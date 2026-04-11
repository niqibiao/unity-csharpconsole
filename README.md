<div align="center">

# CSharp Console

**Interactive C# REPL for Unity — powered by Roslyn**

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Unity](https://img.shields.io/badge/Unity-2022.3%2B-black.svg?logo=unity)](https://unity.com/)
[![Claude Code](https://img.shields.io/badge/Claude_Code-blueviolet.svg?logo=anthropic)](https://claude.ai/code)
[![UPM](https://img.shields.io/badge/UPM-Package-brightgreen.svg)](package.json)

Execute C# code on the fly in Unity Editor & Runtime — no compilation wait, no boilerplate,<br/>
full access to your project's live state. **Editor zero-config, Runtime just works with HybridCLR.**

[Features](#features) · [Installation](#installation) · [Quick Start](#quick-start) · [REPL Usage](#repl-usage) · [Extending Commands](#extending-commands)

English | [中文](README_zh.md)

</div>

---

## ✦ Features

### Core Capabilities

| | Feature | Description |
|:--:|---------|-------------|
| **>\_** | **Interactive REPL** | Roslyn-based script submissions with persistent session state — variables, `using` directives, and types survive across executions |
| **#** | **Top-level Syntax** | Write statements directly. No `class`, no `Main`, no boilerplate |
| **@** | **Command Framework** | Extensible `[CommandAction]` commands with automatic JSON-to-parameter binding (positional & named args), `/batch` endpoint for multi-command workflows |
| **Tab** | **Semantic Completion** | Real-time member, namespace, and type completions directly from Roslyn |
| **🔓** | **Private Member Access** | Bypass `private` / `protected` / `internal` access modifiers at compile time for deep inspection |
| **📡** | **Remote Execution** | Compile in the Editor, execute on a connected Player build (IL2CPP via HybridCLR) |

### How It Looks

<img src="Docs~/images/repl-0.gif" width="100%" />

#### Immediate evaluation — no class, no Main, just code

```csharp
DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
```

<img src="Docs~/images/repl-1.png" />

#### Cross-submission state — variables survive across submissions

```csharp
var cam = Camera.main; cam.transform.position
```

<img src="Docs~/images/repl-2.png" />

#### Private member access — bypass access modifiers at compile time

```csharp
var go = GameObject.Find("Main Camera");
go.m_InstanceID
```

<img src="Docs~/images/repl-3.png" />

#### LINQ over live scene objects

```csharp
string.Join(", ", UnityEngine.Object.FindObjectsOfType<Rigidbody>().Select(x => x.name))
```

<img src="Docs~/images/repl-4.png" />

#### Command expressions — invoke server-side commands directly

```csharp
@editor.status()
```

<img src="Docs~/images/repl-5.png" />

## ⚙ Installation

Add via `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.zh1zh1.csharpconsole": "https://github.com/niqibiao/unity-csharpconsole.git"
  }
}
```

Or reference as a local package:

```json
{
  "dependencies": {
    "com.zh1zh1.csharpconsole": "file:../com.zh1zh1.csharpconsole"
  }
}
```

> **Note:** Both assembly definitions have `autoReferenced: false`. If your code needs to reference this package, add `Zh1Zh1.CSharpConsole.Runtime` (or `.Editor`) to your asmdef's references explicitly.

## ▶ Quick Start

### Editor — Zero Configuration

**Import the package and it just works.** The Editor-side HTTP service starts automatically via `[InitializeOnLoadMethod]` — no initialization code, no settings to tweak, no manual setup. Open the REPL from the Unity menu:

| Menu Item | Target |
|-----------|--------|
| **Console > C#Console** | Local Editor |
| **Console > RemoteC#Console** | Remote Editor / Player |

### Runtime — One Line, No Extra Setup

Enable the remote console on a Player build with a single call:

```csharp
#if DEVELOPMENT_BUILD
Zh1Zh1.CSharpConsole.RuntimeInitializer.ConsoleInitialize();
#endif
```

Runtime execution only depends on **HybridCLR**'s `Assembly.Load` capability for IL2CPP (no additional configuration needed).

> The runtime assembly is gated by `DEVELOPMENT_BUILD || UNITY_EDITOR`.

| | Port |
|--|------|
| Editor | `14500` (default) |
| Runtime | `15500` (default) |

If a port is occupied, the service automatically advances to the next available one.

## ⌨ REPL Usage

### Starting the REPL

The recommended way is through the Unity menu. You can also launch directly:

```bash
# Auto-discover running Unity Editors
python "Editor/ExternalTool~/console-client/csharp_repl.py"

# Connect to a specific Editor
python "Editor/ExternalTool~/console-client/csharp_repl.py" --editor --ip 127.0.0.1 --port 14500

# Connect to a Runtime Player (with Editor as compile server)
python "Editor/ExternalTool~/console-client/csharp_repl.py" \
  --mode runtime --ip 127.0.0.1 --port 15500 \
  --compile-ip 127.0.0.1 --compile-port 14500
```

Python 3.7+ is required. Python dependencies (`requests`, `prompt_toolkit`, `Pygments`) are installed automatically on first launch.

### Remote Runtime — Optional Settings

When connecting to a Runtime Player via **Console > RemoteC#Console**, two optional settings are available to improve compilation accuracy:

| Setting | Description |
|---------|-------------|
| **Runtime Dll Path** | Directory containing the player's compiled assemblies. The compiler uses these DLLs instead of Editor assemblies to resolve types, ensuring the compiled code matches what the player actually has. Recommended path: `Library/Bee/PlayerScriptAssemblies` (populated after a player build). |
| **Runtime Defines File** | A `.txt` file listing preprocessor defines that match the player's build configuration, ensuring `#if` directives evaluate the same way as in the player. Supports one define per line or semicolon-separated (e.g. `UNITY_ANDROID;IL2CPP;DEVELOPMENT_BUILD`). |

Both settings are persisted in `EditorPrefs` and only apply when **Remote Is Editor** is unchecked. Leave them empty to use defaults (Editor assemblies and defines).

### Key Bindings

| Key | Action |
|-----|--------|
| `Enter` | Submit input |
| `Ctrl+Enter` | Insert newline without submitting |
| `Tab` | Accept completion candidate |
| `Ctrl+R` | Reverse history search |
| `Ctrl+C` | Clear input (confirm quit if empty) |

Completion activates automatically as you type. The toolbar shows semantic completion status: on `●` / off `○`.

### Built-in Commands

| Command | Description |
|---------|-------------|
| `/completion <0\|1>` | Toggle semantic completion |
| `/using` | Show default `using` file path |
| `/define` | Show preprocessor defines file path |
| `/reload` | Reload `using` / `define` files |
| `/reset` | Reset the REPL session |
| `/clear` | Clear the terminal |
| `/dofile <path>` | Execute a local `.cs` file |

### Command Expressions

The REPL supports `@`-prefixed command expressions that invoke the server-side command framework directly — bypassing Roslyn compilation:

```text
@project.scene.open(scenePath: "Assets/Scenes/SampleScene.unity", mode: "single")
@editor.status()
@session.inspect(sessionId: "session-1")
```

Tab completion works for both command names and argument names.

## 📋 Built-in Actions

46 built-in commands across 12 namespaces, covering editor control, scene manipulation, asset management, and more.

| Namespace | Action | Description |
|-----------|--------|-------------|
| **gameobject** | `find` | Find GameObjects by name, tag, or component type |
| | `create` | Create a new GameObject (empty or primitive) |
| | `destroy` | Destroy a GameObject |
| | `get` | Get detailed info about a GameObject |
| | `modify` | Change name, tag, layer, active state, or static flag |
| | `set_parent` | Reparent a GameObject |
| | `duplicate` | Duplicate a GameObject |
| **component** | `add` | Add a component to a GameObject |
| | `remove` | Remove a component from a GameObject |
| | `get` | Get serialized field data of a component |
| | `modify` | Modify serialized fields of a component |
| **transform** | `get` | Get position, rotation, and scale |
| | `set` | Set position, rotation, and/or scale (local or world) |
| **scene** | `hierarchy` | Get the full scene hierarchy tree, optionally with component info |
| **prefab** | `create` | Create a prefab asset from a scene GameObject |
| | `instantiate` | Instantiate a prefab into the active scene |
| | `unpack` | Unpack a prefab instance |
| **material** | `create` | Create a new material asset with a specified shader |
| | `get` | Get material properties from an asset or a Renderer |
| | `assign` | Assign a material to a Renderer component |
| **screenshot** | `scene_view` | Capture the Scene View to an image file |
| | `game_view` | Capture the Game View to an image file |
| **profiler** | `start` | Start Profiler recording (optional deep profiling) |
| | `stop` | Stop Profiler recording |
| | `status` | Get current Profiler state |
| | `save` | Save recorded profiler data to a `.raw` file |
| **editor** | `status` | Get editor state and play mode info |
| | `playmode.status` | Get current play mode state |
| | `playmode.enter` | Enter play mode |
| | `playmode.exit` | Exit play mode |
| | `menu.open` | Execute a menu item by path |
| | `window.open` | Open an editor window by type name |
| | `console.get` | Get editor console log entries |
| | `console.clear` | Clear the editor console |
| **project** | `scene.list` | List all scenes in the project |
| | `scene.open` | Open a scene by path |
| | `scene.save` | Save the current scene |
| | `selection.get` | Get the current editor selection |
| | `selection.set` | Set the editor selection |
| | `asset.list` | List assets by type filter |
| | `asset.import` | Import an asset by path |
| | `asset.reimport` | Reimport an asset by path |
| **session** | `list` | List active REPL sessions |
| | `inspect` | Inspect a session's state |
| | `reset` | Reset a session's compiler and executor |
| **command** | `list` | List all registered commands (built-in + custom) |

> Most actions are editor-only. `session.*` and `command.list` are available on Runtime builds as well.

## 🔌 Extending Commands

The command framework lets any project add custom commands without modifying the package source — declare a `[CommandAction]` method and the framework handles discovery, parameter binding, and routing automatically.

See the full guide: **[Extending Commands](Docs~/ExtendingCommands.md)**

## 📦 Requirements

| Dependency | Version |
|------------|---------|
| Unity | 2022.3+ (theoretically 2019+ compatible, but untested) |
| Python | 3.x (on system `PATH`) |
| Windows Terminal | Optional (falls back to Python directly) |

## 🔗 Related Projects

- **[unity-cli-plugin](https://github.com/niqibiao/unity-cli-plugin)** — Non-interactive CLI for the same HTTP service, designed for scripting and automation workflows.
- **[python-prompt-toolkit](https://github.com/prompt-toolkit/python-prompt-toolkit)** — Terminal UI library powering the REPL's interactive interface.
- **[HybridCLR](https://github.com/focus-creative-games/hybridclr)** — IL2CPP hot-reload solution enabling `Assembly.Load` in Runtime mode.

## 📄 Third-Party Notices

This package bundles Roslyn compiler assemblies and dnlib under `Editor/Plugins/`. See [ThirdPartyNotices.md](ThirdPartyNotices.md) for full attribution and license details.

## 📜 License

[Apache License 2.0](LICENSE)
