# C# Console — Lite Mode (Experimental)

> **Status: Experimental.** Lite mode is functional and verified on Windows Standalone IL2CPP (Minimal and High managed-stripping). Android / iOS IL2CPP have not yet been verified on-device. Treat it as preview-quality and validate on your target platform before relying on it in production.

**Language / 语言**: [English](#english) · [中文](#中文)

---

## English

### What is Lite mode?

Lite mode lets the C# Console run a REPL inside a **player build that does not have HybridCLR installed**. Instead of compiling each submission to a DLL and loading it with `Assembly.Load` (the "Full" / HybridCLR path), Lite mode:

1. Compiles your C# on the **Editor** side using Roslyn,
2. Translates it to a `System.Linq.Expressions` tree,
3. Ships that tree to the **player** as a compact binary,
4. Runs it on the player through the **BCL expression interpreter** (`LambdaExpression.Compile(preferInterpretation: true)`).

The player side depends on **nothing but the .NET base class library** — no HybridCLR, no `Assembly.Load`, no runtime code generation.

### Why use it?

| Concern | Full mode (HybridCLR) | Lite mode |
|---|---|---|
| Player-side dependency | HybridCLR (IL2CPP runtime patch) | None (BCL only) |
| iOS / strict platforms | `Assembly.Load` may be restricted | No `Assembly.Load`; AOT-safe |
| Integration cost for consumers | Install + configure HybridCLR | Drop in the package, no extra middleware |
| Capability ceiling | Full C# (can declare types, etc.) | Expression-shaped C# (see limitations) |

Use Lite when you want a console on a player that **can't or shouldn't** carry HybridCLR. Use Full when you need to declare new types/methods at runtime or want the broadest C# surface.

### How a player chooses its mode

You don't choose explicitly — **the player auto-detects** at startup by reflecting for `HybridCLR.RuntimeApi`:

- HybridCLR present → `hybridCLR` mode
- HybridCLR absent → `lite` mode (the Lite executor always ships with the package, so any player is Lite-capable)

The mode is reported in the player's `/health` response as `playerExecutorMode`. The Editor is always the compile server and never runs Lite itself.

### Setup

1. **Add the package** to your project (UPM). Reference both asmdefs explicitly — they have `autoReferenced: false`.
2. **The runtime service only exists in development builds.** `Runtime/Zh1Zh1.CSharpConsole.Runtime.asmdef` is gated by `DEVELOPMENT_BUILD || UNITY_EDITOR`. Build a **Development Build** if you want the console in a player.
3. **Bootstrap the service on the player.** In a player build the service needs to be started once. Minimal pattern:

   ```csharp
   #if !UNITY_EDITOR
   using UnityEngine;
   using Zh1Zh1.CSharpConsole.Service;
   using Zh1Zh1.CSharpConsole.Interface;

   internal static class ConsoleBootstrap
   {
       [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
       private static void Boot()
       {
           // The REPL service runs request handlers on the main thread, so it
           // needs Update() to keep ticking even when the window is unfocused.
           // runInBackground is a global app setting — YOU own it, the package
           // only warns if it's off. Opt in if you want the console usable while
           // the player is unfocused (typical for remote-driven dev consoles).
           Application.runInBackground = true;

           // The runtimeExecutorGenerator is only used by the HybridCLR path.
           // When HybridCLR isn't present, the dispatcher routes to the Lite
           // executor and never calls this — a throwing stub is fine.
           ConsoleHttpService.InitializeForRuntime(() => new NullHybridExecutor());
       }
   }

   internal sealed class NullHybridExecutor : IREPLExecutor
   {
       public System.Threading.Tasks.Task<object> ExecuteAsync(byte[] assemblyBytes, string scriptClass)
           => throw new System.InvalidOperationException("HybridCLR path disabled; submit a Lite envelope.");
   }
   #endif
   ```

4. **Connect with the REPL client** in runtime mode, pointing at the player and using the Editor as the compile server:

   ```bash
   python "Editor/ExternalTool~/console-client/csharp_repl.py" \
       --mode runtime --ip <player-ip> --port 15500 \
       --compile-ip 127.0.0.1 --compile-port 14500
   ```

   Note the `Application.runInBackground = true` in the bootstrap above: the console's request handlers run on the main thread, so it needs `Update()` ticking while the window is unfocused. This is a global app setting you own — the package does **not** force it; it only logs a warning if it's off.

### What you can write

Lite mode supports the great majority of everyday REPL C# — roughly 90% of submissions behave identically to Full mode:

- Expressions, arithmetic, string interpolation, comparisons, ternary, null-coalescing
- Method calls, member access, indexers, `new`, casts, `is`/`as`, `typeof`/`nameof`
- Lambdas (including statement bodies), `ref`/`out` arguments, `params`, named/optional args
- Control flow: `if`/`while`/`for`/`foreach`/`try-catch-finally`, `switch` expressions & statements
- Collections & generics: `List<T>`, `Dictionary<K,V>`, arrays (incl. multi-dim & jagged), LINQ query and method syntax
- Pattern matching: declaration, constant, relational, property, list, `and`/`or`/`not`
- Tuples, ranges (`..`), index-from-end (`^`)
- **Cross-submission state**: `var x = 10;` in one submission, `x + 5` in the next

> **Note on closures**: A lambda that captures a session variable reads it **dynamically** — at call time, not at capture time. `var x = 10; var f = () => x; x = 20; f()` returns **20** in Lite mode. This matches REPL intuition ("I changed x, so f sees the new x").

### Limitations (fail-fast, with guidance)

These are rejected at compile time with a clear error code and a suggested rewrite. They are **not** silent failures — Lite mode never lets you run code whose data would be quietly corrupted.

| You write… | Error code | Do this instead |
|---|---|---|
| Top-level `class`/`struct`/`record`/`interface`/`enum`/`delegate` | `E_LITE_TYPE_DECL` | Put the type in a regular `.cs` file, or use Full mode |
| Top-level method / local function | `E_LITE_METHOD_DECL` | Use a `Func<...>` / `Action<...>` lambda |
| `yield return` / `yield break` | `E_LITE_ITERATOR` | Use `Enumerable.Range/Select` or build a `List<T>` |
| `await` / `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` | `E_LITE_DEADLOCK_FORBIDDEN` | Use a callback, or `Task.Run` and read the result in a later submission |
| `unsafe` / pointers / `stackalloc` | `E_LITE_UNSAFE` | Not expressible in the expression interpreter |
| `dynamic` | `E_LITE_DYNAMIC` | Use a concrete type |
| `ref` local / `ref struct` local | `E_LITE_REF_LOCAL` | Copy to a normal local |
| Redeclaring a session var with a different type | `E_SESSION_REDECLARE_TYPE_MISMATCH` | Rename, or restart the REPL |
| Passing a session var as `ref`/`out` | `E_SESSION_BYREF_FORBIDDEN` | Copy to a local first, assign back |
| Mutating a field of a value-type session var | `E_SESSION_VALUETYPE_MUTATION` | Reassign the whole struct |

### Session resets on player restart

If the player process restarts (crash, redeploy, manual relaunch), its in-memory session state is gone. The next submission that references an old variable triggers an **automatic session reset**: the console clears both sides' state and returns a notice prefixed `[SESSION_AUTO_RESET]`. Just redeclare your variables and continue — no manual `:reset` needed.

### Performance

Measured on Windows Standalone IL2CPP (Development Build):

| Workload | Editor Roslyn (Mono JIT) | Lite on player (IL2CPP) |
|---|---|---|
| 1M-iteration `Math.Sqrt` loop | ~9 ms | ~13 ms (≈1.4×) |
| 10M-iteration integer sum | ~14 ms | ~22 ms (≈1.6×) |

The BCL interpreter on IL2CPP is AOT-compiled, so it runs only **1.4–1.6× slower than JIT-compiled code** — entirely comfortable for interactive REPL use. Steady-state submission round-trip (compile + wire + execute) is ~30 ms.

### Troubleshooting

- **`executor=hybridCLR` when you expected lite** — your player has HybridCLR linked in. Auto-detect found `HybridCLR.RuntimeApi`. Remove the dependency for a pure-Lite player.
- **Console works in Editor but not in the player** — confirm you built a **Development Build** (the runtime asmdef is stripped from release builds by design).
- **`/execute` times out** — if `Application.runInBackground` is off and the window loses focus, the main thread halts and handlers never run. The package logs a warning at init when it's off; set `runInBackground = true` in your bootstrap (the demo does).
- **A submission fails with `E_LITE_*` / `E_SESSION_*`** — see the limitations table; the message includes the rewrite.

---

## 中文

### 什么是 Lite 模式？

Lite 模式让 C# Console 能在 **没有安装 HybridCLR 的 player 包体** 里跑 REPL。它不走"把每次提交编成 DLL 再 `Assembly.Load`"（即 Full / HybridCLR 路径），而是：

1. 在 **Editor** 侧用 Roslyn 编译你的 C#，
2. 翻译成 `System.Linq.Expressions` 表达式树，
3. 以紧凑 binary 形式发给 **player**，
4. 在 player 上用 **BCL 表达式解释器**（`LambdaExpression.Compile(preferInterpretation: true)`）执行。

Player 侧 **只依赖 .NET 基础类库**——无 HybridCLR、无 `Assembly.Load`、无运行时代码生成。

### 为什么用它？

| 关注点 | Full 模式 (HybridCLR) | Lite 模式 |
|---|---|---|
| Player 侧依赖 | HybridCLR（IL2CPP 运行时魔改） | 无（仅 BCL） |
| iOS / 严格平台 | `Assembly.Load` 可能受限 | 无 `Assembly.Load`，AOT 安全 |
| 消费方接入成本 | 装 + 配置 HybridCLR | 丢进包即可，无额外中间件 |
| 能力上限 | 完整 C#（可声明类型等） | 表达式形态 C#（见限制） |

当你想在一个**不能 / 不该**带 HybridCLR 的 player 上用 console 时，选 Lite。当你需要运行时声明新类型/方法、或想要最广的 C# 表面时，选 Full。

### Player 怎么选模式

你不用显式选——**player 启动时自动探测**，反射查找 `HybridCLR.RuntimeApi`：

- 找到 HybridCLR → `hybridCLR` 模式
- 找不到 → `lite` 模式（Lite 执行器随包发布，任何 player 都具备 Lite 能力）

模式通过 player 的 `/health` 响应里的 `playerExecutorMode` 字段上报。Editor 永远是编译服务器，自己从不跑 Lite。

### 接入步骤

1. **添加包**（UPM）。两个 asmdef 都要显式引用——它们是 `autoReferenced: false`。
2. **运行时服务只存在于 development build。** `Runtime/Zh1Zh1.CSharpConsole.Runtime.asmdef` 受 `DEVELOPMENT_BUILD || UNITY_EDITOR` 条件编译保护。要在 player 里用 console，必须 build **Development Build**。
3. **在 player 启动服务。** Player 包体里需要启动一次服务。最小模式：

   ```csharp
   #if !UNITY_EDITOR
   using UnityEngine;
   using Zh1Zh1.CSharpConsole.Service;
   using Zh1Zh1.CSharpConsole.Interface;

   internal static class ConsoleBootstrap
   {
       [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
       private static void Boot()
       {
           // REPL 服务的请求处理跑在主线程，所以窗口失焦时也需要 Update() 持续 tick。
           // runInBackground 是全局应用设置——由**你**决定，包只在它关着时警告。
           // 如果你想让 console 在 player 失焦时仍可用（远程驱动的开发 console 常见），
           // 就在这里 opt in。
           Application.runInBackground = true;

           // runtimeExecutorGenerator 只被 HybridCLR 路径使用。
           // 没有 HybridCLR 时，dispatcher 路由到 Lite 执行器、永不调用它——
           // 一个抛异常的 stub 就够了。
           ConsoleHttpService.InitializeForRuntime(() => new NullHybridExecutor());
       }
   }

   internal sealed class NullHybridExecutor : IREPLExecutor
   {
       public System.Threading.Tasks.Task<object> ExecuteAsync(byte[] assemblyBytes, string scriptClass)
           => throw new System.InvalidOperationException("HybridCLR 路径已禁用；请提交 Lite envelope。");
   }
   #endif
   ```

4. **用 REPL 客户端连接**，runtime 模式，指向 player、用 Editor 作编译服务器：

   ```bash
   python "Editor/ExternalTool~/console-client/csharp_repl.py" \
       --mode runtime --ip <player-ip> --port 15500 \
       --compile-ip 127.0.0.1 --compile-port 14500
   ```

   注意上面 bootstrap 里的 `Application.runInBackground = true`：console 的请求处理跑在主线程，窗口失焦时需要 `Update()` 持续 tick。这是个你拥有的全局应用设置——包**不强制**它，只在它关着时打一条警告 log。

### 能写什么

Lite 模式支持绝大多数日常 REPL C#——约 90% 的提交与 Full 模式行为完全一致：

- 表达式、算术、字符串插值、比较、三元、空合并
- 方法调用、成员访问、索引器、`new`、cast、`is`/`as`、`typeof`/`nameof`
- Lambda（含语句体）、`ref`/`out` 参数、`params`、命名/可选参数
- 控制流：`if`/`while`/`for`/`foreach`/`try-catch-finally`、`switch` 表达式与语句
- 集合与泛型：`List<T>`、`Dictionary<K,V>`、数组（含多维与交错）、LINQ 查询与方法语法
- 模式匹配：声明、常量、关系、属性、列表、`and`/`or`/`not`
- 元组、范围 (`..`)、从末尾索引 (`^`)
- **跨 submission 状态**：一次提交里 `var x = 10;`，下一次提交里 `x + 5`

> **关于闭包**：捕获 session 变量的 lambda 是**动态读取**——调用时取值，而非捕获时。`var x = 10; var f = () => x; x = 20; f()` 在 Lite 模式下返回 **20**。这符合 REPL 直觉（"我改了 x，f 就该看到新 x"）。

### 限制（fail-fast，附引导）

以下形态在编译期被拒绝，附明确错误码 + 改写建议。它们**不是**静默失败——Lite 模式绝不让你跑那些数据会被悄悄损坏的代码。

| 你写… | 错误码 | 改用 |
|---|---|---|
| 顶级 `class`/`struct`/`record`/`interface`/`enum`/`delegate` | `E_LITE_TYPE_DECL` | 把类型放进正常 `.cs` 文件，或用 Full 模式 |
| 顶级方法 / 局部函数 | `E_LITE_METHOD_DECL` | 用 `Func<...>` / `Action<...>` lambda |
| `yield return` / `yield break` | `E_LITE_ITERATOR` | 用 `Enumerable.Range/Select` 或构造 `List<T>` |
| `await` / `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` | `E_LITE_DEADLOCK_FORBIDDEN` | 用回调，或 `Task.Run` 后在后续提交读结果 |
| `unsafe` / 指针 / `stackalloc` | `E_LITE_UNSAFE` | 表达式解释器无法表达 |
| `dynamic` | `E_LITE_DYNAMIC` | 用具体类型 |
| `ref` 局部 / `ref struct` 局部 | `E_LITE_REF_LOCAL` | 复制到普通局部变量 |
| 用不同类型重声明 session 变量 | `E_SESSION_REDECLARE_TYPE_MISMATCH` | 改名，或重启 REPL |
| session 变量作 `ref`/`out` 实参 | `E_SESSION_BYREF_FORBIDDEN` | 先复制到局部，再赋回 |
| 修改值类型 session 变量的字段 | `E_SESSION_VALUETYPE_MUTATION` | 整体重新赋值结构体 |

### Player 重启时 session 重置

如果 player 进程重启（崩溃、重新部署、手动重启），它内存里的 session 状态就没了。下一次引用旧变量的提交会触发**自动 session 重置**：console 清掉双边状态，返回一条 `[SESSION_AUTO_RESET]` 前缀的提示。重新声明变量继续即可——不需要手动 `:reset`。

### 性能

在 Windows Standalone IL2CPP (Development Build) 上实测：

| 负载 | Editor Roslyn (Mono JIT) | Lite on player (IL2CPP) |
|---|---|---|
| 100 万次 `Math.Sqrt` 循环 | ~9 ms | ~13 ms（≈1.4×） |
| 1000 万次整数累加 | ~14 ms | ~22 ms（≈1.6×） |

IL2CPP 上的 BCL 解释器是 AOT 编译的，所以只比 JIT 编译的代码**慢 1.4–1.6 倍**——对交互式 REPL 完全够用。稳态提交往返（编译 + wire + 执行）约 30 ms。

### 排错

- **预期 lite 却显示 `executor=hybridCLR`** —— 你的 player 链入了 HybridCLR，自动探测找到了 `HybridCLR.RuntimeApi`。要纯 Lite player，移除该依赖。
- **Console 在 Editor 能用、player 里不行** —— 确认你 build 的是 **Development Build**（运行时 asmdef 在 release build 里被设计性地剥离）。
- **`/execute` 超时** —— 如果 `Application.runInBackground` 是关的、窗口又失焦，主线程会停摆、handler 永不执行。包在 init 时若发现它关着会打警告 log；在你的 bootstrap 里设 `runInBackground = true`（demo 已经这么做）。
- **提交报 `E_LITE_*` / `E_SESSION_*`** —— 见限制表；错误信息里带改写建议。
