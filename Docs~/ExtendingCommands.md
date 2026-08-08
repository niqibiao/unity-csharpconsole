# Extending Commands

The command framework lets any project add custom commands without modifying the package source. Commands use an ASP.NET Minimal API-style design: declare a static method with `[CommandAction]`, and the framework automatically discovers it and binds parameters from JSON.

Commands discovered from project assemblies are classified as `custom` registry
contracts automatically. Package-owned handlers registered by the service are
classified as `builtin`; command authors do not select this partition.

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

    // runOnMainThread defaults to true — Unity API calls are safe.
    [CommandAction(
        "mygame",
        "spawn",
        editorOnly: true,
        summary: "Spawn prefab instances",
        resultType: typeof(SpawnResult))]
    private static CommandResponse Spawn(
        [CommandArgument(NonEmpty = true)] string prefabPath,
        float x = 0,
        float y = 0,
        float z = 0,
        [CommandArgument(Minimum = 1)] int count = 1)
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
| Primitives | `string`, `bool`, signed/unsigned integer types, `float`, `double`, `decimal`, `char` |
| Nullable | `int?`, `float?`, `Vector3?`, etc. |
| Enums | Any enum, by name |
| Arrays | `int[]`, `string[]`, `FieldPair[]`, etc. |
| Lists | `List<int>`, `List<string>`, `List<FieldPair>`, etc. |
| Structs / Classes | Any `[Serializable]` type (via `JsonUtility`) |

- **Required** parameters (no default value) produce a validation error if missing
- **Optional** parameters use C# default values: `string name = "default"`, `int count = 1`

## `[CommandAction]` Attribute Reference

```csharp
[CommandAction(
    "namespace",          // Command namespace (required)
    "action",             // Action name (required)
    editorOnly: false,    // true = unavailable on Player builds
    runOnMainThread: true,// default true — the framework dispatches to the Unity main thread
                          // Set false only when the handler self-dispatches internally
    summary: "",          // Human-readable description
    resultType: null,     // Actual structured result DTO, when one is returned
    requiresSessionId: false
)]
```

## Executable Contract Metadata

The registry derives parameter order, wire schemas, required/default values, and
result fields from the handler signature and declared result DTO. Add attributes
only for semantics reflection cannot infer:

```csharp
[CommandAction(
    "mygame",
    "inspect",
    editorOnly: true,
    resultType: typeof(InspectResult))]
[CommandRule(
    CommandRuleKind.ExactlyOneOf,
    "path",
    "instanceId")]
private static CommandResponse Inspect(
    [CommandArgument(NonEmpty = true)] string typeName,
    string path = "",
    int instanceId = 0,
    [CommandArgument(Minimum = 0)] int index = 0)
{
    // Handler logic.
}
```

`CommandArgument` supports `NonEmpty`, `Minimum`, `Maximum`, `AllowedValues`, and
`AllowedValuesIgnoreCase`. `CommandRule` supports `ExactlyOneOf`, `AtMostOneOf`,
`AtLeastOneOf`, `AtLeastOneMutation`, and `RequiresWhen`. Invalid metadata rejects
registration; invalid requests are rejected before the handler is invoked.

The package uses the same compiled contract for request preflight, Registry
Snapshots, and Registry Fingerprints. Do not maintain a separate documentation-only
schema for a command. Recursive result DTOs use root-scoped `$defs` and `$ref`
entries, so tree-shaped results remain finite without losing their recursive shape.
Recursive input DTOs are rejected at registration; this keeps request preflight
bounded without an arbitrary runtime depth cap.

Structured input DTOs are validated strictly before `JsonUtility` materializes
them: unknown, duplicate, and missing required fields are rejected, and arrays are
validated item by item. Input DTO fields are required and non-null by default.
Use `[CommandField(Optional = true)]` for an optional field and
`[CommandField(AllowNull = true)]` when a reference field accepts explicit JSON
`null`, or add the same sparse `NonEmpty`, range, and allowed-value metadata when
the field needs it. Allowed values in Registry contracts are canonical JSON texts,
including quotes for string values.

## Injected Parameters

Declare a `CommandInvocation` parameter to receive request metadata. The framework injects it automatically and does not expose it in the command catalog or bind it from args:

```csharp
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

public static class MyCommands
{
    [CommandAction("mygame", "status", summary: "Return session-aware status")]
    private static (bool, string) Status(CommandInvocation inv)
    {
        return (true, $"Session: {inv.sessionId}");
    }
}
```

`CommandInvocation` exposes `commandNamespace`, `action`, `sessionId`, and the raw `argsJson`.

## Editor Helper Utilities

When writing editor commands, `CommandHelpers` (in `Zh1Zh1.CSharpConsole.Service.Commands.Handlers`) provides utilities that match the built-in handler conventions:

| Method | Description |
|--------|-------------|
| `CommandHelpers.ResolveGameObject(path, instanceId, out error)` | Find a scene GameObject by hierarchy path or instance ID |
| `CommandHelpers.FindByPath(path)` | Walk the scene hierarchy by path string |
| `CommandHelpers.ResolveType(typeName, out error)` | Resolve a `Type` by name, with Unity namespace fallbacks |
| `CommandHelpers.GetHierarchyPath(transform)` | Build the full `/Parent/Child` path for a transform |

```csharp
#if UNITY_EDITOR
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Handlers;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

public static class MyEditorCommands
{
    [CommandAction("mygame", "ping", editorOnly: true, summary: "Ping a GameObject")]
    private static (bool, string) Ping(string path = "", int instanceId = 0)
    {
        var go = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
        if (go == null) return (false, error);
        return (true, $"Found: {CommandHelpers.GetHierarchyPath(go.transform)}");
    }
}
#endif
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
`Configure` copies the option values and prefix array. Mutating the original options
object later has no effect; call `Configure` again to publish a new configuration.
An `ICommandAssemblyFilter` must remain deterministic for that configuration
lifetime. If its state changes, call `Configure` again so the registry is invalidated.

Discovery is refreshed lazily after an assembly is loaded. The framework builds and
validates the complete built-in + custom candidate registry before publishing it;
assembly, type, and method traversal order does not affect the resulting snapshot.
If any custom command has invalid metadata, a duplicate route, or an incompletely
loadable type set, the candidate is rejected as a whole and registry requests fail
explicitly. Fix the offending assembly (or discovery filter) and let Unity reload it;
the framework never exposes a reflection-order-dependent partial custom registry.

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

Commands execute sequentially. When `stopOnError` is `true`, execution halts on the first failure and remaining commands are skipped. The response includes per-command results. The `total` field reflects the number of commands actually executed (not the number submitted), so `succeeded + failed == total` always holds.
