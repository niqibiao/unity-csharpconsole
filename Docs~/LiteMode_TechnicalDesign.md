# Lite Mode — Technical Design & Implementation / 技术实现方案

> A self-contained technical write-up of how Lite mode works, suitable for sharing with engineers who don't have the repository open. Each section pairs an English and a Chinese explanation; code excerpts (language-neutral) are shown once.
>
> 一份自包含的 Lite 模式技术说明，适合分享给没有打开仓库的工程师。每节英中双语对照；代码片段（语言中立）只展示一次。

**Contents / 目录**: [1. Problem](#1-problem--问题) · [2. Architecture](#2-architecture--架构) · [3. Data flow](#3-data-flow--数据流) · [4. Wire protocol](#4-wire-protocol--wire-协议) · [5. Translator](#5-translator--翻译器) · [6. Session state](#6-session-state--session-状态) · [7. Type registry & epoch](#7-type-registry--epoch--类型注册表与-epoch) · [8. Player executor](#8-player-executor--player-执行器) · [9. IL2CPP stripping](#9-il2cpp-stripping--il2cpp-裁剪) · [10. Performance](#10-performance--性能) · [11. Design decisions](#11-design-decisions--设计决策) · [12. File map](#12-file-map--文件地图)

---

## 1. Problem / 问题

**EN** — The console originally executed REPL submissions on a player by compiling each one to a DLL on the Editor and loading it on the player with `Assembly.Load`. That requires HybridCLR (an IL2CPP runtime patch) on the player, which is heavy, restricted on some platforms (notably iOS), and a real integration cost for package consumers. The goal: a parallel execution path whose player side depends on **nothing but the .NET BCL** — no `Assembly.Load`, no runtime codegen, AOT-safe — while keeping the existing HybridCLR path untouched.

**中文** —— Console 原本在 player 上执行 REPL 提交的方式是：每次提交在 Editor 编成 DLL，在 player 用 `Assembly.Load` 加载。这要求 player 装 HybridCLR（IL2CPP 运行时魔改），重、在某些平台（尤其 iOS）受限、对包消费方是实打实的接入成本。目标：并行新增一条执行路径，player 侧**只依赖 .NET BCL**——无 `Assembly.Load`、无运行时代码生成、AOT 安全——同时保留原 HybridCLR 路径不动。

---

## 2. Architecture / 架构

**EN** — Lite splits compile and execute across two processes. The **Editor** does all the Roslyn work (parse, semantic analysis, translation to a `System.Linq.Expressions` tree, binary encoding, type-id allocation). The **player** does only decode + interpret. The player never sees Roslyn, never calls `Assembly.Load`, never needs HybridCLR.

**中文** —— Lite 把编译和执行拆到两个进程。**Editor** 做全部 Roslyn 工作（解析、语义分析、翻译成 `System.Linq.Expressions` 树、binary 编码、type-id 分配）。**player** 只做解码 + 解释执行。Player 永远见不到 Roslyn、不调 `Assembly.Load`、不需要 HybridCLR。

```
                Editor (compile server, :14500)              Player (:15500)
              ┌──────────────────────────────────┐        ┌────────────────────────────┐
   /compile   │  Roslyn SyntaxTree + SemanticModel│        │                            │
 (REPL ─────▶ │            ↓                      │/execute│  LiteWireReader            │
  client)     │  RoslynToExpressionTranslator     │ ─────▶ │   ↓ (bodyBinary+typeReg)   │
              │            ↓                      │bodyBin │  Expression tree            │
              │  Expression<Func<object>>         │+typeReg│   ↓                        │
              │            ↓                      │+epoch  │  lambda.Compile(            │
              │  LiteWireWriter + SessionTypeReg  │        │   preferInterpretation:true)│
              │            ↓ binary body          │        │   ↓                        │
              │  JSON envelope (typeReg delta)    │ ◀───── │  invoke → result/errorCode  │
              └──────────────────────────────────┘ result └────────────────────────────┘
```

**EN** — The player auto-detects its mode at startup by reflecting for `HybridCLR.RuntimeApi`; absence ⇒ Lite. The result is cached in a `static readonly` and surfaced via `/health`:

**中文** —— Player 启动时反射查找 `HybridCLR.RuntimeApi` 自动探测模式；不存在 ⇒ Lite。结果缓存到 `static readonly`，通过 `/health` 上报：

```csharp
// ConsoleHttpService.cs
private static readonly string s_PlayerExecutorMode = DetectPlayerExecutorMode();

private static string DetectPlayerExecutorMode()
{
    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
    {
        Type t = null;
        try { t = asm.GetType("HybridCLR.RuntimeApi", throwOnError: false); }
        catch { /* assembly load issues — keep scanning */ }
        if (t != null) return "hybridCLR";
    }
    return "lite"; // LiteREPLExecutor ships with the package
}
```

---

## 3. Data flow / 数据流

**EN** — One submission, end to end:

1. Client probes the player's `/health`, reads `playerExecutorMode`, and stamps `executorMode` onto every `/compile` request.
2. Editor's `ProcessCompileRuntimeREPL` branches on `executorMode == "lite"`.
3. Editor compiles **without committing session state** (two-phase commit, §6), serializes the lambda to a binary body, drains the new type-id entries as a delta.
4. Editor POSTs `/execute` to the player with `bodyBinary` + `typeReg` (delta) + `registryEpoch`.
5. Player decodes, interprets, returns result (or `needsResync` / `errorCode`).
6. **Only if the player confirms success** does the Editor commit the submission (advance the Roslyn chain, promote slot types).

**中文** —— 一次提交的端到端流程：

1. 客户端探测 player 的 `/health`，读 `playerExecutorMode`，把 `executorMode` 盖到每个 `/compile` 请求上。
2. Editor 的 `ProcessCompileRuntimeREPL` 按 `executorMode == "lite"` 分支。
3. Editor **不提交 session 状态** 地编译（两阶段提交，见 §6），把 lambda 序列化成 binary body，把新分配的 type-id 条目作为 delta 取出。
4. Editor 把 `bodyBinary` + `typeReg`(delta) + `registryEpoch` POST 到 player 的 `/execute`。
5. Player 解码、解释、返回结果（或 `needsResync` / `errorCode`）。
6. **只有 player 确认成功**，Editor 才提交本次提交（推进 Roslyn 链、提升 slot 类型）。

```csharp
// ConsoleHttpService.ProcessCompileRuntimeREPL (Lite branch, simplified)
prepared = session.Compiler.PrepareSubmission(code, defaultUsing); // build, don't commit
var writer = new LiteWireWriter(session.Registry, session.Compiler.Slots);
bodyBytes = writer.WriteRoot(prepared.Lambda);
var delta  = session.Registry.FlushDelta();      // new (id, AQN) since last submission
// ... POST /execute with bodyBytes + delta + epoch ...
var (text, transportOk, executeOk, needsResync) = await PostLiteToPlayerAsync(ip, port, request);
if (transportOk && executeOk)      prepared.Commit();          // success → promote state
else if (!transportOk)             session.Registry.BumpEpoch();
else if (needsResync)            { session.ResetState(); return "[SESSION_AUTO_RESET] ..."; }
```

---

## 4. Wire protocol / wire 协议

**EN** — Two layers, both zero-dependency:

- **Envelope** — flat JSON via Unity's built-in `JsonUtility` (request id, session id, `registryEpoch`, `typeReg` delta array, base64 `bodyBinary`). Flat fields `JsonUtility` handles fine.
- **Body** — the Expression tree itself, encoded as a **hand-written binary tagged-union** (`LiteWireWriter` / `LiteWireReader`). Every node is `[NodeKind: byte][payload…]`; integers are varint (`Write7BitEncodedInt`), strings are length-prefixed UTF-8, floats are IEEE-754 little-endian. No `Reflection.Emit`, no third-party serializer — all BCL primitives, fully IL2CPP-safe.

The enum authority is `LiteWireProtocol.cs`: `NodeKind` (23 values), `UnaryOp` (20), `BinaryOp` (36), `ValueKind` (16), and `PROTOCOL_VERSION = 1`. Enum values are append-only and never reuse retired numbers, so they don't drift across BCL versions.

**中文** —— 两层，都零依赖：

- **Envelope（信封）** —— 扁平 JSON，走 Unity 内置 `JsonUtility`（请求 id、session id、`registryEpoch`、`typeReg` delta 数组、base64 `bodyBinary`）。扁平字段 `JsonUtility` 够用。
- **Body（树体）** —— Expression 树本身，编码成 **手写 binary tagged-union**（`LiteWireWriter` / `LiteWireReader`）。每个节点是 `[NodeKind: byte][payload…]`；整数走 varint（`Write7BitEncodedInt`），字符串走 length-prefixed UTF-8，浮点走 IEEE-754 little-endian。无 `Reflection.Emit`、无第三方序列化器——全是 BCL 原语，完全 IL2CPP 安全。

枚举权威定义在 `LiteWireProtocol.cs`：`NodeKind`（23 个）、`UnaryOp`（20）、`BinaryOp`（36）、`ValueKind`（16）、`PROTOCOL_VERSION = 1`。枚举值 append-only、绝不复用废弃编号，所以跨 BCL 版本不漂移。

```
// Representative node encodings (actual byte values per LiteWireProtocol.cs)
Constant:  [NodeKind=Constant][typeId: varint][valueKind: byte][value: payload]
Parameter: [NodeKind=Parameter][paramId: varint]
SlotsRef:  [NodeKind=SlotsRef][slotTypeId: varint]          // session slot dictionary anchor
Call:      [NodeKind=Call][methodToken][hasInstance: bool][instance?][argCount][args…]
Binary:    [NodeKind=Binary][BinaryOp: byte][left][right][hasMethod: bool][methodToken?]
methodToken = [declTypeId][methodName: string][isStatic: bool][argTypeIds…][genericArgIds…]
```

**EN** — Measured wire size after the type-id registry compression (next section): a pure-expression submission shrank from ~1.2 KB (JSON+AQN) to ~37 bytes; a slot-variable submission from ~3.3 KB to ~85 bytes (~97% reduction).

**中文** —— 叠加 type-id 注册表压缩（下一节）后的实测体积：纯表达式提交从 ~1.2 KB（JSON+AQN）降到 ~37 字节；slot 变量提交从 ~3.3 KB 降到 ~85 字节（~97% 削减）。

---

## 5. Translator / 翻译器

**EN** — `RoslynToExpressionTranslator` (in `LiteREPLCompiler.cs`) walks the Roslyn syntax tree and emits a `System.Linq.Expressions` tree. Two design points worth highlighting:

**(a) A single `targetType` chokepoint.** `VisitExpression(expr, targetType)` wraps a raw visitor and enforces one invariant: *if a caller passes a non-null `targetType`, the returned expression's `.Type` is exactly that type.* Roslyn's literal visitor doesn't honor target context (a literal `1` stays `Int32`), so without this, feeding `1` into a `float` parameter slot would make the downstream BCL factory reject. Centralizing the `Expression.Convert` here closes the entire bug class (constructor args, array initializers, params arrays, collection-init `Add`, method overload promotion) in one place rather than per call site.

**(b) Fail-fast over silent corruption.** Forms whose Lite semantics can't be guaranteed equal to Roslyn-script semantics are rejected at translation time with a structured error code and a rewrite suggestion — never executed. See the limitations table in `LiteMode_Feature.md`.

**中文** —— `RoslynToExpressionTranslator`（在 `LiteREPLCompiler.cs` 里）遍历 Roslyn 语法树、产出 `System.Linq.Expressions` 树。两个值得强调的设计点：

**(a) 单一 `targetType` 收口点。** `VisitExpression(expr, targetType)` 包裹一个 raw visitor，强制一条不变量：*调用方传非空 `targetType` 时，返回表达式的 `.Type` 一定就是该类型。* Roslyn 的字面量 visitor 不认 target 上下文（字面量 `1` 永远是 `Int32`），所以没有这层收口，把 `1` 喂进 `float` 参数位会让下游 BCL factory 拒绝。在这里集中插入 `Expression.Convert`，一处就关掉整个 bug 类（ctor 参数、数组初始化、params 数组、collection-init `Add`、方法重载提升），而不用每个 call site 各自处理。

**(b) Fail-fast 优于静默损坏。** 那些 Lite 语义无法保证与 Roslyn-script 语义相等的形态，在翻译期就用结构化错误码 + 改写建议拒绝——绝不执行。见 `LiteMode_Feature.md` 的限制表。

```csharp
// The chokepoint: every VisitExpression(e, T) call gets a result whose .Type == T.
private Expression VisitExpression(ExpressionSyntax expr, Type targetType)
{
    var raw = VisitExpressionRaw(expr, targetType);
    if (targetType != null && raw != null && raw.Type != targetType)
        return Expression.Convert(raw, targetType);
    return raw;
}
```

---

## 6. Session state / Session 状态

**EN** — Cross-submission state (e.g. `var x = 10;` then `x + 5`) is held in a `Dictionary<string, object>` slot store, **not** in Roslyn's `<Factory>(object[])` parameters. The translator turns `var x = 10` into a write to that dictionary, and a later read of `x` into a dictionary lookup. The dictionary is embedded into the Expression tree as a `SlotsRef` token (rather than a raw `ConstantExpression`), and the writer detects it by `ReferenceEquals` against the session's slots instance.

A lambda capturing a slot reads it **dynamically** at call time (`var x=10; var f=()=>x; x=20; f()` ⇒ 20) — this is a deliberate decision matching REPL intuition; slot type immutability (guarded by `E_SESSION_REDECLARE_TYPE_MISMATCH`) means there's no cast-crash risk.

**Two-phase commit.** Because compile (Editor) and execute (player) are in different processes, the Editor must not advance its session state until the player confirms success — otherwise a transport failure or player-side error leaves the Editor's slot-type table and Roslyn chain ahead of the player's slot dictionary. So `PrepareSubmission` builds the lambda without committing; `Commit()` (promote slot types + advance Roslyn chain) runs only after the player returns success.

**中文** —— 跨 submission 状态（如 `var x = 10;` 后 `x + 5`）保存在一个 `Dictionary<string, object>` slot 容器里，**而非** Roslyn 的 `<Factory>(object[])` 参数。翻译器把 `var x = 10` 翻成对该字典的写入，把后续读 `x` 翻成字典查找。字典作为 `SlotsRef` token（而非裸 `ConstantExpression`）嵌入 Expression 树，writer 通过对 session slots 实例的 `ReferenceEquals` 识别它。

捕获 slot 的 lambda 在调用时**动态读取**（`var x=10; var f=()=>x; x=20; f()` ⇒ 20）——这是刻意决策，符合 REPL 直觉；slot 类型不变性（由 `E_SESSION_REDECLARE_TYPE_MISMATCH` 守住）意味着没有 cast 崩溃风险。

**两阶段提交。** 因为编译（Editor）和执行（player）在不同进程，Editor 在 player 确认成功前不能推进自己的 session 状态——否则一次传输失败或 player 侧错误会让 Editor 的 slot 类型表和 Roslyn 链跑到 player slot 字典前面。所以 `PrepareSubmission` 构建 lambda 但不提交；`Commit()`（提升 slot 类型 + 推进 Roslyn 链）只在 player 返回成功后运行。

```csharp
public interface IPreparedLiteSubmission
{
    Expression<System.Func<object>> Lambda { get; }
    void Commit();   // promote slot types + advance Roslyn chain; idempotent
}
```

---

## 7. Type registry & epoch / 类型注册表与 epoch

**EN** — `SessionTypeRegistry` maps each closed reflectable `Type` to a small int id (allocated monotonically from 1; `List<int>` gets one id, not two). Instead of shipping `AssemblyQualifiedName` strings (which dominated wire size), each submission ships only the **new** ids as a delta. Both processes hold a copy; the player ingests the delta before decoding the body.

An **epoch** is the consistency version. Steady state keeps both sides at the same epoch. A player restart wipes the player's registry (and its slot dictionary) — detected when the player either sees an epoch mismatch or resolves an id it doesn't know (`E_TYPEREG_UNKNOWN_ID`), at which point it returns `needsResync`. Because a real restart also loses the slot values (which the registry can't restore), the recovery is a **double-sided hard reset** rather than a registry-only resync: the Editor clears its Roslyn chain + slot tables + registry and returns `[SESSION_AUTO_RESET]`; the user redeclares and continues.

**中文** —— `SessionTypeRegistry` 把每个闭合可反射 `Type` 映射到一个小 int id（从 1 起单调分配；`List<int>` 占一个 id，不拆两个）。它不发 `AssemblyQualifiedName` 字符串（这是 wire 体积大头），每次提交只把**新**分配的 id 作为 delta 发出。两个进程各持一份；player 在解码 body 前先 ingest delta。

**epoch** 是一致性版本号。稳态下双边 epoch 相同。Player 重启会清空 player 的注册表（和它的 slot 字典）——当 player 检测到 epoch 不匹配、或解析到不认识的 id（`E_TYPEREG_UNKNOWN_ID`）时检出，此时返回 `needsResync`。因为真实重启同时丢失了 slot 值（注册表救不回来），恢复策略是**双边硬重置**而非仅注册表 resync：Editor 清掉 Roslyn 链 + slot 表 + 注册表，返回 `[SESSION_AUTO_RESET]`；用户重新声明继续。

```csharp
public int GetOrRegister(Type t) {            // writer side: allocate or look up
    if (m_TypeToId.TryGetValue(t, out var id)) return id;
    id = m_NextId++;
    m_TypeToId[t] = id; m_IdToType[id] = t;
    m_DeltaBuffer.Add(new TypeRegEntry(id, t.AssemblyQualifiedName));
    return id;
}
public Type Resolve(int id) {                 // reader side: throws E_TYPEREG_UNKNOWN_ID
    if (!m_IdToType.TryGetValue(id, out var t))
        throw new LiteWireException("E_TYPEREG_UNKNOWN_ID", $"typeId {id} not present");
    return t;
}
public bool DetectEpochMismatch(int envelopeEpoch) => envelopeEpoch != m_Epoch;
```

---

## 8. Player executor / Player 执行器

**EN** — The whole player side of execution is `LiteREPLExecutor.ExecuteAsync`. It is deliberately small: epoch check → ingest typeReg delta → decode → compile (interpreted) → invoke. Note the **synchronous invocation** — it must run on the main thread (the HTTP layer already dispatches it there via `MainThreadRequestRunner`) and must not wrap with `Task.Run`, or a synchronous caller doing `GetAwaiter().GetResult()` would deadlock against the captured main-thread `SynchronizationContext`.

**中文** —— Player 侧执行的全部就是 `LiteREPLExecutor.ExecuteAsync`。它刻意保持精简：epoch 校验 → ingest typeReg delta → 解码 → 编译（解释模式）→ 调用。注意**同步调用**——它必须在主线程跑（HTTP 层已经通过 `MainThreadRequestRunner` 派发到主线程），且不能用 `Task.Run` 包装，否则同步调用方做 `GetAwaiter().GetResult()` 会和捕获的主线程 `SynchronizationContext` 死锁。

```csharp
// LiteREPLExecutor.ExecuteAsync — core path (error branches elided)
if (m_TypeReg.DetectEpochMismatch(envelopeEpoch))
    return new LiteExecuteOutcome { ErrorCode = "E_TYPEREG_EPOCH_MISMATCH", NeedsResync = true, ServerEpoch = m_TypeReg.Epoch };

foreach (var entry in typeRegDelta)
    m_TypeReg.Register(entry.id, Type.GetType(entry.aqn, throwOnError: false));

var reader = new LiteWireReader(m_TypeReg, m_Slots);
var lambda = (LambdaExpression)reader.ReadRoot(bodyBinary);
var compiled = lambda.Compile(preferInterpretation: true);   // BCL interpreter, AOT-safe
var resultObj = compiled is Func<object> f ? f() : compiled.DynamicInvoke();
return new LiteExecuteOutcome { Result = resultObj?.ToString() ?? "", ServerEpoch = m_TypeReg.Epoch };
```

---

## 9. IL2CPP stripping / IL2CPP 裁剪

**EN** — IL2CPP managed stripping walks the IL reference graph and drops anything unreachable. Two risks for Lite:

1. **`System.Linq.Expressions.Interpreter`** is internal to the BCL and only reached at runtime through `Compile(preferInterpretation: true)`. Static analysis sees `Compile()` but not every interpreter instruction subclass. If one is stripped, the interpreter falls back to the light compiler, then fails on AOT with "Operation is not supported on this platform."
2. **Codec primitives** (`BinaryReader`/`BinaryWriter`/`MemoryStream`/`UTF8Encoding`) — directly referenced, but preserved explicitly as a belt-and-suspenders guard.

The package ships `Runtime/link.xml` preserving `System.Linq.Expressions` in full plus those codec primitives across both `mscorlib` and `netstandard` assembly variants. **Unity API surface is the consumer's responsibility** — different projects reference different API subsets, so the package doesn't ship a blanket preserve for `UnityEngine.*`. Verified on Windows Standalone IL2CPP at **Managed Stripping High** with the full test suite passing.

**中文** —— IL2CPP 托管裁剪遍历 IL 引用图、丢弃不可达的东西。对 Lite 有两个风险：

1. **`System.Linq.Expressions.Interpreter`** 是 BCL 内部类型，只在运行时通过 `Compile(preferInterpretation: true)` 触达。静态分析看得到 `Compile()`，但触达不到每个 interpreter 指令子类。一旦某个被裁掉，interpreter 退化到 light compiler，然后在 AOT 上报 "Operation is not supported on this platform"。
2. **编解码原语**（`BinaryReader`/`BinaryWriter`/`MemoryStream`/`UTF8Encoding`）——直接引用，但作为双保险显式保留。

包内 `Runtime/link.xml` 全量保留 `System.Linq.Expressions` 加上述编解码原语，覆盖 `mscorlib` 和 `netstandard` 两套程序集变体。**Unity API 表面由消费方负责**——不同项目引用不同 API 子集，所以包不为 `UnityEngine.*` 做全量保留。已在 Windows Standalone IL2CPP **Managed Stripping High** 下验证全套测试通过。

---

## 10. Performance / 性能

**EN** — Same hot loop, three execution paths, in-band `Stopwatch` timing (excludes HTTP/wire):

**中文** —— 同一 hot loop，三条执行路径，`Stopwatch` 内部计时（剔除 HTTP/wire）：

| Workload / 负载 | Editor Roslyn (Mono JIT) | Lite on Editor Mono | Lite on player IL2CPP |
|---|---|---|---|
| 1M `Math.Sqrt` | ~9 ms | ~123 ms | ~13 ms |
| 10M int sum | ~14 ms | ~1400 ms | ~22 ms |

**EN** — The counterintuitive result is that the interpreter is **far faster on IL2CPP than on Editor Mono** (≈9–63×). The reason: IL2CPP AOT-compiles the interpreter's dispatch loop itself to native code, so each per-instruction dispatch is native; on Editor Mono the interpreter is Mono-JIT'd with more abstraction overhead. Net: Lite on a player runs only ~1.4–1.6× slower than JIT'd Roslyn — fine for interactive use.

**中文** —— 反直觉的结果是 interpreter 在 **IL2CPP 上远比 Editor Mono 快**（≈9–63×）。原因：IL2CPP 把 interpreter 的 dispatch loop 本身 AOT 编译成 native 码，每条指令 dispatch 都是 native；Editor Mono 上 interpreter 是 Mono-JIT 出来的、抽象 overhead 更多。净结果：Lite 在 player 上只比 JIT 出来的 Roslyn 慢 ~1.4–1.6×——交互用途完全够。

---

## 11. Design decisions / 设计决策

**EN**

- **Binary body, not JSON.** `JsonUtility` can't do polymorphic serialization (the Expression DTO is a 30+ node-type polymorphic tree), and adding Newtonsoft would introduce a UPM dependency the team explicitly didn't want at the protocol layer. A hand-written tagged-union is ~950 LOC of mechanical encode/decode, zero dependency, IL2CPP-safe, and ~97% smaller on the wire.
- **Interpreter, not codegen.** `Compile()` (without `preferInterpretation`) uses `Reflection.Emit`, banned under IL2CPP AOT. `preferInterpretation: true` forces the BCL interpreter, which AOT-compiles cleanly.
- **Auto-reset, not protocol auto-resync.** The "ingest a full-registry resync frame" handshake only helps when the type registry desynced *but the slot values survived*. Under single-flight + `AfterSceneLoad` boot + monotone allocation, that essentially never happens — the dominant trigger is player restart, which loses slots too. So recovery is a clean double-sided reset, not a partial resync that would fail at the first slot access anyway.
- **Editor never runs Lite.** The Editor has Mono + full Roslyn; running its own submissions through the slower interpreter would be pure downside. Lite exists only for the cross-process player case.

**中文**

- **Binary body 而非 JSON。** `JsonUtility` 做不了多态序列化（Expression DTO 是 30+ 节点类型的多态树），引入 Newtonsoft 又会在协议层带进团队明确不要的 UPM 依赖。手写 tagged-union 是 ~950 LOC 的机械编解码，零依赖、IL2CPP 安全、wire 体积小 ~97%。
- **解释器而非代码生成。** `Compile()`（不带 `preferInterpretation`）用 `Reflection.Emit`，IL2CPP AOT 下被禁。`preferInterpretation: true` 强制用 BCL interpreter，AOT 编译干净。
- **Auto-reset 而非协议级 auto-resync。** "ingest 全量注册表 resync 帧"的握手只在类型注册表失同步*但 slot 值还活着*时有用。在单飞 + `AfterSceneLoad` boot + 单调分配下，这基本不发生——主要触发是 player 重启，连 slot 也丢了。所以恢复是干净的双边重置，而非那种一访问 slot 就失败的半 resync。
- **Editor 永不跑 Lite。** Editor 有 Mono + 完整 Roslyn；让它自己的提交走更慢的 interpreter 纯属亏。Lite 只为跨进程 player 场景存在。

---

## 12. File map / 文件地图

| File / 文件 | Role / 职责 |
|---|---|
| `Runtime/Lite/LiteWireProtocol.cs` | NodeKind / UnaryOp / BinaryOp / ValueKind enums + `PROTOCOL_VERSION` |
| `Runtime/Lite/LiteWireWriter.cs` | Expression tree → binary (full method/conversion/lifted fidelity) |
| `Runtime/Lite/LiteWireReader.cs` | binary → Expression tree (`MakeBinary`/`MakeUnary` overload selection) |
| `Runtime/Lite/SessionTypeRegistry.cs` | (id ↔ Type) bidirectional table + epoch + delta + resync |
| `Runtime/Lite/LiteREPLExecutor.cs` | Player-side: decode + `Compile(preferInterpretation:true)` + invoke |
| `Runtime/Lite/ILiteCompiler.cs` | Editor-compiler abstraction (keeps Runtime asm Roslyn-free) |
| `Runtime/Lite/LiteContracts.cs` | `TypeRegEntryDto` (wire) + `LiteExecuteResponseData` (envelope payload) |
| `Runtime/Lite/LiteWireException.cs` | Carries structured `ErrorCode` |
| `Editor/Compiler/LiteREPLCompiler.cs` | `RoslynToExpressionTranslator` + completion provider |
| `Editor/Compiler/ReplCompletionEngine.cs` | Shared Roslyn completion engine (both compilers) |
| `Runtime/Service/ConsoleHttpService.cs` | Routing: `executorMode` branch, two-phase commit, auto-reset, mode detect |
| `Runtime/link.xml` | IL2CPP managed-stripping preservation |

---

*This document describes the implementation as of the `feat/lite-mode` branch. The internal verification ledger lives in `LiteMode_zh.md`; the protocol decision log lives in `ExpressionInterpreterFeasibility_zh.md`; consumer-facing usage lives in `LiteMode_Feature.md`.*
