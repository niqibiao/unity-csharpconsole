# Lite Mode — Roslyn → Expression → BCL Interpreter REPL

> 本文件汇总 `feat/lite-mode` 分支（31 个未推送 commit，~6500 LOC 新增）的设计、实现与端到端验证状态。可行性研究和 spike 实证账本在 `ExpressionInterpreterFeasibility_zh.md`（§3.1/§7.6/§9 是单一事实源），这里只讲生产实现。

---

## 1. 问题

包原本只支持 HybridCLR 路径作为 Player 的执行器（`Runtime/Executor/REPLExecutor.cs:30` 走 `Assembly.Load(assemblyBytes)`），意味着：

- Player 必须装 HybridCLR（IL2CPP 内核魔改），运行时第三方依赖重；
- iOS / 部分严格平台对 `Assembly.Load` 有限制，HybridCLR 不一定可用；
- 包消费方接入门槛高。

目标：保留 HybridCLR Full mode 不动，并行新增一条 **Lite mode** 路径——Player 只依赖 `System.Linq.Expressions.Interpreter`（BCL 的 IL2CPP-safe interpreter）。

## 2. 总体架构

```
                          Editor 14500                              Player 15500
                     ┌──────────────────────┐                ┌──────────────────────┐
        /compile     │                      │   /execute      │                      │
   (REPL ──────────▶ │ Roslyn(SyntaxTree)   │  ──────────▶   │ LiteWireReader       │
    client          │  ↓                    │  bodyBinary    │  ↓                    │
                     │ RoslynToExpression-  │  + typeReg     │ Expression tree       │
                     │  Translator          │  + epoch       │  ↓                    │
                     │  ↓                    │                │ lambda.Compile(       │
                     │ Expression<Func<T>>  │                │  preferInterpretation │
                     │  ↓                    │                │  = true)              │
                     │ LiteWireWriter +     │                │  ↓                    │
                     │ SessionTypeRegistry  │                │ DynamicInvoke         │
                     └──────────────────────┘                └──────────────────────┘
```

Editor 侧承担所有 Roslyn 工作（语法解析、语义分析、表达式翻译、二进制编码 + typeID 注册表分配）。Player 侧只剩**解码 + 解释执行**，无 Roslyn、无 `Assembly.Load`、无 HybridCLR。

## 3. 设计要点

### 3.1 路由分流（HybridCLR / Lite 二选一）

新增 `CompileREPLRequest.executorMode` 字段，三态：
- `""`（默认 / 旧客户端）→ HybridCLR 路径（原行为）
- `"hybridCLR"` → HybridCLR 路径
- `"lite"` → Lite 路径

Editor 在 `ProcessCompileRuntimeREPL` 按 `executorMode == "lite"` 分支；Player 在 `ProcessExecuteRuntimeREPL` 按 `bodyBinary` 非空判定走 `ProcessLiteExecute`。两路径完全隔离，老客户端零回归。

### 3.2 自动模式探测

- Player 启动时 `DetectPlayerExecutorMode()` 反射扫程序集找 `HybridCLR.RuntimeApi`：找到 → `"hybridCLR"`；找不到 → `"lite"`（LiteREPLExecutor 已 ship，可作为保底）。
- Python REPL 启动时 `_refresh_executor_mode()` 调 **player URL** `/health`（不是 editor URL — runtime 模式下两者不同 host），缓存 `playerExecutorMode` 字段。
- 客户端每次 `/compile` 把缓存值塞进 `executorMode` payload 字段。

### 3.3 Wire 协议 — Binary tagged-union body + JSON envelope

详见 `ExpressionInterpreterFeasibility_zh.md §3.1 v3`。

| 层 | 编码 | 第三方依赖 |
|---|---|---|
| Envelope（信封） | `JsonUtility`（Unity 内置） | 零 |
| Body（Expression 树） | 手写 binary tagged-union（`LiteWireWriter`/`Reader`） | 零 |
| TypeID 注册表（reflection 重引用压缩） | session 级 `(int id, string AQN)` 双向表 + epoch | 零 |

Wire 实测体积削减 ~97%（B-9：1177B → 37B for pure-expr，3339B → 85B for slot-var）。

### 3.4 Session 状态语义合同

- `Slots: Dictionary<string, object>` — 跨 submission 持久化的 var 值容器，Editor 翻译时把 `var x = 10` 翻成对该字典的写入 Expression，Player 端 LiteREPLExecutor 持有同名字典，通过 `SlotsRef` token 在 wire 上引用同一容器。
- `SessionTypeRegistry` — 单飞（single-flight）不变量：同 session 不并发提交，monotone-allocation；epoch 用来检测 player 重启（player 重启 → 本地 epoch=0，editor 上的 epoch≥1，detect mismatch → `needsResync`）。
- 12 个 fail-fast 错误码覆盖跨 submission 边界（详见 §7.6）。

### 3.5 IL2CPP managed stripping

- 包内 `Runtime/link.xml` 保留 `System.Linq.Expressions`（interpreter 本体）+ `BinaryReader/Writer/MemoryStream/UTF8Encoding`（codec）—— 这些是协议自身的硬需求。
- **Unity API surface（UnityEngine.CoreModule 等）由消费方 link.xml 管**——不同项目用的 API 子集不同，包不应强制 ship 全量。
- 消费方若需访问 `private`/`internal` 字段，需额外在 link.xml 用 `<field name="..." />` 显式 preserve。但 Unity 自身的 `m_InstanceID` 等是 `#if UNITY_EDITOR` 字段，Player 二进制不包含，link.xml 救不回。

### 3.6 Player Update() 不能停

REPL service 在监听线程接收请求，通过 `MainThreadRequestRunner` 把 work 派发到 Unity 主线程执行。如果 Player 窗口失焦、`Application.runInBackground=false`，Update() 暂停，所有 `/execute` 30s 超时。

→ `ConsoleHttpService.InitializeForRuntime` 在 `#if !UNITY_EDITOR` 分支无条件设 `Application.runInBackground = true`。这是 REPL 服务的硬要求，不暴露给消费方决定。

## 4. 文件清单（按层）

### 4.1 Runtime/Lite/（新增，独立目录 + 独立命名空间 `Zh1Zh1.CSharpConsole.Lite`）

| 文件 | LOC | 职责 |
|---|---|---|
| `LiteWireProtocol.cs` | 152 | `NodeKind`(23) / `UnaryOp`(20) / `BinaryOp`(36) / `ValueKind`(16) 枚举 + `PROTOCOL_VERSION = 1` |
| `LiteWireWriter.cs` | 762 | Expression → binary，含 method/conversion/lifted 全保真，varint 编码 typeId |
| `LiteWireReader.cs` | 604 | binary → Expression，`Expression.MakeBinary/MakeUnary` 重载选择，user-defined operator 路径 |
| `SessionTypeRegistry.cs` | 204 | (int id ↔ Type) 双向表 + epoch + delta buffer + `PrepareResync`/`IngestResync`/`DetectEpochMismatch` |
| `LiteWireException.cs` | 21 | 携带 `ErrorCode` 的协议异常，错误码字符串与 `feasibility §7.6` 对齐 |
| `ILiteREPLExecutor.cs` | 38 | Player 侧执行器接口 + `LiteExecuteOutcome` DTO |
| `LiteREPLExecutor.cs` | 150 | Player 侧实现：epoch 校验 → typeReg ingest → reader.ReadRoot → `lambda.Compile(preferInterpretation:true)` → `DynamicInvoke` |
| `ILiteCompiler.cs` | 22 | Editor 侧编译器接口（Runtime 看见的最小契约，避免 Runtime 反向引用 Editor） |
| `LiteContracts.cs` | 36 | `TypeRegEntryDto`（wire DTO，public）+ `LiteExecuteResponseData`（Lite path envelope payload，internal） |

### 4.2 Editor/Compiler/（新增）

| 文件 | LOC | 职责 |
|---|---|---|
| `LiteREPLCompiler.cs` | 3122 | `RoslynToExpressionTranslator` — Roslyn SyntaxNode → System.Linq.Expressions 树；覆盖 C# 1.0–11.0 主流语法；fail-fast 12 个错误码 + `SetIgnoreAccessibility` 反射 hack 对齐 HybridCLR private/internal 访问 |

### 4.3 Runtime/Service/（修改）

| 文件 | 改动 | 职责 |
|---|---|---|
| `ConsoleHttpService.cs` | +242/-22 | (1) `InitializeForEditor` 加 `liteCompilerGenerator` 参数；(2) `ProcessCompileRuntimeREPL` 加 `executorMode == "lite"` 分支调 `CompileAndForwardLiteAsync`；(3) 新增 `PostLiteToPlayerAsync` 解析 `LiteExecuteResponseData`（surface `needsResync` 给 client）；(4) Player 侧 `ProcessExecuteRuntimeREPL` 加 `bodyBinary` 非空分支 → `ProcessLiteExecute`；(5) `InitializeForRuntime` 强制 `Application.runInBackground = true`；(6) `DetectPlayerExecutorMode` 无 HybridCLR 时返 `"lite"` |
| `Contracts/EditorContracts.cs` | +5 | `CompileREPLRequest.executorMode` 字段（默认 ""，向后兼容） |
| `Contracts/ExecutionContracts.cs` | +16 | `ExecuteREPLRequest.bodyBinary / typeReg / registryEpoch` 字段 |
| `Contracts/HealthContracts.cs` | +15 | `playerExecutorMode` 字段（`#if !UNITY_EDITOR` gated） |
| `Internal/ReplServiceRegistry.cs` | +54 | 加 `_liteSessions` per-session 字典（`LiteEditorSession { Compiler, Registry }` POCO）+ `FetchLiteSession/RemoveLiteSession`；`ListSessions` 现在也覆盖 Lite-only sessions |
| `Internal/MainThreadRequestRunner.cs` | +62/-4 | 延迟 driver GameObject 创建到首个 scene 加载完（修了 `[RuntimeInitializeOnLoadMethod(AfterAssembliesLoaded)]` 调用时 `DontDestroyOnLoad` orphan 的 bug）+ 幂等 `sceneLoaded` 订阅 |

### 4.4 Editor/（修改）

| 文件 | 改动 | 职责 |
|---|---|---|
| `EditorInitializer.cs` | +7/-1 | 给 `InitializeForEditor` 注入 `() => new LiteREPLCompiler()` |

### 4.5 Runtime/link.xml（新增）

47 行，preserve `System.Linq.Expressions` + `BinaryReader/Writer/MemoryStream/UTF8Encoding/Encoding` 在 mscorlib + netstandard 两套程序集变体（覆盖 Unity 2022.3 BCL 命名）。

### 4.6 Python REPL（修改）

| 文件 | 改动 | 职责 |
|---|---|---|
| `csharpconsole_core/client_base.py` | +3 | `execute_runtime_request` 加 `executor_mode` kwarg，写入 payload `executorMode` |
| `repl/client.py` | +19 | (1) `execute_runtime_request` 透传 `executor_mode`；(2) 新增 `post_json_to_player` + `request_player_health`（runtime 模式专门探 player URL） |
| `repl/session_ui.py` | +22 | banner/footer 显示 `executor=hybridCLR\|lite` |
| `csharp_repl_core.py` | +51/-1 | (1) `executor_mode` 模块全局；(2) `_refresh_executor_mode` 用 `request_player_health`；(3) `_execute_runtime_request_with_mode` 包裹器把缓存值塞进每次提交 |

### 4.7 Docs~/（新增 / 修改）

| 文件 | 状态 |
|---|---|
| `ExpressionInterpreterFeasibility_zh.md` | 全新（777 行）— 可行性研究 + spike 账本 + protocol 决断 + §9 任务清单 |
| `LiteMode_zh.md` | 本文（新增） |

## 5. 端到端验证状态

| 测试 | 结果 |
|---|---|
| Editor 内 spike B-3..B-12（13 套，252 cases） | 全绿 |
| Windows Standalone IL2CPP Development Player | ✅（curl + Python REPL wire 验证全过） |
| HybridCLR 路径回归 | ✅（未引入回归，老客户端 `executorMode=""` 走原路） |
| `1+2*3` / `var x=10; x+5` / `x*2`（跨 submission slot） | `7 / 15 / 20` |
| `GameObject.Find("X")?.name ?? "none"` | `"none"`（link.xml UnityEngine.CoreModule preserve 救回 81 个方法） |
| `$"{Application.unityVersion} on {Application.platform}"` | `"2022.3.10f1 on WindowsPlayer"` |
| `new Test().m_TestPrivate`（user-defined private 字段） | `10`（验 `SetIgnoreAccessibility` 反射 hack） |
| `class Foo {}` fail-fast | `[E_LITE_TYPE_DECL]` |
| HybridCLR-mode 显式提交（无 executorMode 字段） | `NullHybridExecutor` 抛错（证明分支正确） |
| P1-1 Translator 三类 case · Editor in-process spike | ✅ 15/15 PASS direct + wire-roundtrip（`Editor/Spike/LiteTranslatorP1Spike.cs`） |
| P1-1 Translator 三类 case · Win Standalone IL2CPP Dev Player E2E | ✅ 15/15 PASS 通过 Editor→HTTP→Player 完整链路（2026-05-20；Player 二进制无需重建——新 translator 输出全部复用既有 NodeKind/UnaryOp/BinaryOp 值，向后兼容旧 reader） |
| VisitExpression targetType 中心化收口 · 8 个 type-enforcement gate case（ctor 多参/混合、List<T> init、Dict 复合 init、explicit `new T[]{...}`、params varargs、方法 overload 选择） | ✅ 8/8 PASS direct + wire（spike D 类）；ad-hoc Player IL2CPP 包括 `new Vector2(1,0)` / `new Vector3(1,2.5f,3)` 全过 |
| Win Standalone IL2CPP Dev Build · ManagedStrippingLevel=**High** · 全 23 case (P1-1 15 + D 8) | ✅ 23/23 PASS（2026-05-20；`Build/LiteOnly_StripHigh/`，GameAssembly.dll 44.8MB vs Minimal 50.1MB ~11% 压缩）。证明 `Runtime/link.xml` 覆盖足够：`string.Concat` 各 overload、enum 反射、`Expression.NewArrayInit` element handling、Unity API surface（Vector2/3 ctor、List/Dict 泛型方法）在 High stripping 下全部存活。 |
| **P1-6 性能基线**：同一 hot loop 走三条路径对比（`Editor/Spike/LiteBenchmark.cs`） | ✅ 见下文「性能基线」一节 |
| Android / iOS IL2CPP | ⏳ 待验 |

### 5.1 性能基线 (P1-6)

同一段 hot loop 走三条路径对比，每条 3 次（warm-up 不计），Stopwatch 在 lambda 内部计时（剔除 HTTP/wire/编译开销）。Bench 源代码在 `Editor/Spike/LiteBenchmark.cs`。

| Loop | A: Editor Roslyn (Mono JIT) | B: Lite on Editor Mono | C: Lite on Player IL2CPP (Stripping=High) |
|---|---|---|---|
| Sqrt 累加 1M 迭代 | **9.2 ms** | 123 ms (13× A) | **13.3 ms** (1.4× A) |
| Int 累加 10M 迭代 | **13.8 ms** | ~1400 ms (100× A) | **22 ms** (1.6× A) |

**意外发现 C ≪ B**：BCL interpreter 在 IL2CPP 上比在 Mono 上快 **9-63 倍**。原因：

- A 是 Mono JIT 把用户的 hot loop 直接编译成 native code 然后跑（无 dispatch 开销）
- B 是 Mono JIT 把 `System.Linq.Expressions.Interpreter` 编译成 native code，interpreter 再 walk 用户 Expression 树（每次循环要走节点 dispatch + boxing + 方法调用间接）
- C 是 **IL2CPP 把 `System.Linq.Expressions.Interpreter` 自己 AOT 编译成 native code**，interpreter dispatch loop 本身是 AOT 原生码——内层循环近乎 native 速度

**结论**：Lite mode 在 IL2CPP Player 上**性能可用**，相比 JIT 只慢 1.4-1.6×，REPL 用途绰绰有余。Mono Editor 上跑 Lite 慢一个量级以上，但那条路径只在 spike 验证里用，不上用户面。

**附带**：Stopwatch 在 Stripping=High 下没有被 strip 掉（C 跑得通），`Runtime/link.xml` 不需要补 Stopwatch。

## 6. 已知 P1 后续

- ~~**Translator 边界 case**：`new[]{...}`（ImplicitArrayCreation）、`string + string`（应翻 `string.Concat`）、`enum | enum`（按位或）未支持~~ — **已完成**：commit `72c25f4`（translator）+ `c4b3530`（spike 加 wire-roundtrip）；§5 IL2CPP Player E2E 15/15 PASS。
- ~~`VisitArrayCreation` 显式 `new double[]{1,2,3}` 同 numeric-promotion gap、`new Vector2(1,0)` ctor int→float 不通——同一类 bug~~ — **已完成**：commit `af470d2` 把 `VisitExpression` 改成 raw + targetType 中心化包装层，**所有 `VisitExpression(e, T)` 调用现在保证返回值 `.Type == T`**。spike D 类 8 个 case 回归覆盖，包含 ctor 多参、collection init、explicit array、params、overload 选择。VisitImplicitArrayCreation 与 BindArguments 等的 per-callsite Convert/EnsureType 思路被这次改动取代。
- **完整自动 resync**：当前 player 不支持 ingest resync frame；client 看到 `needsResync` 后只能手动 `:reset`
- **Android/iOS IL2CPP 真机扩验**
- ~~**Release Build（Managed Stripping High）下 link.xml 验证**~~ — **已完成**：2026-05-20 在 LiteOnly Win Standalone IL2CPP Dev Build (Stripping=High) 上跑全 23 case PASS（见 §5）。**注**：纯 Release Build（不带 `DEVELOPMENT_BUILD`）下 `Runtime/Zh1Zh1.CSharpConsole.Runtime.asmdef` 整个被 asmdef 条件编译排除，HTTP service 根本不进 Player——这是按设计（CLAUDE.md 项目说明），不需要验。所以"Release Build"上限就是 Dev Build + High stripping。
- **异常诊断 probe**：NRE stack trace / line number 跨 wire 序列化定位
- ~~**性能基线**：BCL interpreter vs Editor Mono 同负载循环~~ — **已完成**：见 §5.1 性能基线。结论：Lite on Player IL2CPP **比 Editor Roslyn JIT 只慢 1.4-1.6×**，比 Lite on Editor Mono 快 9-63×。Player IL2CPP AOT 编译了 BCL interpreter 本身。Lite 模式对 REPL 用途**性能可用**。

## 7. Commit 列表（31 个，按时间正序）

### 7.1 协议设计与决断（docs）

```
d64dcba  docs: lite-mode (Roslyn -> Expression) feasibility research
41ee888  docs: lite-mode protocol decision — Newtonsoft + typeID registry
73b7192  docs: protocol switch to hand-written binary body (v3, no Newtonsoft)
a8d94d9  docs: tighten typeID registry protocol (codex finding #2)
c3f9cc8  docs: lite protocol scope updates from codex adversarial review (v3.1)
```

### 7.2 Spike 验证（docs，落地 phase B-3..B-8 共 214 cases）

```
b01111a  docs: phase B-3 cross-session DTO end-to-end (14/14)
c352985  docs: phase B-4 ConstantPattern mixed-type fix (180/180 regression)
fcc2230  docs: phase B-5 deadlock fail-fast (188/188 regression)
c231789  docs: phase B-6 session-state fail-fast (201/201 regression)
ee31125  docs: phase B-7 E_LITE_* SyntaxKind fail-fast (214/214 regression)
87c92bb  docs+spike: B-8 translator gap fill + align §7.6 fail-fast table
983283e  docs: mark lite-protocol task 2 done (B-9 25/25 PASS)
```

### 7.3 协议生产实现（7 个任务）

```
5376d53  feat(lite-protocol): NodeKind/UnaryOp/BinaryOp/ValueKind enums (task 1/7)
575e804  feat(lite-protocol): binary Expression codec (task 2/7, Writer+Reader)
3256dde  chore(lite-protocol): expose Writer/Reader/Registry as public API
ec50fda  feat(lite-protocol): SessionTypeRegistry full version (task 4/7)
e47cfc5  feat(lite-protocol): /execute envelope schema for Lite path (task 5/7)
f486201  feat(lite-protocol): LiteREPLExecutor + /execute Lite dispatch (task 6/7)
e1d186b  feat(lite-protocol): managed-stripping link.xml (task 7/7 partial)
```

### 7.4 集成 / 整理

```
86e0096  refactor(lite): consolidate Lite types under Runtime/Lite/ + split LiteContracts
27e3b63  fix(runtime): defer MainThreadRequestRunner driver creation until first scene
5184ba4  feat(lite-compiler): add LiteREPLCompiler (Roslyn -> Expression translator)
774fdb5  feat(lite-compile): wire LiteREPLCompiler to /compile route
```

### 7.5 模式探测与客户端

```
57d4a24  feat(health): expose player executor mode + show in REPL banner/footer
e7cf9c3  fix(health): scope playerExecutorMode to player + never claim "lite"
582691d  fix(health): DetectPlayerExecutorMode returns "lite" instead of ""
bec2e89  feat(repl-client): propagate executorMode into /compile payload
4cd386e  fix(repl-client): probe player URL (not editor) for executor mode
```

### 7.6 E2E 实战发现 + 修复

```
f5edd81  feat(lite-compiler): UnityEngine default + honor defaultUsing
a52826f  fix(runtime): force runInBackground=true on Player REPL init
3cbee7d  fix(lite-compiler): mirror HybridCLR IgnoreAccessibility for private member access
```

### 7.7 /simplify 整理

```
1ae097b  refactor(lite): move LiteCompilerException to Runtime/Lite + simplify dispatch
74adef4  refactor(lite): bundle Lite session state + minor cleanups
```

> 整个分支随后 squash 为 1 个 commit。原始 33 条 commit 的标题在本节作为审计记录。

## 8. 对 Editor / HybridCLR 路径的零回归保证

[[feedback-lite-no-regression]] memory 记录。逐项核对：

| 改动 | 旧路径影响 |
|---|---|
| `executorMode` 字段 default `""` | 旧客户端 / 未声明字段 → JsonUtility 兼容 → Editor 落 `else` 分支走 `RuntimeREPLCompiler + ForwardDllToPlayer`，**零行为变化** |
| `bodyBinary` 字段 default `""` | Player 端 dispatch 按 `bodyBinary` 非空判 Lite；旧客户端 `dllBase64` 路径不动 |
| `ReplServiceRegistry` 加 Lite 字典 | 全新独立集合；HybridCLR 的 `_executors` / `_compilers` 没碰；`ResetSessionState` 同时清理两路状态保持对称 |
| `Application.runInBackground = true` | `#if !UNITY_EDITOR` gated，Editor 不动；对 HybridCLR Player 也只是确保 Update() 不停（HybridCLR 同样依赖主线程 dispatch，有利无害） |
| `DetectPlayerExecutorMode` 返回 `"lite"` 替代 `""` | Python client 容忍任意值；HybridCLR Player 仍优先报 `"hybridCLR"`，只在 HybridCLR 缺失时才落 `"lite"` |
| `MainThreadRequestRunner` defer fix | 没改 driver 行为，只是把 GameObject 创建从「立即」改为「等 scene 加载完」；现有 HybridCLR Player 也受益（其 LiteBootstrap 用 AfterSceneLoad，恰好不触发该 bug，所以是纯防御性修复） |
| `SetIgnoreAccessibility` 在 LiteREPLCompiler | 仅 Lite 路径；HybridCLR `BaseREPLCompiler` 早就有同样反射 hack，行为对齐而非引入新差异 |
