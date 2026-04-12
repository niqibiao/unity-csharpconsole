# 扩展命令

命令框架允许任何项目在不修改本包源码的情况下添加自定义命令。命令采用类似 ASP.NET Minimal API 的设计：声明一个带 `[CommandAction]` 的静态方法，框架自动发现并从 JSON 绑定参数。

## 第一步 — 引用 Runtime 程序集

创建一个 asmdef（或使用已有的），引用 `Zh1Zh1.CSharpConsole.Runtime`：

```json
{
  "name": "MyGame.Commands",
  "references": ["Zh1Zh1.CSharpConsole.Runtime"]
}
```

## 第二步 — 编写命令 Handler

### 最简形式 — 返回 `(bool, string)` 元组

最简单的写法，除了 attribute 外不需要引用任何框架类型：

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

返回 `(true, "消息")` 表示成功，`(false, "消息")` 表示失败。

### 完整形式 — 返回 `CommandResponse` 附带结构化数据

需要返回结构化 JSON 数据供程序化消费时使用：

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

    // runOnMainThread 默认为 true，Unity API 调用是安全的。
    [CommandAction("mygame", "spawn", editorOnly: true, summary: "Spawn prefab instances")]
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

## 第三步 — 调用

在 REPL 中 — 支持命名参数和位置参数两种写法：

```text
@mygame.greet(name: "Unity")
@mygame.greet("Unity")

@mygame.spawn(prefabPath: "Enemies/Slime", x: 10, count: 3)
@mygame.spawn("Enemies/Slime", 10, 0, 0, 3)
```

位置参数按声明顺序映射到 handler 参数。命名参数和位置参数可以混用，位置参数从左到右填充未匹配的参数。

## 参数绑定

Handler 参数从 JSON args 按名称自动绑定，不需要 DTO 类。

| 类别 | 支持的类型 |
|------|-----------|
| 原始类型 | `string`、`bool`、`int`、`long`、`short`、`byte`、`float`、`double`、`decimal`、`char` |
| 可空类型 | `int?`、`float?`、`Vector3?` 等 |
| 枚举 | 任意枚举（按名称或数值） |
| 数组 | `int[]`、`string[]`、`FieldPair[]` 等 |
| 列表 | `List<int>`、`List<string>`、`List<FieldPair>` 等 |
| Struct / Class | 任意 `[Serializable]` 类型（通过 `JsonUtility`） |

- **必选**参数（无默认值）缺失时产生校验错误
- **可选**参数使用 C# 默认值：`string name = "default"`、`int count = 1`

## `[CommandAction]` Attribute 参考

```csharp
[CommandAction(
    "namespace",          // 命令命名空间（必填）
    "action",             // Action 名称（必填）
    editorOnly: false,    // true = Player 构建中不可用
    runOnMainThread: true,// 默认 true — 框架自动调度到 Unity 主线程
                          // 仅当 handler 自行管理主线程调度时才设为 false
    summary: ""           // 人类可读的描述
)]
```

## 注入参数

声明 `CommandInvocation` 参数可获取请求元数据。框架自动注入，不将其暴露到命令目录中，也不从 args 中绑定：

```csharp
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

public static class MyCommands
{
    [CommandAction("mygame", "status", summary: "返回包含 session 信息的状态")]
    private static (bool, string) Status(CommandInvocation inv)
    {
        return (true, $"Session: {inv.sessionId}");
    }
}
```

`CommandInvocation` 包含 `commandNamespace`、`action`、`sessionId` 以及原始 `argsJson`。

## Editor 辅助工具

编写 editor 命令时，`CommandHelpers`（位于 `Zh1Zh1.CSharpConsole.Service.Commands.Handlers`）提供与内置 handler 一致的工具方法：

| 方法 | 说明 |
|------|------|
| `CommandHelpers.ResolveGameObject(path, instanceId, out error)` | 通过层级路径或 instanceId 查找场景中的 GameObject |
| `CommandHelpers.FindByPath(path)` | 按路径字符串遍历场景层级 |
| `CommandHelpers.ResolveType(typeName, out error)` | 按名称解析 `Type`，自动尝试 Unity 命名空间前缀 |
| `CommandHelpers.GetHierarchyPath(transform)` | 构建 transform 的完整 `/Parent/Child` 路径 |

```csharp
#if UNITY_EDITOR
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;
using Zh1Zh1.CSharpConsole.Service.Commands.Handlers;
using Zh1Zh1.CSharpConsole.Service.Commands.Routing;

public static class MyEditorCommands
{
    [CommandAction("mygame", "ping", editorOnly: true, summary: "Ping 一个 GameObject")]
    private static (bool, string) Ping(string path = "", int instanceId = 0)
    {
        var go = CommandHelpers.ResolveGameObject(path, instanceId, out var error);
        if (go == null) return (false, error);
        return (true, $"找到: {CommandHelpers.GetHierarchyPath(go.transform)}");
    }
}
#endif
```

## 返回类型

| 返回类型 | 适用场景 |
|---------|---------|
| `(bool, string)` | 简单命令 — `(true, "msg")` 成功，`(false, "msg")` 失败 |
| `CommandResponse` | 需要结构化 `resultJson` 或细粒度控制 |

`CommandResponse` 工具方法：

| 方法 | 说明 |
|------|------|
| `CommandResponseFactory.Ok(summary)` | 成功，无数据 |
| `CommandResponseFactory.Ok(summary, resultJson)` | 成功，附带 JSON 字符串 |
| `CommandResponseFactory.Ok<T>(summary, result)` | 成功，自动序列化对象 |
| `CommandResponseFactory.ValidationError(summary)` | 输入校验失败 |

## 配置命令发现

默认框架会扫描所有已加载程序集中的 `[CommandAction]` attribute。大型项目中可以限制扫描范围：

```csharp
using Zh1Zh1.CSharpConsole.Service.Commands.Core;

// 在 ConsoleInitialize() 之前调用
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

如需更细粒度控制，可实现 `ICommandAssemblyFilter` 并作为第二个参数传入 `Configure(...)`。

## 批量执行

`/batch` 端点支持在单次 HTTP 请求中执行多个命令，减少多步工作流的网络往返：

```json
{
  "commands": [
    { "commandNamespace": "gameobject", "action": "create", "argsJson": "{\"name\":\"Player\"}" },
    { "commandNamespace": "component", "action": "add", "argsJson": "{\"path\":\"Player\",\"typeName\":\"Rigidbody\"}" }
  ],
  "stopOnError": true
}
```

命令按顺序执行。当 `stopOnError` 为 `true` 时，首次失败后停止执行，剩余命令被跳过。响应包含每个命令的结果。`total` 字段反映实际执行的命令数（而非提交数），因此 `succeeded + failed == total` 始终成立。
