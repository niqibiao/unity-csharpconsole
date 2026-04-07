# Extending Commands

The command framework lets any project add custom commands without modifying the package source. Commands use an ASP.NET Minimal API-style design: declare a static method with `[CommandAction]`, and the framework automatically discovers it and binds parameters from JSON.

## Step 1 — Reference the Runtime assembly

Create an asmdef (or use an existing one) that references `Zh1Zh1.CSharpConsole.Runtime`:

```json
{
  "name": "MyGame.Commands",
  "references": ["Zh1Zh1.CSharpConsole.Runtime"]
}
```

## Step 2 — Write a command handler

### Minimal form — return `(bool, string)` tuple

The simplest way to write a command. No need to reference any framework types beyond the attribute:

```csharp
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

public static class MyCommands
{
    [CommandAction("mygame", "greet", summary: "Say hello")]
    private static (bool, string) Greet(string name = "World")
    {
        return (true, $"Hello, {name}!");
    }
}
```

Return `(true, "message")` for success, `(false, "message")` for failure.

### Full form — return `CommandResponse` with structured data

When you need to return structured JSON data for programmatic consumption:

```csharp
using System;
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

public static class MyCommands
{
    [Serializable]
    private sealed class SpawnResult
    {
        public int count;
        public string prefabPath = "";
    }

    [CommandAction("mygame", "spawn", editorOnly: true, runOnMainThread: true,
        summary: "Spawn prefab instances")]
    private static CommandResponse Spawn(string prefabPath, float x = 0, float y = 0, float z = 0, int count = 1)
    {
        if (string.IsNullOrEmpty(prefabPath))
            return CommandResponseFactory.ValidationError("prefabPath is required");

        var prefab = Resources.Load<GameObject>(prefabPath);
        if (prefab == null)
            return CommandResponseFactory.ValidationError($"Prefab not found: {prefabPath}");

        for (var i = 0; i < count; i++)
            UnityEngine.Object.Instantiate(prefab, new Vector3(x, y + i * 2, z), Quaternion.identity);

        return CommandResponseFactory.Ok($"Spawned {count} instance(s)",
            new SpawnResult { count = count, prefabPath = prefabPath });
    }
}
```

## Step 3 — Invoke

From the REPL — both named and positional arguments are supported:

```text
@mygame.greet(name: "Unity")
@mygame.greet("Unity")

@mygame.spawn(prefabPath: "Enemies/Slime", x: 10, count: 3)
@mygame.spawn("Enemies/Slime", 10, 0, 0, 3)
```

Positional arguments are mapped to handler parameters in declaration order. Named and positional arguments can be mixed, but positional arguments fill unmatched parameters left to right.

## Parameter Binding

Handler parameters are bound automatically from JSON args by name. No DTO classes needed.

| Category | Supported Types |
|----------|-----------------|
| Primitives | `string`, `bool`, `int`, `long`, `short`, `byte`, `float`, `double`, `decimal`, `char` |
| Nullable | `int?`, `float?`, `Vector3?`, etc. |
| Enums | Any enum (by name or numeric value) |
| Arrays | `int[]`, `string[]`, `FieldPair[]`, etc. |
| Lists | `List<int>`, `List<string>`, `List<FieldPair>`, etc. |
| Structs / Classes | Any `[Serializable]` type (via `JsonUtility`) |

- **Required** parameters (no default value) produce a validation error if missing
- **Optional** parameters use C# default values: `string name = "default"`, `int count = 1`

## `[CommandAction]` Attribute Reference

```csharp
[CommandAction(
    "namespace",           // Command namespace (required)
    "action",              // Action name (required)
    editorOnly: false,     // true = unavailable on Player builds
    runOnMainThread: false,// true = dispatch to Unity main thread
    summary: ""            // Human-readable description
)]
```

## Return Types

| Return Type | When to Use |
|-------------|-------------|
| `(bool, string)` | Simple commands — `(true, "msg")` success, `(false, "msg")` failure |
| `CommandResponse` | Structured `resultJson` or fine-grained control |

`CommandResponse` helpers:

| Helper | Description |
|--------|-------------|
| `CommandResponseFactory.Ok(summary)` | Success, no data |
| `CommandResponseFactory.Ok(summary, resultJson)` | Success with JSON string |
| `CommandResponseFactory.Ok<T>(summary, result)` | Success with auto-serialized object |
| `CommandResponseFactory.ValidationError(summary)` | Input validation failure |

## Configuring Command Discovery

By default the framework scans all loaded assemblies for `[CommandAction]` attributes. For large projects you can restrict scanning to specific assemblies:

```csharp
using Zh1Zh1.CSharpConsole.Service.Commands.Core;

// Call before ConsoleInitialize()
CommandDiscoveryOptions.Configure(
    new CommandDiscoveryOptions
    {
        assemblyNamePrefixes = new[] { "MyGame", "MyCompany" },
        scanReferencingAssembliesOnly = true,
        includeEditorAssemblies = false
    },
    assemblyFilter: null);

Zh1Zh1.CSharpConsole.RuntimeInitializer.ConsoleInitialize();
```

For finer-grained control, implement `ICommandAssemblyFilter` and pass it as the second argument to `Configure(...)`.

## Batch Execution

The `/batch` endpoint executes multiple commands in a single HTTP request, reducing round-trips for multi-step workflows:

```json
{
  "commands": [
    { "commandNamespace": "gameobject", "action": "create", "argsJson": "{\"name\":\"Player\"}" },
    { "commandNamespace": "component", "action": "add", "argsJson": "{\"path\":\"Player\",\"typeName\":\"Rigidbody\"}" }
  ],
  "stopOnError": true
}
```

Commands execute sequentially. When `stopOnError` is `true`, execution halts on the first failure. The response includes per-command results and overall success/failure counts.
