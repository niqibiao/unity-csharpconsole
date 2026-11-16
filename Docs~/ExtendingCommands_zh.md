# 扩展命令

命令框架允许任何项目在不修改本包源码的情况下添加自定义命令。命令采用类似 ASP.NET Minimal API 的设计：声明一个带 `[CommandAction]` 的静态方法，框架自动发现并从 JSON 绑定参数。

从项目程序集自动发现的命令会被归入 `custom` 注册表分区。服务显式注册的包内
handler 会被归入 `builtin`；命令作者不需要、也不能自行选择这个分区。

## 第一步 — 引用 Runtime 程序集

创建一个 asmdef（或使用已有的），引用 `Zh1Zh1.CSharpConsole.Runtime`：

```json
{
  "name": "MyGame.Commands",
  "references": ["Zh1Zh1.CSharpConsole.Runtime"]
}
```

Runtime API 只在 Editor 和 Development Player 中可用。程序集如果也用于正式包，
请把引用 Runtime API 的代码放在 `#if UNITY_EDITOR || DEVELOPMENT_BUILD` 中。

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

### 托管代码裁剪

`[CommandAction]` 继承 `PreserveAttribute`。Unity 处理某个程序集时会保留带此标注
的方法。没有被引用的程序集仍可能被整个移除；用 `typeof` 显式注册 handler 可让
注册代码引用到该程序集。

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
| 原始类型 | `string`、`bool`、有符号/无符号整数、`float`、`double`、`decimal`、`char` |
| 可空类型 | `int?`、`float?`、`Vector3?` 等 |
| 枚举 | 任意枚举（按名称） |
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
    summary: "",          // 人类可读的描述
    resultType: null,     // 返回结构化结果时填写实际 result DTO
    requiresSessionId: false
)]
```

## 可执行 Contract 元数据

Registry 会从 handler signature 与声明的 result DTO 推导参数顺序、wire schema、
required/default 和 result fields。只有反射无法推导的语义才需要 attribute：

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
    // Handler 逻辑。
}
```

`CommandArgument` 支持 `NonEmpty`、`Minimum`、`Maximum`、`AllowedValues` 和
`AllowedValuesIgnoreCase`。`CommandRule` 支持 `ExactlyOneOf`、
`AtMostOneOf`、`AtLeastOneOf`、`AtLeastOneMutation` 和 `RequiresWhen`。
非法元数据会使注册失败；非法请求会在 handler 执行前被拒绝。

Package 使用同一个编译后的 contract 驱动请求 preflight、Registry Snapshot
和 Registry Fingerprint。不要为命令再维护一份只用于文档的 schema。在
递归 result DTO 使用根级 `$defs` 与 `$ref`，因此树形 result 可保持有限
wire 表达，同时不会丢失递归结构。递归 input DTO 会在注册时被拒绝，使请求
preflight 保持有界，同时不引入任意 runtime depth cap。

结构化输入 DTO 会先经过严格递归校验，再交给 `JsonUtility` 实例化：未知、
重复、缺失的 required 字段会被拒绝，数组也会逐项校验。输入 DTO 字段默认
required 且不允许 null；需要可选字段时使用
`[CommandField(Optional = true)]`；reference field 需要接受显式 JSON
`null` 时使用 `[CommandField(AllowNull = true)]`。字段需要非空、范围或
allowed-value 约束时也由同一个 attribute 稀疏声明。Registry 中的 allowed
values 使用 canonical JSON text，因此字符串值包含 JSON 引号。

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

默认只扫描已加载且引用 Runtime 的程序集。大型项目可以显式注册包含自定义命令的
类型，避免在第一次请求时枚举程序集的全部类型：

```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;
using Zh1Zh1.CSharpConsole.Service.Commands.Core;

internal static class MyCommandDiscovery
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
#else
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
    private static void Configure()
    {
        CommandDiscoveryOptions.Configure(new CommandDiscoveryOptions
        {
            handlerTypes = new[] { typeof(MyCommands) }
        });
    }
}
#endif
```

Player 回调要放在 Unity 启动扫描可见的 asmdef 中。Unity 2022 的这一步可能跳过受
`DEVELOPMENT_BUILD` 约束的 asmdef，因此示例在源码中用 `#if`，而不限制 asmdef。

`handlerTypes` 非空时只注册列表中的自定义命令，内置命令照常可用。要扫描程序集，
则将列表留空，通过 `assemblyNamePrefixes`、`scanReferencingAssembliesOnly`、
`includeEditorAssemblies` 或 `ICommandAssemblyFilter` 限制扫描范围。`Configure`
会复制配置和数组；更新配置需要再次调用，下次注册表请求时会重建。

程序集加载后，framework 会在下一次请求时惰性刷新 discovery。它先完整构建并
校验 built-in + custom 候选 registry，再一次性发布；assembly、type、method
的反射遍历顺序不会影响最终 snapshot。任一 custom command 存在非法 metadata、
重复 route 或无法完整加载的 type set 时，整个候选会被拒绝，registry 请求会
明确失败。修复对应 assembly（或 discovery filter）并让 Unity reload 后即可
重新发现；framework 不会暴露依赖反射顺序的 partial custom registry。

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
