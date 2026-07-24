# unity-cli-plugin 协议同步记录

## 2026-07-24：Unity Tests 有界异步状态机

新增两个 Editor-only 内置命令，wire namespace 独立为 `tests`：

```text
tests/run(mode, testNames?)
tests/status(runId, waitSeconds=0)
```

`tests/run` 的 `mode` 必须是 `edit` 或 `play`。省略 `testNames` 表示运行该模式
下的全部测试；若提供，则必须包含 1–32 个非空的精确测试全名，每项最多 512 个
UTF-16 代码单元（与 C# `string.Length` 一致）。它只能作为受 at-most-once
保护的直连 `/command` 请求执行，不能放进
`/batch`；缺少受保护 invocation id 时，dispatcher 会在参数绑定、主线程切换和
handler 执行之前拒绝。`runId` 直接取受保护 invocation UUID 的 32 位十六进制
`N` 格式；当前或保留历史中已有同一 `runId` 时不会再次调用
`TestRunnerApi.Execute`。

执行前还会检查所有已加载 scene；只要存在 dirty scene 就返回
`validation_error`，要求先保存或丢弃修改，避免非交互执行触发保存确认框。
成功响应只返回紧凑 acceptance：

```json
{
  "runId": "32 位十六进制 id",
  "phase": "requested",
  "mode": "edit",
  "accepted": true
}
```

调用方必须保存 `runId`。Test Framework 运行 GUID 只保存在内部 durable
state，用于把全局 callback 归属到唯一运行，不能作为公开 selector。

`tests/status` 必须传入上述 `runId`，它本身不要求受保护 invocation。
`waitSeconds` 范围为 0–20；大于 0 时，
若当前请求不在 Editor 主线程，会短暂等待 terminal state，以减少复杂工作流的
轮询次数和重复上下文。状态结果不返回测试树、passing test 列表、完整 Output 或
XML，只返回：

- `phase`: `requested`、`running`、`completed` 或 `interrupted`；
- `outcome`: terminal 时为 `passed`、`failed`、`skipped`、
  `inconclusive`、`no_tests` 或 `unknown`；
- total/completed/passed/failed/skipped/inconclusive 的扁平计数；
- 当前测试、根结果、耗时、时间戳和操作级 message；
- 有界 `failureDetails`、`returnedFailureCount` 与
  `failuresTruncated`。

`status.resultJson` 的 UTF-8 大小硬限制为 16 KiB。失败详情最多保留 20 条，
单条测试名、message 与 stack 都有独立上限；超过状态文件或 response 预算时从
尾部省略并显式设置 `failuresTruncated=true`。完成裁剪后仍无法满足 16 KiB 时
返回 `system_error`，不会发送超限 payload。

### durable ownership 与 reload

内部状态位于
`Library/CSharpConsole/TestRuns/v1/state.json`，采用 flush + 同卷原子替换，
最大 64 KiB。当前终态会在下一次 run acceptance 前原子归档到 `history/`；
`tests/status` 可查询当前 run 或最近 16 个已归档终态。超出保留数量的详细记录
会连同对应的 `seen/` marker 一起裁剪；marker 总量因此只覆盖 current + 16 条
历史，不形成无界 machine-local state。
历史文件损坏、文件名与内容中的 `runId` 不一致或归档失败时均 fail closed，且
等待 run A 的请求在唤醒后会按 A 的 `runId` 重新取当前/历史记录，绝不会误返回
随后启动的 run B。

执行顺序固定为：

```text
持久化 requested + seen marker → TestRunnerApi.Execute 恰好一次
→ 持久化 Test Framework run id → callback 推进 running/completed
```

Test Framework callback 不携带 run id、属于全局订阅，并会在 domain reload 后
丢失。因此集成放在独立 Editor assembly 中，每次 reload 重新注册 callback；一个
版本隔离的内部 probe 会在 dispatch 后证明唯一活动 run，并把 ownership proof
持久化。进入 Play Mode 后，Test Framework 可能先移除 editor-side job，再发送
remote callback；此时只在 proof 已持久化且没有其他活动 framework job 时接受该
callback stream。出现冲突 run、probe 不可用、离开 Play Mode 后超过短暂宽限仍
没有 terminal callback、状态文件损坏或关键写入失败时，状态一律 fail closed 为
`interrupted`，不会推断成功，也不会再次调用 `Execute`。

测试断言失败属于 `phase=completed` + `outcome=failed`，与基础设施证据中断分开。
完成状态优先使用根结果的 `TestStatus` / `ResultState` 判定；cancel、setup error、
teardown error 或其他根失败不会被误报为 `passed` / `no_tests`，空根结果也不会
生成 `completed`。suite/setup/teardown 失败与 leaf failure 共用同一套 20 条和
字节预算。
`IErrorCallbacks` 本身无法可靠归属到已被移除的 run，因此只作为
`interrupted` 诊断保存。

live command descriptor 新增 `requiresProtectedInvocation` 与 `allowInBatch`。
Editor `/health` 新增精确 capability `test_runs_v1`；Player 不声明该 capability。

推荐调用方使用：

```text
tests/run → tests/status(runId, waitSeconds=10)
```

如果 Play Mode 或 assembly reload 使连接暂时断开，先用 `wait-ready` 恢复同一
Unity 2022 target，再继续查询同一个测试 `runId`；不得通过新的
`tests/run` 重试结果未明的运行。
服务端只保证 health 公告的 dedupe window；该窗口之外的长期“不重发”由 CLI
machine-local outbox 保证。调用方不得把已发送过的旧 invocation UUID 作为新意图
再次 dispatch。

## 2026-07-24：`editor/console.get` 有界诊断读取

新增只读内置命令 `editor/console.get`。接口只包含一个可选参数：

```json
{
  "afterMarkerId": "editor/console.mark 返回的 32 位十六进制 id"
}
```

- 省略参数时读取 Unity 2022 `Editor.log` 的最近有界窗口；
- 传入 marker id 时只返回该 marker 记录结束后的日志；
- marker 格式非法时返回 `validation_error`；格式正确但在最近 8 MiB 日志快照内
  找不到时返回 `system_error`，不会退化为当前日志尾部，也不能据此重跑结果未明的
  原操作；
- 调用开始时固定文件长度，调用期间新增的日志不进入本次结果；
- 不读取或修改 Unity Console 的搜索、级别、折叠、清理选项；
- `resultJson` 的 UTF-8 大小硬限制为 16 KiB。

为了让 marker 边界不可被 label 伪造，`editor/console.mark` 的 `label` 现在限制为
单行、最多 200 个字符，并拒绝保留的 marker 前缀。

成功结果固定为：

```json
{
  "text": "按原顺序返回并统一为 LF 的日志文本",
  "truncated": false
}
```

`truncated=true` 表示历史尾部或 marker 后内容因固定预算被省略。调用方不能在
该状态下声称窗口内不存在其他诊断。推荐的复杂工作流是：

```text
editor/console.mark → 执行操作 → wait-ready / diagnose
→ editor/console.get(afterMarkerId=<mark id>)
```

该命令刻意不提供分页、正则、任意日志路径、source 选择或可调输出上限，避免把
日志查询复杂度和无界 token 成本暴露给 Agent。`console.mark` 的 marker 格式和
编辑器日志路径解析也已收拢到同一个内部模块。

## 2026-07-24：HTTP protocol v2 invocation 去重与诊断字段

本次将 `ConsoleServiceConfig.ProtocolVersion` 从 1 升为 2，package 版本不变。

### at-most-once 请求

以下 JSON mutation endpoint 支持同一 target 内、24 小时窗口的持久化
at-most-once：

- `/command`
- `/batch`（整批共用一个 invocation id）
- `/editor`
- `/compile`
- `/editor-compile`
- `/runtime-compile`
- `/refresh`
- `/execute`

客户端通过通用 HTTP header 发送：

- `X-CSharpConsole-Invocation-Id`: UUID
- `X-CSharpConsole-Target-Id`: 当前 `/health` 返回的 `targetId`

两个 header 必须同时存在。都不存在时保持 protocol v1 的兼容执行，但 response
receipt 的 `guarantee` 为 `none`。只传一个 header、UUID 非法、target 不匹配或
journal 不可写时，server 会在执行前 fail closed。

server 使用 `targetId + endpoint + 原始 UTF-8 body bytes 的 SHA-256` 作为
invocation fingerprint。同一个 UUID：

- fingerprint 相同且已完成：重放持久化 response，不重复执行；
- 正在执行：返回 `operation_in_progress`；
- 上次执行在完成结果落盘前中断：返回 `outcome_unknown`，不重复执行；
- fingerprint 不同：返回 `invocation_conflict`。

结果会先持久化，再写 HTTP socket。Editor ledger 位于
`Library/CSharpConsole/InvocationLedger/v1`；development Player ledger 位于
`Application.persistentDataPath/CSharpConsole/InvocationLedger/v1`。Player 使用
按进程区分的 `identities/<pid>-<process-start>.json`，并把 invocation record
写入 `targets/<targetId>/`；因此共享 `persistentDataPath` 的并发 Player 不会争用
identity，也不会因使用相同 UUID 而互相冲突。Player target 的生命周期等于进程
生命周期：新进程不会冒充旧 target 查询或重放旧结果；死亡进程的 identity 与
target ledger 在 24 小时去重窗口结束后由存活 Player 做尽力清理。

Editor 的受保护 `/compile` 转发到 development Player `/execute` 时，第二跳也
受 protocol v2 保护。Editor 先读取 Player `/health`，确认它是已初始化的
Unity 2022 Player、journal 可写、主线程 heartbeat 正常并具有四个可靠性
capability；然后由 parent invocation UUID、`player/execute` 和动作标签稳定派生
child invocation UUID，并对实际 UTF-8 body 计算 digest。Player response 必须带
匹配 child UUID、target、endpoint、digest、去重窗口和 `at-most-once` receipt。
连接或读取失败、receipt 不一致，以及 child `outcome_unknown`、
`operation_in_progress` 或 conflict 都会把 parent 返回为 `outcome_unknown`，
不会换 child id 重发。明确的 protected rejection 表示本次 Player 副作用确定未
执行。

### response receipt

所有 `HttpResponseEnvelope` 新增 `invocation`：

- `invocationId`
- `targetId`
- `serviceEpoch`
- `endpoint`
- `requestDigest`
- `state`
- `guarantee`（`at-most-once` 或 `none`）
- `replayed`
- `dedupeWindowSeconds`
- `createdAtUtc`
- `updatedAtUtc`

### invocation 状态查询

新增 `POST /CSharpConsole/invocation-status`，body：

```json
{
  "invocationId": "UUID",
  "targetId": "health 返回的 targetId"
}
```

`targetId` 也可以通过 `X-CSharpConsole-Target-Id` 发送；body 与 header 同时存在
时必须一致。完成状态的 `dataJson.responseJson` 包含可恢复的原始 response
envelope。状态查询也会即时检查 24 小时窗口；过期 record 会在本次查询中删除并
返回 `state=protection_expired`、`protectionExpired=true` 与 `previousState`，
不会在维护定时器尚未运行时继续声称受保护。

### health v2

`/health` 的 `dataJson` 新增：

- `targetId`：Editor 使用规范化 project root 的 SHA-256 前 24 位，
  格式为 `editor-<24 hex>`；development Player 在同一进程内的 service 重启时
  复用 target，新进程获得新 target；同一 `persistentDataPath` 下的并发 Player
  使用各自独立的 target ledger；
- `serviceEpoch`：每次 service 初始化生成；domain reload 后会变化；
- `capabilities`：`invocation_headers`、`invocation_receipts`、
  `invocation_status`、`at_most_once`；Editor 还会在测试状态机可用时声明精确的
  `test_runs_v1`，Player 不声明；
- `journalWritable`
- `dedupeWindowSeconds`
- `isUpdating`
- `isPlaying`
- `mainThreadHeartbeatAgeMs`

CLI 必须先读取 health target，再为 mutation 生成新的 invocation UUID；遇到连接
超时或 reset 时只能使用同一个 UUID 重试或查询状态，不能用新 UUID 盲重试。
Editor 的本地 `identity.json` 同时记录 `projectRoot`；CLI 只有在该路径解析后与
当前项目根一致时才可用它处理 junction/symlink，不能信任从其他项目复制来的
`Library` identity。

### refresh readiness 收紧

`RefreshOperationState` 新增 `triggerStarted`、`exitPlayModeRequested`、
`waitingForEditMode`、内部恢复用的 `changedFiles` 和公开计数
`changedFileCount`。`/refresh` 现在先持久化 invocation acceptance，再退出
Play Mode 或把 refresh 排入主线程；若 acceptance 无法落盘，refresh 不会启动。
已有 refresh 正在执行时，新请求返回 `ok=false`、`accepted=false`，其
`changedFiles` 不会被假定已合并。请求 body 的读取、JSON 解析和路径校验都发生
在 acceptance 之前；坏请求只会拒绝本次调用，不会改写已有 refresh operation。

refresh 状态文件也属于 acceptance 边界：状态的 durable write 成功后才会发布到
`/health`、清空 session 或返回 `accepted=true`。因此 `accepted=true` 只表示
refresh 意图已可靠接收，最终结果仍须按对应 `opId` / `generation` 等待。

主线程执行 refresh 时，会先可靠写入 `triggerStarted=true`，之后才允许退出
Play Mode、调用 `AssetDatabase.Refresh` 或导入指定资源。该写入失败时不会触发
任何上述操作，operation 会进入 `failed` 并说明持久化错误；如果失败标记本身也
无法落盘，当前 service 仍报告内存中的 `failed`，下一 service epoch 会把最后
落盘的 active 状态按“中断”处理，而不会恢复成 `ready`。
`requested` 的超时基准会在状态发布前初始化；编译和 assembly reload 回调在
`triggerStarted=false` 时不会推进该 operation，避免无关的 Unity 编译污染等待
状态。排入主线程的 trigger 同时绑定 `opId`、`generation` 和本次文件列表；若等待
期间 operation 已超时或被替换，旧 trigger 会直接跳过，不产生副作用。不支持
`File.Replace` 的运行时通过 canonical/backup 同卷换名，并在启动时恢复中断的
替换，不会直接截断唯一状态文件。
所有 lifecycle transition 都按 `opId + generation` 做原子 compare-and-set；
旧 callback 不能推进或覆盖新 operation。

带 `--exit-playmode` 的请求会先持久化待续文件列表与等待阶段，设置
`isPlaying=false` 后立即结束当前 trigger，不会在仍处于 Play Mode 的同一调用栈
里刷新；`isPlayingOrWillChangePlaymode=true` 的 EnteringPlayMode 也会先进入
durable waiting state，再用同一 setter 取消/退出。只有收到 `EnteredEditMode`、确认
`isPlayingOrWillChangePlaymode=false`，且 Editor compile/update 空闲后，才恢复
同一 `opId/generation`。预期中的 ExitingPlayMode service restart 会保留该
waiting state；进程重启也从 Library 状态恢复，不创建第二个 intent。

refresh 状态 canonical path 改为
`Library/CSharpConsole/RefreshState/v1/refresh_state.json`，不再把可清理的
`Temp` 当作 durable acceptance。升级时会读取一次旧
`Temp/CSharpConsole/refresh_state.json`：没有 operation 的旧 ready 记录迁移成
pristine，旧 active operation 保守迁移成 `failed`，因为无法证明它已完成。空白、
损坏、缺字段、未知 phase 或字段组合不一致的状态同样恢复为明确的 `failed`，
不会静默当作从未执行过 refresh。

完整 `changedFiles` 只保留在 Library 内部恢复状态；`/health` 与 `/refresh`
response 的 public operation snapshot 会清空该数组，只返回
`changedFileCount`，避免 `wait-ready` 每次 poll 重复传输大量路径。

`wait-ready` 只应接受匹配 `opId` / `generation` 且 phase 为 `ready` 的状态：

- `requested` 超时改为 `failed`，不再转成 `ready`；
- targeted/full refresh 都会在 Import/Refresh 前发布
  `compileRequested=true` 并显式请求 compilation；不再只靠 `.cs` 后缀推断，
  因此 `.asmdef`、`.asmref`、`.rsp`、预编译 DLL 或 package 配置变化不会提前
  ready；
- 编译必须绑定当前 `opId/generation` 并观察真实 lifecycle；30 秒内未开始或
  300 秒内未结束会转成 `failed`；
- 如果至少一个 assembly 真正开始编译，成功后必须在 60 秒内观察到 assembly
  reload，只有 `afterAssemblyReload` 才发布 `ready`；compile failure 直接
  `failed`；
- 如果 compilation pipeline 结束但没有 assembly 需要重编，则至少等待 2 秒和
  3 个 Editor update 的稳定 idle 窗后才发布 `ready`；
- service reload 只有在 `triggerStarted=true` 且已进入 `reloading` 时才可恢复为
  `ready`；
- 其他被中断的 active phase 会恢复为 `failed`。

development Player 的 service 初始化强制 `Application.runInBackground=true`，
确保失焦时主线程仍能排空 mutation 队列。

### 主线程副作用提交边界

受 protocol v2 保护的 `editor/playmode.enter` 与 `editor/playmode.exit` 在
durable invocation claim 之后，把校验与 `EditorApplication.isPlaying` setter
合并在同一次同步主线程执行中，再生成 terminal response。若 Play Mode reload 使
response 来不及落盘，该 invocation 会恢复为
`outcome_unknown` 而不是虚假的 completed；同一 UUID 不会再次投递。异步主线程
工作若超时但仍在运行，会继续独占串行执行锁；后续异步请求最多等待一秒获取该锁，
未获取时明确在执行前失败，避免串行 HTTP 接收循环永久阻塞。
