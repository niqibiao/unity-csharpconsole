# CSharp Console

[English](README.md) | 中文

为 Unity Editor 和 Runtime 提供交互式 C# REPL、命令框架与远程执行能力，基于 Roslyn。

## 相关项目

- [unity-cli-plugin](https://github.com/niqibiao/unity-cli-plugin) — 非交互式 CLI，连接同一 CSharp Console HTTP 服务，面向脚本和自动化场景。

## 功能

- **交互式 REPL** — 基于 Roslyn 的连续脚本提交，支持会话状态保持
- **Top-level 语法** — 直接写语句，不需要 `class` / `Main` 样板代码
- **语义补全** — 来自 Roslyn 编译器的成员、命名空间、类型补全
- **命令框架** — 可扩展的 `[CommandAction]` 命令，自动参数绑定
- **远程 Runtime 执行** — Editor 编译，Player 执行（IL2CPP 通过 HybridCLR）
- **跨提交状态保持** — 变量、`using` 指令、submission state 在执行间保留
- **访问私有成员** — 编译阶段绕过访问修饰符，可直接访问 `private` / `protected` / `internal` 成员

### 1. 即时求值 — 无需 class，无需 Main，直接写代码

```csharp
DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
```

![即时求值](Docs~/images/repl-1.png)

### 2. 跨提交状态保持 — 变量在后续提交中存活

```csharp
var cam = Camera.main; cam.transform.position
```

![跨提交状态](Docs~/images/repl-2.png)

### 3. 访问私有成员 — 编译时绕过访问修饰符

```csharp
var go = GameObject.Find("Main Camera");
go.m_InstanceID
```

![私有成员访问](Docs~/images/repl-3.png)

### 4. LINQ 查询运行中的场景对象

```csharp
string.Join(", ", UnityEngine.Object.FindObjectsOfType<Rigidbody>().Select(x => x.name))
```

![LINQ 查询](Docs~/images/repl-4.png)

### 5. 命令表达式 — 直接调用服务端命令

```csharp
@editor.status()
```

![命令表达式](Docs~/images/repl-5.png)

## 安装

通过 `Packages/manifest.json` 添加：

```json
{
  "dependencies": {
    "com.zh1zh1.csharpconsole": "https://github.com/niqibiao/unity-csharpconsole.git"
  }
}
```

或克隆仓库后作为本地包引用：

```json
{
  "dependencies": {
    "com.zh1zh1.csharpconsole": "file:../com.zh1zh1.csharpconsole"
  }
}
```

> **注意：** 两个 asmdef 均设置了 `autoReferenced: false`。如果你的代码需要引用本包，请在 asmdef 中显式添加 `Zh1Zh1.CSharpConsole.Runtime`（或 `.Editor`）引用。

## 快速开始

### Editor

Editor 侧服务通过 `[InitializeOnLoadMethod]` 自动启动，无需手动初始化。

从 Unity 菜单打开 REPL：

- **Console -> C#Console** — 连接本地 Editor
- **Console -> RemoteC#Console** — 连接远程 Editor 或 Player

### Runtime

在初始化路径中调用一次即可启用远程控制台：

```csharp
#if DEVELOPMENT_BUILD || UNITY_EDITOR
Zh1Zh1.CSharpConsole.RuntimeInitializer.ConsoleInitialize();
#endif
```

Runtime 程序集受 `DEVELOPMENT_BUILD || UNITY_EDITOR` 条件编译约束。所有引用 `Zh1Zh1.CSharpConsole` 的代码都必须包在相同的 `#if` 中，否则非 development build 会编译失败。

Runtime 服务默认监听 `15500` 端口（Editor 使用 `14500`）。端口被占用时会自动递增。

### HybridCLR

Runtime 执行依赖 HybridCLR 提供的 IL2CPP `Assembly.Load` 支持。项目已接入 HybridCLR 的话无需额外配置。

## REPL 使用

### 启动

推荐通过上面的 Unity 菜单启动。也可以直接运行：

```bash
# 自动发现运行中的 Unity Editor 并选择
python "Editor/ExternalTool~/console-client/csharp_repl.py"

# 连接指定 Editor
python "Editor/ExternalTool~/console-client/csharp_repl.py" --editor --ip 127.0.0.1 --port 14500

# 连接 Runtime Player（Editor 作为编译服务器）
python "Editor/ExternalTool~/console-client/csharp_repl.py" \
  --mode runtime --ip 127.0.0.1 --port 15500 \
  --compile-ip 127.0.0.1 --compile-port 14500
```

Python 依赖（`requests`、`prompt_toolkit`、`Pygments`）在首次启动时自动安装。

### 交互

- **Enter** — 提交当前输入
- **Ctrl+Enter** — 插入换行，不提交
- **Tab** — 接受选中的补全候选
- **Ctrl+R** — 反向历史搜索
- **Ctrl+C** — 清空输入（输入为空时进入退出确认）

输入时自动触发补全。工具栏右侧显示语义补全状态：开启 (`●`) 或关闭 (`○`)。

### 内置命令

| 命令 | 说明 |
| --- | --- |
| `/completion <0\|1>` | 切换语义补全：`0` 关闭，`1` 开启 |
| `/using` | 显示默认 using 文件路径和编辑说明 |
| `/define` | 显示默认预处理宏文件路径和编辑说明 |
| `/reload` | 重新加载默认 using / define |
| `/reset` | 重置 REPL 会话 |
| `/clear` | 清空终端 |
| `/dofile <path>` | 执行本地 `.cs` 文件 |

### 命令表达式

除了 C# 代码和内置命令外，REPL 还支持顶层命令表达式，直接调用服务端命令框架：

```text
@project.scene.open(scenePath: "Assets/Scenes/SampleScene.unity", mode: "single")
@editor.status()
@session.inspect(sessionId: "session-1")
```

- 命令表达式以 `@` 开头，路由到 `/command` 端点，不经过 Roslyn 编译
- Tab 补全支持命令名（来自服务端 catalog）和参数名

## 扩展命令

命令框架允许任何项目在不修改本包源码的情况下添加自定义命令。命令采用类似 ASP.NET minimal API 的设计：声明一个带 `[CommandAction]` 的静态方法，框架自动发现并从 JSON 绑定参数。

### 第一步 — 引用 Runtime 程序集

创建一个 asmdef（或使用已有的），引用 `Zh1Zh1.CSharpConsole.Runtime`：

```json
{
  "name": "MyGame.Commands",
  "references": ["Zh1Zh1.CSharpConsole.Runtime"]
}
```

### 第二步 — 编写命令 Handler

#### 最简形式 — 返回 `(bool, string)` 元组

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

#### 完整形式 — 返回 `CommandResponse` 附带结构化数据

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

        return CommandResponseFactory.Ok($"Spawned {count} instance(s)", new SpawnResult { count = count, prefabPath = prefabPath });
    }
}
```

### 第三步 — 调用

在 REPL 中：

```text
@mygame.greet(name: "Unity")
@mygame.spawn(prefabPath: "Enemies/Slime", x: 10, count: 3)
```

### 参数绑定

Handler 参数从 JSON args 按名称自动绑定，不需要 DTO 类。

| 类别 | 支持的类型 |
|------|-----------|
| 原始类型 | `string`、`bool`、`int`、`long`、`short`、`byte`、`float`、`double`、`decimal`、`char` |
| 可空类型 | `int?`、`float?`、`Vector3?` 等 |
| 枚举 | 任意枚举类型（按名称或数值） |
| 数组 | `int[]`、`string[]`、`FieldPair[]` 等 |
| Struct/Class | 任意 `[Serializable]` 类型（通过 `JsonUtility` 反序列化） |

- **必选**参数（无默认值）缺失时产生校验错误
- **可选**参数使用 C# 默认值：`string name = "default"`、`int count = 1`

### `[CommandAction]` Attribute 参考

```csharp
[CommandAction(
    "namespace",           // 命令命名空间（必填）
    "action",              // Action 名称（必填）
    editorOnly: false,     // true = Player 构建中不可用
    runOnMainThread: false,// true = 在 Unity 主线程执行
    summary: "",           // 人类可读的描述
    supportsCliInvocation: true,
    supportsStructuredInvocation: true,
    supportsAgentInvocation: false,
    limitations: ""
)]
```

### 返回类型

| 返回类型 | 适用场景 |
|---------|---------|
| `(bool, string)` | 简单命令 — `(true, "msg")` 成功，`(false, "msg")` 失败 |
| `CommandResponse` | 需要结构化 `resultJson` 或细粒度控制时 |

`CommandResponse` 工具方法：

| 方法 | 说明 |
|------|------|
| `CommandResponseFactory.Ok(summary)` | 成功，无结果数据 |
| `CommandResponseFactory.Ok(summary, resultJson)` | 成功，附带 JSON 字符串 |
| `CommandResponseFactory.Ok<T>(summary, result)` | 成功，自动序列化结果对象 |
| `CommandResponseFactory.ValidationError(summary)` | 输入校验失败 |

### 配置命令发现

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

## 环境要求

- **Unity** 2022.3 或更高版本
- **Python** 3（系统 PATH 可访问）
- **Windows Terminal**（可选 — 不可用时回退到直接启动 Python）

## 第三方声明

本包在 `Editor/Plugins/` 下捆绑了 Roslyn 编译器程序集和 dnlib。完整归属和许可信息见 [ThirdPartyNotices.md](ThirdPartyNotices.md)。

## 许可证

[Apache License 2.0](LICENSE)
