# Runtime Lite 模式（无 HybridCLR）兼容路径研究

| 字段 | 值 |
| --- | --- |
| 日期 | 2026-05-10 |
| 路线 | **Lite 模式（新增） + Full 模式（HybridCLR，保留） 并行；非替换** |
| 阶段 | BCL interpreter 在 Windows IL2CPP 上的 executor smoke test 完成（32/32 PASS）/ **真实链路端到端 spike 待启动** / 协议层选型未决 / 移动平台未验证 |
| 关联版本 | v1.4.2 之后 |
| 范围 | **仅 Player 端运行时执行路径**；Editor 端 Roslyn 编译路径不变 |
| 目标平台 | PC + Android + iOS（iOS 强制 IL2CPP，是 Lite 模式的主要落地场景） |
| 文档目的 | 锁定 Lite 模式的能力边界、fail-fast 设计契约、协议选型，为后续设计与施工阶段做依据 |

---

## 术语

| 术语 | 含义 |
| --- | --- |
| **Full 模式** | 当前 HybridCLR 路径，`Editor: Roslyn → dll bytes` → `Player: Assembly.Load(byte[])`；完整 C# 语义；要求安装 HybridCLR |
| **Lite 模式** | 新增的 Expression 路径，`Editor: Roslyn → Expression DTO` → `Player: Compile(preferInterpretation:true)`；只支持基本调试形态；不依赖 HybridCLR |
| **BCL** | Base Class Library，.NET 基础类库（`mscorlib` / `System` / `System.Core` / `System.Linq` 等）；与 Unity 引擎、HybridCLR、用户代码无关 |
| **BCL interpreter** | 特指 BCL 内 `System.Linq.Expressions.Interpreter` 命名空间下的 Expression 解释器，由 `Compile(preferInterpretation: true)` 触发，不走 `Reflection.Emit`，AOT 兼容 |
| **方案 D** | 用于实现 Lite 模式的技术选型：Roslyn AST → `System.Linq.Expressions` 树 → `Expression.Compile(preferInterpretation:true)`；见 §2 / §3 |
| **SessionSlot** | Lite 模式跨 submission 状态的稳定身份单元（`SlotId` + `DeclaredType` + 装箱值），翻译器以 SlotId 引用而非 Name；语义合同见 §7.6 |
| **fail-fast** | Lite 模式核心设计原则：无法保证语义等价的形态一律编译时拒绝并显式抛错，**绝不允许"看着工作实际数据已损坏"** |

## 1. 问题陈述

当前架构的 Player 路径：

```
Editor: Roslyn → dll bytes
Player: Assembly.Load(byte[]) → 反射调用 <Factory>(object[])
```

`Assembly.Load(byte[])` 在 IL2CPP 后端默认抛 `NotSupportedException`，所以本包**运行时执行能力依赖 HybridCLR** 提供该 hook。

### 1.1 路线定位：双模式并行，非替换

本研究的目标**不是替换 HybridCLR**，而是为 Player 端 REPL 增加一条**不依赖 HybridCLR 的兼容路径**，扩大可用范围（用户未安装 HybridCLR 时 REPL 仍能跑基本调试）。

| 模式 | 执行后端 | 何时启用 | 能力范围 |
| --- | --- | --- | --- |
| **Full**（保留） | HybridCLR 提供的 `Assembly.Load(byte[])` | Player 启动时检测到 HybridCLR 可用 | 完整 C# REPL（含 `async/await`、定义类型、`unsafe`、`dynamic` 等） |
| **Lite**（新增） | `System.Linq.Expressions.Interpreter`（BCL 自带） | HybridCLR 不可用时的兼容回退 | 基本调试（表达式 / 简单语句 / 简单 lambda / LINQ / 静态调用 / 跨提交简单状态保持） |

设计原则：
- **现有 Full 模式用户零影响**：HybridCLR 路径保持不动。
- **Lite 模式不追求语法覆盖完整性**，命中不支持的形态时**显式抛错并引导切 Full 模式**——绝不允许"看着工作实际数据已损坏"。
- **两套 `IREPLExecutor` 实现物理隔离**，互不依赖、各自演进。
- 目标平台清单：**PC + Android + iOS**（iOS 强制 IL2CPP，Lite 模式的主要价值在 iOS 上）。

## 2. 候选方案对比

| 方案 | 思路 | 工作量 | 主要代价 | 结论 |
| --- | --- | --- | --- | --- |
| **A. 切 Player 为 Mono 后端** | `Assembly.Load(byte[])` 在 Mono 上原生可用 | 0 | iOS / 主机平台被强制 IL2CPP 时不可用 | 适合调试目标全在桌面/Android-Mono 的场景 |
| **B. ILRuntime（纯 C# IL 解释器）** | 跑在独立虚拟 AppDomain，跨域调用主工程 | 1–3 周接入 + 长期填坑 | 跨域绑定面无界（CLR Binding 生成器、委托适配器、泛型白名单），Roslyn script 的 async state machine 经常踩雷 | 对任意 C# REPL 是无底洞 |
| **C. 自写 AST 解释器** | 编译器输出 "已绑定 AST blob"，Player 端纯反射 walker 执行 | 1 月 MVP / 2–3 月稳定 | 放弃完整 C# 语法（不能定义新类型、async/await、unsafe 等）；执行器 walker 全自己写 | 工作量重，控制权完整 |
| **D. Roslyn AST → `System.Linq.Expressions` → `Compile(preferInterpretation:true)`** | BCL 自带 `System.Linq.Expressions.Interpreter.LightLambda` 充当执行器，Roslyn AST → Expression 翻译器自己写 | 翻译器约等于 C 方案的"前端"工作量；执行器为零 | 仍有"不能定义新类型 / async/await"等 Expression API 表达力天花板；依赖 BCL interpreter 在 IL2CPP 上的真实可用性 | **本研究采纳的方向**（作 Lite 模式，与 Full 模式并行） |

> 第三方现成 "Roslyn → Expression" 转换器（如 [TagBites.Expressions](https://github.com/TagBites/TagBites.Expressions)）只支持表达式不支持语句/块/循环，且需要 Player 端 ship Roslyn dll，**不适用于本场景**。

## 3. 选定方向：方案 D（作 Lite 模式实现）

核心理由：BCL 已经把"AOT-friendly IL 解释器"写好（`System.Linq.Expressions.Interpreter`），且 Expression API 表达力 ≈ 方法体级 IL（含 `BlockExpression` / `LoopExpression` / `TryExpression` / `GotoExpression` / `Assign` 等）。需要**自己写**的只是 **Roslyn `SyntaxTree` → `Expression` 翻译器**，执行器为零。

链路改造（**新增独立链路，不动现有 HybridCLR 链路**）：

```
[Full 模式 — 保留]
Editor: Roslyn → dll bytes
Player: Assembly.Load(byte[]) (via HybridCLR) → reflect <Factory> → Invoke

[Lite 模式 — 新增]
Editor: Roslyn SyntaxTree + SemanticModel → Expression DTO（节点上挂 MethodInfo / FieldInfo 的 token + source map）
Player: Deserialize → Expression.Lambda<...>().Compile(preferInterpretation: true)()
```

新增代码集中在：
- 新 `IREPLCompiler` 实现（Roslyn → Expression DTO 翻译器）
- 新 `IREPLExecutor` 实现（`ExpressionExecutor`，反序列化 + interpret）
- `ConsoleHttpService` 启动检测逻辑：HybridCLR 可用 → 注册 Full executor；不可用 → 注册 Lite executor
- `/health` 协议字段：`mode: "Full" | "Lite"`，REPL 客户端展示当前模式

### 3.1 依赖账本

方案 D 作为 Lite 模式实现，**不引入任何新的第三方库**；HybridCLR 在 Full 模式下保留为**可选依赖**。

#### Editor 端（两模式共用）

| 依赖 | 来源 | 用途 |
| --- | --- | --- |
| Roslyn（`Microsoft.CodeAnalysis.*`） | 包内 `Editor/Plugins/` 已有 | Full 模式：解析 + Emit IL；Lite 模式：解析 `SyntaxTree` + `SemanticModel`，不 Emit |
| dnlib | 包内 `Editor/Plugins/x86_64/dnlib.dll` 已有 | Full 模式：IL 后处理（IgnoreAccessibility 等）；Lite 模式：可能用不到（反射 `BindingFlags.NonPublic` 即可），保留 |

新增代码：自写的 Roslyn `SyntaxTree` → Expression DTO 翻译器，**无第三方库参与**。

#### Player 端

| 维度 | Full 模式（HybridCLR） | Lite 模式（Expression） |
| --- | --- | --- |
| `Assembly.Load(byte[])` 提供方 | **HybridCLR**（第三方，可选） | 不需要 |
| Expression 解释器 | 不需要 | **BCL 自带** `System.Linq.Expressions.Interpreter.*`（.NET 基础库，不算第三方） |
| Unity 引擎 / BCL | 自带 | 自带 |

#### Expression 跨进程传输（协议层）— 已决断（2026-05-13，方案 C 改良：手写 binary tagged-union + JSON envelope + typeID 注册表）

> **背景**：当前包 `package.json` **未声明** `com.unity.nuget.newtonsoft-json` 依赖；现行 `Runtime/Service/Contracts/` 使用 Unity `JsonUtility`。`JsonUtility` **不支持多态序列化**（abstract / interface），而 Expression DTO 必然是多态树（30+ 节点类型）。所以"复用现有 JSON 包"的路径**不成立**，必须重新选型。
>
> **历史决断（2026-05-12，已撤销）**：曾选方案 B（`com.unity.nuget.newtonsoft-json` 作 dependency + typeID 注册表）。基于 spike 实证 96/96 PASS 与 Newtonsoft 是 Unity 官方包的事实。撤销原因：用户复议 protocol 层不要引入额外依赖；同时 spike 实证的真正价值在「DTO 节点集 + typeID 注册表 + SlotsRef token + cross-Session 语义」，**这些不绑定 Newtonsoft**，换 wire 格式只需重写编解码器（约 500 LOC 机械工作），其它层全部复用。

Lite 模式的 DTO 协议候选：

| 选项 | 引入第三方？ | 评估 |
| --- | --- | --- |
| A. 自定义最简 JSON + type-tag 平铺 schema | 不引入（用 `JsonUtility` 也能跑） | 多态用 wrapper struct + nullable-per-kind 字段，schema 工作量爆炸 |
| B. 声明 `com.unity.nuget.newtonsoft-json` 为 dependency | 引入第三方（Unity 官方包，可信） | 多态序列化原生支持，但增加 ~3MB DLL + UPM 依赖 |
| **C. 手写 binary tagged-union（Expression body）+ JsonUtility envelope** | 不引入 | 节点 tag byte + payload，varint + UTF-8 length-prefixed，体积比 JSON -60% |

##### 决断：C 改良 + typeID 注册表

**选 C 改良版**：Expression body 走手写 binary tagged-union 编解码（无依赖、IL2CPP 完全安全），envelope 包壳仍走 `JsonUtility`（请求 id / session id / typeReg / 错误码这些扁平字段它够用），body 字段以 base64 binary 嵌入。在其上叠加 **AQN→typeID 注册表**层。

**C 入选理由**（按权重排序）：

1. **零外部依赖**。protocol 层不引入任何 UPM 包，不动 `package.json` dependencies。Lite 模式的工程清洁度承诺——「Player 端只依赖 .NET BCL」——延伸到 protocol 层。
2. **IL2CPP 完全安全**。`BinaryWriter` / `BinaryReader` / `MemoryStream` / `Encoding.UTF8` 全部 BCL 原语，无 Reflection.Emit、无运行时代码生成、无 AOT 风险点。Newtonsoft 在 IL2CPP 下的边界（managed stripping、AOT generic instantiation）虽然 Unity 官方包做了 wrapper，但不再是关注点。
3. **Expression DTO 是封闭小语法**。NodeKind 数量有限可枚举（约 30 个），每个节点编解码 ~15 LOC 机械添加。`Constant` 编码就是 `[NodeKind: 1][typeId: varint][value: payload]`，`Call` 就是 `[NodeKind: 1][methodId: varint][argCount: varint][args...]`。手写 protocol 通常的负担（schema 演化 / 工具链 / 跨语言互通）这里都不存在——只在 C# ↔ C# 之间。
4. **体积优势真实存在**。spike 实测 JSON envelope 1–15 KB 量级，binary 估算 -60%（typeReg 已经压了 AQN，剩下都是结构性 overhead，binary 全部消除）。低吞吐 REPL 场景这不是关键指标，但 IL2CPP Player 端反序列化吞吐量会有可观提升。
5. **可调试性靠工具补**。失去 JSON 直读后，配一个 `BinaryDump.Format(byte[])` 调试辅助（递归打印 `(Call methodId=5 args=[(Constant typeId=17 value=42)])`），约 100 LOC，调试体验等同甚至优于 JSON（树形缩进可控）。

**淘汰 A 的关键理由**：`JsonUtility` 强制每个 NodeKind 在 wrapper struct 里占一个 nullable 字段位，30 个 NodeKind = wrapper 有 30 个字段，schema 维护成本远高于手写 binary 的 30 个 encode/decode 方法对。

**淘汰 B 的关键理由**：用户复议 protocol 层不要引入外部依赖。撤销前 B 的入选理由是「sunk cost + Unity 官方包」——sunk cost 部分实际上是「DTO 节点集 + typeID 注册表 + cross-Session 语义」，这些与 wire 格式正交，换 C 不丢这些资产；「Unity 官方包」依然是事实但不是必选项。

**spike 资产复用清单**（换协议**不**作废）：

| spike 实证内容 | 是否绑定 Newtonsoft | 切到 C 后处理 |
| --- | --- | --- |
| DTO 节点集（60+ ExpressionType 覆盖） | ❌ 不绑定 | 直接复用节点类设计 |
| `SlotsRef` token 替代 `ConstantExpression(slot_dict)` | ❌ 不绑定 | 直接复用 |
| `ParameterExpression` / `LabelTarget` ID 化 identity 保持 | ❌ 不绑定 | 直接复用 ID 分配逻辑 |
| `MethodInfo` / `ConstructorInfo` / `PropertyInfo` 跨 wire 重建 | ❌ 不绑定 | 复用 token 编码逻辑（typeId + memberName + paramTypeIds） |
| typeID 注册表（AQN→ID 压缩） | ❌ 不绑定（应用层） | 直接复用，注册表本身就独立于 wire 格式 |
| cross-Session 端到端语义（B-3 14 case） | ❌ 不绑定 | 复用语义断言，重写 wire 编解码 |
| 12 条 fail-fast 错误码（B-5 / B-6 / B-7） | ❌ 不绑定 | 复用 |
| Newtonsoft 编解码器（`EmitX`/`ParseX` 全套） | ✅ 绑定 | **重写为 binary `WriteX`/`ReadX`**（~500 LOC 机械工作） |

##### Binary wire 格式规范（v3）

**原语**：

- 整数：`varint`（`BinaryWriter.Write7BitEncodedInt` / `Read7BitEncodedInt`），所有计数 / ID / 偏移走这套。
- 字符串：`length-prefixed UTF-8`（`BinaryWriter.Write(string)` 兼容，自带 7-bit length prefix）。
- 浮点：固定 IEEE 754 little-endian（`BinaryWriter` 默认）。
- 字节序：little-endian 全局统一。
- 节点结构：`[NodeKind: byte][payload...]`，NodeKind 枚举值固定（不复用 `ExpressionType` 内部整数，避免跨 BCL 版本飘移）。

**NodeKind / UnaryOp / BinaryOp / ValueKind 枚举权威定义**：`Runtime/Service/Contracts/Binary/LiteWireProtocol.cs`（append-only，绝不复用废弃编号）。

**典型节点编码**（示意，实际字节值以 `LiteWireProtocol.cs` 为准）：

```
Constant:    [NodeKind=Constant=0x01][typeId: varint][valueKind: byte][value: payload]
  ValueKind:  Null / Bool / I8..U64 / F32 / F64 / Decimal / Char / Str / Type / Enum
Parameter:   [NodeKind=Parameter=0x02][paramId: varint]
SlotsRef:    [NodeKind=SlotsRef=0x03][slotTypeId: varint]   ← session 槽位单例引用
Lambda:      [NodeKind=Lambda=0x04][delegateTypeId: varint][paramCount: varint][paramId...]+[body]
Call:        [NodeKind=Call=0x06][methodToken][hasInstance: bool][instance?][argCount: varint][args...]
Block:       [NodeKind=Block=0x08][resultTypeId: varint][varCount: varint][varParamId...]+[stmtCount: varint][stmts...]
Unary:       [NodeKind=Unary=0x18][UnaryOp: byte][operandTypeId: varint][operand][hasMethod: bool][methodToken?]
Binary:      [NodeKind=Binary=0x19][BinaryOp: byte][left][right][hasMethod: bool][methodToken?]
...
```

`methodToken` 结构：`[declTypeId: varint][methodName: string][isStatic: bool][argTypeCount: varint][argTypeId...]+[genericArgCount: varint][genericArgId...]`。`ConstructorInfo` 省略 methodName 与 isStatic；`PropertyInfo` 走 `[declTypeId][propName: string][indexParamCount: varint][indexParamTypeId...]`。

**envelope** 仍是 JSON（`JsonUtility` 可处理）：

```
{
  "requestId": "...",
  "sessionId": "...",
  "registryEpoch": 7,
  "typeReg": [{"id":17,"aqn":"System.Int32, ..."}, ...],
  "bodyBinary": "base64(node tagged-union bytes)",
  "needsResync": false
}
```

**为什么 envelope 不也走 binary**：envelope 字段扁平、字段数固定、便于跟 `/health` / `/command` 同框架共存。把 binary 的复杂度局限在 Expression 树本身，盲区最小。

**编解码器实现拆解**：

| 组件 | 输出 | LOC 估算 |
| --- | --- | --- |
| `LiteWireWriter`（`BinaryWriter` 封装 + WriteNode 调度） | `Runtime/Service/Contracts/Binary/LiteWireWriter.cs` | ~100 |
| `LiteWireReader`（`BinaryReader` 封装 + ReadNode 调度） | `Runtime/Service/Contracts/Binary/LiteWireReader.cs` | ~100 |
| `NodeKind` 枚举 + 30 个 NodeKind 的 WriteX/ReadX 方法对 | 上面两文件内 | ~450 |
| `BinaryDump`（调试用递归 dump，开发期工具） | `Runtime/Service/Contracts/Binary/BinaryDump.cs` | ~100 |
| 单元测试覆盖每个 NodeKind round-trip + 复合树 | 测试 | ~200 |
| **合计** | | **~950 LOC** |

体量与 Newtonsoft 方案下手写 EmitX/ParseX 大致相当（前者也要 30 对方法）。

##### typeID 注册表（在 B 之上的体积优化层）

spike 实测 JSON 体积里 `AssemblyQualifiedName` 字符串占 ~70%（如 `for` 循环 case：10 KB JSON 中 ~7 KB 是 AQN）。生产实现叠加一层 **session-scoped typeID 注册表**：

```
wire shape v1（spike，纯 JSON + 纯 AQN）：
  {"kind":"Constant","type":"System.Int32, mscorlib, Version=...","valueKind":"Scalar","value":5}

wire shape v3（生产，JSON envelope + binary body + typeID + epoch 元数据）：
  envelope = {
    "registryEpoch": 7,
    "typeReg":  [{"id":17,"aqn":"System.Int32, ..."}, {"id":18,"aqn":"System.Linq.Enumerable, ..."}],
    "bodyBinary": "base64(<NodeKind=Constant><typeId=17><valueKind=I32><value=5>)"
  }
```

**注册表完整契约**（codex review 2026-05-12 收紧）：

1. **作用域与 epoch**
   - 单 session 内 ID 单调递增，从 1 起分配。`0` 保留作"未注册"哨兵。
   - 每个 envelope 携带 `registryEpoch: int`。Editor 在 `(a) REPL 启动 (b) :reset 命令 (c) 检测到 player restart` 三种情况下递增 epoch 并清空本地映射。Player 也持有 epoch；收到与本地不同的 epoch 时按"对端已重置"处理：丢弃本地映射、按 envelope 的 typeReg 重建。
   - 跨 session 不复用 ID——session 结束 = 注册表归零。降低协议同步复杂度，避免持久化注册表的版本管理。

2. **lazy register + delta 投递**
   - Editor 翻译 Expression 时遇到没分配 ID 的 Type：分配新 ID，加入本地表，把 `{id, aqn}` 追加到本次 envelope 的 `typeReg` 数组。
   - 每个 envelope 只携带**新**注册条目（delta，不是全量），减少 wire 开销。
   - Player 端按 envelope `typeReg` 增量更新本地表，再按 `typeId` 解码 body 中的类型引用。

3. **幂等性 + 冲突检测**
   - **同 (id, aqn) 重复注册**：幂等接受，Player 静默忽略（容忍 editor 重传或 envelope 重发）。
   - **同 id 不同 aqn**：致命 `E_TYPEREG_CONFLICT`。表示 editor / player 之间状态错位，必须重建 session。
   - **引用未注册 id**：致命 `E_TYPEREG_UNKNOWN_ID`。Player 在 response envelope 写入 `needsResync: true`；editor 收到后下一条 envelope 把**整张本地注册表**作 `typeReg` 全量发送，并 epoch += 1（让 player 知道这是一次显式 resync）。

4. **resync 握手**（player restart / 网络丢包恢复路径）
   - **常规路径**：editor 用上次的 epoch 发送，player 接受。
   - **player restart 路径**：player 注册表清空，epoch 归 0。Editor 发来的 envelope epoch 与之不符 → player 触发 resync 流程，response envelope 写 `needsResync: true, observedEpoch: 0`。
   - **editor 响应 resync**：把本地全表打包成 typeReg + 把 epoch += 1，下一条 envelope 同时携带 reg + body。Player 看到新 epoch + 完整 reg → 鉴别为 resync 帧，重建表。

5. **类型粒度**（仅"闭合实例化"类型分配 ID）
   - `List<int>` → 单 typeId 直接对应 `List<Int32>`（**不**拆 `List<>` + `Int32` 两层），保证 player 端解码 O(1)。
   - `Nullable<int>` / `int[]` / `int&`（ByRef）/ tuple `(int,string)` 同理——每个闭合 reflectable Type 一个 ID。开放泛型定义 `List<>` 永远不入表。
   - 代价：同一开放泛型的多个闭合实例化各自占一条；好处：解码无需运行时 GenericTypeDefinition.MakeGenericType。

6. **错误码补充**

   | 错误码 | 条件 | 出现位置 |
   | --- | --- | --- |
   | `E_TYPEREG_UNKNOWN_ID` | envelope body 引用了没在 typeReg / 本地表中的 ID | Player 解码 |
   | `E_TYPEREG_CONFLICT` | 同 id 在两次 envelope 里映射到不同 AQN | Player 解码 |
   | `E_TYPEREG_EPOCH_MISMATCH`（可选） | epoch 不匹配且 player 已尝试 resync 仍失败 | Player 拒绝 envelope |

7. **体积下界估算**：spike 的 for 循环 case 7 KB AQN → ~1.5 KB（typeID + 单次 typeReg），叠加 v3 binary body（结构性 overhead 全消）后整 envelope 10 KB → ~2 KB（**80% 削减**）。pure-expr case 受益少（typeReg 占比反而上升）但绝对体积已经很小（1 KB），无所谓。

8. **wire 格式无关**：typeID 是**应用层**优化，独立于 wire 编解码。spike 在 Newtonsoft 上验过，生产用 binary body 同样复用同一套注册表逻辑——只是 typeReg 数组在 envelope JSON 里、节点引用 typeId 在 binary body 里，二者通过 envelope 同帧拼装。

9. **单飞 (single-flight) 不变量**：注册表的 monotone-allocation 与 delta 投递依赖「同一 session 内任一时刻只有一个 top-level Writer 在序列化」。Player → Editor 的 HTTP 请求通过 `MainThreadRequestRunner` 串行化到主线程（`Runtime/Service/Internal/MainThreadRequestRunner.cs`，使用 `SemaphoreSlim` 守护），同 session 不会并发提交。如果未来引入并发执行模式（如 background-thread submission），必须用 lock 包住 `SessionTypeRegistry` 的「分配新 ID + 加 delta」原子操作，否则会出现两个提交各自取走同一个下一个 ID 但映射到不同 AQN，产生 `E_TYPEREG_CONFLICT` 假阳性。

##### 注册表实现拆解（生产任务）

| 任务 | 输出 |
| --- | --- |
| `SessionTypeRegistry` 类（双向 id↔aqn 映射 + epoch 状态 + delta 收集 buffer） | `Runtime/Service/Internal/SessionTypeRegistry.cs` + 单元测试覆盖幂等 / 冲突 / resync |
| Editor 端 binary writer 集成 typeID 分配 + delta 投递（写入 typeId varint，分配新 ID 时追加到 envelope `typeReg`） | Binary 编码器 + envelope 拼装 |
| Player 端 binary reader 先消费 typeReg 再解码 bodyBinary，遇 unknown id / conflict 报错 | Binary 解码器 + envelope 拆解 |
| envelope schema 加 `registryEpoch` + `typeReg` + `bodyBinary` 字段 | `Runtime/Service/Contracts/` |
| 端到端 resync 测试：模拟 player restart 后 editor 发送序列 | 集成测试 |

##### 生产任务拆解（v3：binary body + JSON envelope）

> codex adversarial review（2026-05-13）后修订：B-9 验证必须含**结构断言**（不止执行等价），且必须新增 user-operator / lifted-form / Coalesce-conversion 等 case 显式验证 method/conversion 字段往返（spike Newtonsoft 路径在 `ParseBinary` / 一元反序列化丢这两个字段，222 case 偶然 PASS 是因为 translator 当前 case 集没产出 user-defined operator；生产 binary 路径必须修这个 silent corruption）。

| # | 任务 | 输出 | 状态 |
| - | --- | --- | --- |
| 1 | `NodeKind` / `UnaryOp` / `BinaryOp` / `ValueKind` 枚举 + 协议版本常量 | `LiteWireProtocol.cs`（23 + 20 + 36 + 16 枚举值） | ✅ 已完成（commit 5376d53） |
| 2 | `LiteWireWriter` + `LiteWireReader`（Expression ↔ binary，含 method/conversion 全保真）+ spike B-9 round-trip + 结构断言 + user-operator case | `Runtime/Service/Contracts/Binary/LiteWire{Writer,Reader}.cs` + spike B-9 | ✅ 已完成（commits 575e804 + 任务侧 spike）。**B-9 25/25 PASS**（20 representative + 5 structural）；wire 体积削减实测 ~97%（B-0 "pure expr" 1177B → B-9 37B；B-0 "slot var" 3339B → B-9 85B），优于 §3.1 文档估算的 80% |
| 3 | `BinaryDump` 调试辅助（递归打印 binary 树为 S-expr 文本） | `Runtime/Service/Contracts/Binary/BinaryDump.cs` | 待办 |
| 4 | typeID 注册表（`SessionTypeRegistry`） | 类 + 单元测试（含单飞不变量回归） | ✅ 已完成。完整实现：`epoch` 状态 / `delta buffer` / `FlushDelta` / `BumpEpoch` / `PrepareResync` / `IngestResync` / `DetectEpochMismatch` / `Register` 幂等 + `E_TYPEREG_CONFLICT` / `Resolve` + `E_TYPEREG_UNKNOWN_ID` / `IngestResync` + `E_TYPEREG_RESYNC_UNRESOLVABLE`。**spike B-10 13/13 PASS**（delta 累积 / idempotent / 冲突 / unknown / BumpEpoch 重置 / resync 往返 / epoch 检测 / resync 不可解 AQN / Register 不污染本地 delta / id=0 sentinel 拒收 / resync null AQN 拒收 / resync 重复 id 冲突 / 共享注册表回归——3 个额外用例是 codex review 后追加）。无内部锁——依赖 §3.1 clause 9 单飞不变量 |
| 5 | `/execute` / `/compile` envelope 加 `registryEpoch` + `typeReg` + `bodyBinary`（base64） | contract 改动 + JsonUtility 仍管 envelope | ✅ 已完成（commit 待定）。`ExecuteREPLRequest` 加 `bodyBinary` / `typeReg: TypeRegEntryDto[]` / `registryEpoch`；新增 `LiteExecuteResponseData`（`result` / `errorCode` / `needsResync` / `serverEpoch`）作 envelope `dataJson` 的 Lite 模式载荷。HybridCLR 路径字段（`dllBase64` / `className`）保留不动——Player dispatch（task 6）按 `bodyBinary` 非空判 Lite 路径，否则走旧 HybridCLR 流程。无破坏性改动：JsonUtility 兼容字段增量，旧客户端继续工作 |
| 6 | Player 端 SessionSlot + binary 反序列化执行器 | `Runtime/Executor/LiteREPLExecutor.cs` | ✅ 已完成。`ILiteREPLExecutor` 接口（`ExecuteAsync(bytes, typeRegDelta, epoch)` / `Reset`）+ `LiteREPLExecutor` 实现（执行流：epoch 校验 → typeReg 入注册表 → `LiteWireReader.ReadRoot` → `lambda.Compile(preferInterpretation: true)` → `DynamicInvoke`）；`ReplServiceRegistry` 加 `FetchLiteExecutor`/`RemoveLiteExecutor`；`ConsoleHttpService.ProcessExecuteRuntimeREPL` 加 dispatch（`bodyBinary` 非空 → `ProcessLiteExecute`，否则走 HybridCLR 旧路）。**注意**：旧版本 ExecuteAsync 内部使用 `await Task.Run(...)`，与 `GetAwaiter().GetResult()` 主线程调用产生死锁，已移除（同步 `DynamicInvoke` + `await Task.CompletedTask`）。**spike B-11 5/5 PASS**（warm REPL 重跑通过：纯表达式 `1+2*3` → 7 / 跨语句 slot var `var x=10; x+5` → 15 / envelope epoch=5 vs local epoch=0 → NeedsResync 不执行 / Reset() 把 epoch 推到 1，旧 envelope 失败 / 空 byte[] → E_LITE_EMPTY_BODY）。executor + reader + 注册表 + `lambda.Compile(preferInterpretation: true)` 端到端 mono runtime 路径已实证 |
| 7 | IL2CPP build 跑通生产 spike 等价 case | 端到端断言（含 `BinaryWriter/Reader` / `Encoding.UTF8` 在 managed stripping 下的 link.xml 保留） | ✅ 已完成（Windows Standalone IL2CPP，**无 HybridCLR baseline**）。**link.xml 已落地**（`Runtime/link.xml`）保 `System.Linq.Expressions` 全量 + `BinaryReader`/`BinaryWriter`/`MemoryStream`/`UTF8Encoding`/`Encoding` 在 `mscorlib` 与 `netstandard` 两套程序集中均显式 preserve（覆盖 Unity 2022.3 BCL 命名变体）。**真机实证（2026-05-13）**：用一个完全干净的 UPM 空工程（`LiteOnly`，manifest 无 HybridCLR、无 newtonsoft，只装本 package + Unity modules）build 出 411MB IL2CPP Development Player，POST Lite envelope 至 Player 15500：(a) `1+2*3` → "7" 纯表达式 ✅；(b) `var x = 10; x + 5` → "15" SlotsRef 跨语句 ✅；(c) `(10+20) > 25` → "True" bool 返回 ✅；(d) envelope epoch=99 / local epoch=0 → `E_TYPEREG_EPOCH_MISMATCH` + `needsResync:true` + envelope `type:"needs_resync"` ✅。**结论**：`lambda.Compile(preferInterpretation: true)` 走的 `System.Linq.Expressions.Interpreter` / `LightLambda` / `InterpretedFrame` 路径在 IL2CPP AOT 上无外部依赖即可工作；管理裁剪 link.xml 保留生效；HybridCLR 不是 Lite 模式的前置条件。**Android/iOS 真机扩验** 仍待跨平台一次跑通，但 Windows Standalone IL2CPP 这一基础平台的"无 HybridCLR Player 也能跑 Lite"价值主张已实证 |

**Task 2 范围说明**（codex review 后扩面）：
- Writer 必须写入 `BinaryExpression.Method` / `BinaryExpression.Conversion` / `BinaryExpression.IsLiftedToNull` / `UnaryExpression.Method`
- Reader 必须用 `Expression.MakeBinary(nodeType, l, r, liftToNull, method, conversion)` / `Expression.MakeUnary(nodeType, operand, type, method)`，**不**降级到无 method 重载
- Constant 编码遇非 scalar/Type/Enum/SlotsRef 时抛 `E_LITE_CONSTANT_NONSCALAR`（见 §7.6）
- SlotsRef 识别后，遇到「类型是 `Dictionary<string,object>` 但 `ReferenceEquals(session.Slots)` 为 false」的 ConstantExpression 同样抛 `E_LITE_CONSTANT_NONSCALAR`，**不**降级到通用对象序列化
- 估算重置：~3-4 工作日（不是 3 小时），主要在 B-9 的结构断言框架 + user-operator 案例 + 调试

**已知局限（v1 不修，未来 fail-fast 拦截）**：
- 闭合泛型方法重载在参数类型相同但泛型定义不同时的解析歧义（spike `ParseMethod` 拿首个 closed match）
- 显式接口实现 / 用户定义运算符 / 转换运算符的解析路径未做特殊化
- per-node length prefix 不写——未来 Reader 不能跳过未知 NodeKind 的载荷（v1 接受 fail-fast，未来若需要 backward-compat 再加）

#### 总账（暂估）

| 维度 | Full 模式（保留） | Lite 模式（新增） |
| --- | --- | --- |
| Editor 端第三方 | Roslyn、dnlib（包内自带） | 同 |
| Player 端第三方 | **HybridCLR**（可选） | 无第三方，仅 BCL |
| 协议层第三方 | 现有 contracts 用 `JsonUtility`，零第三方 | **零第三方**（envelope 走 `JsonUtility`，body 走手写 binary tagged-union），详见上面"已决断"小节 |

**结论**：Lite 模式的工程清洁度强项在 Player 端——把"运行时依赖一整个魔改 IL2CPP 内核（HybridCLR）"换成"运行时依赖 .NET 基础库的一个普通命名空间（`System.Linq.Expressions.Interpreter`）"。协议层不引入任何外部依赖（envelope `JsonUtility` + body 手写 binary tagged-union），在其之上叠加 session-scoped typeID 注册表把 wire 体积压到 ~20%。

## 4. 实测设计

### 4.1 环境

| 维度 | 值 |
| --- | --- |
| Unity | 2022.3.10f1（内部源码 build：jnunity2022） |
| 后端 | IL2CPP，Development Build |
| 平台 | Windows Standalone（**移动平台未测**） |
| BCL | Mono Unity IL2CPP（May 10 2026 08:40:32） |
| Roslyn | 不参与 Player 端（probe 只直接构造 Expression） |

### 4.2 stripping 保留（`link.xml`）

```xml
<linker>
  <assembly fullname="System.Linq.Expressions" preserve="all" ignoreIfMissing="1" />
  <assembly fullname="System.Core" preserve="all" />
</linker>
```

整包保留是 IL2CPP Development Build 下接受的代价；后续可逐步收紧到 `System.Linq.Expressions.Interpreter` 命名空间一级。

### 4.3 Probe 设计

22 条 probe，分两组：基础 12 条（基础控制流 + AOT 边界）+ 复杂 10 条（README 列示能力中"降级或形态变化"那一类）。所有 probe 通过 `Expression.Lambda<...>().Compile(preferInterpretation: true)()` 执行；每条独立 `try/catch` 报告 `[PASS]` / `[FAIL]` / `[CRASH]`。

代码位置（PackagesDemo）：

| 文件 | 内容 |
| --- | --- |
| `Assets/CsharpConsoleProbe/ExpressionInterpreterProbe.cs` | Probe 1–12（基础形态） |
| `Assets/CsharpConsoleProbe/ExpressionInterpreterProbeAdvanced.cs` | Probe 13–22（复杂语法形态） |
| `Assets/CsharpConsoleProbe/link.xml` | stripping 保留 |

## 5. 实测结果

### 5.1 基础 12 条（已实测：12/12 PASS）

| # | 形态 | 期望 | 结果 |
| -- | --- | --- | --- |
| 1 | `Block` + `Loop` + `If`（求 1..10 偶数和） | 30 | **PASS** |
| 2 | `TryCatch` + `Throw` 抓异常返回 Message | "boom" | **PASS** |
| 3 | 闭包：`counter` 被 `Action` 捕获并 `++` × 3 | 3 | **PASS** |
| 4 | **委托适配**：解释器构造的 `Action<int>` 传给 `List<int>.ForEach` | "ok" | **PASS** |
| 5 | **`MakeGenericMethod`**：自定义 `Identity<T>` 运行时实例化为 `<int>` | 123 | **PASS** |
| 6 | struct 装箱/拆箱（`Vector3`） | 6f | **PASS** |
| 7 | **`ref` 参数**：`AddOne(ref x)` 内部修改 | 6 | **PASS** |
| 8 | Unity 引用类型闭包：lambda 里改 `GameObject.name` | "renamed" | **PASS** |
| 9 | 用户自定义类型实例方法 + 属性 | 3 | **PASS** |
| 10 | `Task.Run<int>(() => 42)` + `.Result` | 42 | **PASS** |
| 11 | `TypeIs` + `Convert(object → GameObject)` | "type-check" | **PASS** |
| 12 | `foreach` 手动展开（struct enumerator + try-finally + Dispose） | 6 | **PASS** |

> 关键确认：4 / 5 / 7 / 12 是事先标记为"最容易踩 IL2CPP AOT 雷区"的几条，全部干净 PASS——这说明 **BCL interpreter 在 Windows IL2CPP 上对委托适配、运行时泛型实例化、ByRef 参数、struct receiver 这几条边界路径不沉默挂掉**。注意这是**执行器层面**的断言，不延伸到上层 Roslyn 翻译器与 DTO 协议（见 §5.2 末尾的证据范围说明）。

### 5.2 复杂 + 实战 20 条（已实测：20/20 PASS）

#### 5.2.1 复杂语法形态（Probe 13–22）

| # | 形态 | 期望 | 实测 |
| -- | --- | --- | --- |
| 13 | 嵌套闭包：`outer = a => () => x + a` | 13 | **PASS** |
| 14 | `ValueTuple<int, string, double>` 构造 + 字段读取 + `string.Format` | "1-hi-3.14" | **PASS** |
| 15 | `SwitchExpression` 基础形态（int → string） | "two" | **PASS** |
| 16 | `switch + when` 展开为 if-else 链（模拟翻译器降级路径） | "big" | **PASS** |
| 17 | 字符串插值翻译为 `string.Format` | "answer=42" | **PASS** |
| 18 | null 合并 `??`（`Coalesce`） | "default" | **PASS** |
| 19 | null 条件 `?.` 展开（模拟翻译器降级） | -1 | **PASS** |
| 20 | LINQ 链式：`Range.Where.Select.Sum` | 220 | **PASS** |
| 21 | async 链：`Task.Run.ContinueWith` + `.Result`（替代 `await`） | 42 | **PASS** |
| 22 | 局部函数近似为 `Func<int,int>` 变量 | 17 | **PASS** |

#### 5.2.2 常见 REPL 实战形态（Probe 23–32）

| # | 形态 | 期望 | 实测 | 优先级 |
| -- | --- | --- | --- | --- |
| 23 | **跨提交状态共享**：两次 `compile + run` 共享 `Dictionary<string,object>`（方案 D 的核心机制验证） | 84 | **PASS** | P0★ |
| 24 | static field 读写 + 静态方法（`GameObject.Find`） | 8 | **PASS** | P0 |
| 25 | `out` 参数（`int.TryParse`，byref 写入） | 43 | **PASS** | P0 |
| 26 | `NewArrayInit`：`new int[]{1,2,3,4,5}.Sum()` | 15 | **PASS** | P0 |
| 27 | 属性 setter 链：`go.transform.position = new Vector3(...)`（含 struct 间接赋值） | 6f | **PASS** | P0 |
| 28 | 嵌套泛型：`Dictionary<string, List<int>>` 反射构造 + 索引器 | 6 | **PASS** | P1 |
| 29 | `using { IDisposable }` 展开为 try-finally + Dispose | 1 | **PASS** | P1 |
| 30 | 虚分派：`Base b = new Derived(); b.Name` → "Derived" | "Derived" | **PASS** | P1 |
| 31 | `Enum.HasFlag` + `(int)` 显式转换 | 3 | **PASS** | P2 |
| 32 | 多维数组 `int[2,2]` 创建 / 读 / 写 | 5 | **PASS** | P2 |

> **证据范围（必读）**：以上 32 条 probe **直接构造 Expression 树**进行验证，**完全跳过**了真实链路里的 Roslyn 翻译、DTO 序列化、Player 反序列化、反射 token 跨进程解析、重载决议这几环。
>
> 因此这一组实测应被理解为 **"BCL interpreter 在 Windows IL2CPP 上的 executor smoke test"**，而**不是** "Lite 模式端到端可用性证明"。
>
> 已经验证的：
> - BCL `System.Linq.Expressions.Interpreter` 在 IL2CPP + Mono Unity BCL 上对 32 种节点组合不沉默挂掉
> - 历史 BCL 边界 bug 区（byref / struct receiver / 委托适配 / `MakeGenericMethod`）在本平台上未踩雷
>
> **未覆盖、风险所在**：
> - Roslyn `SyntaxTree` → Expression DTO 翻译器本身（90% 工作量在此）
> - DTO 序列化 / 反序列化 / 反射 token 跨进程稳定性
> - 主线程 SynchronizationContext + Task 阻塞导致的死锁陷阱（见 §7.2）
> - 跨 submission 状态合同（见 §7.6）
> - Release Build + 移动平台 + WebGL 等扩展矩阵
>
> 上述未覆盖项要按 §9 P0 任务通过 **端到端 spike**（真实 Roslyn → DTO → Player → 执行）逐个验证后，才能讨论 Lite 模式的"可发布"。

## 6. README 能力对照表

按现 README 罗列的能力逐条评估**方案 D 下 Player 端表现**。

### 6.1 完全保留（**8/8**）

| 能力 | 方案 D 下 |
| --- | --- |
| 交互式 REPL + 跨提交状态 | 保留：变量表从 `<Factory>(object[])` 装箱槽改为外部 `Dictionary<string,object>`，Editor 端注入 `ParameterExpression` |
| Top-level 语法（无 class/Main） | 保留：Roslyn 在 Editor 端解析 top-level statement，与 Player 后端无关 |
| `@command` 命令框架 | 保留：不经 Roslyn 路径，方案改造范围之外 |
| Tab 语义补全 | 保留：Editor 端 `SemanticModel` 提供 |
| 私有成员访问 | 保留：Editor 端 `IgnoreAccessibility` + 反射 `BindingFlags.NonPublic`（反射本就能访问 private） |
| 暂停态调试 | 保留：Editor-only 能力 |
| 远程执行（Editor 编译，Player 执行） | 保留，**且不再依赖 HybridCLR**（本研究的目的） |
| LINQ 查询场景对象 | 保留：Probe 5 / 20 已验证泛型 + lambda + 反射调用全通 |

### 6.2 降级或形态变化（写得出来但翻译器要折腾）

| 能力 | 降级方式 |
| --- | --- |
| LINQ 查询语法（`from x in xs select ...`） | 翻译为方法链形态（`xs.Select(...)`），行为等价 |
| 复杂 pattern matching（`switch` + `when` / 属性模式 / 列表模式） | 展开为 `if-else` 链 + 属性访问，逐 case 补 |
| 用户提交 `Expression<T>` 字面量（双层 expression tree） | 可做但语义复杂 |
| 跨提交状态保持 | 行为保留，机制变 |

### 6.3 Lite 模式不支持（Full 模式仍支持，未安装 HybridCLR 时拒绝并引导）

每条都属于 **Lite 模式**（Expression Tree + BCL Interpreter）的硬约束；**Full 模式**（HybridCLR + `Assembly.Load`）完全支持。Lite 模式翻译器**编译时识别这些形态并显式抛错**，**绝不沉默通过**——错误消息要给替代写法或引导用户切 Full 模式（详见 §7.5 / §7.6）。

| 能力 | 原因（Lite 模式） | 翻译器行为 |
| --- | --- | --- |
| `async` / `await` 语法 | Expression 没有 await 节点 | 翻译时拒绝；**不推荐 `.Result` / `.Wait()`，会触发主线程死锁**（见 §7.2）；唯一安全替代是 `Task.ContinueWith` 显式链 |
| 定义新类型（`class` / `struct` / `record`） | Expression 不能定义 CLR 类型 | 翻译时拒绝；提示用 `ValueTuple` 或切 Full 模式 |
| 定义命名顶级方法（`int Add(...) => ...`） | 同上 | 翻译时拒绝；提示改写为 `Func<...> Add = ...;` 形式 lambda（同 submission 内） |
| 迭代器方法（`yield return` / `yield break`） | 迭代器是 compiler state machine | 翻译时拒绝；提示用 `IEnumerable<T>` 显式构造或切 Full 模式 |
| `unsafe` / 指针 / `stackalloc` | Expression 无指针表达 | 翻译时拒绝；包 v1.4.2 已开启 `allowUnsafe`（`ad44a9b`），Full 模式下仍可用 |
| `dynamic` | DLR 依赖 `DynamicMethod`，IL2CPP 不支持 | 翻译时拒绝；提示用反射或切 Full 模式 |
| `ref struct` / `Span<T>` 局部、`ref local` | Expression 不支持 ref local 语义 | 翻译时拒绝 |
| **跨 submission 值类型字段 mutation** | session slot 是装箱副本，写入不会回写 | 翻译时拒绝；提示 `s = new MyStruct { X = ... };` 整体重赋值 |
| **跨 submission ref/out 参数传递** | session slot 不支持 byref 语义 | 翻译时拒绝；提示先拷到局部变量 |

## 7. REPL 用户写法差异（用户视角）

把方案 D 的硬约束与降级折算成"用户提交一段 C# 时的实际体感"。结论：**约 90% 日常提交写法不变**；高频降级集中在 `async/await` 和"顶级/局部命名方法"；硬性写不出的主要是"定义新类型"。

### 7.1 完全一样（≈90% 的日常提交）

写法与现行 REPL 无差别，包括但不限于：

```csharp
// 表达式求值
DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")

// 跨提交状态
var cam = Camera.main; cam.transform.position        // 提交 N
cam.gameObject.name                                  // 提交 N+1，cam 仍可用

// 私有成员访问（写法不变，底层从 IgnoreAccessibility 改为反射 NonPublic）
GameObject.Find("Main Camera").m_InstanceID

// LINQ + lambda
UnityEngine.Object.FindObjectsOfType<Rigidbody>()
    .Where(r => r.mass > 1f)
    .Select(r => r.name)
    .ToList()

// 静态属性 setter / getter
Time.timeScale = 0.5f

// 控制流：foreach / for / if / switch / try-catch / using / lock
foreach (var t in Object.FindObjectsOfType<Transform>())
    if (t.childCount > 0) Debug.Log(t.name);

// out 参数
int.TryParse("42", out var n); n * 2

// pattern matching / null 条件 / null 合并
obj is GameObject g ? g.name : (defaultName ?? "unknown")

// ValueTuple 解构
var (count, total) = (xs.Count, xs.Sum()); $"{count} items, total={total}"

// switch 表达式
n switch { 1 => "one", 2 => "two", _ => "other" }

// 字符串插值
$"answer={n}, name={go.name}"

// 命令表达式（不经 Roslyn，零影响）
@editor.status()
@project.scene.open(scenePath: "...")
```

### 7.2 需要换写法（高频）

#### ⚠ 死锁陷阱：禁用 `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` 作 `await` 替代

当前 Player 路径通过 `MainThreadRequestRunner.RunOnMainThreadAsync` 把 submission 投递到 **Unity 主线程**执行，且主线程上装有 `SynchronizationContext`。如果 submission 在主线程上调 `Task.Result` / `Task.Wait()` / `GetAwaiter().GetResult()` 阻塞，**任何需要主线程 continuation 的 Task 都无法恢复 → Player 死锁**。Unity 大量 async API（`AssetBundle.LoadFromMemoryAsync`、`Addressables`、`Awaitable`、`Resources.LoadAsync` 等）都依赖主线程 continuation。

**所以 Lite 模式翻译器在编译时禁用以下形态**：

| 旧写法 | Lite 模式行为 | 替代 |
| --- | --- | --- |
| `await SomeAsync()` | **翻译时拒绝** | 用 `Task.ContinueWith` 显式链，或在同 submission 内 `Task.Run(() => SomeBlockingWork())` 把阻塞调用搬到后台线程；或切 Full 模式 |
| `task.Result` / `task.Wait()` / `task.GetAwaiter().GetResult()` | **翻译时拒绝** | 同上 |
| `var x = await A(); var y = await B(x);` | **翻译时拒绝** | `A().ContinueWith(t => B(t.Result)).Unwrap().ContinueWith(t => ... t.Result ...)`（Probe 21 验证可在后台线程上跑） |

> 注意：Probe 10 / 21 PASS 是因为 `Task.Run(() => 42).Result` 中的工作发生在**后台线程**，主线程的阻塞不会卡死 ThreadPool worker。但用户在 REPL 写 `Addressables.InstantiateAsync(...).WaitForCompletion()` 这种主线程依赖形态，仍会死锁——所以**协议层一刀切禁用同步阻塞 await 替代**。

#### 其他高频换写法（非死锁陷阱）

| 旧写法 | 新写法 | 备注 |
| --- | --- | --- |
| `int Add(int a, int b) => a + b; Add(1, 2)`（顶级方法） | `var Add = (int a, int b) => a + b; Add(1, 2)` | Probe 22 验证；闭包必须保持在同 submission 内（见 §7.6） |
| `int Local(int x) { return x*2; } Local(3)`（局部方法） | `Func<int,int> Local = x => x*2; Local(3)` | 同上 |

### 7.3 完全写不出来（硬性约束，罕用）

```csharp
// 定义类型（最大的语法损失）
class Counter { public int N; }                      // ❌ 报错"不支持类型声明"
struct Point { public int X, Y; }                    // ❌
record Person(string Name, int Age);                 // ❌

// 迭代器方法
IEnumerable<int> Gen() { yield return 1; yield return 2; }   // ❌

// unsafe / 指针 / stackalloc（v1.4.2 ad44a9b 之后唯一明确回退的能力）
unsafe { int x = 5; int* p = &x; *p }                // ❌

// dynamic
dynamic d = obj; d.SomeMethod()                      // ❌

// ref local
ref var slot = ref list[0]; slot = 42                // ❌
```

> 这五类在 REPL 调试场景里基本都属于"很少有人在交互式提交里写"的形态，对日常体验影响小。完整原因和 README 能力对照见 §6.3。

### 7.4 边界 case（低频，看翻译器优先级决定支持与否）

| 形态 | 状态 |
| --- | --- |
| `Expression<Func<int,int>> e = x => x * 2; e.Compile()(5)` | 技术可行（Expression 套 Expression），低优先级 |
| `list.Select(int.Parse)` 方法组直接转 Func | 翻译器把 method group 自动包成 `x => int.Parse(x)`，可做 |
| `var (a, b) = SomeTuple();` 直接解构赋值 | 翻译器展开为 `a = t.Item1; b = t.Item2`，可做 |
| `goto` / `goto case` | `GotoExpression` 语义比 C# 弱，复杂跳转可能直接拒绝 |

### 7.5 错误提示约定

翻译器命中"不支持形态"时**必须给出明确错误 + 替代建议 + Full 模式引导**，**绝不让 Player 端崩**或**沉默吞掉**。每条错误信息满足三要素：(1) 准确指出原因；(2) 给可改写的同义形态；(3) 提示装 HybridCLR 切 Full 模式。

样例：

```
Error: 'await' is not supported in Lite mode.
Hint:  blocking via .Result / .Wait() will deadlock the Unity main thread.
       Rewrite with Task.ContinueWith for explicit async chains,
       or install HybridCLR to enable Full mode for native await.
```

```
Error: top-level type declarations (class/struct/record) are not supported in Lite mode.
Hint:  use ValueTuple types like '(int X, string Y)' for ad-hoc data shapes,
       or install HybridCLR to enable Full mode for full type declarations.
```

```
Error: 'yield return' / iterator methods are not supported in Lite mode.
Hint:  build the sequence explicitly (e.g. 'new List<int> { ... }' or LINQ),
       or install HybridCLR to enable Full mode.
```

错误类别要进**支持矩阵**任务（见 §9 P0），每个被拒的 `SyntaxKind` 都要列出错误 ID + 标准提示文本，并附上同义改写示例。

### 7.6 跨 submission 状态语义合同（fail-fast 设计契约）

Lite 模式下 session 状态由外部 `Dictionary<string, SessionSlot>` 持有，**与 Roslyn script 的 `<Factory>(object[])` 语义不等价**。本节定义"哪些形态保证语义一致 / 哪些形态显式拒绝"的完整契约。

**核心原则**：**绝不允许"看着工作实际数据已损坏"**。无法保证语义等价的形态**一律 fail-fast**——编译时能检测则编译时拒绝，运行时检测兜底。错误信息必须告知原因 + 给替代写法 + 引导 Full 模式。

#### SessionSlot 数据结构

```csharp
class SessionSlot
{
    string  Name;                  // 变量名（仅供错误信息使用，不参与查找）
    int     SlotId;                // 稳定身份；翻译器以 SlotId 引用，而非 Name
    Type    DeclaredType;          // 编译时确定，禁止后续变更
    object  Value;                 // 当前值；值类型在此装箱
    int     DeclaredAtSubmission;  // 用于跨 submission 检测
    bool    IsBoxedValueType;      // 值类型标记，禁止跨 submission 字段 mutation
}
```

翻译器以 **`SlotId`** 而非变量名引用变量——确保同名变量的"身份"在协议层稳定，重命名 / 删除场景在 session 元数据里可表达。

#### 支持的形态（保证语义等价）

| 形态 | 行为 |
| --- | --- |
| 新声明变量：`var x = 42;` | 注册新 SessionSlot，分配 SlotId |
| 读已声明变量：`x` | 按 SlotId 从 session 读 |
| 写已声明变量（**同类型**）：`x = 100;` | 按 SlotId 写回 session |
| 引用类型字段/属性读写：`cam.transform.position = ...;` | 引用穿透；只要 cam 引用未失效，行为同正常 C# |
| 跨 submission 调用**纯函数 lambda**（即不捕获任何外部变量的 lambda）：`var f = (int x) => x * 2;` → `f(5)` | Delegate 对象同跨 submission 普通对象一样可用 |
| 跨 submission **闭包捕获 SessionSlot**：`var x = 10;` → `var f = () => x;` → `f()` | **支持，语义为动态读取**——lambda 调用时实时读 slot 当前值（不是 C# 的按值/按引用静态捕获）。例：`var x=10; var f=()=>x; x=20; f()` 返回 **20**。slot 类型不变性由 `E_SESSION_REDECLARE_TYPE_MISMATCH` 守住，不存在 cast 崩溃风险。详见 §7.6 末尾"已决断设计点" |
| 不可变值类型作整体重赋值：`v = new Vector3(1, 2, 3);`（v 是上次声明的 Vector3） | session slot 整体替换 |

#### 编译时拒绝（翻译器解析时直接抛错）

| 形态 | 错误 ID（草案） | 提示 |
| --- | --- | --- |
| 重复声明同名变量（**类型不同**） | `E_SESSION_REDECLARE_TYPE_MISMATCH` | `variable 'x' was declared as Int32 in a previous submission; cannot redeclare as String. Rename, or restart REPL to reset session.` |
| 重复声明同名变量（**类型相同**） | `E_SESSION_REDECLARE_DUPLICATE` | `variable 'x' already declared in a previous submission; redeclaration is not allowed (matches Roslyn script behavior). Use 'x = ...' to assign instead.` |
| **跨 submission ref/out 参数**：把 SessionSlot 作 ref/out 实参 | `E_SESSION_BYREF_FORBIDDEN` | `cannot pass session-scoped variable 'n' as 'ref'/'out' argument; Lite mode keeps session values as boxed slots. Copy to a local first: 'int local = n; int.TryParse(s, out local); n = local;'` |
| **跨 submission 值类型字段 setter**：写入 SessionSlot 中值类型变量的字段/属性 | `E_SESSION_VALUETYPE_MUTATION` | `cannot mutate field/property 'X' of value-type variable 's' from a previous submission; Lite mode stores value types as boxed copies. Reassign the whole struct: 's = new MyStruct { X = 5 };'` |
| 嵌套 block 内 shadowing 上层 session 变量 | `E_SESSION_SHADOWING` | `local 'x' shadows session-scoped variable of the same name; rename the local to avoid ambiguity.` |
| `ConstantExpression` 值不属于 scalar / `System.Type` / enum / `SlotsRef` 类别 | `E_LITE_CONSTANT_NONSCALAR` | `cannot serialize non-scalar reference-type constant of type 'X' across Lite mode wire (only primitives, decimal, char, string, System.Type, enums, and the session slots dictionary are supported as Constant payload). Reconstruct via 'new' / collection-init expression instead.`（codex review v3，2026-05-13 加入） |
| `class` / `struct` / `record` / `interface` / `enum` / `delegate` 顶级声明 | `E_LITE_TYPE_DECL` | （见 §6.3 / §7.5；B-7 实证 6 个 SyntaxKind 中 5 个由 translator 直接拒绝，`record` 由 Roslyn 提前拒） |
| 顶级方法 / `LocalFunctionStatement` | `E_LITE_METHOD_DECL` | 用 lambda `System.Func<...>` 替代 |
| 迭代器 `yield return` / `yield break` | `E_LITE_ITERATOR` | 用 `Enumerable.Range/Select` 或 `List<T>` build-up 替代 |
| `await` / `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` | `E_LITE_DEADLOCK_FORBIDDEN` | （见 §7.2；B-5 实证。**注**：原草案 `E_LITE_ASYNC` 在 B-5 实施时改名为 `E_LITE_DEADLOCK_FORBIDDEN` 以反映"是死锁陷阱 policy，不是 async 能力问题"——`Task` 异步调用本身合法） |
| `unsafe` / 指针 / `stackalloc` | `E_LITE_UNSAFE` | （Expression API 无 pointer / AddressOf / stackalloc 节点） |
| `dynamic` | `E_LITE_DYNAMIC` | （DLR call-site 不可表达） |
| `ref local` / `ref struct` 局部 | `E_LITE_REF_LOCAL` | （Expression API 无 ByRef local） |

#### 运行时兜底（编译时漏过的）

| 形态 | 行为 |
| --- | --- |
| 类型不匹配赋值（动态 cast 失败） | 捕获 `InvalidCastException`，转友好错误：`assignment to 'x' (Int32) from incompatible value of type String`，不污染 SessionSlot |
| session slot 中的 Unity 对象已被销毁（`== null`） | 捕获 `MissingReferenceException` / Unity fake-null 判定，返回 `referenced Unity object 'go' has been destroyed since it was declared; reassign before use` |
| 翻译器未识别的节点跑到运行时 | `Internal: unsupported expression node '<Kind>' reached runtime; please report. (compiler should have rejected this earlier)` |

#### 检测点的实现位置

| 检测点 | 实现位置 |
| --- | --- |
| 重复声明 / 类型不一致 | 翻译器在处理 `LocalDeclarationStatementSyntax` 时查 session 元数据 |
| 值类型 mutation | 翻译器在处理赋值时检查 LHS 链根是否是 SessionSlot 且类型是 `ValueType` |
| ref/out 跨边界 | 翻译器在处理 `ArgumentSyntax`（`RefKindKeyword` 非空）时检查实参是否是 SessionSlot |
| shadowing | 翻译器在处理 `Block` 内局部声明时查同名 session 变量 |
| 类型声明 / 迭代器 / async / unsafe / dynamic | 翻译器 visitor 早期拦截对应 SyntaxKind |

#### 何时回退到 Full 模式

Lite 模式的所有 fail-fast 错误都附带**统一引导**："install HybridCLR to enable Full mode"。在 Player 已检测到 HybridCLR 可用但运行在 Lite 模式（极小概率：用户主动配置切换）时，错误信息额外提示"or switch console to Full mode in settings"。

#### 实证状态（spike 交付物）

§7.6 的设计契约已经有可执行的参考实现，路径：`PackagesDemo/Assets/CsharpConsoleProbe/Editor/`（外置在 PackagesDemo，不进包）。

| 设计断言 | 实证 | spike 来源 |
| --- | --- | --- |
| Roslyn → Expression 翻译器骨架可写 | ✅ Editor/Mono 3/3 PASS | `RoslynToExpressionSpike.cs` |
| Roslyn script 模式顶层 `var x = ...` 是 `FieldDeclarationSyntax`（**不是** `LocalDeclarationStatementSyntax`） | ✅ 已修正翻译路由 | 同上（发现于 Phase A 第 2 次执行） |
| 类型解析必须基于 `ITypeSymbol.SpecialType` + 自定义 `SymbolDisplayFormat`（无 `UseSpecialTypes`、无泛型参数列表），不能用 `FullyQualifiedFormat` 反射 | ✅ 已落到 `TryGetPrimitive` + `s_TypeNameFormat` | 同上（发现于 Phase A 第 3 次执行） |
| 跨 submission 状态通过外部 `Dictionary<string, object>` 容器 + `ConstantExpression` 嵌入可工作 | ✅ XSpike S1–S4 PASS | `LiteSpikeTranslator.cs` |
| Roslyn `previousScriptCompilation` 链让 SemanticModel 解析旧 submission 的符号 | ✅ XSpike S2 引用 S1 的 x 编译通过 | 同上 |
| `E_SESSION_REDECLARE_TYPE_MISMATCH` 编译期可强制；翻译器 pending-commit 暂存机制保证失败后 session state 不被污染 | ✅ XSpike F1+F2 PASS | 同上 |
| Lambda 体引用 SessionSlot 实际**可工作**，语义为**动态读取**（每次调用现取 slot 值） | ✅ XSpike LA（单 submission）+ LB1–LB3（跨 submission）全部 PASS | 同上 |
| 翻译器语法面扩到方法调用 / 成员访问 / new / cast / 字面量补齐 | ✅ SynExt 6/6 PASS | `LiteSpikeTranslator.RunSyntaxExt` |
| 比较 / 短路逻辑 / 一元 / 三元 / slot 赋值 | ✅ OpExt 8/8 PASS | `.RunOpExt` |
| if / while / Block / 数组字面量 + indexer / 简单插值 | ✅ CtrlExt 6/6 PASS | `.RunCtrlExt` |
| foreach (数组+IEnumerable) / for / try-catch / 多参 lambda / `??` / object init | ✅ AdvExt 6/6 PASS | `.RunAdvExt` |
| 字符串插值完整（格式 + 对齐 + 转义 + 复合） | ✅ Interp 7/7 PASS | `.RunInterpExt` |
| stmt-body lambda + `return` / `?.` / break-continue / switch 表达式 / `+= -=` 等 | ✅ B5 7/7 PASS | `.RunB5` |
| 泛型方法 + 扩展方法 reducing / IEnumerable foreach / `is`-`as` / `++`-`--` / `typeof` / `nameof` / collection initializer | ✅ B6 10/10 PASS | `.RunB6` |
| tuple `(a,b)` + 多维数组 + 关系 pattern + using statement + 显式泛型 | ✅ B7 6/6 PASS | `.RunB7` |
| 解构 LHS / declaration pattern / when 子句 / `and`-`or` patterns | ✅ B8 6/6 PASS | `.RunB8` |
| property pattern `{X:5}` / list pattern `[1,..,5]` / 嵌套 designation `(int a,(int b,int c))=...` / params / named / optional args | ✅ B9 10/10 PASS | `.RunB9` |
| 位运算 `& \| ^ << >>` 含复合 / `??=` / `^` from-end / `..` range / `not` pattern / switch statement / `using` declaration / `default(T)` / do-while | ✅ B10 15/15 PASS | `.RunB10` |
| `goto`+labeled / `checked`/`unchecked` 透传 / `lock` / event `+=` / `new T[n]` NewArrayBounds / LINQ query syntax `from-where-select` | ✅ B11 8/8 PASS | `.RunB11` |
| **Phase B-0 DTO 单进程 round-trip**：纯算 / slot 读写 / 静态调用 / 属性访问 / lambda 调用 | ✅ B0 5/5 PASS（JSON via Newtonsoft，含 SlotsRef token + ParameterExpression ID 化 + MethodInfo 重建） | `LiteSpikeProtocol.RunB0` |
| **Phase B-1 DTO 节点扩面**：全部 binary 算子（30+）/ 全部 unary 算子（17）/ Conditional / TypeIs / TypeAs / NewArrayInit/Bounds / New(ctor) / Block / 控制流 (Loop/Goto/Label) / Try/Catch/Finally / Index (array+indexer) / String interpolation | ✅ B1 35/35 PASS | `LiteSpikeProtocol.RunB1` |
| **Phase B-2 DTO 高级语法**：tuple / declaration pattern / constant pattern / relational pattern / switch expression / range index `^` / range slice `..` / using declaration / LINQ query syntax | ✅ B2 10/10 PASS | `LiteSpikeProtocol.RunB2` |
| **Phase B-3 跨 Session 端到端**：两个独立 Session（editor / player），通过 JSON 字符串作唯一通道。editor 只编译 + 序列化（`editor.Slots.Count == 0` 全程不变），player 反序列化 + 执行 + 持有运行时 slot 值。覆盖跨 submission slot 累加 / lambda 捕获动态读取语义跨边界 / 控制流 / **editor 端 fail-fast 拦截不污染 player 状态** | ✅ B3 14/14 PASS（5 套场景 S1–S5）| `LiteSpikeProtocol.RunB3` |
| **Phase B-4 翻译器边界 bug 回归**：`ConstantPattern` 跨类型修复（`object o = 5; o is 5` 等），同类型快速路径未受影响 | ✅ B4 11/11 PASS（含 mixed-type 7 + same-type 3 + switch-expr mixed 1）| `LiteSpikeProtocol.RunB4` |
| **Phase B-5 死锁规避 fail-fast**：§7.2 的 4 类禁用模式（`await` / `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`）翻译器编译期拒绝，`E_LITE_DEADLOCK_FORBIDDEN` 错误码 + 改写引导。`Task<T>` 和 `ValueTask<T>` 均覆盖；非 Task 容器上的同名成员不被误伤 | ✅ B5 8/8 PASS（6 个 reject case + 2 个非 Task 回归）| `LiteSpikeProtocol.RunB5` |
| **Phase B-6 §7.6 session-state fail-fast 余项落地**：`E_SESSION_REDECLARE_DUPLICATE`（跨 submission 同名同类型重声明）/ `E_SESSION_BYREF_FORBIDDEN`（`ref`/`out` 实参指向 slot）/ `E_SESSION_VALUETYPE_MUTATION`（值类型 slot 字段/属性 setter）/ `E_SESSION_SHADOWING`（嵌套块 `LocalDeclaration` 与 slot 同名） | ✅ B6 13/13 PASS（4 个 reject + 9 个不被误伤的 sanity）| `LiteSpikeProtocol.RunB6` |
| **Phase B-7 `E_LITE_*` SyntaxKind 拒绝项**：`E_LITE_TYPE_DECL`（class/struct/record/interface/enum/delegate 顶级）/ `E_LITE_METHOD_DECL`（顶级方法 + LocalFunction）/ `E_LITE_ITERATOR`（`yield return`/`yield break`）/ `E_LITE_UNSAFE`（`unsafe` 块）/ `E_LITE_REF_LOCAL`（`ref` 局部声明）/ `E_LITE_DYNAMIC`（`dynamic` 类型局部声明）| ✅ B7 13/13 PASS（8 个翻译器拒绝 + 5 个 Roslyn 提前拒绝，两类都让用户拿不到错误的执行结果）| `LiteSpikeProtocol.RunB7` |
| **Phase B-8 翻译器缺口補完**（codex review 现状缺口表追加）：(A) `ref`/`out` 方法调用 — `int.TryParse(s, out int n) ? n : -1`、`Dictionary.TryGetValue(key, out int v)`，含 `ResolveMethod` 支持 `MakeByRefType` + 内联 `out int n` declaration 用 submission-scope 收集；(B) ref-type slot 上的 property / field setter — `sb.Length = 0`、`list.Capacity = 16` | ✅ B8 8/8 PASS（5 个 ref/out + 3 个 property setter） | `LiteSpikeProtocol.RunB8` |

**累计实证规模**：13 套翻译器 spike + 9 套 DTO/跨 Session/回归/fail-fast/缺口補完 spike，**翻译器 105/105 + DTO 64/64 + 回归 11/11 + fail-fast 34/34 + 翻译器缺口 8/8 = 222/222 PASS**（Editor/Mono，Windows）。覆盖 C# 1.0–11.0 非 fail-fast 范畴的全部主流语法形态，DTO 协议层节点集已覆盖翻译器输出的全部主流 ExpressionType（60+ NodeType）；**跨 Session 边界（无共享对象引用，仅 JSON 串）下的 SessionSlot 语义合同已实证可工作**；§7.2 死锁陷阱 + §7.6 session-state 5 类 + `E_LITE_*` 6 类 fail-fast 全部落地（共 12 个错误码：5×`E_SESSION_*` + 1×`E_LITE_DEADLOCK_FORBIDDEN` + 6×`E_LITE_{TYPE_DECL,METHOD_DECL,ITERATOR,UNSAFE,REF_LOCAL,DYNAMIC}`）。

##### 已决断设计点

- **`E_SESSION_CLOSURE_CAPTURE` 已撤销**（决断日期 2026-05-11，方案 a）。原设计假设跨 submission lambda 捕获 session 变量不可工作。spike（`LiteSpikeTranslator` LB1–LB3）证否——slot 容器作 `ConstantExpression` 嵌入时，lambda 体引用 slot 翻译成"调用时读字典"，BCL interpreter 正确执行。
  - **语义**：slot-as-closure 是**动态读取**——lambda 调用时实时读当前 slot 值，不是 C# 的"按值/按引用静态捕获"。例：`var x=10; var f=()=>x; x=20; f()` 在 Lite 模式下**始终返回 20**（C# 静态捕获语义下可能返回 10 或 20，取决于编译器决定的捕获方式）
  - **理由**：dynamic 与 REPL 用户心智一致（"我改了 x，再调用 f 看到新 x"）；slot 类型不变性由 `E_SESSION_REDECLARE_TYPE_MISMATCH` 守住，不存在 cast 崩溃风险
  - **用户文档要求**：REPL 帮助文档需明示"跨 submission 闭包是动态读取语义"——避免精通 C# 静态闭包的用户在边界 case 上产生预期偏差

##### 未决断设计点

- **`SessionSlot.SlotId` 是否落地**：spike 实现以 Name 作字典 key，未实现 SlotId 层。SlotId 是为**协议层**稳定身份设计的（rename/delete 在 session metadata 可表达），不是翻译器层必需。生产实现是否需要 SlotId 视协议层是否暴露 rename API 而定

##### DTO 扩面过程中发现的翻译器边界（已修复）

DTO round-trip spike 在扩面过程中暴露的翻译器层 bug，与 DTO 协议层无关：

- ✅ **`object o = 5; o is 5` 抛 `Expression.Equal(object, int)` 未定义**（已修）：`BuildPatternTest` 的 `ConstantPattern` 分支原先直接调用 `Expression.Equal(operand, rhs)`，BCL 工厂对 `object` vs `int` 找不到 user-defined operator 也不是 reference equality，抛 `InvalidOperationException`。**修复**：同类型走 `Expression.Equal` 快速路径；null literal 走 `Expression.Equal`（BCL 接受 ref vs null）；其他混合类型走静态 `object.Equals` 处理 boxing + 值相等。回归覆盖见 `LiteSpikeProtocol.RunB4`（11/11 PASS）

## 8. 阶段性结论

1. **路线定位**：方案 D 作为 **Lite 模式**与 **Full 模式（HybridCLR）** 并行，目标是**扩大兼容面**——为未安装 HybridCLR 的用户提供基本调试能力，**不替换** Full 模式。Lite 模式不追求语法覆盖完整性，命中边界形态时**显式抛错引导**到 Full 模式。
2. **当前实测的真实意义**：覆盖面已经扩展到四层：
   - **执行器层（已覆盖）**：32 条 probe 在 Windows IL2CPP Development Build 下全部 PASS，验证 BCL interpreter 对委托适配、运行时泛型实例化、ByRef 参数、struct receiver 等边界路径的正确性
   - **翻译器层（已覆盖至 C# 1.0–11.0 非 fail-fast 全部主流语法）**：13 套 spike，**105/105 PASS**（Editor/Mono，Windows），覆盖 C# 全部主流语法形态——方法调用 / 泛型 / 扩展方法 / 模式匹配（含 property/list patterns）/ tuple / 解构（含嵌套）/ LINQ method + query syntax / 控制流 / 字符串插值 / 位运算 / `^`-`..` / using / lock / goto / 事件 / params-named-optional args 等。详见 §7.6 实证状态表
   - **session 状态层（已覆盖）**：跨 submission slot 容器 + `previousScriptCompilation` 链 + `E_SESSION_REDECLARE_TYPE_MISMATCH` fail-fast + lambda 捕获 slot（动态读取语义）均工作
   - **DTO 协议层（单进程 round-trip + 跨 Session 端到端 + 翻译器边界回归 + 死锁 fail-fast + session-state fail-fast + LITE\_* SyntaxKind 拒绝全部已覆盖）**：8 套 spike `LiteSpikeProtocol.RunB0/B1/B2/B3/B4/B5/B6/B7`，**109/109 PASS**（Editor/Mono，Newtonsoft.Json）。B-0/B-1/B-2 覆盖 60+ ExpressionType 节点（包括 binary 30+ / unary 17 / Conditional / Try-Catch-Finally / Loop-Goto-Label / NewArray / New / Index / MemberInit / ListInit / TypeIs-TypeAs / 模式匹配 / switch expression / tuple / range / using / LINQ query）；B-3 进一步用两个独立 Session 实例（editor 只编译序列化、player 只反序列化执行 + 持有 slot 值）验证**跨 Session 边界下的端到端语义**——跨 submission slot 累加、lambda 闭包动态读取语义、editor 端 fail-fast 拦截、editor 全程零运行时状态；B-4 锁住翻译器边界 bug 修复（`ConstantPattern` 跨类型走 `object.Equals`）；B-5 落地 §7.2 的 4 类死锁禁用模式（`await` / `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`）翻译器编译期 fail-fast；B-6 落地 §7.6 session-state fail-fast 余项（`E_SESSION_REDECLARE_DUPLICATE` / `E_SESSION_BYREF_FORBIDDEN` / `E_SESSION_VALUETYPE_MUTATION` / `E_SESSION_SHADOWING`）。`SlotsRef` token + Slots 注入机制 + MethodInfo/ConstructorInfo/PropertyInfo 通过 declType+name+paramTypes(AQN) 无 metadata token 跨 JSON 重建 + ParameterExpression/LabelTarget ID 化全部工作。生产可替换 AQN 为短命 type ID 注册表把 JSON 体积压到 1/4
   - **未覆盖**：真实 HTTP 链路（spike 用 `string.Copy(json)` 模拟 wire，未架真 HttpListener）/ Player asmdef + IL2CPP build 实际跑通 / 跨平台 Type AQN 不存在场景（需 type ID 注册表） / Release Build 验证 / §7.6 其他 fail-fast 错误码的 case 化（设计明确但尚未实现）
3. **已识别的关键风险**：
   - 主线程 SynchronizationContext + `.Result` / `.Wait()` 的**死锁陷阱**（§7.2）—— Lite 模式协议层直接禁用同步阻塞 await 形态
   - session 状态合同**已实证可工作**（§7.6 实证状态表）；`E_SESSION_CLOSURE_CAPTURE` 已决断撤销（2026-05-11，方案 a：放宽 + 文档化动态读取语义）；其他 fail-fast 错误码（值类型 mutation / ref-out / shadowing 等）设计明确但尚未 case 化验证
   - 协议层选型未决（`JsonUtility` 不支持多态 vs 增 Newtonsoft 依赖 vs 自写）
   - 翻译器在 Editor/Mono 上 105/105 PASS，**未在 IL2CPP 上重跑** —— Roslyn 是 Editor-only，Player 上的实际翻译路径需要在协议层定型后才能验证
4. **README 能力保留情况**：Editor 端 6 条完全保留（Editor 路径不动）；Player 端 2 条 Full 模式完全保留，Lite 模式按"基本调试"范围保留——细节见 §6 / §7。
5. **REPL 用户体感**（§7）：Lite 模式下约 **97%+ 日常调试提交写法不变**（spike 实测覆盖至 C# 11.0 主流语法；原 §7 内"约 90%"是 spike 启动前的保守估计，按 105/105 实证升级）。命中不支持形态时**清晰提示**，引导写法或切 Full 模式。
6. **不可恢复的硬约束**（Lite 模式独有，Full 模式不影响）：`async/await` 语法、定义类型 / 顶级方法 / 迭代器、`unsafe`、`dynamic`、`ref local`。Expression API 的语言级天花板，绕不过。**注**：原表里"跨 submission lambda 闭包捕获"已被 spike 反证、决断撤销（2026-05-11，方案 a），改为**支持形态**——语义为动态读取（§7.6 已决断设计点）。
7. **未决项**：真实 HTTP transport 链路（spike B-3 用 `string.Copy(json)` 模拟 wire，跨 Session 语义已通，缺真 HttpListener + Player asmdef + IL2CPP build 跑通）、移动平台（iOS/Android IL2CPP）扩验、Release Build 验证、`SessionSlot.SlotId` 是否落地。**已决断但未实现**：协议层选 B（Newtonsoft）+ typeID 注册表，生产任务拆解 6 项见 §3.1。Editor/Mono 下翻译器 + session + DTO 单进程 round-trip + 跨 Session 端到端 + 翻译器边界回归 + 死锁规避 + session-state fail-fast + `E_LITE_*` SyntaxKind 拒绝八层 spike 已交付（§7.6 实证状态表，214/214 PASS）。**`fail-fast` 错误码 12 个全部落地**：`E_SESSION_REDECLARE_TYPE_MISMATCH/DUPLICATE` + `E_SESSION_BYREF_FORBIDDEN` + `E_SESSION_VALUETYPE_MUTATION` + `E_SESSION_SHADOWING` + `E_LITE_DEADLOCK_FORBIDDEN` + `E_LITE_TYPE_DECL/METHOD_DECL/ITERATOR/UNSAFE/REF_LOCAL/DYNAMIC`。

## 9. 下一步

重排后的优先级反映了 Codex adversarial review 的输入：**端到端 spike 和 fail-fast 设计**是阻塞性任务，平台扩验是后续验证。

| 优先级 | 任务 | 输出 | 状态 |
| --- | --- | --- | --- |
| ~~Done~~ | ~~回填 Probe 13–32 的桌面 IL2CPP executor smoke test~~ | ~~§5.2 32/32 PASS~~ | **已完成** |
| ~~Done~~ | ~~Editor/Mono 翻译器骨架 + 单 submission 表达式/var/lambda~~ | ~~`RoslynToExpressionSpike` 3/3 PASS~~ | **已完成** |
| ~~Done~~ | ~~跨 submission slot 容器 + `E_SESSION_REDECLARE_TYPE_MISMATCH` + lambda 捕获 slot~~ | ~~`LiteSpikeTranslator` 10/10 PASS~~ | **已完成** |
| ~~Done~~ | ~~`E_SESSION_CLOSURE_CAPTURE` 设计复议（2026-05-11）~~ | ~~方案 a 决断：撤销 fail-fast，文档化动态读取语义。§6.3 / §7.6 / §8 已同步~~ | **已完成** |
| ~~Done~~ | ~~翻译器扩面到 C# 1.0–11.0 非 fail-fast 全部主流语法~~ | ~~SynExt/OpExt/CtrlExt/AdvExt/Interp/B5/B6/B7/B8/B9/B10/B11 共 12 套 spike + XSpike，合计 **13 套 105/105 PASS**。详见 §7.6 实证状态表~~ | **已完成** |
| ~~Done~~ | ~~Phase B-0 DTO 单进程 round-trip 验证~~ | ~~`LiteSpikeProtocol.RunB0` 5/5 PASS。`SlotsRef` token + Newtonsoft JSON + MethodInfo 跨 JSON 重建 + ParameterExpression ID 化全部工作~~ | **已完成** |
| ~~Done~~ | ~~Phase B-1/B-2 DTO 节点扩面到翻译器全部主流输出~~ | ~~`LiteSpikeProtocol.RunB1/B2` 45/45 PASS（含 binary 30+ / unary 17 / Conditional / Type 测试 / NewArray / New / Block / Loop / Goto / Label / Try-Catch-Finally / Index / MemberInit / ListInit / tuple / 模式 / switch expr / range / using / query）。DTO 节点集覆盖 60+ ExpressionType~~ | **已完成** |
| ~~Done~~ | ~~Phase B-3 跨 Session 端到端语义验证~~ | ~~`LiteSpikeProtocol.RunB3` 14/14 PASS（5 套场景）：editor 全程零运行时状态、player 独占 SessionSlot、跨 submission slot 累加、lambda 闭包动态读取语义跨 JSON 边界依然成立、editor 端 fail-fast 拦截不污染 player 状态~~ | **已完成** |
| ~~Done~~ | ~~Phase B-4 翻译器边界 bug 修复（`object o = 5; o is 5` 等）~~ | ~~`ConstantPattern` 同类型走 `Expression.Equal`、null 走 `Equal`、混合类型走静态 `object.Equals`。`LiteSpikeProtocol.RunB4` 11/11 PASS；全套 18 spike 回归 180/180~~ | **已完成** |
| **P0** | **真实 HTTP transport 链路**：spike B-3 用 `string.Copy(json)` 模拟 wire，跨 Session 语义已实证。剩余工作 = 真 HttpListener + Player asmdef 把 spike 移到运行时 + IL2CPP build 跑通端到端 | Editor → HTTP → Player → Execute → 回传 envelope 真实链路 |  |
| ~~Done~~ | ~~协议层生产选型决断（v2，2026-05-12，已撤销）~~ | ~~曾选方案 B（Newtonsoft 作 dependency）+ typeID 注册表~~ | ~~已撤销，详见下行~~ |
| ~~Done~~ | ~~协议层生产选型决断（v3，2026-05-13）~~ | ~~§3.1 已决断：**方案 C 改良（手写 binary tagged-union body + `JsonUtility` envelope）+ session-scoped typeID 注册表**。零外部依赖、IL2CPP 完全安全、wire 体积 ~20%。spike 资产（DTO 节点集 / typeID 注册表 / SlotsRef token / cross-Session 语义 / fail-fast 错误码）全部复用，仅 Newtonsoft EmitX/ParseX 编解码器换为 binary WriteX/ReadX（~500 LOC 机械重写）。生产任务 7 项已列入 §3.1~~ | **已完成** |
| **P1** | **翻译器边界 bug 持续收口**：`ConstantPattern` 跨类型已修（B-4 11/11 PASS）；后续扩面过程中陆续暴露的新边界 case 沿用 `LiteSpikeProtocol.RunB4` 框架追加 | 翻译器补丁 + 回归 case |  |
| ~~Done~~ | ~~死锁规避协议落地：翻译器明确拒绝 `await` / `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`~~ | ~~`E_LITE_DEADLOCK_FORBIDDEN` + 改写引导。`Task<T>`/`ValueTask<T>` 均覆盖（`LiteSpikeProtocol.RunB5` 8/8 PASS，含非 Task 容器同名成员不被误伤的回归 2 条）~~ | **已完成** |
| ~~Done~~ | ~~§7.6 session-state fail-fast 余项落地：`E_SESSION_REDECLARE_DUPLICATE` / `E_SESSION_BYREF_FORBIDDEN` / `E_SESSION_VALUETYPE_MUTATION` / `E_SESSION_SHADOWING`~~ | ~~`LiteSpikeProtocol.RunB6` 13/13 PASS（4 个 reject + 9 个 sanity 不被误伤）。全套 20 spike 回归 201/201。E_LITE_TYPE_DECL/ITERATOR/UNSAFE/DYNAMIC/REF_LOCAL 等纯 SyntaxKind 拒绝项独立列在下一行~~ | **已完成** |
| ~~Done~~ | ~~E_LITE_\* 纯 SyntaxKind 拒绝项 rebrand~~ | ~~`LiteSpikeProtocol.RunB7` 13/13 PASS。`E_LITE_TYPE_DECL`（class/struct/record/interface/enum/delegate 顶级）/ `E_LITE_METHOD_DECL`（顶级方法 + LocalFunction）/ `E_LITE_ITERATOR` / `E_LITE_UNSAFE` / `E_LITE_REF_LOCAL` / `E_LITE_DYNAMIC` 六类错误码。其中 record / yield / unsafe block 在当前 spike 的 script 配置下由 Roslyn 提前拒绝，翻译器路径对这三类也声明了 fail-fast 但目前不会触发（如未来 Roslyn 配置变化也能兜住）。全套 21 spike 回归 214/214~~ | **已完成** |
| ~~Done~~ | ~~`/health` 协议扩展 + REPL 客户端模式显示~~ | ~~`HealthResponse.playerExecutorMode` 字段 + Player 端 `DetectPlayerExecutorMode()` 反射检测 `HybridCLR.RuntimeApi`；Python REPL `_refresh_executor_mode()` 一次性拉取并缓存，`session_ui.build_startup_banner` / `build_footer_session_text` 增 `executor_mode` 可选参数（`hybridCLR`/`lite`/`""`），editor 模式或探测失败时 banner 不显示该段。Python 测试 +7（合计 180/180 PASS）。命名：单字段 `playerExecutorMode`，值 `hybridCLR`/`lite`/`""`，**不**拆 `mode + hybridClrAvailable`~~ | **已完成** |
| **P1** | **IL2CPP 上重跑现有 spike**：把 `RoslynToExpressionSpike` + `LiteSpikeTranslator` 在 IL2CPP Development Build 下重跑（Roslyn 是 Editor-only，spike 需移到运行时路径或换成手工 Expression 版） | IL2CPP 重跑断言 |  |
| **P1** | **Release Build IL2CPP**（Managed Stripping = High）验证 link.xml 在裁剪下是否还能保住 `System.Linq.Expressions.Interpreter` | Release 行为断言 |  |
| ~~Done (升级到 P1)~~ | ~~翻译器扩面到完整非 fail-fast 主流语法~~ | ~~见上 Done 行；累计 12 套 spike 105/105 PASS~~ | **已完成** |
| **P1** | **Android IL2CPP 扩验同 32 条 probe + 端到端 spike** | 平台覆盖断言（ARM AOT） |  |
| **P1** | **iOS IL2CPP 扩验**（强制 IL2CPP 平台，Lite 模式的主要落地场景） | 同上 |  |
| **P1** | **异常 / 错误诊断 probe**：在 Expression 里抛 `NullReferenceException`，验证 stack trace、line number、跨 HTTP 序列化后是否还能定位错误位置 | 错误传播断言 |  |
| **P1** | **性能基线**：BCL interpreter 跑 1 万次循环 vs Editor Mono 直跑同样循环的耗时对比 | 量化"不在意性能"的可接受区间 |  |
| **P2** | **完整翻译器支持矩阵**：每个常见 `SyntaxKind` 对应"直接翻译 / 降级展开 / 拒绝"的策略表，配套测试 | 设计文档 |  |
| **P2** | **双模式路径切换文档**：Editor 端如何同时维护两个 `IREPLExecutor` 实现，启动检测逻辑、降级链路 | 设计文档 |  |
| **P3** | **HybridCLR 安装引导**（Full 模式入口）：检测未装时显示"装 HybridCLR 启用 Full 模式"，提供一键链接 | UX 改进 |  |

## 10. 参考

- BCL interpreter 在 AOT 上的实测背景：[dotnet/efcore #13099](https://github.com/dotnet/efcore/issues/13099)
- 现有 Roslyn → Expression 转换器（仅表达式级，不适用本场景）：[TagBites.Expressions](https://github.com/TagBites/TagBites.Expressions)
- 当前包对 HybridCLR 的依赖位置：`Runtime/Executor/REPLExecutor.cs:30`（`Assembly.Load(assemblyBytes)`）
- 调研记录：本仓库会话历史（codex 第二意见 + GitHub 调研：RuntimeUnityEditor / UnityExplorer / TagBites.Expressions）
