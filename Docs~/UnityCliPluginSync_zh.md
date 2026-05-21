# unity-cli-plugin 同步说明（feat/lite-mode 分支）

> **目的**：把 `feat/lite-mode` 分支上所有可能影响 unity-cli-plugin（消费方 CLI / Python client）的协议层与行为层改动集中整理，交对方按需同步。本文件由包仓维护方在分支落地时同步更新；CLI 侧自行决定支持节奏。
>
> **覆盖分支**：`feat/lite-mode`（13 commits 自 `main`，2026-05-14 至 2026-05-20）
> **协议版本**：`ProtocolVersion = 1`（**未 bump**——所有新字段 default-empty，**老客户端零回归**）
> **包版本**：`PackageVersion = 1.4.2`（release 时会 bump）

---

## TL;DR

新增**一种执行模式 `Lite`**，并行于原 HybridCLR 路径。客户端**完全不改也能继续用 HybridCLR**；但要让消费方在没装 HybridCLR 的 Player 上跑 REPL，CLI 必须：

1. 启动后调 `/health`，读 **新字段 `playerExecutorMode`**（仅 Player 响应里有，`"hybridCLR"` / `"lite"`）
2. `/compile` 和 `/completion` 请求把 `playerExecutorMode` 透传到 **新字段 `executorMode`**
3. `/execute` 响应识别 **新 envelope 数据形态 `LiteExecuteResponseData`** 和 **新文本前缀 `[SESSION_AUTO_RESET]`**

老 client（不识别这些字段）在 HybridCLR Player 上一切正常；接到 Lite Player 会**报老的 HybridCLR 错误**（`Assembly.Load` 路径失败），但不会崩。

---

## 1. 概念变更：双执行模式

| 模式 | Player 依赖 | 执行路径 | 触发条件 |
|---|---|---|---|
| `hybridCLR`（原有） | HybridCLR 中间件 | Editor compile DLL → Player `Assembly.Load` → 反射调用 | Player /health 报 `playerExecutorMode=hybridCLR`；客户端 `/compile` 传 `executorMode=""` 或 `"hybridCLR"` |
| `lite`（新增） | 无外部依赖 | Editor Roslyn→Expression 树→手写 binary 编码 → Player BCL interpreter (`lambda.Compile(preferInterpretation:true)`) | Player /health 报 `playerExecutorMode=lite`；客户端 `/compile` 传 `executorMode="lite"` |

**Player 模式由 Player 自身决定**（启动时反射扫 `HybridCLR.RuntimeApi` 类型存在与否）；客户端**不能**强制让 Lite Player 走 HybridCLR 或反之。

**Editor 永不使用 Lite**——Editor 端的提交始终走原 Roslyn + `Assembly.Load` 路径；Lite 只服务跨进程 runtime 场景。

---

## 2. HTTP 协议契约变更

所有改动在 `Runtime/Service/Contracts/` 和 `Runtime/Lite/LiteContracts.cs`。

### 2.1 `/health` 响应：新增 `playerExecutorMode`

**文件**：`Runtime/Service/Contracts/HealthContracts.cs`

```csharp
[Serializable]
internal class HealthResponse
{
    // ... 原有字段不变 ...

#if !UNITY_EDITOR
    public string playerExecutorMode = "";  // "hybridCLR" | "lite"
#endif
}
```

**关键语义**：
- **互斥单态字段**：值要么 `"hybridCLR"` 要么 `"lite"`，**不会同时存在 hybridClrAvailable + mode 两个字段**。客户端不要试图加这种 boolean。
- **Player-only**：字段只出现在 Player 的 /health 响应里（`#if !UNITY_EDITOR` 守护）。**Editor /health 整个字段缺失**——不是空字符串。客户端解析时要容忍字段不存在。
- **检测一次性**：Player 启动时 `LiteREPLExecutor` 反射扫一次 HybridCLR 类型，结果缓存到 `static readonly`。运行期不会变。
- **客户端应做的**：runtime 模式下，启动时**调 Player URL 的 /health**（而不是 Editor 的 /health——runtime 模式下两者不同 host:port），缓存 `playerExecutorMode`，在每次 `/compile` 和 `/completion` 时塞进 `executorMode` 字段。

### 2.2 `/compile` 请求：`CompileREPLRequest.executorMode`

**文件**：`Runtime/Service/Contracts/EditorContracts.cs`

```csharp
[Serializable]
internal class CompileREPLRequest
{
    // ... 原有字段不变 ...
    public string executorMode = "";  // "" | "hybridCLR" | "lite"
}
```

**路由规则**（Editor 端 `ConsoleHttpService.ProcessCompileRuntimeREPL`）：
- `""` 或 `"hybridCLR"` → 走 `RuntimeREPLCompiler` + `Assembly.Load` 路径（**原行为**）
- `"lite"` → 走 `LiteREPLCompiler` + binary body 路径

**back-compat 不可破**：default 空字符串 **必须**继续路由到 HybridCLR。任何让老 client（不带这个字段）误进 Lite 的改动都是 regression。

### 2.3 `/completion` 请求：`CompletionRequest.executorMode`

**文件**：`Runtime/Service/Contracts/EditorContracts.cs`

```csharp
[Serializable]
internal class CompletionRequest
{
    // ... 原有字段不变 ...
    public string executorMode = "";  // "" | "hybridCLR" | "lite"
}
```

**路由规则**（Editor 端 `CompletionEndpointHandler`）：
- `"lite"` → 拿 Lite session 的 `IREPLCompletionProvider`（看 Lite session 里 `var x = 10` 这些跨 submission 声明）
- 其他 → 走原 HybridCLR-flavored `IREPLCompletionProvider`（**原行为**）

**Lite 客户端不传 `executorMode=lite` 的后果**：completion 会落到 HybridCLR session，但 **Lite 用户的跨 submission 声明的 var 在那里看不到**——表现是补全列表少了用户自己的变量。功能可用但不完整。

### 2.4 `/execute` 请求：3 个 Lite 字段

**文件**：`Runtime/Service/Contracts/ExecutionContracts.cs`

```csharp
[Serializable]
internal class ExecuteREPLRequest
{
    public string uuid = "";
    public bool reset;

    // HybridCLR 路径（原有）
    public string dllBase64 = "";
    public string className = "";

    // Lite 路径（新增）
    public string bodyBinary = "";
    public TypeRegEntryDto[] typeReg = Array.Empty<TypeRegEntryDto>();
    public int registryEpoch;
}
```

**Player 端 dispatch 规则**（`ConsoleHttpService.ProcessExecuteRuntimeREPL`）：
- `bodyBinary` 非空 → 走 Lite 路径（`LiteREPLExecutor`）
- 否则 → 走 HybridCLR 路径（**原行为**）

**客户端注意**：通常 CLI 不**直接**构造 `/execute` 请求——Editor 是 compile server，会代客户端构造并 POST 给 Player。CLI 只对接 `/compile`。但若 CLI 有任何直连 Player 的代码（直发 `/execute`），需要识别这两条路径。

### 2.5 `/execute` 响应：新 envelope 数据形态 `LiteExecuteResponseData`

**文件**：`Runtime/Lite/LiteContracts.cs`

Editor 端把 Player Lite 路径的 `/execute` 响应包成 `HttpResponseEnvelope.dataJson`，JSON shape：

```csharp
[Serializable]
internal class LiteExecuteResponseData
{
    public string result = "";         // 表达式结果的字符串形式
    public string errorCode = "";      // 结构化错误码（见 §3），成功时空
    public bool needsResync;           // 表态：Player 检测到 typeReg / epoch 失同步
    public int serverEpoch;            // Player 当前 epoch（Editor 用来判 player 重启）
}
```

**与原 `TextResponseData` 的区别**：原 HybridCLR 路径只回 `text` 一个字段；Lite 加了 4 个结构化字段（错误码、resync 信号、epoch）。**`HttpResponseEnvelope.type` 字段会标记是哪种**——但目前 Editor 端会把 needsResync 转成 P1-2 auto-reset 文本前缀返回给 client（见 §3.2），所以 CLI 实际上**不需要**直接反序列化 `LiteExecuteResponseData`，只需要识别文本前缀。

---

## 3. 新错误码 / response marker

### 3.1 结构化错误码（出现在 envelope text / errorCode 字段）

| 错误码 | 含义 | 谁返回 |
|---|---|---|
| `E_LITE_TYPE_DECL` | 顶级 class/struct/record/interface/enum/delegate 声明 | Editor compile |
| `E_LITE_METHOD_DECL` | 顶级方法 / LocalFunction | Editor compile |
| `E_LITE_ITERATOR` | yield return/break | Editor compile |
| `E_LITE_DEADLOCK_FORBIDDEN` | await / .Result / .Wait() / .GetAwaiter().GetResult() | Editor compile |
| `E_LITE_UNSAFE` | unsafe / pointer / stackalloc | Editor compile |
| `E_LITE_DYNAMIC` | dynamic 类型 | Editor compile |
| `E_LITE_REF_LOCAL` | ref local / ref struct 局部 | Editor compile |
| `E_LITE_CONSTANT_NONSCALAR` | 不可序列化的 ConstantExpression（非 scalar/Type/Enum/SlotsRef） | Editor compile |
| `E_SESSION_REDECLARE_DUPLICATE` | 跨 submission 同名同类型重声明 | Editor compile |
| `E_SESSION_REDECLARE_TYPE_MISMATCH` | 同名变量第二次声明类型不同 | Editor compile |
| `E_SESSION_BYREF_FORBIDDEN` | session slot 作 ref/out 实参 | Editor compile |
| `E_SESSION_VALUETYPE_MUTATION` | 值类型 slot 的字段/属性 setter | Editor compile |
| `E_SESSION_SHADOWING` | 嵌套 block 里局部声明 shadow 上层 session 变量 | Editor compile |
| `E_TYPEREG_UNKNOWN_ID` | Player 解码 body 时引用了 Player 注册表里没有的 typeId | Player runtime |
| `E_TYPEREG_CONFLICT` | 同 id 在两次 envelope 里映射到不同 AQN | Player runtime |
| `E_TYPEREG_EPOCH_MISMATCH` | envelope epoch ≠ Player local epoch | Player runtime |
| `E_TYPEREG_RESYNC_UNRESOLVABLE` | resync 帧含 Player 解析不到的 AQN | Player runtime |
| `E_TYPEREG_NULL_TYPE` / `E_TYPEREG_NULL_RESYNC` | 协议层内部错误 | Player runtime |
| `E_LITE_EMPTY_BODY` | bodyBinary 为空但路由到 Lite | Player runtime |

CLI 解析建议：**字符串前缀 `[ERROR_CODE]` 在 text 字段最前面**（如 `[E_LITE_TYPE_DECL] ...`）。客户端可以用 regex `^\[E_[A-Z_]+\]` 识别。

### 3.2 文本前缀 marker（P1-2 auto-reset）

| 前缀 | 含义 | CLI 建议处理 |
|---|---|---|
| `[SESSION_AUTO_RESET]` | Editor 检测到 Player 重启（epoch mismatch 或 unknown typeId），已**双边重置 session state**。本次提交未执行；用户需要重新声明之前的 var 然后再提交 | 渲染为提示色（蓝/黄），告诉用户 "Session was reset by Player restart, please redeclare and resubmit"。**不要**当成普通 runtime error 红色显示 |

P1-2 设计完整细节见 `LiteMode_zh.md §6 P1-2 已完成` 段，简短版：
- needsResync = 双边 session 状态丢失（Player 重启是常见触发）
- Editor 端清掉 `LiteREPLCompiler` 的 Roslyn chain + SlotTypes + Slots，替换 `SessionTypeRegistry` 为全新实例
- 当前提交**不重试**（之前的 var 已失效）；client 看到 marker 后引导用户重声明

---

## 4. 不变 / 保留兼容的部分

- `ProtocolVersion` 仍是 `1`，**未 bump**。
- `HttpResponseEnvelope` shape 不变（`ok` / `stage` / `type` / `summary` / `sessionId` / `dataJson` 字段集）。
- 所有 `[CommandAction]` 命令不变（没新增 / 删除 / 改签名）。
- `/command` / `/completion` 端点 URL 不变；`/execute` URL 不变。
- HybridCLR 路径所有字段不变；HybridCLR Player 行为不变。
- Editor 模式（非 runtime mode）所有行为不变——Lite 不影响 in-Editor REPL。

---

## 5. 建议的 unity-cli-plugin 改动 TODO（按优先级）

### P0（让 Lite Player 能跑通基本流程）

- [ ] `cs.py exec --mode runtime`：
  - 在初次连接时调 **Player URL** 的 `/health`（不是 Editor 的），缓存 `playerExecutorMode`
  - 把缓存值塞到 `/compile` 请求的 `executorMode` 字段
- [ ] 客户端识别响应文本前缀 `[SESSION_AUTO_RESET]`，渲染为提示而非 error
- [ ] 客户端识别 `[E_LITE_*]` / `[E_SESSION_*]` / `[E_TYPEREG_*]` 错误码，提取友好显示

### P1（让 Lite 用户体验完整）

- [ ] `cs.py complete --mode runtime`：同样把 `playerExecutorMode` 塞 `executorMode` 字段——否则补全里看不到 Lite session 的跨 submission var
- [ ] REPL banner/footer 显示当前 `executorMode`（hybridCLR / lite），方便用户感知
- [ ] `cs.py health --verbose` 输出包含 `playerExecutorMode` 字段

### P2（健壮性）

- [ ] 处理 `/health` 字段缺失（Editor 响应里没有 `playerExecutorMode`）的兼容路径
- [ ] 处理 Player 重启场景：客户端如果想做自动重试，需要在收到 `[SESSION_AUTO_RESET]` 后清空本地"已声明 var"提示缓存（如果有）

---

## 6. 验证与参考

- 包侧 Lite mode 完整实证状态：`Docs~/LiteMode_zh.md §5 端到端验证状态`
- Lite 协议设计源头：`Docs~/ExpressionInterpreterFeasibility_zh.md §3.1`
- Session 状态语义合同：`Docs~/ExpressionInterpreterFeasibility_zh.md §7.6`
- 包侧 reference 实现（Python REPL 客户端已经做了 §5 列出的所有 P0+P1 改动，可参考代码）：
  - `Editor/ExternalTool~/console-client/csharpconsole_core/client_base.py`
  - `Editor/ExternalTool~/console-client/repl/client.py`
  - `Editor/ExternalTool~/console-client/repl/session_ui.py`
  - `Editor/ExternalTool~/console-client/csharp_repl_core.py`

包侧 REPL 客户端可以作为 CLI 集成的**参考实现**——同样的核心库（`csharpconsole_core/`）在两边都使用，差别只在交互 UX 层。

---

## 7. 联系 / 沟通

- 任何契约疑问、行为不一致的发现：在包仓提 issue 或 PR 标 `unity-cli-plugin-sync`
- 这份文档**变更应当伴随包仓提交**——如果未来新增 / 修改字段，先 update 本文档再提交
- 本文档生成时点：见 git log of this file
