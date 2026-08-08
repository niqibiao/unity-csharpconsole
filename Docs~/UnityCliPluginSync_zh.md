# unity-cli-plugin 协议同步记录

## 2026-07-25：Package-owned Registry Snapshot 与 Fingerprint

新增两个 control-plane command，继续通过现有 `/command` transport 调用：

```text
command/registry.fingerprint
command/registry.snapshot(partition = "all")
```

`command/registry.fingerprint` 返回：

```json
{
  "schemaVersion": 1,
  "registryGeneration": "64 字符小写 SHA-256",
  "builtin": {
    "count": 57,
    "fingerprint": "64 字符小写 SHA-256"
  },
  "custom": {
    "count": 0,
    "fingerprint": "64 字符小写 SHA-256"
  }
}
```

`command/registry.snapshot` 的 `partition` 接受 `all`、`builtin` 或 `custom`。
响应始终保留两个分区的 `count` 与 `fingerprint`，但只有请求的分区设置
`included: true` 并携带 `commands`；未请求的分区设置 `included: false`，
`commands` 为空数组。这样 CLI 可以只下载发生变化的分区，同时确认响应仍属于
同一个 `registryGeneration`。

`registryGeneration` 是由 schema version、两个分区的 count 与 fingerprint
派生出的不透明内容 generation。它不是时间戳或单调计数；任一分区内容变化时
它都会变化，用于证明轻量响应与 snapshot 属于同一个有效 registry state。

Registry Snapshot 中的 contract 使用 `partition: "builtin" | "custom"`。
包内通过显式 handler 注册的命令强制归入 `builtin`；从已加载项目程序集自动
发现的 `[CommandAction]` 强制归入 `custom`。命令作者不再通过 attribute 参数
选择分区，原 `CommandType` 公共枚举和 `commandType` attribute 参数已移除。

### schema version 1 的 Canonical Command Contract

每个 command 使用以下规范化结构。`wire` 是 package 内部实际 transport route；
`id` 是 CLI 与 agent 使用的 Canonical Command ID。下例只展示一个 argument
以及 result schema 的字段形状，实际 snapshot 不省略其余 arguments 或 fields。

```json
{
  "id": "component/get",
  "wire": {
    "commandNamespace": "component",
    "action": "get"
  },
  "summary": "Get serialized field data of a component",
  "partition": "builtin",
  "requirements": {
    "editor": true,
    "mainThread": true,
    "sessionId": false
  },
  "arguments": [
    {
      "name": "index",
      "schema": {
        "kind": "integer",
        "format": "int32",
        "nullable": false,
        "enumValues": [],
        "fields": []
      },
      "required": false,
      "hasDefault": true,
      "defaultJson": "0",
      "nonEmpty": false,
      "hasMinimum": true,
      "minimum": 0,
      "hasMaximum": false,
      "allowedValues": [],
      "allowedValuesIgnoreCase": false
    }
  ],
  "result": {
    "kind": "object",
    "format": "",
    "nullable": false,
    "enumValues": [],
    "fields": [
      {
        "name": "typeName",
        "schema": {
          "kind": "string",
          "format": "",
          "nullable": false,
          "enumValues": [],
          "fields": []
        }
      }
    ]
  },
  "rules": [
    {
      "kind": "exactlyOneOf",
      "arguments": ["gameObjectPath", "gameObjectInstanceId"],
      "whenArgument": "",
      "whenEqualsJson": "",
      "requires": []
    }
  ]
}
```

参数顺序来自 handler 声明顺序。参数名称、wire schema、required/default 和
result fields 从 handler signature 与实际 result DTO 反射生成；仅无法由类型
推导的 non-empty、range、allowed-values 与跨参数 rule 需要稀疏 attribute。
同一个编译后的 contract 同时驱动 package preflight、snapshot 与 fingerprint，
不允许另写一份只用于文档的 schema。

`schema.kind` 当前为 `empty`、`string`、`boolean`、`integer`、`number`、
`enum`、`array`、`object`、`map` 或 `reference`。数组增加递归 `items`；
`map` 使用 `items` 描述每个 string-keyed value；object
增加按名称 ordinal 排序的 `fields`；enum values 也按 ordinal 排序。没有
声明 result type 时，`result.kind` 明确为 `empty`。

递归 result DTO 使用 schema 根上的 `$defs` object 与 occurrence 上的
`$ref`：

```json
{
  "kind": "object",
  "fields": [
    {
      "name": "roots",
      "schema": {
        "kind": "array",
        "items": {"kind": "reference", "$ref": "d0"}
      }
    }
  ],
  "$defs": {
    "d0": {
      "kind": "object",
      "fields": [
        {
          "name": "children",
          "schema": {
            "kind": "array",
            "items": {"kind": "reference", "$ref": "d0"}
          }
        }
      ]
    }
  }
}
```

Definition ID 按 ordinal field traversal 的首次使用顺序生成，不包含 CLR
type name。`$defs` 只出现在该 argument/result schema 的根；空、重复、
dangling 或未使用的 definition 会使注册失败。这样 hierarchy result 不会
被截断为空 object，也不会展开成无限 JSON tree。递归 input DTO 在注册时
被拒绝，因此 strict request preflight 的深度由非递归 input schema 决定，
不需要增加 runtime byte/depth cap。

Package 在调用 handler 前统一拒绝未知/重复参数、缺失 required、类型错误、
non-empty、range、allowed-values、session requirement 和跨参数 rule 违规。
结构化 argument 还会递归拒绝 unknown/duplicate/missing fields、null item
和错误的 nested wire type，再交给 `JsonUtility` 实例化。Input DTO field
默认 required 且 non-null；`CommandField` 只稀疏声明 optional、non-empty、
allow-null、range 或 allowed-values。

Allowed-values 会在注册时按参数 wire type 解析并规范化为 canonical JSON
text；例如 string `Cube` 写为 `"\"Cube\""`，因此不会与其他 JSON scalar
混淆。无法转换的值使注册失败，不能进入 snapshot 后再成为永远无法执行的
“合法值”。
`component/get` 现在要求 `gameObjectPath` 与 `gameObjectInstanceId` 恰好一个
具有非默认、非空值；`index` 必须大于等于 `0`。

Registry Snapshot 由 package 的确定性 JSON writer 生成，不使用
`JsonUtility` 序列化递归 schema tree。`declaringType`、`methodName` 和 CLR
`typeName` 不属于 Registry contract。

现有 `command/list` 仍保留 `{ "commands": [...] }` view，但 commands 现在复用
同一个 normalized contract writer，不再通过 `JsonUtility` 输出缺少 schema/result
的半份 descriptor。CLI discovery、cache 与 preflight 仍必须使用支持分区与
fingerprint 的 `command/registry.snapshot`。

### Editor、Object 与 Scene surface 收口

Builtin executable registry 现在是 57 个 command：51 个 authoring command
与 6 个 control-plane command。以下 route 已从 package registry 和 dispatch
中直接删除，不提供 alias 或 redirect：

```text
editor/menu.open
editor/window.open
editor/playmode.status
transform/get
```

CLI 应继续在 Deny Policy 中阻止 menu/window intent，不能自动改走 snippet
或 raw execution。Play-mode read 统一使用 `editor/status`；其 result 新增
`isPlayingOrWillChangePlaymode` 与以下稳定 `playmodeState`：

```text
editMode
enteringPlaymode
playMode
exitingPlaymode
```

Transform read 统一使用 `gameobject/get.transform`。Projection 字段是
`localPosition`、`localEulerAngles`、`localScale`、`position` 与
`eulerAngles`；不再暴露语义错误的 `localRotation` / `rotation` 名称。
`gameobject/get` 还新增 `isStatic`，用于正式 readback。

`gameobject/modify` 不再使用 `-1` sentinel：

| argument | wire schema | default |
|---|---|---|
| `layer` | nullable int32，range `0..31` | `null` |
| `active` | nullable boolean | `null` |
| `isStatic` | nullable boolean | `null` |

显式 `false` 与 `0` 都是有效 mutation；省略或显式 `null` 不计 mutation。
`component/add|remove|get|modify`、`transform/set` 与 GameObject selector/
mutation rules 均由 package 在 handler 前执行。`FieldPair[]` 与 `Vector3`
现在按 snapshot 中的 nested schema 做 exact-shape preflight。

`scene/hierarchy` 的 recursive `children` 使用 `$ref` 指回同一个稳定 node
definition，`depth` 在 dispatch 前要求大于等于 `-1`。

### schema version 1 的 canonical bytes

Fingerprint 不依赖 `JsonUtility` 的字段顺序。每个分区独立写入以下 canonical
binary stream，再对完整 byte stream 计算 SHA-256：

1. little-endian Int32 `schemaVersion`；
2. 分区名；
3. little-endian Int32 command count；
4. command 按 Canonical Command ID 做 ordinal 排序后，依次写入：
   `id`、wire `commandNamespace`、wire `action`、`summary`、`partition`、
   requirements 的 `editor`、`mainThread`、`sessionId`，然后是 argument
   count 与声明顺序中的 arguments；
5. 每个 argument 依次写入 `name`、递归 schema、`required`、
   `hasDefault`、canonical `defaultJson`、`nonEmpty`、`hasMinimum` 及可选
   IEEE-754 little-endian Float64 minimum、`hasMaximum` 及可选 Float64
   maximum、ordinal-normalized allowed values 和
   `allowedValuesIgnoreCase`；
6. 每个 schema 依次写入 `kind`、`format`、`nullable`、`reference`、
   `hasItems` 与可选 recursive items、field count 与按名称 ordinal 排序的
   fields、ordinal enum values，以及 definitions；
7. 每个 field 依次写入 `name`、递归 schema、`required`、`nonEmpty`、
   `hasMinimum` 与可选 minimum、`hasMaximum` 与可选 maximum、ordinal
   allowed values 和 `allowedValuesIgnoreCase`；每个 definition 写入 `id`
   与有限 recursive schema；
8. 写入递归 result schema；
9. 写入 rule count；rules 按完整规范化内容排序，每条依次写入 `kind`、
   声明顺序的 arguments、`whenArgument`、canonical `whenEqualsJson` 和
   声明顺序的 requires。

字符串编码固定为“little-endian Int32 UTF-8 byte length + UTF-8 bytes”，无
BOM；布尔值固定为单字节 `0` 或 `1`。默认 JSON 与数值使用 invariant
canonical representation；未声明的约束数值归一为 `0`，`-0` 归一为 `0`。
`declaringType`、`methodName`、transport projection、generation 和
fingerprint 自身不参与分区 hash。

### CLI 对接要求

- 每个 live discovery cycle 先调用轻量 fingerprint。
- 只有 cache 缺失、对应分区 fingerprint 变化或用户显式 refresh 时，才调用
  snapshot 并请求必要分区。
- 两次调用的 `registryGeneration` 不一致时，丢弃 snapshot 并重新解析一次
  registry state，不能把跨 generation 的分区合并进同一 cache。
- 这两个 command 属于 control-plane，不进入 Unity authoring surface。

## 2026-07-25：完整 Builtin Contract、原子 Custom Discovery 与离线产物

57 个 executable builtin（51 个 authoring + 6 个 control-plane）现在都从
实际 handler signature/result DTO 生成非空 Canonical Command Contract。
本次补齐的 authoring 分组是 `asset/*`、`project/*`、`material/*`、
`prefab/*`、`screenshot/*` 与 `profiler/*`；`session/*`、
`command/list`、`command/registry.fingerprint` 和
`command/registry.snapshot` 的 result contract 也已补齐。

CLI 需要注意以下不兼容收紧：

- `material/get` 要求 `assetPath` 与 `gameObjectPath` 恰好一个；
- `material/assign`、`prefab/create`、`prefab/unpack` 的 GameObject
  selector 也在 dispatch 前执行 exactly-one preflight；
- `prefab/asset_get.transform.localRotation` 正名为
  `localEulerAngles`，并新增顶层 `isStatic` readback；
- `prefab/asset_modify_gameobject` 的 `layer`、`active`、`isStatic`
  改为 nullable mutation，默认 `null`；`layer` range 为 `0..31`，
  显式 `0` / `false` 有效；
- `project/scene.open.mode` 只接受大小写不敏感的 `single` 或
  `additive`；`saveAsCopy=true` 时 `project/scene.save.scenePath`
  必填且非空；
- `asset/delete` 保留单路径与数组可同时提供的 at-least-one 语义；
- screenshot size 必须非负，`game_view.superSize >= 1`；
- asset import 与 reimport 继续是两个独立 canonical commands，
  只有 reimport 添加 `ForceUpdate`。

Registry control-plane 的自描述 result schema 与真实 JSON wire 现在一致：
recursive schema 仍在 occurrence 使用 `$ref`，根上使用 `$defs` object。
为了描述 `$defs` 的动态 definition key，自描述 meta-schema 使用
`kind: "map"`，其 `items` 指向同一个 recursive value-schema definition。
这只新增 schema 表达能力，不改变 authoring command 的 request shape。

Custom command discovery 改为原子发布：

1. 只有可能引用 command runtime 的非 dynamic `AssemblyLoad` 才推进 dirty
   epoch，无关 framework/plugin assembly load 不触发重扫；
2. 下一次 dispatch/fingerprint/snapshot 按 assembly、type、method 的稳定
   ordinal 顺序构建完整候选 registry；
3. 所有 binding、duplicate、contract normalization、reference 与
   fingerprint 均成功，且构建前后 assembly/config epoch 一致后，才替换
   live registry；
4. 非法 custom metadata、partial type load 或 filter 失败会使当前请求明确
   失败，不会发布部分 custom 集合，也不会把旧 generation 冒充当前状态。

`CommandDiscoveryOptions.Configure` 会复制 options/prefix array；调用者修改原
object 不会绕过 generation invalidation。自定义 assembly filter 在一次配置
生命周期内必须保持确定性，state 变化时需要再次调用 `Configure`。

Package 随附由同一 builtin 注册路径生成的首次离线 snapshot：

```text
Editor/ExternalTool~/console-client/csharpconsole_core/data/
  builtin_registry_snapshot.v1.json
```

它是 schema-version 1 的完整 Registry Snapshot：builtin 分区含 57 个
contracts，custom 分区固定 `included:false` 且不含 commands；没有 timestamp、
machine path 或项目 custom metadata。维护者通过以下 Unity Editor 方法生成或
检查，不得手工修改 JSON：

```text
Zh1Zh1.CSharpConsole.Editor.EditorTools.
  BuiltinRegistrySnapshotGenerator.Generate
Zh1Zh1.CSharpConsole.Editor.EditorTools.
  BuiltinRegistrySnapshotGenerator.Check
```

后续 unity-cli 实现首次纯离线 fallback 时，应从这个产物显式 export 一份
byte-for-byte copy 随 skill 分发，并在测试中比较两份 bytes/fingerprint；
这不是恢复手写 `command_manifest.json`。若 package 已解析，可直接读取 package
copy；live fingerprint 不同仍走正常 changed-partition refresh。

Routing Overlay 的 tier 仍由 CLI 拥有：`asset/create_folder` 与
`screenshot/scene_view` 应投影为 Advanced。Pagination/cursor、overwrite
safety、异步 screenshot 完成 readback 与 material 非零 slot readback 不在
本次协议变更中，需另开后续 PR。

## 2026-07-26：Scene Save 路径改为合同级必填

`project/scene.save.scenePath` 现在始终 required、non-empty，不再只在
`saveAsCopy=true` 时通过条件 rule 要求。Package handler 不再隐式回退到当前
Scene 路径，而是统一使用调用方显式提供的目标路径。

这是有意的不兼容收紧：CLI 应从新的 package-owned contract 与同步离线
snapshot 执行通用 preflight。缺失或空 `scenePath` 必须在 HTTP dispatch 前
失败；调用方保存已有 Scene 时也应显式传入它的当前路径。新目标路径的父目录
仍需在保存前存在。

## 2026-08-05：Registry Snapshot 改为条件请求，fingerprint 端点退役

`command/registry.fingerprint` 移除，registry 解析收敛为一次条件请求：

```text
command/registry.snapshot(ifGeneration = null)
```

- `ifGeneration` 为可选 nullable string。省略或为 null 时始终返回完整
  snapshot（两个分区全量）。
- `ifGeneration` 与当前 `registryGeneration` 逐字符相等时，响应只含
  `schemaVersion`、`registryGeneration` 与 `unchanged: true`，不携带分区。
- 不相等时返回完整当前 snapshot；完整响应不写 `unchanged` 字段，因此既有
  离线 snapshot 解析器无需变更即可继续读取完整形态。
- `partition` 参数与分区级差量下载一并移除；CLI 不再按分区合并，也不再在
  运行时复算 fingerprint，`registryGeneration` 对 CLI 是不透明 token。

Builtin 分区随本次收缩为 56 个 command（authoring 仍为 51 个，
control-plane 由 6 个减为 5 个）。CLI 侧对
snapshot 响应的判定顺序：先读 `unchanged`，为 true 时复用既有 cache，
否则按完整 snapshot 校验并替换 cache。

## 2026-08-05：移除离线 builtin snapshot 产物与生成器

生成式离线产物整体退役：`BuiltinRegistrySnapshotGenerator`（含菜单项）、
`CommandRegistryArtifacts` 与打包内的
`builtin_registry_snapshot.v1.json` 全部删除，不再存在需要手动生成或
跨仓同步的入库产物。

语义变化：CLI 首次使用必须有一次可用的 Editor 服务连接来初始化
machine-local Command Cache；此后所有离线场景由该 cache 承担。没有
cache 且离线时，CLI 报出明确指引（启动一次 Editor 服务）而不再回退到
打包快照。序列化器跨实现一致性改为在 live seam 上验证（CLI 拉取当前
snapshot 并独立复算 fingerprint 比对），不再依赖入库时点的产物。

漂移防御的判据是 contract 数据层：command id、参数 schema、描述等由
包单方下发，CLI 直接消费不手抄，live parity 复算证明两端序列化一致。
两端参数校验器的实现语义差异不设门禁；包侧 binder 是权威判定，任何
分歧最终以 binder 为准。
